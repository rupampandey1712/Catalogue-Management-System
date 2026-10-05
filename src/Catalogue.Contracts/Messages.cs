namespace Catalogue.Contracts;

/// <summary>Queue message published when an import file is ready for processing.</summary>
public sealed record ImportRequested(
    long ImportId,
    long SupplierId,
    string BlobPath,
    string Format,
    string CorrelationId,
    string? TraceParent = null);

/// <summary>Canonical product row; external EDI/CSV/REST shapes are mapped into this.</summary>
public sealed record CanonicalProductImport(
    string Sku,
    string Name,
    string? Description,
    string? CategoryCode,
    decimal? Price,
    string? Currency,
    int? Quantity)
{
    // HVAC / B2B extensions (all optional so older feeds keep working).
    public string? Brand { get; init; }
    public string? ManufacturerPartNumber { get; init; }
    public string? Gtin { get; init; }
    /// <summary>Supplier's cost price to us (EDI PRI+AAB, CSV "cost").</summary>
    public decimal? Cost { get; init; }
    public int? LeadTimeDays { get; init; }
    public int? MinOrderQuantity { get; init; }
    public int? PackSize { get; init; }
    public bool Discontinued { get; init; }
    /// <summary>Raw specification values keyed by attribute code (validated against the category template).</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = EmptyAttributes;
    /// <summary>Links to images/documents (EDI carries URLs, never bytes); downloaded in the background.</summary>
    public IReadOnlyList<MediaReference> Media { get; init; } = [];

    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes = new Dictionary<string, string>();
}

/// <summary>A supplier-provided media link: Kind is "Image" or a document kind ("Datasheet", "Manual"...).</summary>
public sealed record MediaReference(string Kind, string Url);

/// <summary>Supplier REST DTO (external shape, never persisted directly).</summary>
public sealed record SupplierProductDto(
    string SupplierSku,
    string Name,
    decimal? Price,
    string? Currency,
    int? Quantity,
    string? Description = null,
    string? Category = null);

/// <summary>
/// One Event Hubs event: a self-contained PRICAT chunk (UNH..UNT) holding up to N line items
/// cut from a large supplier EDI interchange. Carried as event body + application properties.
/// </summary>
public sealed record EdiChunkEvent(
    long ImportId,
    long SupplierId,
    int ChunkIndex,
    long FirstLineItem,
    int LineItems,
    string CorrelationId,
    string Payload,
    string? TraceParent = null);
