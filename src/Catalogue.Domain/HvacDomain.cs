using System.Globalization;

namespace Catalogue.Domain;

// ============================================================================
// Typed product attributes (HVAC specifications)
// ============================================================================

public enum AttributeDataType { Number, Text, Enum, Boolean }

/// <summary>
/// A specification field such as "Heat output (kW)" or "Refrigerant". Definitions are global;
/// categories declare which ones apply to their products (see <see cref="CategoryAttribute"/>).
/// </summary>
public sealed class AttributeDefinition
{
    public long Id { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public AttributeDataType DataType { get; private set; }
    public string? Unit { get; private set; }
    /// <summary>Pipe-separated allowed values for <see cref="AttributeDataType.Enum"/>.</summary>
    public string? AllowedValues { get; private set; }
    public decimal? MinValue { get; private set; }
    public decimal? MaxValue { get; private set; }
    public bool IsFilterable { get; private set; } = true;
    public int SortOrder { get; private set; }
    public string? Description { get; private set; }

    private AttributeDefinition() { }

    public AttributeDefinition(string code, string name, AttributeDataType dataType, string? unit = null,
        IEnumerable<string>? allowedValues = null, decimal? min = null, decimal? max = null,
        bool filterable = true, int sortOrder = 0, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Code = code.Trim().ToUpperInvariant();
        Name = name.Trim();
        DataType = dataType;
        Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        AllowedValues = allowedValues is null ? null : string.Join('|', allowedValues.Select(v => v.Trim()).Where(v => v.Length > 0));
        if (dataType == AttributeDataType.Enum && string.IsNullOrEmpty(AllowedValues))
            throw new ArgumentException("Enum attributes need at least one allowed value.", nameof(allowedValues));
        if (min is not null && max is not null && min > max)
            throw new ArgumentException("MinValue cannot exceed MaxValue.");
        MinValue = min;
        MaxValue = max;
        IsFilterable = filterable;
        SortOrder = sortOrder;
        Description = description;
    }

    public IReadOnlyList<string> AllowedValueList =>
        AllowedValues?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <summary>
    /// Parses and validates a raw value for this attribute. Returns an error message or null.
    /// Enum matching is case-insensitive and normalised to the canonical spelling.
    /// </summary>
    public string? TryParse(string? raw, out AttributeValue value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return $"{Name} is empty.";
        raw = raw.Trim();
        switch (DataType)
        {
            case AttributeDataType.Number:
                if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                    return $"{Name}: '{raw}' is not a number.";
                if (MinValue is { } lo && n < lo) return $"{Name}: {n} is below the minimum {lo}{Unit}.";
                if (MaxValue is { } hi && n > hi) return $"{Name}: {n} is above the maximum {hi}{Unit}.";
                value = new AttributeValue(null, n, null);
                return null;
            case AttributeDataType.Boolean:
                var b = raw.ToLowerInvariant() switch
                {
                    "true" or "yes" or "y" or "1" => (bool?)true,
                    "false" or "no" or "n" or "0" => false,
                    _ => null
                };
                if (b is null) return $"{Name}: '{raw}' is not yes/no.";
                value = new AttributeValue(null, null, b);
                return null;
            case AttributeDataType.Enum:
                var match = AllowedValueList.FirstOrDefault(v => v.Equals(raw, StringComparison.OrdinalIgnoreCase));
                if (match is null) return $"{Name}: '{raw}' is not allowed (expected {string.Join(", ", AllowedValueList)}).";
                value = new AttributeValue(match, null, null);
                return null;
            default:
                if (raw.Length > 200) return $"{Name}: value is longer than 200 characters.";
                value = new AttributeValue(raw, null, null);
                return null;
        }
    }

    public void Update(string name, string? unit, IEnumerable<string>? allowedValues, decimal? min, decimal? max, bool filterable, int sortOrder, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        if (DataType == AttributeDataType.Enum && allowedValues is not null)
            AllowedValues = string.Join('|', allowedValues.Select(v => v.Trim()).Where(v => v.Length > 0));
        MinValue = min;
        MaxValue = max;
        IsFilterable = filterable;
        SortOrder = sortOrder;
        Description = description;
    }
}

public readonly record struct AttributeValue(string? Text, decimal? Number, bool? Bool)
{
    public string Display(string? unit = null) =>
        Number is { } n ? $"{n.ToString("0.##", CultureInfo.InvariantCulture)}{(unit is null ? "" : " " + unit)}"
        : Bool is { } b ? (b ? "Yes" : "No")
        : Text ?? "";
}

/// <summary>Which attributes a category's products carry, and which are mandatory.</summary>
public sealed class CategoryAttribute
{
    public long CategoryId { get; set; }
    public long AttributeDefinitionId { get; set; }
    public bool IsRequired { get; set; }
    public int SortOrder { get; set; }
    public Category? Category { get; set; }
    public AttributeDefinition? Attribute { get; set; }
}

/// <summary>Typed EAV value: one of Text/Number/Bool is set according to the definition's type.</summary>
public sealed class ProductAttributeValue
{
    public long ProductId { get; set; }
    public long AttributeDefinitionId { get; set; }
    public string? TextValue { get; set; }
    public decimal? NumberValue { get; set; }
    public bool? BoolValue { get; set; }
    public Product? Product { get; set; }
    public AttributeDefinition? Attribute { get; set; }

