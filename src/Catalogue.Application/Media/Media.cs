using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Application.Media;

public sealed record MediaContent(Stream Content, string ContentType, long Length);

/// <summary>Byte storage for product media (Blob container or the project's disk fallback).</summary>
public interface IMediaStorage
{
    string Mode { get; }
    string Location { get; }
    Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct);
    Task<MediaContent?> OpenAsync(string path, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}

public sealed record ImageInfo(int Width, int Height);

/// <summary>Decodes images and renders thumbnails (SkiaSharp in Infrastructure).</summary>
public interface IImageProcessor
{
    ImageInfo? Probe(byte[] bytes);
    byte[] Thumbnail(byte[] bytes, int maxEdge);
}

/// <summary>
/// Identifies files by their bytes, never by name or the client's Content-Type. Only safe, renderable
/// formats are accepted (no SVG/HTML, which could carry script).
/// </summary>
public static class MediaSniffer
{
    public const long MaxImageBytes = 10 * 1024 * 1024;
    public const long MaxDocumentBytes = 50 * 1024 * 1024;

    public static (string ContentType, string Extension, bool IsImage)? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ("image/jpeg", "jpg", true);
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ("image/png", "png", true);
        if (b.Length >= 6 && (b[..6].SequenceEqual("GIF87a"u8) || b[..6].SequenceEqual("GIF89a"u8))) return ("image/gif", "gif", true);
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return ("image/webp", "webp", true);
        if (b.Length >= 5 && b[..5].SequenceEqual("%PDF-"u8)) return ("application/pdf", "pdf", false);
        return null;
    }

    /// <summary>Document kind from a file name hint (images are always <see cref="AssetKind.Image"/>).</summary>
    public static AssetKind GuessKind(string fileName, bool isImage)
    {
        if (isImage) return AssetKind.Image;
        var n = fileName.ToLowerInvariant();
        return n.Contains("manual") || n.Contains("install") || n.Contains("instruction") ? AssetKind.Manual
            : n.Contains("energy") || n.Contains("label") || n.Contains("erp") ? AssetKind.EnergyLabel
            : n.Contains("cert") || n.Contains("declaration") || n.Contains("doc_") ? AssetKind.Certificate
            : n.Contains("drawing") || n.Contains("dimension") ? AssetKind.Drawing
            : AssetKind.Datasheet;
    }
}

/// <summary>
/// Short-lived HMAC-signed media URLs. Browsers load &lt;img&gt; tags without the bearer token, so the
/// API hands out signed links (default 1 hour) instead of making the container public.
/// </summary>
public sealed class MediaUrlSigner(byte[] key)
{
    public string Url(long assetId, bool thumbnail, TimeSpan? ttl = null)
    {
        var exp = DateTimeOffset.UtcNow.Add(ttl ?? TimeSpan.FromHours(1)).ToUnixTimeSeconds();
        // Round expiry to 5 minutes so the same image keeps the same URL briefly (browser cache hits).
        exp -= exp % 300;
        var variant = thumbnail ? "t" : "o";
        return $"/api/media/{assetId}/{variant}?exp={exp}&sig={Sign(assetId, variant, exp)}";
    }

    public bool Verify(long assetId, string variant, long exp, string? sig) =>
        sig is not null && exp >= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(assetId, variant, exp)), Encoding.ASCII.GetBytes(sig));

    private string Sign(long id, string variant, long exp) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}:{variant}:{exp}")))[..32].ToLowerInvariant();
}

public sealed class MediaRejectedException(string message) : InvalidOperationException(message);

/// <summary>Stores product images and documents: validate → de-duplicate → store → thumbnail → record.</summary>
public sealed class MediaService(ICatalogueDbContext db, IMediaStorage storage, IImageProcessor images)
{
    public const int ThumbnailEdge = 320;

    public async Task<ProductAsset> AddAsync(long productId, Stream content, string fileName, AssetKind? kind, string? title,
        AssetSource source, string uploadedBy, CancellationToken ct, string? sourceUrl = null, long? supplierId = null)
        => (await AddWithStatusAsync(productId, content, fileName, kind, title, source, uploadedBy, ct, sourceUrl, supplierId)).Asset;

