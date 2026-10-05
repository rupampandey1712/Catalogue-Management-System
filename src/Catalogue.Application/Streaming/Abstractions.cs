using Catalogue.Contracts;

namespace Catalogue.Application.Streaming;

/// <summary>Publishes EDI chunk events (Azure Event Hubs / emulator, or the in-memory hub locally).</summary>
public interface IEventHubProducer
{
    /// <summary>"EventHubs" when talking to Azure/emulator, "InMemory" for the local simulated hub.</summary>
    string Mode { get; }
    string HubName { get; }
    string ConsumerGroup { get; }
    Task SendAsync(IReadOnlyList<EdiChunkEvent> events, CancellationToken ct);
    Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(CancellationToken ct);
}

public sealed record PartitionInfo(string PartitionId, long LastEnqueuedSequenceNumber,
    DateTimeOffset? LastEnqueuedTimeUtc, bool IsEmpty);

/// <summary>Set-based catalogue writer for high-volume ingestion (SqlBulkCopy + MERGE).</summary>
public interface IBulkCatalogueWriter
{
    /// <summary>
    /// Applies one chunk atomically. Returns false when the chunk was already applied
    /// (at-least-once redelivery), in which case nothing is written.
    /// </summary>
    Task<bool> WriteChunkAsync(BulkChunk chunk, CancellationToken ct);

    /// <summary>Records the file's final row count and completes the import if consumers are done.</summary>
    Task<bool> SetTotalAndTryCompleteAsync(long importId, int totalRows, CancellationToken ct);

    /// <summary>Clears chunk ledger + row errors before a re-stream.</summary>
    Task ResetStreamAsync(long importId, CancellationToken ct);
}

public sealed record BulkRow(string Sku, string Name, string? Description, long? CategoryId,
    decimal? Price, string Currency, int? Quantity)
{
    public string? Brand { get; init; }
    public string? Mpn { get; init; }
    /// <summary>Normalised GTIN-14; used to match the same item offered by several suppliers.</summary>
    public string? Gtin { get; init; }
    public decimal? Cost { get; init; }
    public int? LeadTimeDays { get; init; }
    public int? MinOrderQuantity { get; init; }
    public int? PackSize { get; init; }
    public string Availability { get; init; } = "Unknown";
    public IReadOnlyList<ParsedAttribute> Attributes { get; init; } = [];
    public IReadOnlyList<Contracts.MediaReference> Media { get; init; } = [];
}

public sealed record BulkError(int RowNumber, string Field, string Message, string? RawValue);

public sealed record BulkChunk(long ImportId, long SupplierId, int ChunkIndex,
    IReadOnlyList<BulkRow> Rows, IReadOnlyList<BulkError> Errors, int ErrorRowCount)
{
    public SupplierPolicy Policy { get; init; } = SupplierPolicy.None;
}

/// <summary>Where streamable EDI source files live (server-side library of large interchanges).</summary>
public interface IEdiSourceLibrary
{
    string Root { get; }
    IReadOnlyList<EdiSourceFile> List();
    string? Resolve(string name);
}

public sealed record EdiSourceFile(string Name, long SizeBytes, DateTime ModifiedUtc, long EstimatedLineItems);
