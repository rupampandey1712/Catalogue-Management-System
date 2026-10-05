using Catalogue.Application;
using Catalogue.Application.Media;
using Catalogue.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Net.Http.Headers;

namespace Catalogue.Web.Endpoints;

/// <summary>Product images and documents (bytes in Blob storage, served through signed URLs).</summary>
public static class MediaEndpoints
{
    public sealed record AssetDto(long Id, string Kind, string Title, string FileName, string ContentType, long SizeBytes,
        int? Width, int? Height, bool IsPrimary, int SortOrder, string Source, string? SourceUrl, string UploadedBy,
        DateTime UploadedAtUtc, string Url, string? ThumbnailUrl);
    public sealed record UpdateAssetRequest(string Title, string Kind, int SortOrder);

    public static AssetDto ToDto(ProductAsset a, MediaUrlSigner signer) => new(a.Id, a.Kind.ToString(), a.Title, a.FileName,
        a.ContentType, a.SizeBytes, a.Width, a.Height, a.IsPrimary, a.SortOrder, a.Source.ToString(), a.SourceUrl, a.UploadedBy,
        a.UploadedAtUtc, signer.Url(a.Id, false), a.ThumbnailPath is null ? null : signer.Url(a.Id, true));

    public static void Map(IEndpointRouteBuilder api)
    {
        // Public endpoint, but only reachable with a valid, unexpired signature minted by the API.
        api.MapGet("/media/{id:long}/{variant}", async (long id, string variant, long exp, string? sig, HttpContext http,
            MediaUrlSigner signer, ICatalogueDbContext db, IMediaStorage storage, CancellationToken ct) =>
        {
            if (variant is not ("o" or "t") || !signer.Verify(id, variant, exp, sig)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var asset = await db.ProductAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            if (asset is null) return Results.NotFound();
            var etag = new EntityTagHeaderValue($"\"{asset.Sha256[..16]}{variant}\"");
            if (http.Request.Headers.IfNoneMatch.ToString() == etag.ToString()) return Results.StatusCode(StatusCodes.Status304NotModified);
            var path = variant == "t" && asset.ThumbnailPath is not null ? asset.ThumbnailPath : asset.StoragePath;
            var content = await storage.OpenAsync(path, ct);
            if (content is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=3600";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            var inline = asset.Kind == AssetKind.Image || variant == "t";
            return Results.Stream(content.Content, content.ContentType, inline ? null : asset.FileName,
                entityTag: etag, enableRangeProcessing: true);
        }).AllowAnonymous().DisableRequestTimeout().WithTags("Media");

        var products = api.MapGroup("/products/{productId:long}/assets").WithTags("Media");

        products.MapGet("", async (long productId, ICatalogueDbContext db, MediaUrlSigner signer, CancellationToken ct) =>
            Results.Ok((await db.ProductAssets.AsNoTracking().Where(a => a.ProductId == productId)
                .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Kind).ThenBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync(ct))
                .Select(a => ToDto(a, signer))))
            .RequireAuthorization("ReadCatalogue");

        products.MapPost("", async (long productId, HttpRequest request, ICatalogueDbContext db, MediaService media,
            MediaUrlSigner signer, IActorContext actor, HybridCache cache, CancellationToken ct) =>
        {
            if (!await db.Products.AnyAsync(p => p.Id == productId, ct)) return Results.NotFound();
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Multipart form with file(s) is required." });
            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0) return Results.BadRequest(new { error = "Attach at least one file." });
            AssetKind? kind = Enum.TryParse<AssetKind>(form["kind"], true, out var k) ? k : null;

            var added = new List<AssetDto>();
            var rejected = new List<object>();
            foreach (var file in form.Files.Take(20))
            {
                try
                {
                    await using var s = file.OpenReadStream();
                    var asset = await media.AddAsync(productId, s, file.FileName, kind, form["title"].FirstOrDefault(),
                        AssetSource.Upload, actor.ActorId, ct);
                    added.Add(ToDto(asset, signer));
                }
                catch (MediaRejectedException ex) { rejected.Add(new { file = file.FileName, error = ex.Message }); }
            }
            if (added.Count > 0)
            {
                db.AuditEvents.Add(new AuditEvent(actor.ActorId, "MediaUploaded", "Product", productId.ToString(),
                    $"{added.Count} file(s): {string.Join(", ", added.Select(a => a.FileName))}", actor.CorrelationId));
                await db.SaveChangesAsync(ct);
                await cache.RemoveAsync(CacheTags.Product(productId), ct);
            }
            return added.Count == 0
                ? Results.BadRequest(new { error = "No file was accepted.", rejected })
                : Results.Ok(new { added, rejected });
        }).RequireAuthorization("CatalogueAdmin").DisableAntiforgery()
          .WithMetadata(new RequestSizeLimitAttribute(250L * 1024 * 1024)).WithRequestTimeout(Policies.LongRunning);

        products.MapPut("/{assetId:long}", async (long productId, long assetId, UpdateAssetRequest req, ICatalogueDbContext db,
            MediaUrlSigner signer, CancellationToken ct) =>
        {
            var asset = await db.ProductAssets.FirstOrDefaultAsync(a => a.Id == assetId && a.ProductId == productId, ct);
            if (asset is null) return Results.NotFound();
            if (!Enum.TryParse<AssetKind>(req.Kind, true, out var kind)) return Results.BadRequest(new { error = $"Unknown kind '{req.Kind}'." });
            asset.Update(req.Title, kind, req.SortOrder);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(asset, signer));
        }).RequireAuthorization("CatalogueAdmin");