    /// <summary>As <see cref="AddAsync"/>, also reporting whether a new asset was created (false = duplicate bytes).</summary>
    public async Task<(ProductAsset Asset, bool Created)> AddWithStatusAsync(long productId, Stream content, string fileName,
        AssetKind? kind, string? title, AssetSource source, string uploadedBy, CancellationToken ct,
        string? sourceUrl = null, long? supplierId = null)
    {
        // Buffer with a hard cap (documents can be large, images less so); never trust Content-Length.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MediaSniffer.MaxDocumentBytes) throw new MediaRejectedException("File is larger than 50 MB.");
        }
        var bytes = buffer.ToArray();
        if (bytes.Length == 0) throw new MediaRejectedException("File is empty.");

        var detected = MediaSniffer.Detect(bytes) ?? throw new MediaRejectedException(
            $"'{Path.GetFileName(fileName)}' is not a supported file. Images: JPEG, PNG, WebP, GIF. Documents: PDF.");
        if (detected.IsImage && bytes.Length > MediaSniffer.MaxImageBytes) throw new MediaRejectedException("Images must be 10 MB or smaller.");

        var finalKind = kind ?? MediaSniffer.GuessKind(fileName, detected.IsImage);
        if (detected.IsImage != (finalKind == AssetKind.Image))
            throw new MediaRejectedException(detected.IsImage ? "An image cannot be stored as a document." : "Only images can be stored as product images.");

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var existing = await db.ProductAssets.FirstOrDefaultAsync(a => a.ProductId == productId && a.Sha256 == sha, ct);
        if (existing is not null) return (existing, false); // idempotent re-upload / re-fetch

        ImageInfo? info = null;
        string? thumbPath = null;
        var basePath = $"products/{productId}/{sha[..16]}";
        if (detected.IsImage)
        {
            info = images.Probe(bytes) ?? throw new MediaRejectedException("The image could not be decoded (corrupt file?).");
            var thumb = images.Thumbnail(bytes, ThumbnailEdge);
            thumbPath = $"{basePath}_thumb.jpg";
            await using var ts = new MemoryStream(thumb);
            await storage.SaveAsync(thumbPath, ts, "image/jpeg", ct);
        }
        var path = $"{basePath}.{detected.Extension}";
        await using (var ms = new MemoryStream(bytes))
            await storage.SaveAsync(path, ms, detected.ContentType, ct);

        var asset = new ProductAsset(productId, finalKind, title ?? "", fileName, detected.ContentType, bytes.Length, sha, path,
            thumbPath, info?.Width, info?.Height, source, uploadedBy, sourceUrl, supplierId);
        if (finalKind == AssetKind.Image && !await db.ProductAssets.AnyAsync(a => a.ProductId == productId && a.IsPrimary, ct))
            asset.SetPrimary(true);
        db.ProductAssets.Add(asset);
        await db.SaveChangesAsync(ct);
        return (asset, true);
    }

    public async Task SetPrimaryAsync(long productId, long assetId, CancellationToken ct)
    {
        var assets = await db.ProductAssets.Where(a => a.ProductId == productId && a.Kind == AssetKind.Image).ToListAsync(ct);
        if (assets.All(a => a.Id != assetId)) throw new KeyNotFoundException("Image not found on this product.");
        foreach (var a in assets) a.SetPrimary(a.Id == assetId);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(long productId, long assetId, CancellationToken ct)
    {
        var asset = await db.ProductAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.ProductId == productId, ct)
            ?? throw new KeyNotFoundException("Asset not found.");
        db.ProductAssets.Remove(asset);
        if (asset.IsPrimary)
        {
            var next = await db.ProductAssets.Where(a => a.ProductId == productId && a.Kind == AssetKind.Image && a.Id != assetId)
                .OrderBy(a => a.SortOrder).ThenBy(a => a.Id).FirstOrDefaultAsync(ct);
            next?.SetPrimary(true);
        }
        await db.SaveChangesAsync(ct);
        await storage.DeleteAsync(asset.StoragePath, ct);
        if (asset.ThumbnailPath is not null) await storage.DeleteAsync(asset.ThumbnailPath, ct);
    }
}