    public AttributeValue Value => new(TextValue, NumberValue, BoolValue);

    public void Set(AttributeValue v)
    {
        TextValue = v.Text;
        NumberValue = v.Number;
        BoolValue = v.Bool;
    }
}

// ============================================================================
// Product identity + supplier offers
// ============================================================================

public static class Gtin
{
    /// <summary>Validates a GTIN-8/12/13/14 including its GS1 mod-10 check digit.</summary>
    public static bool IsValid(string? gtin)
    {
        if (string.IsNullOrWhiteSpace(gtin)) return false;
        gtin = gtin.Trim();
        if (gtin.Length is not (8 or 12 or 13 or 14) || !gtin.All(char.IsAsciiDigit)) return false;
        return CheckDigit(gtin[..^1]) == gtin[^1] - '0';
    }

    /// <summary>GS1 check digit for the payload (all digits except the check digit).</summary>
    public static int CheckDigit(string payload)
    {
        var sum = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            var digit = payload[payload.Length - 1 - i] - '0';
            sum += digit * (i % 2 == 0 ? 3 : 1);
        }
        return (10 - sum % 10) % 10;
    }

    /// <summary>Normalises to GTIN-14 so 13- and 14-digit forms of the same item match.</summary>
    public static string Normalize(string gtin) => gtin.Trim().PadLeft(14, '0');
}

public enum Availability { Unknown, InStock, LowStock, OnOrder, Discontinued }

public static class AvailabilityRules
{
    public const int LowStockThreshold = 20;

    /// <summary>Derives availability when a feed does not state it explicitly.</summary>
    public static Availability Derive(int? quantity, int? leadTimeDays, bool discontinued = false) =>
        discontinued ? Availability.Discontinued
        : quantity is null ? (leadTimeDays is not null ? Availability.OnOrder : Availability.Unknown)
        : quantity > LowStockThreshold ? Availability.InStock
        : quantity > 0 ? Availability.LowStock
        : Availability.OnOrder;
}

/// <summary>
/// One supplier's offer for a catalogue product: their SKU, cost, lead time and pack rules.
/// Several suppliers can offer the same product (matched by GTIN), which is how the catalogue
/// consolidates the same boiler arriving from different distributors.
/// </summary>
public sealed class SupplierOffer
{
    public long Id { get; private set; }
    public long ProductId { get; private set; }
    public Product? Product { get; private set; }
    public long SupplierId { get; private set; }
    public Supplier? Supplier { get; private set; }
    public string SupplierSku { get; private set; } = null!;
    public decimal? Cost { get; private set; }
    /// <summary>
    /// This supplier's selling price to buyers on the marketplace (EDI PRI+AAA / CSV price). When the
    /// product was consolidated from several suppliers, each keeps its own price here; null falls back
    /// to the product's list price.
    /// </summary>
    public decimal? Price { get; private set; }
    public string Currency { get; private set; } = "GBP";
    public int? LeadTimeDays { get; private set; }
    public int MinOrderQuantity { get; private set; } = 1;
    public int PackSize { get; private set; } = 1;
    public Availability Availability { get; private set; }
    public bool IsPreferred { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; } = DateTime.UtcNow;

