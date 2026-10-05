namespace Catalogue.Domain;

public sealed class Product
{
    public long Id { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public ProductStatus Status { get; private set; } = ProductStatus.Draft;
    public long? SupplierId { get; private set; }
    public Supplier? Supplier { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; private set; } = DateTime.UtcNow;

    public string? Brand { get; private set; }
    public string? ManufacturerPartNumber { get; private set; }
    /// <summary>GS1 GTIN normalised to 14 digits; the key used to consolidate offers across suppliers.</summary>
    public string? Gtin { get; private set; }

    public List<ProductCategory> ProductCategories { get; } = [];
    public List<ProductPrice> Prices { get; } = [];
    public List<Inventory> InventoryRecords { get; } = [];
    public List<ProductAttributeValue> AttributeValues { get; } = [];
    public List<SupplierOffer> Offers { get; } = [];

    private Product() { } // EF Core

    public Product(string sku, string name, string? description = null, long? supplierId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Sku = sku.Trim().ToUpperInvariant();
        Name = name.Trim();
        Description = description;
        SupplierId = supplierId;
    }

    public void Update(string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Description = description;
        Touch();
    }

    /// <summary>Brand, manufacturer part number and GTIN (validated, normalised to GTIN-14).</summary>
    public void SetIdentity(string? brand, string? mpn, string? gtin)
    {
        if (!string.IsNullOrWhiteSpace(gtin) && !Domain.Gtin.IsValid(gtin))
            throw new ArgumentException($"'{gtin}' is not a valid GTIN (check digit or length).", nameof(gtin));
        Brand = Trim(brand, 100);
        ManufacturerPartNumber = Trim(mpn, 64)?.ToUpperInvariant();
        Gtin = string.IsNullOrWhiteSpace(gtin) ? null : Domain.Gtin.Normalize(gtin);
        Touch();
    }

    /// <summary>Sets (or clears, when value is null) one attribute value.</summary>
    public void SetAttribute(long attributeId, AttributeValue? value)
    {
        var existing = AttributeValues.FirstOrDefault(a => a.AttributeDefinitionId == attributeId);
        if (value is null)
        {
            if (existing is not null) AttributeValues.Remove(existing);
            return;
        }
        if (existing is null)
        {
            existing = new ProductAttributeValue { ProductId = Id, Product = this, AttributeDefinitionId = attributeId };
            AttributeValues.Add(existing);
        }
        existing.Set(value.Value);
        Touch();
    }

    public void MarkPendingApproval() { Status = ProductStatus.PendingApproval; Touch(); }
    public void Activate() { Status = ProductStatus.Active; Touch(); }
    public void Deactivate() { Status = ProductStatus.Inactive; Touch(); }

    private static string? Trim(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];
    private void Touch() => UpdatedAtUtc = DateTime.UtcNow;
}

public sealed class Supplier
{
    public long Id { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public IntegrationType IntegrationType { get; private set; }
    public bool IsActive { get; private set; } = true;
    public string? MappingVersion { get; private set; }
    /// <summary>New products from this supplier's feeds wait for catalogue-admin approval.</summary>
    public bool RequireApprovalForNewProducts { get; private set; }
    /// <summary>List-price moves larger than this percentage are held for approval (null = never).</summary>
    public decimal? PriceChangeApprovalThresholdPercent { get; private set; }

    private Supplier() { }

    public Supplier(string code, string name, IntegrationType integrationType, string? mappingVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Code = code.Trim().ToUpperInvariant();
        Name = name.Trim();
        IntegrationType = integrationType;
        MappingVersion = mappingVersion;
    }

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;

    public void Update(string name, string? mappingVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        MappingVersion = string.IsNullOrWhiteSpace(mappingVersion) ? null : mappingVersion.Trim();
    }

    public void SetApprovalPolicy(bool requireApprovalForNewProducts, decimal? priceChangeThresholdPercent)
    {
        if (priceChangeThresholdPercent is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(priceChangeThresholdPercent));
        RequireApprovalForNewProducts = requireApprovalForNewProducts;
        PriceChangeApprovalThresholdPercent = priceChangeThresholdPercent;
    }
}

public sealed class Category
{
    public long Id { get; private set; }
    public long? ParentCategoryId { get; private set; }
    public Category? Parent { get; private set; }
    public string Name { get; private set; } = null!;
    public string Code { get; private set; } = null!;
    public bool IsActive { get; private set; } = true;
    public int SortOrder { get; private set; }
    public List<CategoryAttribute> Attributes { get; } = [];

    private Category() { }

    public Category(string code, string name, long? parentCategoryId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Code = code.Trim().ToUpperInvariant();
        Name = name.Trim();
        ParentCategoryId = parentCategoryId;
    }

    public void Rename(string name, int sortOrder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        SortOrder = sortOrder;
    }

    /// <summary>Re-parents the category. <paramref name="ancestorsOfNewParent"/> guards against cycles.</summary>
    public void MoveTo(long? newParentId, IReadOnlyCollection<long> ancestorsOfNewParent)
    {
        if (newParentId == Id || ancestorsOfNewParent.Contains(Id))
            throw new InvalidOperationException("A category cannot be moved under itself or one of its descendants.");
        ParentCategoryId = newParentId;
    }

    public void SetActive(bool active) => IsActive = active;
}

public sealed class ProductCategory
{
    public long ProductId { get; set; }
    public long CategoryId { get; set; }
    public Product? Product { get; set; }
    public Category? Category { get; set; }
}

public sealed class ProductPrice
{
    public long Id { get; private set; }
    public long ProductId { get; private set; }
    public Product? Product { get; private set; }
    public string Currency { get; private set; } = "GBP";
    public decimal Amount { get; private set; }
    public DateTime ValidFromUtc { get; private set; }
    public DateTime? ValidToUtc { get; private set; }

