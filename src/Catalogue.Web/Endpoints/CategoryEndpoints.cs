using Catalogue.Application;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalogue.Web.Endpoints;

/// <summary>Category tree, per-category attribute templates and the attribute (specification) dictionary.</summary>
public static class CategoryEndpoints
{
    public sealed record CategoryDto(long Id, string Code, string Name, long? ParentCategoryId, bool IsActive, int SortOrder = 0);
    public sealed record CategoryNodeDto(long Id, string Code, string Name, long? ParentCategoryId, bool IsActive, int SortOrder,
        int ProductCount, int AttributeCount, int RequiredAttributeCount);
    public sealed record UpsertCategoryRequest(string Code, string Name, long? ParentCategoryId, int? SortOrder = null, bool? IsActive = null);
    public sealed record TemplateItemDto(long AttributeId, string Code, string Name, string DataType, string? Unit, bool Required, int SortOrder, bool Inherited);
    public sealed record TemplateItemRequest(long AttributeId, bool Required, int SortOrder);
    public sealed record AttributeDto(long Id, string Code, string Name, string DataType, string? Unit,
        IReadOnlyList<string> AllowedValues, decimal? MinValue, decimal? MaxValue, bool IsFilterable, int SortOrder, string? Description, int UsedByCategories);
    public sealed record UpsertAttributeRequest(string Code, string Name, string DataType, string? Unit,
        IReadOnlyList<string>? AllowedValues, decimal? MinValue, decimal? MaxValue, bool IsFilterable = true, int SortOrder = 0, string? Description = null);