        products.MapPost("/{assetId:long}/primary", async (long productId, long assetId, MediaService media, HybridCache cache, CancellationToken ct) =>
        {
            await media.SetPrimaryAsync(productId, assetId, ct);
            await cache.RemoveByTagAsync(CacheTags.Products, ct);
            return Results.NoContent();
        }).RequireAuthorization("CatalogueAdmin");

        products.MapDelete("/{assetId:long}", async (long productId, long assetId, MediaService media, ICatalogueDbContext db,
            IActorContext actor, HybridCache cache, CancellationToken ct) =>
        {
            await media.DeleteAsync(productId, assetId, ct);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "MediaDeleted", "Product", productId.ToString(), $"asset {assetId}", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await cache.RemoveByTagAsync(CacheTags.Products, ct);
            return Results.NoContent();
        }).RequireAuthorization("CatalogueAdmin");

        var media = api.MapGroup("/media").WithTags("Media");

        // Supplier media pack: zip of files named <GTIN|MPN|SKU>[_n].ext
        media.MapPost("/bulk", async (HttpRequest request, BulkMediaImporter importer, ICatalogueDbContext db,
            IActorContext actor, HybridCache cache, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Multipart form with a .zip is required." });
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null || !file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Upload a .zip media pack." });
            try
            {
                await using var s = file.OpenReadStream();
                var report = await importer.ImportAsync(s, actor.ActorId, ct);
                db.AuditEvents.Add(new AuditEvent(actor.ActorId, "MediaPackImported", "Media", file.FileName,
                    $"{report.Attached} attached, {report.Duplicates} duplicate, {report.Unmatched} unmatched, {report.Rejected} rejected", actor.CorrelationId));
                await db.SaveChangesAsync(ct);
                await cache.RemoveByTagAsync(CacheTags.Products, ct);
                return Results.Ok(report);
            }
            catch (MediaRejectedException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "The file is not a valid zip archive." }); }
        }).RequireAuthorization("SupplierAdmin").DisableAntiforgery()
          .WithMetadata(new RequestSizeLimitAttribute(1024L * 1024 * 1024)).WithRequestTimeout(Policies.LongRunning);

        media.MapGet("/stats", async (ICatalogueDbContext db, IMediaStorage storage, HybridCache cache, CancellationToken ct) =>
            Results.Ok(await cache.GetOrCreateAsync("media:stats", async token =>
            {
                var byKind = await db.ProductAssets.GroupBy(a => a.Kind)
                    .Select(g => new { kind = g.Key.ToString(), count = g.Count(), bytes = g.Sum(a => a.SizeBytes) }).ToListAsync(token);
                var withImage = await db.ProductAssets.Where(a => a.IsPrimary).CountAsync(token);
                var fetches = await db.MediaFetchRequests.GroupBy(r => r.Status)
                    .Select(g => new { status = g.Key.ToString(), count = g.Count() }).ToListAsync(token);
                return new
                {
                    storage = new { mode = storage.Mode, location = storage.Location },
                    productsWithImage = withImage,
                    assets = byKind,
                    totalBytes = byKind.Sum(k => k.bytes),
                    fetches
                };
            }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(20), LocalCacheExpiration = TimeSpan.FromSeconds(20) },
            [CacheTags.Products], ct)))
            .RequireAuthorization("ReadCatalogue");

        media.MapGet("/fetches", async (ICatalogueDbContext db, string? status, int? take, CancellationToken ct) =>
        {
            var q = db.MediaFetchRequests.AsNoTracking();
            if (Enum.TryParse<MediaFetchStatus>(status, true, out var s)) q = q.Where(r => r.Status == s);
            return Results.Ok(await q.OrderByDescending(r => r.Id).Take(Math.Clamp(take ?? 50, 1, 500))
                .Select(r => new { r.Id, r.ProductId, kind = r.Kind.ToString(), r.Url, status = r.Status.ToString(), r.Attempts, r.LastError, r.NextAttemptUtc, r.CreatedAtUtc })
                .ToListAsync(ct));
        }).RequireAuthorization("ImportOps");
    }
}