    private SupplierOffer() { }

    public SupplierOffer(Product product, long supplierId, string supplierSku)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierSku);
        Product = product;
        ProductId = product.Id;
        SupplierId = supplierId;
        SupplierSku = supplierSku.Trim().ToUpperInvariant();
    }

    public void Update(decimal? cost, string? currency, int? leadTimeDays, int? moq, int? packSize, int? quantity)
    {
        if (cost is < 0) throw new ArgumentOutOfRangeException(nameof(cost));
        Cost = cost ?? Cost;
        Currency = (currency ?? Currency).ToUpperInvariant();
        LeadTimeDays = leadTimeDays ?? LeadTimeDays;
        MinOrderQuantity = Math.Max(1, moq ?? MinOrderQuantity);
        PackSize = Math.Max(1, packSize ?? PackSize);
        Availability = AvailabilityRules.Derive(quantity, LeadTimeDays);
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkPreferred(bool preferred) => IsPreferred = preferred;

    public void SetSellPrice(decimal? price)
    {
        if (price is < 0) throw new ArgumentOutOfRangeException(nameof(price));
        if (price is not null) Price = price;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Seller portal edit: price, stock-derived availability and lead time.</summary>
    public void UpdateBySeller(decimal? price, int? quantity, int? leadTimeDays, int? moq, int? packSize, bool discontinued)
    {
        if (price is < 0) throw new ArgumentOutOfRangeException(nameof(price));
        Price = price;
        LeadTimeDays = leadTimeDays;
        MinOrderQuantity = Math.Max(1, moq ?? MinOrderQuantity);
        PackSize = Math.Max(1, packSize ?? PackSize);
        Availability = AvailabilityRules.Derive(quantity, leadTimeDays, discontinued);
        UpdatedAtUtc = DateTime.UtcNow;
    }
}

// ============================================================================
// B2B pricing: price lists (with fallback chain), quantity breaks, customer accounts
// ============================================================================

public enum PriceListKind { Trade, Tier, Customer, Promotion }

/// <summary>
/// A named set of prices. Explicit entries win; otherwise <see cref="DiscountPercent"/> off list
/// price applies. A list can fall back to a parent (customer list → tier → trade → list price).
/// </summary>
public sealed class PriceList
{
    public long Id { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public PriceListKind Kind { get; private set; }
    public string Currency { get; private set; } = "GBP";
    public decimal DiscountPercent { get; private set; }
    public long? ParentPriceListId { get; private set; }
    public PriceList? Parent { get; private set; }
    public bool IsActive { get; private set; } = true;

    private PriceList() { }

    public PriceList(string code, string name, PriceListKind kind, decimal discountPercent, long? parentId = null, string currency = "GBP")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (discountPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(discountPercent));
        Code = code.Trim().ToUpperInvariant();
        Name = name.Trim();
        Kind = kind;
        DiscountPercent = discountPercent;
        ParentPriceListId = parentId;
        Currency = currency.ToUpperInvariant();
    }

    public void Update(string name, decimal discountPercent, long? parentId, bool isActive)
    {
        if (discountPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(discountPercent));
        if (parentId == Id && Id != 0) throw new InvalidOperationException("A price list cannot fall back to itself.");
        Name = name.Trim();
        DiscountPercent = discountPercent;
        ParentPriceListId = parentId;
        IsActive = isActive;
    }
}

/// <summary>Explicit net price for a product on a list, from a minimum quantity (price break).</summary>
public sealed class PriceListEntry
{
    public long Id { get; private set; }
    public long PriceListId { get; private set; }
    public PriceList? PriceList { get; private set; }
    public long ProductId { get; private set; }
    public Product? Product { get; private set; }
    public int MinQuantity { get; private set; } = 1;
    public decimal UnitPrice { get; private set; }
    public DateTime ValidFromUtc { get; private set; }
    public DateTime? ValidToUtc { get; private set; }

    private PriceListEntry() { }

    public PriceListEntry(long priceListId, long productId, int minQuantity, decimal unitPrice, DateTime validFromUtc, DateTime? validToUtc = null)
    {
        if (minQuantity < 1) throw new ArgumentOutOfRangeException(nameof(minQuantity), "Minimum quantity must be at least 1.");
        if (unitPrice < 0) throw new ArgumentOutOfRangeException(nameof(unitPrice), "Price cannot be negative.");
        if (validToUtc is not null && validToUtc <= validFromUtc) throw new ArgumentException("ValidTo must be after ValidFrom.");
        PriceListId = priceListId;
        ProductId = productId;
        MinQuantity = minQuantity;
        UnitPrice = unitPrice;
        ValidFromUtc = validFromUtc;
        ValidToUtc = validToUtc;
    }

    public bool IsValidAt(DateTime utc) => ValidFromUtc <= utc && (ValidToUtc is null || ValidToUtc > utc);
}

/// <summary>A trade customer account (installer, merchant) with its negotiated price list.</summary>
public sealed class CustomerAccount
{
    public long Id { get; private set; }
    public string AccountNumber { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public long? PriceListId { get; private set; }
    public PriceList? PriceList { get; private set; }
    public bool IsActive { get; private set; } = true;

    private CustomerAccount() { }

    public CustomerAccount(string accountNumber, string name, long? priceListId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        AccountNumber = accountNumber.Trim().ToUpperInvariant();
        Name = name.Trim();
        PriceListId = priceListId;
    }

    public void AssignPriceList(long? priceListId) => PriceListId = priceListId;
}

// ============================================================================
// Approval workflow for supplier-fed changes
// ============================================================================

public enum ChangeKind { NewProduct, PriceChange }
public enum ChangeStatus { Pending, Approved, Rejected }

/// <summary>
/// A supplier-fed change held for review. New products from suppliers with an approval policy
/// arrive as <see cref="ProductStatus.PendingApproval"/>; list-price moves above the supplier's
/// threshold are parked here instead of being applied.
/// </summary>
public sealed class ProductChangeRequest
{
    public long Id { get; private set; }
    public long ProductId { get; private set; }
    public Product? Product { get; private set; }
    public long SupplierId { get; private set; }
    public Supplier? Supplier { get; private set; }
    public long? ImportId { get; private set; }
    public ChangeKind Kind { get; private set; }
    public string? Currency { get; private set; }
    public decimal? OldPrice { get; private set; }
    public decimal? NewPrice { get; private set; }
    public string? Summary { get; private set; }
    public ChangeStatus Status { get; private set; } = ChangeStatus.Pending;
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public string? DecidedBy { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? DecisionNote { get; private set; }

    private ProductChangeRequest() { }

    public static ProductChangeRequest ForNewProduct(Product product, long supplierId, long? importId) => new()
    {
        Product = product, ProductId = product.Id, SupplierId = supplierId, ImportId = importId,
        Kind = ChangeKind.NewProduct, Summary = $"New product {product.Sku} – {product.Name}"
    };

    public static ProductChangeRequest ForPriceChange(Product product, long supplierId, long? importId,
        string currency, decimal oldPrice, decimal newPrice) => new()
    {
        Product = product, ProductId = product.Id, SupplierId = supplierId, ImportId = importId,
        Kind = ChangeKind.PriceChange, Currency = currency, OldPrice = oldPrice, NewPrice = newPrice,
        Summary = $"List price {oldPrice:0.00} → {newPrice:0.00} {currency}"
    };

    public decimal? ChangePercent => OldPrice is > 0 && NewPrice is { } n ? Math.Round((n - OldPrice.Value) / OldPrice.Value * 100, 1) : null;

    public void Decide(bool approve, string actor, string? note)
    {
        if (Status != ChangeStatus.Pending) throw new InvalidOperationException($"Change request {Id} is already {Status}.");
        Status = approve ? ChangeStatus.Approved : ChangeStatus.Rejected;
        DecidedBy = actor;
        DecidedAtUtc = DateTime.UtcNow;
        DecisionNote = note;
    }

    /// <summary>True when a price move breaches a supplier's approval threshold.</summary>
    public static bool NeedsPriceApproval(decimal? thresholdPercent, decimal oldPrice, decimal newPrice) =>
        thresholdPercent is { } t && oldPrice > 0 && Math.Abs((newPrice - oldPrice) / oldPrice * 100) > t;
}