    public static void Map(IEndpointRouteBuilder api)
    {
        // Flat list (used by pickers); cached 10 min, invalidated on any category write.
        api.MapGet("/categories", async (ICatalogueDbContext db, HybridCache cache, CancellationToken ct) =>
            Results.Ok(await cache.GetOrCreateAsync("categories:all",
                async token => await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
                    .Select(c => new CategoryDto(c.Id, c.Code, c.Name, c.ParentCategoryId, c.IsActive, c.SortOrder))
                    .ToListAsync(token),
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10) },
                [CacheTags.Categories], ct)))
            .RequireAuthorization("ReadCatalogue").WithTags("Categories");

        // Tree with product counts (counts scan ProductCategories, so cached separately for 5 min).
        api.MapGet("/categories/tree", async (ICatalogueDbContext db, HybridCache cache, CancellationToken ct) =>
            Results.Ok(await cache.GetOrCreateAsync("categories:tree", async token =>
            {
                var counts = await db.ProductCategories.GroupBy(pc => pc.CategoryId)
                    .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, token);
                var attrs = await db.CategoryAttributes.GroupBy(ca => ca.CategoryId)
                    .Select(g => new { g.Key, All = g.Count(), Required = g.Count(x => x.IsRequired) }).ToDictionaryAsync(x => x.Key, token);
                return (await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(token))
                    .Select(c => new CategoryNodeDto(c.Id, c.Code, c.Name, c.ParentCategoryId, c.IsActive, c.SortOrder,
                        counts.GetValueOrDefault(c.Id), attrs.GetValueOrDefault(c.Id)?.All ?? 0, attrs.GetValueOrDefault(c.Id)?.Required ?? 0))
                    .ToList();
            }, new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) }, [CacheTags.Categories], ct)))
            .RequireAuthorization("ReadCatalogue").WithTags("Categories");

        api.MapPost("/categories", async (UpsertCategoryRequest req, ICatalogueDbContext db, IActorContext actor,
            HybridCache cache, IReferenceDataProvider refData, CancellationToken ct) =>
        {
            if (await db.Categories.AnyAsync(c => c.Code == req.Code.ToUpper(), ct))
                return Results.Conflict(new { error = "Category code already exists." });
            if (req.ParentCategoryId is { } pid && !await db.Categories.AnyAsync(c => c.Id == pid, ct))
                return Results.BadRequest(new { error = "Parent category not found." });
            var category = new Category(req.Code, req.Name, req.ParentCategoryId);
            category.Rename(req.Name, req.SortOrder ?? 0);
            db.Categories.Add(category);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "CategoryCreated", "Category",
                category.Code, req.Name, actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await Invalidate(cache, refData, ct);
            return Results.Created($"/api/categories/{category.Id}",
                new CategoryDto(category.Id, category.Code, category.Name, category.ParentCategoryId, category.IsActive, category.SortOrder));
        }).RequireAuthorization("CatalogueAdmin").WithTags("Categories");

        api.MapPut("/categories/{id:long}", async (long id, UpsertCategoryRequest req, ICatalogueDbContext db, IActorContext actor,
            HybridCache cache, IReferenceDataProvider refData, CancellationToken ct) =>
        {
            var all = await db.Categories.ToListAsync(ct);
            var category = all.FirstOrDefault(c => c.Id == id);
            if (category is null) return Results.NotFound();
            if (req.ParentCategoryId != category.ParentCategoryId)
            {
                // Ancestors of the new parent: moving under any of them would create a cycle.
                var ancestors = new List<long>();
                for (var cur = req.ParentCategoryId; cur is { } c && ancestors.Count < 50; cur = all.FirstOrDefault(x => x.Id == c)?.ParentCategoryId)
                    ancestors.Add(c);
                category.MoveTo(req.ParentCategoryId, ancestors);
            }
            category.Rename(req.Name, req.SortOrder ?? category.SortOrder);
            if (req.IsActive is { } active) category.SetActive(active);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "CategoryUpdated", "Category", category.Code,
                $"name={req.Name} parent={req.ParentCategoryId} active={category.IsActive}", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await Invalidate(cache, refData, ct);
            return Results.Ok(new CategoryDto(category.Id, category.Code, category.Name, category.ParentCategoryId, category.IsActive, category.SortOrder));
        }).RequireAuthorization("CatalogueAdmin").WithTags("Categories");

        // Template = own attributes + those inherited from the parent (shown read-only).
        api.MapGet("/categories/{id:long}/attributes", async (long id, ICatalogueDbContext db, CancellationToken ct) =>
        {
            var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
            if (category is null) return Results.NotFound();
            var ids = new List<long> { id };
            if (category.ParentCategoryId is { } pid) ids.Add(pid);
            var items = await db.CategoryAttributes.AsNoTracking().Include(ca => ca.Attribute)
                .Where(ca => ids.Contains(ca.CategoryId)).ToListAsync(ct);
            return Results.Ok(items.OrderBy(i => i.CategoryId == id ? 1 : 0).ThenBy(i => i.SortOrder)
                .Select(i => new TemplateItemDto(i.AttributeDefinitionId, i.Attribute!.Code, i.Attribute.Name, i.Attribute.DataType.ToString(),
                    i.Attribute.Unit, i.IsRequired, i.SortOrder, i.CategoryId != id)));
        }).RequireAuthorization("ReadCatalogue").WithTags("Categories");

        api.MapPut("/categories/{id:long}/attributes", async (long id, IReadOnlyList<TemplateItemRequest> items, ICatalogueDbContext db,
            IActorContext actor, HybridCache cache, IReferenceDataProvider refData, CancellationToken ct) =>
        {
            if (!await db.Categories.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();
            var known = await db.AttributeDefinitions.Select(a => a.Id).ToListAsync(ct);
            if (items.Any(i => !known.Contains(i.AttributeId))) return Results.BadRequest(new { error = "Unknown attribute in template." });

            var existing = await db.CategoryAttributes.Where(ca => ca.CategoryId == id).ToListAsync(ct);
            db.CategoryAttributes.RemoveRange(existing.Where(e => items.All(i => i.AttributeId != e.AttributeDefinitionId)));
            foreach (var item in items.DistinctBy(i => i.AttributeId))
            {
                var row = existing.FirstOrDefault(e => e.AttributeDefinitionId == item.AttributeId);
                if (row is null) db.CategoryAttributes.Add(new CategoryAttribute { CategoryId = id, AttributeDefinitionId = item.AttributeId, IsRequired = item.Required, SortOrder = item.SortOrder });
                else { row.IsRequired = item.Required; row.SortOrder = item.SortOrder; }
            }
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "CategoryTemplateChanged", "Category", id.ToString(),
                $"{items.Count} attributes, {items.Count(i => i.Required)} required", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await Invalidate(cache, refData, ct);
            await cache.RemoveByTagAsync(CacheTags.Products, ct);
            return Results.NoContent();
        }).RequireAuthorization("CatalogueAdmin").WithTags("Categories");

        // ----- attribute dictionary -----

        api.MapGet("/attributes", async (ICatalogueDbContext db, CancellationToken ct) =>
        {
            var usage = await db.CategoryAttributes.GroupBy(ca => ca.AttributeDefinitionId)
                .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var defs = await db.AttributeDefinitions.AsNoTracking().OrderBy(a => a.SortOrder).ThenBy(a => a.Name).ToListAsync(ct);
            return Results.Ok(defs.Select(a => ToDto(a, usage.GetValueOrDefault(a.Id))));
        }).RequireAuthorization("ReadCatalogue").WithTags("Attributes");

        api.MapPost("/attributes", async (UpsertAttributeRequest req, ICatalogueDbContext db, IActorContext actor,
            IReferenceDataProvider refData, CancellationToken ct) =>
        {
            if (!Enum.TryParse<AttributeDataType>(req.DataType, true, out var type))
                return Results.BadRequest(new { error = $"Unknown data type '{req.DataType}'." });
            if (await db.AttributeDefinitions.AnyAsync(a => a.Code == req.Code.ToUpper(), ct))
                return Results.Conflict(new { error = "Attribute code already exists." });
            var def = new AttributeDefinition(req.Code, req.Name, type, req.Unit, req.AllowedValues, req.MinValue, req.MaxValue,
                req.IsFilterable, req.SortOrder, req.Description);
            db.AttributeDefinitions.Add(def);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "AttributeCreated", "Attribute", def.Code, def.Name, actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            refData.Invalidate();
            return Results.Created($"/api/attributes/{def.Id}", ToDto(def, 0));
        }).RequireAuthorization("CatalogueAdmin").WithTags("Attributes");

        api.MapPut("/attributes/{id:long}", async (long id, UpsertAttributeRequest req, ICatalogueDbContext db, IActorContext actor,
            IReferenceDataProvider refData, HybridCache cache, CancellationToken ct) =>
        {
            var def = await db.AttributeDefinitions.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (def is null) return Results.NotFound();
            def.Update(req.Name, req.Unit, req.AllowedValues, req.MinValue, req.MaxValue, req.IsFilterable, req.SortOrder, req.Description);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "AttributeUpdated", "Attribute", def.Code, def.Name, actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            refData.Invalidate();
            await cache.RemoveByTagAsync(CacheTags.Products, ct);
            return Results.Ok(ToDto(def, 0));
        }).RequireAuthorization("CatalogueAdmin").WithTags("Attributes");
    }

    private static AttributeDto ToDto(AttributeDefinition a, int usage) => new(a.Id, a.Code, a.Name, a.DataType.ToString(), a.Unit,
        a.AllowedValueList, a.MinValue, a.MaxValue, a.IsFilterable, a.SortOrder, a.Description, usage);

    private static async Task Invalidate(HybridCache cache, IReferenceDataProvider refData, CancellationToken ct)
    {
        await cache.RemoveByTagAsync(CacheTags.Categories, ct);
        refData.Invalidate();
    }
}
