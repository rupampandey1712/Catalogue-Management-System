using Catalogue.Application;
using Catalogue.Application.Streaming;
using Catalogue.Contracts;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Web.Endpoints;

public static class ImportEndpoints
{
    public sealed record ImportDto(long Id, long SupplierId, string FileName, string Format, string Status,
        int TotalRows, int ProcessedRows, int ErrorRows, string? FailureReason,
        DateTime ReceivedAtUtc, DateTime? CompletedAtUtc, string? CorrelationId, string? SupplierCode = null);
    public sealed record ImportErrorDto(long Id, int RowNumber, string Field, string Message, string? RawValue);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/imports", async (HttpRequest http, ImportService imports, CancellationToken ct) =>
        {
            if (!http.HasFormContentType || http.Form.Files.Count == 0)
                return Results.BadRequest(new { error = "Multipart form with a file is required." });
            var file = http.Form.Files[0];
            if (!long.TryParse(http.Form["supplierId"], out var supplierId))
                return Results.BadRequest(new { error = "supplierId form field is required." });
            try
            {
                await using var stream = file.OpenReadStream();
                var import = await imports.StartImportAsync(supplierId, stream, file.FileName, ct);
                return Results.Accepted($"/api/imports/{import.Id}", ToDto(import));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireAuthorization("SupplierAdmin").WithTags("Imports").DisableAntiforgery().WithRequestTimeout(Policies.LongRunning);

        // Returns a plain array (Logic Apps depend on that shape); the total is in X-Total-Count.
        api.MapGet("/imports", async (ICatalogueDbContext db, HttpResponse response, int? page, int? pageSize,
            string? status, string? format, long? supplierId, string? search, CancellationToken ct) =>
        {
            var pg = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 20, 1, 100);
            var q = db.ImportFiles.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(status))
            {
                var wanted = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => Enum.TryParse<ImportStatus>(x, true, out var st) ? st : (ImportStatus?)null)
                    .OfType<ImportStatus>().ToList();
                q = q.Where(i => wanted.Contains(i.Status));
            }
            if (!string.IsNullOrWhiteSpace(format))
            {
                var fmt = format.ToUpperInvariant();
                q = q.Where(i => i.Format == fmt);
            }
            if (supplierId is not null) q = q.Where(i => i.SupplierId == supplierId);
            if (!string.IsNullOrWhiteSpace(search)) q = q.Where(i => i.FileName.Contains(search));
            response.Headers["X-Total-Count"] = (await q.CountAsync(ct)).ToString();
            var items = await q.OrderByDescending(i => i.ReceivedAtUtc)
                .Skip((pg - 1) * ps).Take(ps)
                .Select(i => new ImportDto(i.Id, i.SupplierId, i.FileName, i.Format, i.Status.ToString(),
                    i.TotalRows, i.ProcessedRows, i.ErrorRows, i.FailureReason,
                    i.ReceivedAtUtc, i.CompletedAtUtc, i.CorrelationId, i.Supplier!.Code)).ToListAsync(ct);
            return Results.Ok(items);
        }).RequireAuthorization("ImportOps").WithTags("Imports");

        api.MapGet("/imports/{id:long}", async (long id, ICatalogueDbContext db, CancellationToken ct) =>
        {
            var import = await db.ImportFiles.AsNoTracking().Include(i => i.Supplier).FirstOrDefaultAsync(i => i.Id == id, ct);
            return import is null ? Results.NotFound() : Results.Ok(ToDto(import));
        }).RequireAuthorization("ImportOps").WithTags("Imports");

        // Streamed imports can carry tens of thousands of row errors: paged, total in X-Total-Count.
        api.MapGet("/imports/{id:long}/errors", async (long id, ICatalogueDbContext db, HttpResponse response,
            int? page, int? pageSize, string? field, string? search, CancellationToken ct) =>
        {
            var pg = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 1000, 1, 5000);
            var q = db.ImportItemErrors.AsNoTracking().Where(e => e.ImportId == id);
            if (!string.IsNullOrWhiteSpace(field)) q = q.Where(e => e.Field == field);
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(e => e.Message.Contains(search) || (e.RawValue != null && e.RawValue.Contains(search)));
            response.Headers["X-Total-Count"] = (await q.CountAsync(ct)).ToString();
            return Results.Ok(await q.OrderBy(e => e.RowNumber).ThenBy(e => e.Id)
                .Skip((pg - 1) * ps).Take(ps)
                .Select(e => new ImportErrorDto(e.Id, e.RowNumber, e.Field, e.Message, e.RawValue))
                .ToListAsync(ct));
        }).RequireAuthorization("ImportOps").WithTags("Imports");

        api.MapGet("/imports/{id:long}/errors/summary", async (long id, ICatalogueDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ImportItemErrors.Where(e => e.ImportId == id)
                .GroupBy(e => e.Field).Select(g => new { field = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count).ToListAsync(ct)))
            .RequireAuthorization("ImportOps").WithTags("Imports");

        api.MapPost("/imports/{id:long}/reprocess", async (long id, ImportReprocessService reprocess, IActorContext actor, CancellationToken ct) =>
        {
            try
            {
                return await reprocess.ReprocessAsync(id, actor.ActorId, actor.CorrelationId, ct)
                    ? Results.Accepted($"/api/imports/{id}") : Results.NotFound();
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireAuthorization("ImportOps").WithTags("Imports");
    }

    private static ImportDto ToDto(ImportFile i) => new(
        i.Id, i.SupplierId, i.FileName, i.Format, i.Status.ToString(),
        i.TotalRows, i.ProcessedRows, i.ErrorRows, i.FailureReason,
        i.ReceivedAtUtc, i.CompletedAtUtc, i.CorrelationId, i.Supplier?.Code);
}