public sealed record BulkMediaItem(string File, string Status, string? Sku, long? ProductId, string? Kind, string? Message);
public sealed record BulkMediaReport(int Files, int Attached, int Duplicates, int Unmatched, int Rejected, IReadOnlyList<BulkMediaItem> Items);

/// <summary>
/// Bulk media import: a zip of files named after the product - <c>&lt;GTIN|MPN|SKU&gt;[_n].ext</c>
/// (e.g. <c>5060000000009.jpg</c>, <c>5060000000009_2.jpg</c>, <c>TH-PLUS-5_manual.pdf</c>). Each file is
/// matched by GTIN first, then manufacturer part number, then SKU. This is how supplier media packs
/// are typically delivered alongside (not inside) EDI price catalogues.
/// </summary>
public sealed class BulkMediaImporter(ICatalogueDbContext db, MediaService media)
{
    public const int MaxEntries = 5000;

    public async Task<BulkMediaReport> ImportAsync(Stream zip, string uploadedBy, CancellationToken ct)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(e => e.Length > 0 && !e.FullName.EndsWith('/')
            && !Path.GetFileName(e.FullName).StartsWith('.')).ToList();
        if (entries.Count > MaxEntries) throw new MediaRejectedException($"A media pack can hold at most {MaxEntries} files.");

        var items = new List<BulkMediaItem>();
        int attached = 0, duplicates = 0, unmatched = 0, rejected = 0;
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry.FullName);
            var key = Path.GetFileNameWithoutExtension(name).Split('_')[0].Trim();
            var product = await MatchAsync(key, ct);
            if (product is null)
            {
                unmatched++;
                items.Add(new BulkMediaItem(name, "Unmatched", null, null, null, $"No product with GTIN, MPN or SKU '{key}'."));
                continue;
            }
            try
            {
                await using var s = entry.Open();
                var (asset, isNew) = await media.AddWithStatusAsync(product.Value.Id, s, name, null, null, AssetSource.BulkZip, uploadedBy, ct);
                if (isNew) attached++; else duplicates++;
                items.Add(new BulkMediaItem(name, isNew ? "Attached" : "Duplicate", product.Value.Sku, product.Value.Id, asset.Kind.ToString(), null));
            }
            catch (MediaRejectedException ex)
            {
                rejected++;
                items.Add(new BulkMediaItem(name, "Rejected", product.Value.Sku, product.Value.Id, null, ex.Message));
            }
        }
        return new BulkMediaReport(entries.Count, attached, duplicates, unmatched, rejected, items);
    }

    private async Task<(long Id, string Sku)?> MatchAsync(string key, CancellationToken ct)
    {
        if (key.Length == 0) return null;
        if (key.All(char.IsAsciiDigit) && Gtin.IsValid(key))
        {
            var gtin = Gtin.Normalize(key);
            var byGtin = await db.Products.Where(p => p.Gtin == gtin).OrderBy(p => p.Id).Select(p => new { p.Id, p.Sku }).FirstOrDefaultAsync(ct);
            if (byGtin is not null) return (byGtin.Id, byGtin.Sku);
        }
        var upper = key.ToUpperInvariant();
        var byMpn = await db.Products.Where(p => p.ManufacturerPartNumber == upper).OrderBy(p => p.Id).Select(p => new { p.Id, p.Sku }).FirstOrDefaultAsync(ct);
        if (byMpn is not null) return (byMpn.Id, byMpn.Sku);
        var bySku = await db.Products.Where(p => p.Sku == upper).OrderBy(p => p.Id).Select(p => new { p.Id, p.Sku }).FirstOrDefaultAsync(ct);
        return bySku is null ? null : (bySku.Id, bySku.Sku);
    }
}
