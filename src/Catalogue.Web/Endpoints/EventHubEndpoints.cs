using Catalogue.Application;
using Catalogue.Application.Streaming;
using Catalogue.Domain;
using Catalogue.Infrastructure.Streaming;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Web.Endpoints;

public static class EventHubEndpoints
{
    public sealed record StartStreamRequest(long SupplierId, string Source, int? ChunkSize);
    public sealed record GenerateRequest(long LineItems, double? ErrorRate, string? Name, long? StartIndex = null, int? Seed = null);
    public sealed record RestreamRequest(int? ChunkSize);
    public sealed record StreamImportDto(long Id, long SupplierId, string SupplierCode, string FileName, string Status,
        int TotalRows, int ProcessedRows, int ErrorRows, DateTime ReceivedAtUtc, DateTime? CompletedAtUtc, string? FailureReason);

    private const long MaxGeneratedItems = 10_000_000;

    public static void Map(IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/eventhub").WithTags("Event Hub").RequireAuthorization("ImportOps");

        group.MapGet("/overview", async (IEventHubProducer hub, StreamTelemetry telemetry, EventHubOptions options,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            IReadOnlyList<PartitionInfo> partitions = [];
            string? connectionError = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                partitions = await hub.GetPartitionsAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                connectionError = ex.Message;
                logs.CreateLogger("EventHub").LogWarning("Event Hub metadata unavailable: {Message}", ex.Message);
            }

            var snapshot = telemetry.Snapshot(partitions);
            return Results.Ok(new
            {
                hub = new
                {
                    mode = hub.Mode,
                    name = hub.HubName,
                    consumerGroup = hub.ConsumerGroup,
                    @namespace = (hub as AzureEventHubProducer)?.Namespace ?? "in-process",
                    partitionCount = partitions.Count > 0 ? partitions.Count : snapshot.Partitions.Count,
                    consumerInProcess = options.ConsumerInProcess || !options.Enabled,
                    connected = connectionError is null,
                    connectionError
                },
                snapshot.StartedUtc,
                snapshot.Totals,
                snapshot.Rates,
                snapshot.TotalLag,
                snapshot.Partitions,
                timeline = snapshot.Timeline,
                snapshot.Recent,
                snapshot.Runs
            });
        });

        group.MapGet("/streams", async (ICatalogueDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ImportFiles
                .Where(i => i.Format == ImportFormats.EdiStream)
                .OrderByDescending(i => i.ReceivedAtUtc).Take(25)
                .Select(i => new StreamImportDto(i.Id, i.SupplierId, i.Supplier!.Code, i.FileName, i.Status.ToString(),
                    i.TotalRows, i.ProcessedRows, i.ErrorRows, i.ReceivedAtUtc, i.CompletedAtUtc, i.FailureReason))
                .ToListAsync(ct)));

        group.MapGet("/sources", (IEdiSourceLibrary library) =>
            Results.Ok(new { root = library.Root, files = library.List() }));

        group.MapPost("/sources/generate", async (GenerateRequest req, IEdiSourceLibrary library,
            ICatalogueDbContext db, IActorContext actor, CancellationToken ct) =>
        {
            if (req.LineItems is < 1 or > MaxGeneratedItems)
                return Results.BadRequest(new { error = $"lineItems must be between 1 and {MaxGeneratedItems:N0}. Use tools/Catalogue.EdiGenerator for more." });
            var name = string.IsNullOrWhiteSpace(req.Name) ? $"generated/pricat-{FormatCount(req.LineItems)}-{DateTime.UtcNow:yyyyMMddHHmmss}.edi" : req.Name.Trim();
            if (!name.EndsWith(".edi", StringComparison.OrdinalIgnoreCase)) name += ".edi";
            var path = Path.GetFullPath(Path.Combine(library.Root, name));
            if (!path.StartsWith(library.Root, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Invalid file name." });

            var size = await EdiSampleGenerator.WriteAsync(path, req.LineItems, Math.Clamp(req.ErrorRate ?? 0.004, 0, 0.5),
                seed: req.Seed ?? 42, ct: ct, startIndex: Math.Max(0, req.StartIndex ?? 0));
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "EdiSampleGenerated", "EdiSource", name,
                $"{req.LineItems} line items, {size} bytes", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            return Results.Ok(library.List().FirstOrDefault(f => f.Name == name.Replace('\\', '/')));
        }).RequireAuthorization("SupplierAdmin").WithRequestTimeout(Policies.LongRunning);

        group.MapPost("/streams", async (StartStreamRequest req, EdiStreamService streams, CancellationToken ct) =>
        {
            try
            {
                var result = await streams.StartFromLibraryAsync(req.SupplierId, req.Source, req.ChunkSize, ct);
                return Results.Accepted($"/api/imports/{result.Import.Id}",
                    new { id = result.Import.Id, status = result.Import.Status.ToString(), existing = result.Existing });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireAuthorization("SupplierAdmin");

        group.MapPost("/streams/upload", async (HttpRequest http, EdiStreamService streams, CancellationToken ct) =>
        {
            if (!http.HasFormContentType)
                return Results.BadRequest(new { error = "Multipart form with a file is required." });
            var form = await http.ReadFormAsync(ct);
            if (form.Files.Count == 0) return Results.BadRequest(new { error = "A .edi file is required." });
            if (!long.TryParse(form["supplierId"], out var supplierId))
                return Results.BadRequest(new { error = "supplierId form field is required." });
            int? chunkSize = int.TryParse(form["chunkSize"], out var cs) ? cs : null;
            try
            {
                var file = form.Files[0];
                await using var stream = file.OpenReadStream();
                var result = await streams.StartFromUploadAsync(supplierId, stream, file.FileName, chunkSize, ct);
                return Results.Accepted($"/api/imports/{result.Import.Id}",
                    new { id = result.Import.Id, status = result.Import.Status.ToString(), existing = result.Existing });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireAuthorization("SupplierAdmin").DisableAntiforgery().WithRequestTimeout(Policies.LongRunning)
          .WithMetadata(new RequestSizeLimitAttribute(EdiStreamService.MaxUploadBytes + 1024 * 1024));

        group.MapPost("/streams/{id:long}/restream", async (long id, RestreamRequest? req, EdiStreamService streams, CancellationToken ct) =>
        {
            try
            {
                var import = await streams.RestreamAsync(id, req?.ChunkSize, ct);
                return Results.Accepted($"/api/imports/{import.Id}", new { id = import.Id });
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    private static string FormatCount(long n) => n switch
    {
        >= 1_000_000 when n % 1_000_000 == 0 => $"{n / 1_000_000}m",
        >= 1_000 when n % 1_000 == 0 => $"{n / 1_000}k",
        _ => n.ToString()
    };
}
