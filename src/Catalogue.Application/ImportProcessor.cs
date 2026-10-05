using Catalogue.Contracts;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Catalogue.Application;

/// <summary>Processes one import message end to end: parse, validate, upsert, finalize state.</summary>
public sealed class ImportProcessor(
    ICatalogueDbContext db,
    IFileStorage storage,
    IEnumerable<ISupplierFileParser> parsers,
    ILogger<ImportProcessor> logger)
{
    public async Task ProcessAsync(ImportRequested message, CancellationToken ct)
    {
        using var correlation = CorrelationContext.Begin(message.CorrelationId);
        using var activity = CatalogueDiagnostics.StartConsumer("import.file.process", message.TraceParent, message.CorrelationId);
        activity?.SetTag("catalogue.import_id", message.ImportId);
        activity?.SetTag("catalogue.format", message.Format);
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["ImportId"] = message.ImportId });

        var import = await db.ImportFiles.Include(i => i.Errors)
            .FirstOrDefaultAsync(i => i.Id == message.ImportId, ct);
        if (import is null)
        {
            logger.LogWarning("Import {ImportId} not found; skipping message.", message.ImportId);
            return;
        }
        if (import.Status is ImportStatus.Completed)
            return; // idempotent replay safety
        if (import.Status is ImportStatus.Processing)
        {
            // Stale processing state (e.g. worker crashed). Reset so this run can proceed.
            import.Fail("Reset: previous processing run was interrupted.");
            await db.SaveChangesAsync(ct);
        }

        import.MarkProcessing();
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Import {ImportId} for supplier {SupplierId} changed to {Status}",
            import.Id, import.SupplierId, import.Status);

        var parser = parsers.FirstOrDefault(p => p.CanHandle(import.Format));
        if (parser is null)
        {
            import.Reject($"No parser for format '{import.Format}'.");
            await db.SaveChangesAsync(ct);
            return;
        }

        var refData = await LoadReferenceDataAsync(ct);
        var policy = refData.PolicyFor(import.SupplierId);

        int total = 0, processed = 0, errors = 0;
        try
        {
            await using var stream = await storage.OpenReadAsync(import.BlobPath, ct);
            var errorBuffer = new List<ImportItemError>();
            var batch = new List<(CanonicalProductImport Row, long? CategoryId, List<ParsedAttribute> Attributes)>(BatchSize);

            await foreach (var parsed in parser.ParseAsync(stream, ct))
            {
                total++;
                var row = parsed.Row;
                var validationErrors = RowValidator.Validate(row).ToList();
                long? categoryId = null;
                if (row.CategoryCode is { Length: > 0 } code)
                {
                    if (refData.CategoryIds.TryGetValue(code.ToUpperInvariant(), out var cid)) categoryId = cid;
                    else validationErrors.Add(("Category", $"Unknown category '{code}'."));
                }
                validationErrors.AddRange(HvacRowValidator.Validate(row, refData, categoryId, out var attributes));

                if (validationErrors.Count > 0)
                {
                    errors++;
                    foreach (var (field, msg) in validationErrors)
                        errorBuffer.Add(new ImportItemError(import.Id, parsed.RowNumber, field, msg, parsed.RawValue));
                    continue;
                }

                batch.Add((row, categoryId, attributes));
                processed++;
                if (batch.Count >= BatchSize)
                {
                    await ApplyBatchAsync(import, policy, batch, ct);
                    db.ImportItemErrors.AddRange(errorBuffer);
                    errorBuffer.Clear();
                    await db.SaveChangesAsync(ct);
                    batch.Clear();
                }
            }

            await ApplyBatchAsync(import, policy, batch, ct);
            db.ImportItemErrors.AddRange(errorBuffer);
            import.Complete(total, processed, errors);
            db.AuditEvents.Add(new AuditEvent("worker", "ImportCompleted", "ImportFile",
                import.Id.ToString(), $"total={total} processed={processed} errors={errors}", import.CorrelationId));
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Import {ImportId} for supplier {SupplierId} changed to {Status}",
                import.Id, import.SupplierId, import.Status);
            CatalogueDiagnostics.RowsIngested.Add(processed, new KeyValuePair<string, object?>("source", "file"));
            CatalogueDiagnostics.RowsRejected.Add(errors, new KeyValuePair<string, object?>("source", "file"));
            CatalogueDiagnostics.ImportsCompleted.Add(1, new KeyValuePair<string, object?>("status", import.Status.ToString()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Import {ImportId} failed.", import.Id);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            CatalogueDiagnostics.ImportsCompleted.Add(1, new KeyValuePair<string, object?>("status", "Failed"));
            import.Fail(ex.Message);
            await db.SaveChangesAsync(ct);
        }
    }

    private const int BatchSize = 500;

    /// <summary>
    /// Applies a batch with the same rules as the bulk (Event Hub) writer: match by this supplier's
    /// offer, then by GTIN across suppliers (consolidate), then by own SKU; preserve status on update;
    /// hold new products / large price moves for approval per supplier policy. Only the batch's
    /// products are loaded, so memory stays flat regardless of catalogue size.
    /// </summary>
    private async Task ApplyBatchAsync(ImportFile import, SupplierPolicy policy,
        List<(CanonicalProductImport Row, long? CategoryId, List<ParsedAttribute> Attributes)> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var supplierId = import.SupplierId;
        var mediaQueue = new List<(Product Product, MediaReference Ref)>();
        // Last occurrence wins when a SKU repeats inside the file.
        var rows = batch.GroupBy(b => b.Row.Sku.Trim().ToUpperInvariant()).Select(g => (Sku: g.Key, Item: g.Last())).ToList();
        var skus = rows.Select(r => r.Sku).ToList();
        var gtins = rows.Select(r => r.Item.Row.Gtin).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => Gtin.Normalize(g!)).Distinct().ToList();

        var offers = await db.SupplierOffers.Where(o => o.SupplierId == supplierId && skus.Contains(o.SupplierSku)).ToListAsync(ct);
        var productIds = offers.Select(o => o.ProductId).ToList();
        var products = await db.Products
            .Include(p => p.Prices.Where(x => x.ValidToUtc == null))
            .Include(p => p.InventoryRecords.Where(i => i.SupplierId == supplierId))
            .Include(p => p.ProductCategories)
            .Include(p => p.AttributeValues)
            .Where(p => productIds.Contains(p.Id)
                || (p.SupplierId == supplierId && skus.Contains(p.Sku))
                || (p.Gtin != null && gtins.Contains(p.Gtin)))
            .ToListAsync(ct);

        foreach (var (sku, (row, categoryId, attributes)) in rows)
        {
            var gtin = string.IsNullOrWhiteSpace(row.Gtin) ? null : Gtin.Normalize(row.Gtin);
            var offer = offers.FirstOrDefault(o => o.SupplierSku == sku);
            var product = offer is not null ? products.FirstOrDefault(p => p.Id == offer.ProductId) : null;
            product ??= gtin is null ? null : products.Where(p => p.Gtin == gtin && p.SupplierId != supplierId).MinBy(p => p.Id);
            product ??= products.FirstOrDefault(p => p.SupplierId == supplierId && p.Sku == sku);
            var owned = product is null || product.SupplierId == supplierId;

            if (product is null)
            {
                product = new Product(sku, row.Name, row.Description, supplierId);
                if (policy.RequireApprovalForNewProducts) product.MarkPendingApproval(); else product.Activate();
                db.Products.Add(product);
                products.Add(product);
                if (policy.RequireApprovalForNewProducts)
                    db.ProductChangeRequests.Add(ProductChangeRequest.ForNewProduct(product, supplierId, import.Id));
            }
            else if (owned)
            {
                product.Update(row.Name, row.Description ?? product.Description); // status preserved
            }

            if (owned)
            {
                product.SetIdentity(row.Brand ?? product.Brand, row.ManufacturerPartNumber ?? product.ManufacturerPartNumber,
                    gtin ?? product.Gtin);
                foreach (var a in attributes) product.SetAttribute(a.AttributeId, a.Value);
                ApplyListPrice(import, policy, product, row);
                if (categoryId is { } catId && product.ProductCategories.All(pc => pc.CategoryId != catId))
                    product.ProductCategories.Add(new ProductCategory { ProductId = product.Id, CategoryId = catId });
            }

            if (row.Quantity is { } qty)
            {
                var inv = product.InventoryRecords.FirstOrDefault(i => i.SupplierId == supplierId);
                if (inv is null) product.InventoryRecords.Add(Inventory.For(product, supplierId, qty));
                else inv.SetQuantity(qty);
            }

            if (offer is null)
            {
                offer = new SupplierOffer(product, supplierId, sku);
                offer.MarkPreferred(owned);
                db.SupplierOffers.Add(offer);
                offers.Add(offer);
            }
            offer.Update(row.Cost, row.Currency, row.LeadTimeDays, row.MinOrderQuantity, row.PackSize, row.Quantity);
            offer.SetSellPrice(row.Price);
            foreach (var m in row.Media.DistinctBy(m => m.Url))
                mediaQueue.Add((product, m));
            if (row.Discontinued) offer.Update(null, null, null, null, null, 0);
        }

        if (mediaQueue.Count > 0)
        {
            await db.SaveChangesAsync(ct); // new products need ids before their media requests reference them
            var ids = mediaQueue.Select(m => m.Product.Id).Distinct().ToList();
            var known = (await db.MediaFetchRequests.Where(f => ids.Contains(f.ProductId)).Select(f => new { f.ProductId, f.Url }).ToListAsync(ct))
                .Select(x => (x.ProductId, x.Url)).ToHashSet();
            foreach (var (product, m) in mediaQueue)
                if (known.Add((product.Id, m.Url)))
                    db.MediaFetchRequests.Add(new MediaFetchRequest(product.Id, supplierId,
                        Enum.TryParse<AssetKind>(m.Kind, true, out var k) ? k : AssetKind.Datasheet, m.Url));
        }
    }

    private void ApplyListPrice(ImportFile import, SupplierPolicy policy, Product product, CanonicalProductImport row)
    {
        if (row.Price is not { } price) return;
        var currency = (row.Currency ?? "GBP").ToUpperInvariant();
        var active = product.Prices.FirstOrDefault(p => p.Currency == currency && p.ValidToUtc is null);
        if (active is not null && active.Amount == price) return;

        if (active is not null && product.Id != 0
            && ProductChangeRequest.NeedsPriceApproval(policy.PriceChangeThresholdPercent, active.Amount, price))
        {
            db.ProductChangeRequests.Add(ProductChangeRequest.ForPriceChange(product, import.SupplierId, import.Id, currency, active.Amount, price));
            return; // held until a catalogue admin approves
        }

        var now = DateTime.UtcNow;
        active?.Close(now);
        product.Prices.Add(ProductPrice.For(product, currency, price, now));
    }

    private async Task<CatalogueReferenceData> LoadReferenceDataAsync(CancellationToken ct)
    {
        var attributes = await db.AttributeDefinitions.AsNoTracking().ToListAsync(ct);
        var templates = await db.CategoryAttributes.AsNoTracking().ToListAsync(ct);
        return new CatalogueReferenceData
        {
            CategoryIds = await db.Categories.AsNoTracking().Where(c => c.IsActive)
                .ToDictionaryAsync(c => c.Code, c => c.Id, StringComparer.OrdinalIgnoreCase, ct),
            AttributesByCode = attributes.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase),
            CategoryTemplates = templates.GroupBy(t => t.CategoryId)
                .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<long, bool>)g.ToDictionary(x => x.AttributeDefinitionId, x => x.IsRequired)),
            SupplierPolicies = await db.Suppliers.AsNoTracking()
                .ToDictionaryAsync(x => x.Id, x => new SupplierPolicy(x.RequireApprovalForNewProducts, x.PriceChangeApprovalThresholdPercent), ct)
        };
    }
}
