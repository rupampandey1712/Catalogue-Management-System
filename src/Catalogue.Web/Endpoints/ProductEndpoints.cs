using System.Globalization;
using Catalogue.Application;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalogue.Web.Endpoints;

public static class ProductEndpoints
{
    public sealed record SpecDto(string Code, string Name, string? Unit, string? Text, decimal? Number, bool? Bool)
    {
        public string Display => new AttributeValue(Text, Number, Bool).Display(Unit);
    }

    public sealed record ProductDto(long Id, string Sku, string Name, string? Description, string Status,
        decimal? Price, string? Currency, int? AvailableQuantity, IReadOnlyList<string> Categories,
        long? SupplierId = null, string? SupplierCode = null, DateTime? UpdatedAtUtc = null,
        string? Brand = null, string? ManufacturerPartNumber = null, string? Gtin = null,
        string Availability = "Unknown", int OfferCount = 0, IReadOnlyList<SpecDto>? Specs = null,
        decimal? NetPrice = null, string? PriceSource = null, decimal? SavingPercent = null, string? ImageUrl = null);

    public sealed record PricePointDto(string Currency, decimal Amount, DateTime ValidFromUtc, DateTime? ValidToUtc);
    public sealed record OfferDto(long Id, long SupplierId, string SupplierCode, string SupplierName, string SupplierSku,
        decimal? Cost, string Currency, int? LeadTimeDays, int MinOrderQuantity, int PackSize, string Availability,
        bool IsPreferred, int? StockQuantity, DateTime UpdatedAtUtc);
    public sealed record SpecDetailDto(long AttributeId, string Code, string Name, string? Unit, string DataType,
        IReadOnlyList<string> AllowedValues, bool Required, string? Value, string Display);
    public sealed record PendingChangeDto(long Id, string Kind, string? Summary, decimal? OldPrice, decimal? NewPrice, DateTime CreatedAtUtc);

    public sealed record ProductDetailDto(long Id, string Sku, string Name, string? Description, string Status,
        decimal? Price, string? Currency, int? AvailableQuantity, IReadOnlyList<string> Categories,
        IReadOnlyList<long> CategoryIds, long? SupplierId, string? SupplierCode, DateTime CreatedAtUtc, DateTime UpdatedAtUtc,
        IReadOnlyList<PricePointDto> PriceHistory,
        string? Brand = null, string? ManufacturerPartNumber = null, string? Gtin = null, string Availability = "Unknown",
        IReadOnlyList<SpecDetailDto>? Specs = null, IReadOnlyList<string>? MissingRequired = null,
        IReadOnlyList<OfferDto>? Offers = null, IReadOnlyList<PendingChangeDto>? PendingChanges = null,
        PriceQuote? Quote = null, IReadOnlyList<MediaEndpoints.AssetDto>? Assets = null, string? ImageUrl = null);

    public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
    public sealed record UpsertProductRequest(string Sku, string Name, string? Description,
        decimal? Price, string? Currency, int? Quantity, long? SupplierId, IReadOnlyList<long>? CategoryIds,
        string? Brand = null, string? ManufacturerPartNumber = null, string? Gtin = null,
        IReadOnlyDictionary<string, string>? Attributes = null);

    public sealed record FacetValue(string Value, int Count);
    public sealed record Facet(string Code, string Name, string? Unit, string DataType, bool Required,
        IReadOnlyList<FacetValue> Values, decimal? Min, decimal? Max);

    private static readonly string[] AvailabilityRank = ["InStock", "LowStock", "OnOrder", "Unknown", "Discontinued"];

