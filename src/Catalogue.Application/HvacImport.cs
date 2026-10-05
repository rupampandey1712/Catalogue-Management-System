using Catalogue.Contracts;
using Catalogue.Domain;

namespace Catalogue.Application;

public sealed record SupplierPolicy(bool RequireApprovalForNewProducts, decimal? PriceChangeThresholdPercent)
{
    public static readonly SupplierPolicy None = new(false, null);
}

/// <summary>Immutable snapshot of the reference data every import row is validated against.</summary>
public sealed class CatalogueReferenceData
{
    public required IReadOnlyDictionary<string, long> CategoryIds { get; init; }
    public required IReadOnlyDictionary<string, AttributeDefinition> AttributesByCode { get; init; }
    /// <summary>Category → attribute ids that apply to it (its template).</summary>
    public required IReadOnlyDictionary<long, IReadOnlyDictionary<long, bool>> CategoryTemplates { get; init; }
    public required IReadOnlyDictionary<long, SupplierPolicy> SupplierPolicies { get; init; }

    public SupplierPolicy PolicyFor(long supplierId) => SupplierPolicies.GetValueOrDefault(supplierId, SupplierPolicy.None);

    public static CatalogueReferenceData Empty { get; } = new()
    {
        CategoryIds = new Dictionary<string, long>(),
        AttributesByCode = new Dictionary<string, AttributeDefinition>(),
        CategoryTemplates = new Dictionary<long, IReadOnlyDictionary<long, bool>>(),
        SupplierPolicies = new Dictionary<long, SupplierPolicy>()
    };
}

/// <summary>Cached reference data (categories, attribute templates, supplier policies).</summary>
public interface IReferenceDataProvider
{
    Task<CatalogueReferenceData> GetAsync(CancellationToken ct);
    void Invalidate();
}

public sealed record ParsedAttribute(long AttributeId, AttributeValue Value);

/// <summary>Validation of the HVAC/B2B parts of an import row beyond the basic field checks.</summary>
public static class HvacRowValidator
{
    public static List<(string Field, string Message)> Validate(CanonicalProductImport row, CatalogueReferenceData refData,
        long? categoryId, out List<ParsedAttribute> attributes)
    {
        var errors = new List<(string, string)>();
        attributes = [];

        if (row.Gtin is { Length: > 0 } gtin && !Gtin.IsValid(gtin))
            errors.Add(("Gtin", $"'{gtin}' is not a valid GTIN-8/12/13/14 (check digit or length)."));
        if (row.Brand is { Length: > 100 })
            errors.Add(("Brand", "Brand must be 100 characters or fewer."));
        if (row.ManufacturerPartNumber is { Length: > 64 })
            errors.Add(("Mpn", "Manufacturer part number must be 64 characters or fewer."));
        if (row.Cost is < 0)
            errors.Add(("Cost", "Cost cannot be negative."));
        if (row.LeadTimeDays is < 0 or > 365)
            errors.Add(("LeadTime", "Lead time must be between 0 and 365 days."));
        if (row.MinOrderQuantity is < 1)
            errors.Add(("Moq", "Minimum order quantity must be at least 1."));
        if (row.PackSize is < 1)
            errors.Add(("PackSize", "Pack size must be at least 1."));
        foreach (var m in row.Media)
            if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http") || m.Url.Length > 1000)
                errors.Add(("MediaUrl", $"'{(m.Url.Length > 80 ? m.Url[..80] + "…" : m.Url)}' is not a valid http(s) link."));

        IReadOnlyDictionary<long, bool>? template = null;
        if (categoryId is { } cid) refData.CategoryTemplates.TryGetValue(cid, out template);

        foreach (var (code, raw) in row.Attributes)
        {
            if (!refData.AttributesByCode.TryGetValue(code, out var def))
            {
                errors.Add(($"attr.{code}", $"Unknown specification '{code}'."));
                continue;
            }
            if (template is not null && !template.ContainsKey(def.Id))
            {
                errors.Add(($"attr.{code}", $"{def.Name} does not apply to category {row.CategoryCode}."));
                continue;
            }
            var problem = def.TryParse(raw, out var value);
            if (problem is not null) errors.Add(($"attr.{code}", problem));
            else attributes.Add(new ParsedAttribute(def.Id, value));
        }
        return errors;
    }

    /// <summary>Required attributes of the category the product does not have (data-quality gap).</summary>
    public static IEnumerable<long> MissingRequired(IReadOnlyDictionary<long, bool>? template, IEnumerable<long> present)
    {
        if (template is null) return [];
        var have = present.ToHashSet();
        return template.Where(t => t.Value && !have.Contains(t.Key)).Select(t => t.Key);
    }
}
