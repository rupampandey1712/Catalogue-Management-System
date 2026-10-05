using System.Diagnostics;
using System.Text;
using Catalogue.Contracts;
using Catalogue.Domain;
using Microsoft.Extensions.Logging;

namespace Catalogue.Application.Streaming;

/// <summary>
/// Consumer-side handler for one Event Hubs event: parse the PRICAT chunk, validate rows,
/// and apply the whole chunk in one set-based transaction. Safe under redelivery because the
/// writer keeps a (ImportId, ChunkIndex) ledger.
/// </summary>
public sealed class EdiChunkProcessor(
    IEnumerable<ISupplierFileParser> parsers,
    IBulkCatalogueWriter writer,
    IReferenceDataProvider referenceData,
    StreamTelemetry telemetry,
    ILogger<EdiChunkProcessor> logger)
{
    private readonly ISupplierFileParser _parser = parsers.First(p => p.CanHandle(ImportFormats.Edi));

    public async Task ProcessAsync(EdiChunkEvent chunk, string partitionId, long sequenceNumber,
        DateTimeOffset enqueuedUtc, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var refData = await referenceData.GetAsync(ct);

        var rows = new Dictionary<string, BulkRow>(chunk.LineItems, StringComparer.Ordinal);
        var errors = new List<BulkError>();
        int parsedCount = 0, errorRows = 0;

        await using var payload = new MemoryStream(Encoding.UTF8.GetBytes(chunk.Payload));
        await foreach (var parsed in _parser.ParseAsync(payload, ct))
        {
            var rowNumber = (int)Math.Min(int.MaxValue, chunk.FirstLineItem + parsedCount);
            parsedCount++;
            var row = parsed.Row;

            var problems = RowValidator.Validate(row).ToList();
            long? categoryId = null;
            if (row.CategoryCode is { Length: > 0 } code)
            {
                if (refData.CategoryIds.TryGetValue(code.ToUpperInvariant(), out var id)) categoryId = id;
                else problems.Add(("Category", $"Unknown category '{code}'."));
            }
            problems.AddRange(HvacRowValidator.Validate(row, refData, categoryId, out var attributes));

            if (problems.Count > 0)
            {
                errorRows++;
                foreach (var (field, message) in problems)
                    errors.Add(new BulkError(rowNumber, field, message, Truncate(parsed.RawValue, 1000)));
                continue;
            }

            // Last occurrence wins if a SKU repeats inside one chunk (MERGE needs unique sources).
            var sku = row.Sku.Trim().ToUpperInvariant();
            rows[sku] = new BulkRow(sku, Truncate(row.Name.Trim(), 256)!, row.Description, categoryId,
                row.Price, (row.Currency ?? "GBP").ToUpperInvariant(), row.Quantity)
            {
                Brand = row.Brand?.Trim(),
                Mpn = row.ManufacturerPartNumber?.Trim().ToUpperInvariant(),
                Gtin = row.Gtin is { Length: > 0 } g ? Gtin.Normalize(g) : null,
                Cost = row.Cost,
                LeadTimeDays = row.LeadTimeDays,
                MinOrderQuantity = row.MinOrderQuantity,
                PackSize = row.PackSize,
                Availability = AvailabilityRules.Derive(row.Quantity, row.LeadTimeDays, row.Discontinued).ToString(),
                Attributes = attributes,
                Media = row.Media
            };
        }

        // Line items the producer counted but the parser could not turn into rows.
        var missing = chunk.LineItems - parsedCount;
        if (missing > 0)
        {
            errorRows += missing;
            errors.Add(new BulkError((int)Math.Min(int.MaxValue, chunk.FirstLineItem + parsedCount), "Segment",
                $"{missing} malformed LIN segment(s) could not be parsed.", null));
        }

        var applied = await writer.WriteChunkAsync(
            new BulkChunk(chunk.ImportId, chunk.SupplierId, chunk.ChunkIndex, rows.Values.ToList(), errors, errorRows)
            {
                Policy = refData.PolicyFor(chunk.SupplierId)
            }, ct);

        var okRows = chunk.LineItems - errorRows;
        if (applied)
        {
            CatalogueDiagnostics.RowsIngested.Add(okRows, new KeyValuePair<string, object?>("source", "eventhub"));
            CatalogueDiagnostics.RowsRejected.Add(errorRows, new KeyValuePair<string, object?>("source", "eventhub"));
        }
        CatalogueDiagnostics.ChunksProcessed.Add(1, new KeyValuePair<string, object?>("duplicate", !applied));
        CatalogueDiagnostics.ChunkDuration.Record(sw.Elapsed.TotalMilliseconds);
        CatalogueDiagnostics.EndToEndLatency.Record((DateTimeOffset.UtcNow - enqueuedUtc).TotalMilliseconds);
        telemetry.Processed(partitionId, sequenceNumber, enqueuedUtc, chunk.ImportId, chunk.ChunkIndex,
            okRows, errorRows, sw.Elapsed.TotalMilliseconds, duplicate: !applied);
        if (!applied)
            logger.LogDebug("Chunk {ImportId}/{Chunk} already applied; skipped redelivery.", chunk.ImportId, chunk.ChunkIndex);
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