    public static string BestAvailability(IEnumerable<Availability> offers) =>
        offers.Select(o => o.ToString()).OrderBy(a => Array.IndexOf(AvailabilityRank, a)).FirstOrDefault() ?? "Unknown";

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/products", async (HttpContext http, ICatalogueDbContext db, IReferenceDataProvider refProvider,
            PricingService pricing, HybridCache cache, string? search, string? status,
            long? categoryId, long? supplierId, string? brand, string? availability, bool? incomplete, bool? hasImage, long? customerId, int? qty,
            Catalogue.Application.Media.MediaUrlSigner signer,
            int? page, int? pageSize, string? sort, bool? deep, CancellationToken ct) =>
        {
            var pg = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 20, 1, 100);
            var specFilters = http.Request.Query["spec"].Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
            var refData = await refProvider.GetAsync(ct);

            var query = db.Products.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                // Default: prefix match on SKU / name / MPN - index seeks, fast at millions of rows.
                // deep=true: substring match on name, which has to scan (opt-in from the UI).
                var term = search.Trim();
                var upper = term.ToUpperInvariant();
                var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
                var looksLikeGtin = digits.Length >= 8 && digits.Length == term.Length;
                var looksLikeSku = !term.Contains(' ') && term.Any(char.IsDigit) && term.Contains('-');
                var gtin14 = looksLikeGtin ? Gtin.Normalize(digits) : null;
                query = looksLikeGtin
                    ? query.Where(p => p.Gtin == gtin14)
                    : looksLikeSku
                        ? query.Where(p => p.Sku.StartsWith(upper) || p.ManufacturerPartNumber == upper)
                        : deep == true
                            ? query.Where(p => p.Sku.StartsWith(upper) || p.Name.Contains(term) || (p.Brand != null && p.Brand.StartsWith(term)))
                            : query.Where(p => p.Sku.StartsWith(upper) || p.Name.StartsWith(term) || (p.Brand != null && p.Brand.StartsWith(term)));
            }
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ProductStatus>(status, true, out var s))
                query = query.Where(p => p.Status == s);
            if (categoryId is not null)
            {
                // A parent category includes its children (the tree is shallow: two levels).
                var ids = await db.Categories.Where(c => c.Id == categoryId || c.ParentCategoryId == categoryId).Select(c => c.Id).ToListAsync(ct);
                query = query.Where(p => p.ProductCategories.Any(pc => ids.Contains(pc.CategoryId)));
            }
            if (supplierId is not null)
                query = query.Where(p => p.Offers.Any(o => o.SupplierId == supplierId));
            if (!string.IsNullOrWhiteSpace(brand))
            {
                var brands = brand.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                query = query.Where(p => p.Brand != null && brands.Contains(p.Brand));
            }
            if (!string.IsNullOrWhiteSpace(availability) && Enum.TryParse<Availability>(availability, true, out var av))
                query = query.Where(p => p.Offers.Any(o => o.Availability == av));
            if (incomplete == true)
                query = query.Where(p => p.ProductCategories.Any(pc => db.CategoryAttributes.Any(ca =>
                    ca.CategoryId == pc.CategoryId && ca.IsRequired
                    && !p.AttributeValues.Any(v => v.AttributeDefinitionId == ca.AttributeDefinitionId))));
            foreach (var f in specFilters)
                query = ApplySpecFilter(query, f, refData);
            if (hasImage is { } wantImage)
                // Two shapes rather than Any(..) == wantImage, which SQL Server cannot turn into a semi-join.
                query = wantImage
                    ? query.Where(p => db.ProductAssets.Any(a => a.ProductId == p.Id && a.IsPrimary))
                    : query.Where(p => !db.ProductAssets.Any(a => a.ProductId == p.Id && a.IsPrimary));

            query = sort?.ToLowerInvariant() switch
            {
                "name" => query.OrderBy(p => p.Name).ThenBy(p => p.Id),
                "name_desc" => query.OrderByDescending(p => p.Name).ThenByDescending(p => p.Id),
                "sku" => query.OrderBy(p => p.Sku).ThenBy(p => p.Id),
                "sku_desc" => query.OrderByDescending(p => p.Sku).ThenByDescending(p => p.Id),
                "updated" => query.OrderBy(p => p.UpdatedAtUtc).ThenBy(p => p.Id),
                "updated_desc" => query.OrderByDescending(p => p.UpdatedAtUtc).ThenByDescending(p => p.Id),
                "id_desc" => query.OrderByDescending(p => p.Id),
                _ => query.OrderBy(p => p.Id)
            };

            // COUNT(*) over millions of rows is the expensive half of every page; paging through the same
            // filter reuses it for a minute. Writes to products invalidate via the tag.
            var countKey = $"products:count:{search}|{deep}|{status}|{categoryId}|{supplierId}|{brand}|{availability}|{incomplete}|{hasImage}|{string.Join(';', specFilters)}";
            var total = await cache.GetOrCreateAsync(countKey, async token => await query.CountAsync(token),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(1), LocalCacheExpiration = TimeSpan.FromMinutes(1) },
                [CacheTags.Products], ct);

            // Fully server-side projection: only the page's rows, their active price, stock and key specs.
            var rows = await query.Skip((pg - 1) * ps).Take(ps)
                .Select(p => new
                {
                    p.Id, p.Sku, p.Name, p.Description, p.Status, p.SupplierId, p.UpdatedAtUtc, p.Brand, p.ManufacturerPartNumber, p.Gtin,
                    Price = p.Prices.Where(x => x.ValidToUtc == null).OrderByDescending(x => x.ValidFromUtc).Select(x => (decimal?)x.Amount).FirstOrDefault(),
                    Currency = p.Prices.Where(x => x.ValidToUtc == null).OrderByDescending(x => x.ValidFromUtc).Select(x => x.Currency).FirstOrDefault(),
                    Qty = p.InventoryRecords.Sum(i => (int?)i.Quantity),
                    Categories = p.ProductCategories.Select(pc => pc.Category!.Name).ToList(),
                    SupplierCode = p.Supplier != null ? p.Supplier.Code : null,
                    Offers = p.Offers.Select(o => o.Availability).ToList(),
                    ImageId = db.ProductAssets.Where(a => a.ProductId == p.Id && a.IsPrimary).Select(a => (long?)a.Id).FirstOrDefault(),
                    Specs = p.AttributeValues.OrderBy(v => v.Attribute!.SortOrder).Take(4)
                        .Select(v => new SpecDto(v.Attribute!.Code, v.Attribute.Name, v.Attribute.Unit, v.TextValue, v.NumberValue, v.BoolValue)).ToList()
                })
                .ToListAsync(ct);

            IReadOnlyDictionary<long, PriceQuote>? quotes = null;
            if (customerId is not null)
            {
                var (_, chain) = await pricing.ContextAsync(customerId, null, ct);
                quotes = await pricing.QuoteAsync(rows.Select(r => (r.Id, r.Price, r.Currency ?? "GBP")).ToList(), chain, qty ?? 1, ct);
            }

            var items = rows.Select(r =>
            {
                var q = quotes?.GetValueOrDefault(r.Id);
                return new ProductDto(r.Id, r.Sku, r.Name, r.Description, r.Status.ToString(), r.Price, r.Currency, r.Qty,
                    r.Categories, r.SupplierId, r.SupplierCode, r.UpdatedAtUtc, r.Brand, r.ManufacturerPartNumber, r.Gtin,
                    BestAvailability(r.Offers), r.Offers.Count, r.Specs, q?.NetUnitPrice, q?.Source, q?.SavingPercent,
                    r.ImageId is { } img ? signer.Url(img, thumbnail: true) : null);
            }).ToList();
            return Results.Ok(new PagedResult<ProductDto>(items, total, pg, ps));
        }).RequireAuthorization("ReadCatalogue").WithTags("Products");

        // Facets for the filter panel, scoped to a category. Counts are per category (not re-scoped by
        // other active filters) and cached for 10 minutes - cheap enough at millions of products.
        api.MapGet("/products/facets", async (long? categoryId, ICatalogueDbContext db, IReferenceDataProvider refProvider,
            HybridCache cache, CancellationToken ct) =>
            Results.Ok(await cache.GetOrCreateAsync($"products:facets:{categoryId}", async token =>
            {
                var refData = await refProvider.GetAsync(token);
                var catIds = categoryId is null ? [] : await db.Categories
                    .Where(c => c.Id == categoryId || c.ParentCategoryId == categoryId).Select(c => c.Id).ToListAsync(token);
                var scope = db.Products.AsNoTracking().Where(p => p.Status != ProductStatus.PendingApproval);
                if (categoryId is not null) scope = scope.Where(p => p.ProductCategories.Any(pc => catIds.Contains(pc.CategoryId)));

                // Order on the anonymous projection (EF can't translate members of a constructed record).
                var brands = (await scope.Where(p => p.Brand != null).GroupBy(p => p.Brand!)
                    .Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(f => f.Count).Take(25).ToListAsync(token))
                    .Select(x => new FacetValue(x.Key, x.Count)).ToList();

                var template = catIds.SelectMany(id => refData.CategoryTemplates.GetValueOrDefault(id) ?? new Dictionary<long, bool>())
                    .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Any(x => x.Value));
                var facets = new List<Facet>();
                foreach (var def in refData.AttributesByCode.Values.Where(d => d.IsFilterable && template.ContainsKey(d.Id)).OrderBy(d => d.SortOrder))
                {
                    var values = db.ProductAttributeValues.AsNoTracking()
                        .Where(v => v.AttributeDefinitionId == def.Id && scope.Any(p => p.Id == v.ProductId));
                    if (def.DataType == AttributeDataType.Number)
                    {
                        var stats = await values.GroupBy(_ => 1)
                            .Select(g => new { Min = g.Min(v => v.NumberValue), Max = g.Max(v => v.NumberValue) }).FirstOrDefaultAsync(token);
                        facets.Add(new Facet(def.Code, def.Name, def.Unit, def.DataType.ToString(), template[def.Id], [], stats?.Min, stats?.Max));
                    }
                    else
                    {
                        var counts = (await values.Where(v => v.TextValue != null).GroupBy(v => v.TextValue!)
                            .Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(f => f.Count).Take(25).ToListAsync(token))
                            .Select(x => new FacetValue(x.Key, x.Count)).ToList();
                        facets.Add(new Facet(def.Code, def.Name, def.Unit, def.DataType.ToString(), template[def.Id], counts, null, null));
                    }
                }
                return new { brands, facets };
            }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10), LocalCacheExpiration = TimeSpan.FromMinutes(10) },
            [CacheTags.Products], ct)))
            .RequireAuthorization("ReadCatalogue").WithTags("Products");

        api.MapGet("/products/{id:long}", async (long id, long? customerId, int? qty, ICatalogueDbContext db,
            PricingService pricing, HybridCache cache, Catalogue.Application.Media.MediaUrlSigner signer, CancellationToken ct) =>
        {
            var detail = await cache.GetOrCreateAsync(CacheTags.Product(id), async token => await LoadDetail(id, db, token),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) }, [CacheTags.Products], ct);
            if (detail is null) return Results.NotFound();
            // Media URLs are signed and short-lived, so they are added per request rather than cached.
            var assets = await db.ProductAssets.AsNoTracking().Where(a => a.ProductId == id)
                .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Kind).ThenBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync(ct);
            detail = detail with
            {
                Assets = assets.Select(a => MediaEndpoints.ToDto(a, signer)).ToList(),
                ImageUrl = assets.FirstOrDefault(a => a.IsPrimary) is { } primary ? signer.Url(primary.Id, false) : null
            };
            if (customerId is null) return Results.Ok(detail);
            // Prices are customer-specific, so they are resolved per request rather than cached.
            var (_, chain) = await pricing.ContextAsync(customerId, null, ct);
            var quotes = await pricing.QuoteAsync([(id, detail.Price, detail.Currency ?? "GBP")], chain, qty ?? 1, ct);
            return Results.Ok(detail with { Quote = quotes[id] });
        }).RequireAuthorization("ReadCatalogue").WithTags("Products");

        api.MapPost("/products", async (UpsertProductRequest req, ICatalogueDbContext db, IReferenceDataProvider refProvider,
            IActorContext actor, HybridCache cache, CancellationToken ct) =>
        {
            var dup = await db.Products.AnyAsync(p => p.Sku == req.Sku.ToUpper() && p.SupplierId == req.SupplierId, ct);
            if (dup) return Results.Conflict(new { error = "A product with this SKU already exists for the supplier." });

            var refData = await refProvider.GetAsync(ct);
            var problems = ValidateProduct(req, refData, out var attributes);
            if (problems.Count > 0) return Results.ValidationProblem(problems);

            var product = new Product(req.Sku, req.Name, req.Description, req.SupplierId);
            product.SetIdentity(req.Brand, req.ManufacturerPartNumber, req.Gtin);
            foreach (var a in attributes) product.SetAttribute(a.AttributeId, a.Value);
            if (req.Price is { } price)
                product.Prices.Add(ProductPrice.For(product, req.Currency ?? "GBP", price, DateTime.UtcNow));
            if (req.Quantity is { } qty)
                product.InventoryRecords.Add(Inventory.For(product, req.SupplierId, qty));
            foreach (var cid in req.CategoryIds ?? [])
                product.ProductCategories.Add(new ProductCategory { CategoryId = cid });

            db.Products.Add(product);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "ProductCreated", "Product",
                product.Sku, req.Name, actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await cache.RemoveByTagAsync(CacheTags.Products, ct);
            return Results.Created($"/api/products/{product.Id}", new { product.Id, product.Sku });
        }).RequireAuthorization("CatalogueAdmin").WithTags("Products");

        api.MapPut("/products/{id:long}", async (long id, UpsertProductRequest req, ICatalogueDbContext db,
            IReferenceDataProvider refProvider, IActorContext actor, HybridCache cache, CancellationToken ct) =>
        {
            var product = await db.Products
                .Include(p => p.ProductCategories)
                .Include(p => p.AttributeValues)
                .Include(p => p.Prices.Where(x => x.ValidToUtc == null))
                .FirstOrDefaultAsync(p => p.Id == id, ct);
            if (product is null) return Results.NotFound();

            var refData = await refProvider.GetAsync(ct);
            var problems = ValidateProduct(req, refData, out var attributes);
            if (problems.Count > 0) return Results.ValidationProblem(problems);

            product.Update(req.Name, req.Description);
            product.SetIdentity(req.Brand, req.ManufacturerPartNumber, req.Gtin);

            // Categories and specs are replaced by what the editor sends.
            var wantedCats = (req.CategoryIds ?? []).ToHashSet();
            product.ProductCategories.RemoveAll(pc => !wantedCats.Contains(pc.CategoryId));
            foreach (var cid in wantedCats.Where(c => product.ProductCategories.All(pc => pc.CategoryId != c)))
                product.ProductCategories.Add(new ProductCategory { ProductId = product.Id, CategoryId = cid });
            var wantedAttrs = attributes.Select(a => a.AttributeId).ToHashSet();
            foreach (var stale in product.AttributeValues.Where(v => !wantedAttrs.Contains(v.AttributeDefinitionId)).ToList())
                product.SetAttribute(stale.AttributeDefinitionId, null);
            foreach (var a in attributes) product.SetAttribute(a.AttributeId, a.Value);

            if (req.Price is { } price)
            {
                var currency = (req.Currency ?? "GBP").ToUpperInvariant();
                var active = product.Prices.FirstOrDefault(p => p.Currency == currency && p.ValidToUtc is null);
                if (active?.Amount != price)
                {
                    var now = DateTime.UtcNow;
                    active?.Close(now);
                    product.Prices.Add(ProductPrice.For(product, currency, price, now));
                }
            }

            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "ProductUpdated", "Product",
                product.Id.ToString(), req.Name, actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await cache.RemoveAsync(CacheTags.Product(id), ct);
            await cache.RemoveByTagAsync(CacheTags.Products, ct);
            return Results.Ok(new { product.Id, product.Sku });
        }).RequireAuthorization("CatalogueAdmin").WithTags("Products");

        api.MapPost("/products/{id:long}/activate", (long id, ICatalogueDbContext db, IActorContext a, HybridCache c, CancellationToken ct)
            => SetStatus(id, true, db, a, c, ct)).RequireAuthorization("CatalogueAdmin").WithTags("Products");
        api.MapPost("/products/{id:long}/deactivate", (long id, ICatalogueDbContext db, IActorContext a, HybridCache c, CancellationToken ct)
            => SetStatus(id, false, db, a, c, ct)).RequireAuthorization("CatalogueAdmin").WithTags("Products");
    }

    /// <summary>spec=CODE:op:value  with op eq | in (a|b) | gte | lte | between (a..b).</summary>
    private static IQueryable<Product> ApplySpecFilter(IQueryable<Product> query, string filter, CatalogueReferenceData refData)
    {
        var parts = filter.Split(':', 3);
        if (parts.Length != 3 || !refData.AttributesByCode.TryGetValue(parts[0], out var def))
            throw new ArgumentException($"Invalid spec filter '{filter}'. Use CODE:op:value.");
        var id = def.Id;
        var (op, raw) = (parts[1].ToLowerInvariant(), parts[2]);
        decimal Num(string v) => decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d : throw new ArgumentException($"'{v}' is not a number for {def.Name}.");

        switch (op)
        {
            case "eq" when def.DataType == AttributeDataType.Number:
                var n = Num(raw);
                return BetweenFilter(query, id, n, n);
            case "eq" when def.DataType == AttributeDataType.Boolean:
                var b = raw.Equals("true", StringComparison.OrdinalIgnoreCase);
                return query.Where(p => p.AttributeValues.Any(v => v.AttributeDefinitionId == id && v.BoolValue == b));
            case "eq":
                return InFilter(query, id, [raw]);
            case "in":
                return InFilter(query, id, raw.Split('|', StringSplitOptions.RemoveEmptyEntries));
            case "gte":
                return BetweenFilter(query, id, Num(raw), null);
            case "lte":
                return BetweenFilter(query, id, null, Num(raw));
            case "between" when raw.Split("..") is [var lo, var hi]:
                return BetweenFilter(query, id, lo.Length > 0 ? Num(lo) : null, hi.Length > 0 ? Num(hi) : null);
            default:
                throw new ArgumentException($"Unsupported spec operator '{op}'.");
        }
    }

    private static IQueryable<Product> InFilter(IQueryable<Product> q, long id, string[] values) =>
        q.Where(p => p.AttributeValues.Any(v => v.AttributeDefinitionId == id && values.Contains(v.TextValue!)));

    private static IQueryable<Product> BetweenFilter(IQueryable<Product> q, long id, decimal? lo, decimal? hi) =>
        q.Where(p => p.AttributeValues.Any(v => v.AttributeDefinitionId == id
            && (lo == null || v.NumberValue >= lo) && (hi == null || v.NumberValue <= hi)));

    /// <summary>Editor validation: identity, spec values, spec applicability and required specs.</summary>
    internal static Dictionary<string, string[]> ValidateProduct(UpsertProductRequest req, CatalogueReferenceData refData,
        out List<ParsedAttribute> attributes)
    {
        var errors = new Dictionary<string, string[]>();
        attributes = [];
        if (string.IsNullOrWhiteSpace(req.Name)) errors["name"] = ["Name is required."];
        if (!string.IsNullOrWhiteSpace(req.Gtin) && !Gtin.IsValid(req.Gtin)) errors["gtin"] = ["Not a valid GTIN (check digit or length)."];
        if (req.Price is < 0) errors["price"] = ["Price cannot be negative."];

        var template = (req.CategoryIds ?? []).SelectMany(c => refData.CategoryTemplates.GetValueOrDefault(c) ?? new Dictionary<long, bool>())
            .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Any(x => x.Value));
        foreach (var (code, raw) in req.Attributes ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!refData.AttributesByCode.TryGetValue(code, out var def)) { errors[$"attr.{code}"] = ["Unknown specification."]; continue; }
            if (!template.ContainsKey(def.Id)) { errors[$"attr.{code}"] = [$"{def.Name} does not apply to the selected categories."]; continue; }
            var problem = def.TryParse(raw, out var value);
            if (problem is not null) errors[$"attr.{code}"] = [problem];
            else attributes.Add(new ParsedAttribute(def.Id, value));
        }
        var present = attributes.Select(a => a.AttributeId).ToHashSet();
        foreach (var missing in template.Where(t => t.Value && !present.Contains(t.Key)))
        {
            var def = refData.AttributesByCode.Values.FirstOrDefault(d => d.Id == missing.Key);
            if (def is not null && !errors.ContainsKey($"attr.{def.Code}")) errors[$"attr.{def.Code}"] = [$"{def.Name} is required for this category."];
        }
        return errors;
    }

    private static async Task<ProductDetailDto?> LoadDetail(long id, ICatalogueDbContext db, CancellationToken ct)
    {
        var p = await db.Products.AsNoTracking()
            .Include(x => x.Supplier)
            .Include(x => x.Prices)
            .Include(x => x.InventoryRecords)
            .Include(x => x.ProductCategories).ThenInclude(pc => pc.Category)
            .Include(x => x.AttributeValues).ThenInclude(v => v.Attribute)
            .Include(x => x.Offers).ThenInclude(o => o.Supplier)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return null;

        var catIds = p.ProductCategories.Select(pc => pc.CategoryId).ToList();
        var template = await db.CategoryAttributes.AsNoTracking().Include(ca => ca.Attribute)
            .Where(ca => catIds.Contains(ca.CategoryId)).ToListAsync(ct);
        var values = p.AttributeValues.ToDictionary(v => v.AttributeDefinitionId);

        // Specs: every template attribute (filled or not) plus any extra values the product carries.
        var defs = template.Select(t => (t.Attribute!, t.IsRequired, t.SortOrder))
            .Concat(p.AttributeValues.Where(v => template.All(t => t.AttributeDefinitionId != v.AttributeDefinitionId))
                .Select(v => (v.Attribute!, false, 1000)))
            .GroupBy(d => d.Item1.Id).Select(g => (Def: g.First().Item1, Required: g.Any(x => x.Item2), Sort: g.Min(x => x.Item3)))
            .OrderBy(d => d.Sort).ThenBy(d => d.Def.SortOrder).ToList();
        var specs = defs.Select(d =>
        {
            values.TryGetValue(d.Def.Id, out var v);
            var raw = v is null ? null : v.NumberValue?.ToString("0.####", CultureInfo.InvariantCulture) ?? v.BoolValue?.ToString().ToLowerInvariant() ?? v.TextValue;
            return new SpecDetailDto(d.Def.Id, d.Def.Code, d.Def.Name, d.Def.Unit, d.Def.DataType.ToString(), d.Def.AllowedValueList,
                d.Required, raw, v is null ? "—" : v.Value.Display(d.Def.Unit));
        }).ToList();

        var pending = await db.ProductChangeRequests.AsNoTracking()
            .Where(c => c.ProductId == id && c.Status == ChangeStatus.Pending).OrderByDescending(c => c.CreatedAtUtc).Take(10)
            .Select(c => new PendingChangeDto(c.Id, c.Kind.ToString(), c.Summary, c.OldPrice, c.NewPrice, c.CreatedAtUtc)).ToListAsync(ct);

        var activePrice = p.Prices.Where(x => x.ValidToUtc is null).OrderByDescending(x => x.ValidFromUtc).FirstOrDefault();
        return new ProductDetailDto(p.Id, p.Sku, p.Name, p.Description, p.Status.ToString(), activePrice?.Amount, activePrice?.Currency,
            p.InventoryRecords.Sum(i => (int?)i.Quantity), p.ProductCategories.Where(pc => pc.Category is not null).Select(pc => pc.Category!.Name).ToList(),
            catIds, p.SupplierId, p.Supplier?.Code, p.CreatedAtUtc, p.UpdatedAtUtc,
            p.Prices.OrderByDescending(x => x.ValidFromUtc).Take(20).Select(x => new PricePointDto(x.Currency, x.Amount, x.ValidFromUtc, x.ValidToUtc)).ToList(),
            p.Brand, p.ManufacturerPartNumber, p.Gtin, BestAvailability(p.Offers.Select(o => o.Availability)),
            specs, specs.Where(s => s.Required && s.Value is null).Select(s => s.Name).ToList(),
            p.Offers.OrderByDescending(o => o.IsPreferred).ThenBy(o => o.Cost)
                .Select(o => new OfferDto(o.Id, o.SupplierId, o.Supplier?.Code ?? "", o.Supplier?.Name ?? "", o.SupplierSku, o.Cost, o.Currency,
                    o.LeadTimeDays, o.MinOrderQuantity, o.PackSize, o.Availability.ToString(), o.IsPreferred,
                    p.InventoryRecords.Where(i => i.SupplierId == o.SupplierId).Sum(i => (int?)i.Quantity), o.UpdatedAtUtc)).ToList(),
            pending);
    }

    private static async Task<IResult> SetStatus(long id, bool activate,
        ICatalogueDbContext db, IActorContext actor, HybridCache cache, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (product is null) return Results.NotFound();
        if (activate) product.Activate(); else product.Deactivate();
        db.AuditEvents.Add(new AuditEvent(actor.ActorId,
            activate ? "ProductActivated" : "ProductDeactivated", "Product",
            product.Id.ToString(), null, actor.CorrelationId));
        await db.SaveChangesAsync(ct);
        await cache.RemoveByTagAsync(CacheTags.Products, ct); // status filter counts changed
        return Results.Ok(new { product.Id, Status = product.Status.ToString() });
    }
}