    private ProductPrice() { }

    public ProductPrice(long productId, string currency, decimal amount, DateTime validFromUtc, DateTime? validToUtc = null)
    {
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), "Price cannot be negative.");
        if (validToUtc is not null && validToUtc <= validFromUtc)
            throw new ArgumentException("ValidTo must be after ValidFrom.", nameof(validToUtc));
        ProductId = productId;
        Currency = currency.Trim().ToUpperInvariant();
        Amount = amount;
        ValidFromUtc = validFromUtc;
        ValidToUtc = validToUtc;
    }

    /// <summary>Ends this price's validity (it becomes history).</summary>
    public void Close(DateTime atUtc) => ValidToUtc = atUtc > ValidFromUtc ? atUtc : ValidFromUtc.AddTicks(1);

    /// <summary>Create a price attached to a product (new or existing); EF fixes the FK on save.</summary>
    public static ProductPrice For(Product product, string currency, decimal amount, DateTime validFromUtc)
        => new(product.Id, currency, amount, validFromUtc) { Product = product };
}

public sealed class Inventory{
    public long Id { get; private set; }
    public long ProductId { get; private set; }
    public Product? Product { get; private set; }
    public long? SupplierId { get; private set; }
    public int Quantity { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; } = DateTime.UtcNow;

    private Inventory() { }

    public Inventory(long productId, long? supplierId, int quantity)
    {
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        ProductId = productId;
        SupplierId = supplierId;
        Quantity = quantity;
    }

    public static Inventory For(Product product, long? supplierId, int quantity)
        => new(product.Id, supplierId, quantity) { Product = product };

    public void SetQuantity(int quantity)
    {
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        Quantity = quantity;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}

public sealed class ImportFile
{
    public long Id { get; private set; }
    public long SupplierId { get; private set; }
    public Supplier? Supplier { get; private set; }
    public string BlobPath { get; private set; } = null!;
    public string FileName { get; private set; } = null!;
    public string FileHash { get; private set; } = null!;
    public string Format { get; private set; } = "CSV";
    public ImportStatus Status { get; private set; } = ImportStatus.Received;
    public string? CorrelationId { get; private set; }
    public DateTime ReceivedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; private set; }
    public int TotalRows { get; private set; }
    public int ProcessedRows { get; private set; }
    public int ErrorRows { get; private set; }
    public string? FailureReason { get; private set; }

    public List<ImportItemError> Errors { get; } = [];

    private ImportFile() { }

    public ImportFile(long supplierId, string blobPath, string fileName, string fileHash, string format, string? correlationId)
    {
        SupplierId = supplierId;
        BlobPath = blobPath;
        FileName = fileName;
        FileHash = fileHash;
        Format = format;
        CorrelationId = correlationId;
    }

    public void MarkProcessing()
    {
        if (Status is not (ImportStatus.Received or ImportStatus.Retrying or ImportStatus.Failed
            or ImportStatus.Rejected or ImportStatus.CompletedWithErrors))
            throw new InvalidOperationException($"Cannot start processing from status {Status}.");
        Status = ImportStatus.Processing;
        FailureReason = null;
        CompletedAtUtc = null;
    }

    public void Complete(int total, int processed, int errors)
    {
        TotalRows = total;
        ProcessedRows = processed;
        ErrorRows = errors;
        Status = errors == 0 ? ImportStatus.Completed : ImportStatus.CompletedWithErrors;
        CompletedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Starts (or restarts) an Event Hubs streamed import. Counters are reset because the
    /// consumers accumulate them per chunk; TotalRows is set once the producer has read the file.
    /// </summary>
    public void BeginStream()
    {
        if (Status is ImportStatus.Processing)
            throw new InvalidOperationException("Import is already streaming.");
        Status = ImportStatus.Processing;
        TotalRows = 0;
        ProcessedRows = 0;
        ErrorRows = 0;
        FailureReason = null;
        CompletedAtUtc = null;
    }

    public bool IsStream => Format == ImportFormats.EdiStream;

    public void Fail(string reason)
    {
        Status = ImportStatus.Failed;
        FailureReason = reason;
        CompletedAtUtc = DateTime.UtcNow;
    }

    public void Reject(string reason)
    {
        Status = ImportStatus.Rejected;
        FailureReason = reason;
        CompletedAtUtc = DateTime.UtcNow;
    }
}

public sealed class ImportItemError
{
    public long Id { get; private set; }
    public long ImportId { get; private set; }
    public ImportFile? Import { get; private set; }
    public int RowNumber { get; private set; }
    public string Field { get; private set; } = "";
    public string Message { get; private set; } = "";
    public string? RawValue { get; private set; }

    private ImportItemError() { }

    public ImportItemError(long importId, int rowNumber, string field, string message, string? rawValue = null)
    {
        ImportId = importId;
        RowNumber = rowNumber;
        Field = field;
        Message = message;
        RawValue = rawValue;
    }
}

public sealed class AuditEvent
{
    public long Id { get; private set; }
    public string ActorId { get; private set; } = "system";
    public string Action { get; private set; } = "";
    public string EntityType { get; private set; } = "";
    public string EntityId { get; private set; } = "";
    public DateTime TimestampUtc { get; private set; } = DateTime.UtcNow;
    public string? Details { get; private set; }
    public string? CorrelationId { get; private set; }

    private AuditEvent() { }

    public AuditEvent(string actorId, string action, string entityType, string entityId,
        string? details = null, string? correlationId = null)
    {
        ActorId = actorId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Details = details;
        CorrelationId = correlationId;
    }
}
