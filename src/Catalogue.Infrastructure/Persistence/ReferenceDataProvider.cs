using Catalogue.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Catalogue.Infrastructure.Persistence;

/// <summary>
/// Loads categories, attribute templates and supplier approval policies once and serves them to
/// every import row from memory (refreshing every 30s, or immediately after an admin change).
/// Without this, validating millions of rows would hit the database per row.
/// </summary>
public sealed class ReferenceDataProvider(IServiceScopeFactory scopes) : IReferenceDataProvider
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CatalogueReferenceData _data = CatalogueReferenceData.Empty;
    private DateTime _loadedUtc = DateTime.MinValue;

    public async Task<CatalogueReferenceData> GetAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _loadedUtc < Ttl) return _data;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _loadedUtc < Ttl) return _data;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ICatalogueDbContext>();

            var categories = await db.Categories.AsNoTracking().Where(c => c.IsActive)
                .ToDictionaryAsync(c => c.Code, c => c.Id, StringComparer.OrdinalIgnoreCase, ct);
            var attributes = await db.AttributeDefinitions.AsNoTracking()
                .ToDictionaryAsync(a => a.Code, StringComparer.OrdinalIgnoreCase, ct);
            var templates = (await db.CategoryAttributes.AsNoTracking().ToListAsync(ct))
                .GroupBy(ca => ca.CategoryId)
                .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<long, bool>)g.ToDictionary(x => x.AttributeDefinitionId, x => x.IsRequired));
            var policies = await db.Suppliers.AsNoTracking()
                .ToDictionaryAsync(s => s.Id, s => new SupplierPolicy(s.RequireApprovalForNewProducts, s.PriceChangeApprovalThresholdPercent), ct);

            _data = new CatalogueReferenceData
            {
                CategoryIds = categories, AttributesByCode = attributes, CategoryTemplates = templates, SupplierPolicies = policies
            };
            _loadedUtc = DateTime.UtcNow;
            return _data;
        }
        finally { _lock.Release(); }
    }

    public void Invalidate() => _loadedUtc = DateTime.MinValue;
}
