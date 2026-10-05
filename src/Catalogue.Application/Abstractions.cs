using Catalogue.Contracts;
using Catalogue.Domain;

namespace Catalogue.Application;

public interface ICatalogueDbContext
{
    Microsoft.EntityFrameworkCore.DbSet<Product> Products { get; }
    Microsoft.EntityFrameworkCore.DbSet<Supplier> Suppliers { get; }
    Microsoft.EntityFrameworkCore.DbSet<Category> Categories { get; }
    Microsoft.EntityFrameworkCore.DbSet<ProductCategory> ProductCategories { get; }
    Microsoft.EntityFrameworkCore.DbSet<ProductPrice> ProductPrices { get; }
    Microsoft.EntityFrameworkCore.DbSet<Inventory> Inventories { get; }
    Microsoft.EntityFrameworkCore.DbSet<ImportFile> ImportFiles { get; }
    Microsoft.EntityFrameworkCore.DbSet<ImportItemError> ImportItemErrors { get; }
    Microsoft.EntityFrameworkCore.DbSet<AuditEvent> AuditEvents { get; }
    Microsoft.EntityFrameworkCore.DbSet<AttributeDefinition> AttributeDefinitions { get; }
    Microsoft.EntityFrameworkCore.DbSet<CategoryAttribute> CategoryAttributes { get; }
    Microsoft.EntityFrameworkCore.DbSet<ProductAttributeValue> ProductAttributeValues { get; }
    Microsoft.EntityFrameworkCore.DbSet<SupplierOffer> SupplierOffers { get; }
    Microsoft.EntityFrameworkCore.DbSet<PriceList> PriceLists { get; }
    Microsoft.EntityFrameworkCore.DbSet<PriceListEntry> PriceListEntries { get; }
    Microsoft.EntityFrameworkCore.DbSet<CustomerAccount> CustomerAccounts { get; }
    Microsoft.EntityFrameworkCore.DbSet<ProductChangeRequest> ProductChangeRequests { get; }
    Microsoft.EntityFrameworkCore.DbSet<ProductAsset> ProductAssets { get; }
    Microsoft.EntityFrameworkCore.DbSet<MediaFetchRequest> MediaFetchRequests { get; }
    Microsoft.EntityFrameworkCore.DbSet<Organization> Organizations { get; }
    Microsoft.EntityFrameworkCore.DbSet<AppUser> Users { get; }
    Microsoft.EntityFrameworkCore.DbSet<RefreshToken> RefreshTokens { get; }
    Microsoft.EntityFrameworkCore.DbSet<SupplierDiscount> SupplierDiscounts { get; }
    Microsoft.EntityFrameworkCore.DbSet<PurchaseOrder> PurchaseOrders { get; }
    Microsoft.EntityFrameworkCore.DbSet<PurchaseOrderLine> PurchaseOrderLines { get; }
    Microsoft.EntityFrameworkCore.DbSet<Payment> Payments { get; }
    Microsoft.EntityFrameworkCore.DbSet<AgentConversation> AgentConversations { get; }
    Microsoft.EntityFrameworkCore.DbSet<AgentMessage> AgentMessages { get; }
    Microsoft.EntityFrameworkCore.DbSet<AgentActionProposal> AgentActionProposals { get; }
    Microsoft.EntityFrameworkCore.DbSet<OperationsReview> OperationsReviews { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>Raw file storage (local disk in dev, Azure Blob in cloud).</summary>
public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string fileName, CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
}

/// <summary>Import queue (in-memory channel locally, Azure Service Bus in cloud).</summary>
public interface IImportQueue
{
    ValueTask PublishAsync(ImportRequested message, CancellationToken ct);
    ValueTask<ImportRequested?> ReceiveAsync(CancellationToken ct);
}

/// <summary>Parses a supplier file (CSV / EDI) stream into canonical rows.</summary>
public interface ISupplierFileParser
{
    bool CanHandle(string format);
    IAsyncEnumerable<ParsedRow> ParseAsync(Stream source, CancellationToken ct);
}

public sealed record ParsedRow(int RowNumber, CanonicalProductImport Row, string? RawValue);

/// <summary>Supplier REST adapter boundary.</summary>
public interface ISupplierProductClient
{
    Task<IReadOnlyList<SupplierProductDto>> GetProductsAsync(CancellationToken ct);
}

/// <summary>Current actor abstraction (correlation + identity).</summary>
public interface IActorContext
{
    string ActorId { get; }
    string CorrelationId { get; }
}
