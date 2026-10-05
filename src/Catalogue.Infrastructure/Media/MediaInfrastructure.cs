using System.Net;
using System.Net.Sockets;
using Catalogue.Application;
using Catalogue.Application.Media;
using Catalogue.Domain;
using Catalogue.Infrastructure.Resilience;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Catalogue.Infrastructure.Media;

/// <summary>SkiaSharp (MIT) decoder/thumbnailer: decode, scale to fit, flatten onto white, JPEG.</summary>
public sealed class SkiaImageProcessor : IImageProcessor
{
    public ImageInfo? Probe(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return codec is null ? null : new ImageInfo(codec.Info.Width, codec.Info.Height);
    }

    public byte[] Thumbnail(byte[] bytes, int maxEdge)
    {
        using var source = SKBitmap.Decode(bytes) ?? throw new MediaRejectedException("The image could not be decoded.");
        var scale = Math.Min(1f, (float)maxEdge / Math.Max(source.Width, source.Height));
        var w = Math.Max(1, (int)(source.Width * scale));
        var h = Math.Max(1, (int)(source.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.White); // transparent PNGs look right as JPEG thumbnails
        using var img = SKImage.FromBitmap(source);
        surface.Canvas.DrawImage(img, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Jpeg, 82);
        return encoded.ToArray();
    }
}

/// <summary>
/// Guards supplier-provided URLs against SSRF: http(s) only, and no loopback/private/link-local
/// targets unless explicitly allowed (Media:AllowPrivateNetworks, for local demos).
/// </summary>
public static class UrlGuard
{
    public static async Task<string?> RejectReasonAsync(string url, bool allowPrivate, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "Only absolute http(s) URLs are allowed.";
        if (allowPrivate) return null;
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct); }
        catch (SocketException) { return "Host could not be resolved."; }
        return addresses.Any(IsPrivate) ? "URL points to a private or loopback address." : null;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254) || b[0] == 0;
    }
}

/// <summary>
/// Downloads media referenced by supplier feeds (EDI carries only the link). Polls due requests,
/// fetches with the standard HTTP resilience pipeline, validates the bytes like any upload, and
/// backs off on transient failures. Several instances can run; a row is claimed before work.
/// </summary>
public sealed class MediaFetchService(IServiceScopeFactory scopes, IHttpClientFactory http, IConfiguration config,
    ILogger<MediaFetchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var allowPrivate = config.GetValue<bool>("Media:AllowPrivateNetworks");
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = 0;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ICatalogueDbContext>();
                var media = scope.ServiceProvider.GetRequiredService<MediaService>();
                var now = DateTime.UtcNow;
                var due = await db.MediaFetchRequests
                    .Where(r => r.Status == MediaFetchStatus.Pending && r.NextAttemptUtc <= now)
                    .OrderBy(r => r.NextAttemptUtc).Take(20).ToListAsync(stoppingToken);
                foreach (var request in due)
                {
                    worked++;
                    await FetchOneAsync(request, db, media, allowPrivate, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Media fetch loop error; continuing.");
            }
            await Task.Delay(worked > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task FetchOneAsync(MediaFetchRequest request, ICatalogueDbContext db, MediaService media, bool allowPrivate, CancellationToken ct)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["ProductId"] = request.ProductId, ["MediaUrl"] = request.Url });
        try
        {
            if (await UrlGuard.RejectReasonAsync(request.Url, allowPrivate, ct) is { } reason)
            {
                request.Failed(reason, permanent: true);
                await db.SaveChangesAsync(ct);
                return;
            }
            var client = http.CreateClient("media");
            using var response = await client.GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var permanent = (int)response.StatusCode is >= 400 and < 500 and not 408 and not 429;
                request.Failed($"HTTP {(int)response.StatusCode}", permanent);
                await db.SaveChangesAsync(ct);
                return;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var name = Path.GetFileName(new Uri(request.Url).LocalPath);
            var asset = await media.AddAsync(request.ProductId, stream, string.IsNullOrWhiteSpace(name) ? "download" : name,
                request.Kind == AssetKind.Image ? AssetKind.Image : null, null, AssetSource.SupplierUrl, "media-fetch", ct,
                request.Url, request.SupplierId);
            request.Succeeded(asset.Id);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Fetched {Kind} for product {ProductId}.", asset.Kind, request.ProductId);
        }
        catch (MediaRejectedException ex)
        {
            request.Failed(ex.Message, permanent: true);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            request.Failed(ex.Message, permanent: !TransientFaults.IsTransient(ex));
            await db.SaveChangesAsync(ct);
        }
    }
}
