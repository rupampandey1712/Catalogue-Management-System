using Catalogue.Application;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Infrastructure.Persistence;

public sealed class CatalogueDbContext(DbContextOptions<CatalogueDbContext> options)
    : DbContext(options), ICatalogueDbContext
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();
    public DbSet<ProductPrice> ProductPrices => Set<ProductPrice>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<ImportFile> ImportFiles => Set<ImportFile>();
    public DbSet<ImportItemError> ImportItemErrors => Set<ImportItemError>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();
    public DbSet<CategoryAttribute> CategoryAttributes => Set<CategoryAttribute>();
    public DbSet<ProductAttributeValue> ProductAttributeValues => Set<ProductAttributeValue>();
    public DbSet<SupplierOffer> SupplierOffers => Set<SupplierOffer>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<PriceListEntry> PriceListEntries => Set<PriceListEntry>();
    public DbSet<CustomerAccount> CustomerAccounts => Set<CustomerAccount>();
    public DbSet<ProductChangeRequest> ProductChangeRequests => Set<ProductChangeRequest>();
    public DbSet<ProductAsset> ProductAssets => Set<ProductAsset>();
    public DbSet<MediaFetchRequest> MediaFetchRequests => Set<MediaFetchRequest>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SupplierDiscount> SupplierDiscounts => Set<SupplierDiscount>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<AgentConversation> AgentConversations => Set<AgentConversation>();
    public DbSet<AgentMessage> AgentMessages => Set<AgentMessage>();
    public DbSet<AgentActionProposal> AgentActionProposals => Set<AgentActionProposal>();
    public DbSet<OperationsReview> OperationsReviews => Set<OperationsReview>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Sku).HasMaxLength(64).IsRequired();
            b.Property(p => p.Name).HasMaxLength(256).IsRequired();
            b.HasIndex(p => new { p.SupplierId, p.Sku }).IsUnique();
            b.HasIndex(p => p.Name);
            b.HasIndex(p => p.Status);
            b.HasIndex(p => p.Sku);
            b.HasIndex(p => p.UpdatedAtUtc);
            b.HasIndex(p => p.CreatedAtUtc); // analytics: products added per day
            b.Property(p => p.Brand).HasMaxLength(100);
            b.Property(p => p.ManufacturerPartNumber).HasMaxLength(64);
            b.Property(p => p.Gtin).HasMaxLength(14).IsUnicode(false);
            b.HasIndex(p => p.Gtin);
            b.HasIndex(p => new { p.Brand, p.ManufacturerPartNumber });
            b.HasIndex(p => p.ManufacturerPartNumber); // media packs / search match by MPN alone
        });

        modelBuilder.Entity<Supplier>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Code).HasMaxLength(32).IsRequired();
            b.HasIndex(s => s.Code).IsUnique();
            b.Property(s => s.PriceChangeApprovalThresholdPercent).HasPrecision(7, 2);
        });

        modelBuilder.Entity<Category>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Code).HasMaxLength(64).IsRequired();
            b.HasIndex(c => c.Code).IsUnique();
            b.HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentCategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProductCategory>(b =>
        {
            b.HasKey(pc => new { pc.ProductId, pc.CategoryId });
            b.HasOne(pc => pc.Product).WithMany(p => p.ProductCategories).HasForeignKey(pc => pc.ProductId);
            b.HasOne(pc => pc.Category).WithMany().HasForeignKey(pc => pc.CategoryId);
        });

        modelBuilder.Entity<ProductPrice>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Currency).HasMaxLength(3).IsRequired();
            b.Property(p => p.Amount).HasPrecision(18, 2);
            b.ToTable(t => t.HasCheckConstraint("CK_ProductPrice_Amount", "[Amount] >= 0"));
            b.HasOne(p => p.Product).WithMany(p => p.Prices).HasForeignKey(p => p.ProductId);
        });

        modelBuilder.Entity<Inventory>(b =>
        {
            b.HasKey(i => i.Id);
            b.ToTable(t => t.HasCheckConstraint("CK_Inventory_Quantity", "[Quantity] >= 0"));
            b.HasOne(i => i.Product).WithMany(p => p.InventoryRecords).HasForeignKey(i => i.ProductId);
        });

        modelBuilder.Entity<ImportFile>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.Status).HasConversion<string>().HasMaxLength(32);
            b.HasIndex(i => i.Status);
            b.HasIndex(i => i.ReceivedAtUtc);
            b.HasIndex(i => new { i.SupplierId, i.FileHash }).IsUnique();
            b.HasOne(i => i.Supplier).WithMany().HasForeignKey(i => i.SupplierId);
        });

        modelBuilder.Entity<ImportItemError>(b =>
        {
            b.HasKey(e => e.Id);
            b.HasOne(e => e.Import).WithMany(i => i.Errors).HasForeignKey(e => e.ImportId);
        });

        modelBuilder.Entity<AttributeDefinition>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Code).HasMaxLength(64).IsRequired();
            b.Property(a => a.Name).HasMaxLength(128).IsRequired();
            b.Property(a => a.DataType).HasConversion<string>().HasMaxLength(16);
            b.Property(a => a.Unit).HasMaxLength(16);
            b.Property(a => a.AllowedValues).HasMaxLength(1000);
            b.Property(a => a.MinValue).HasPrecision(18, 4);
            b.Property(a => a.MaxValue).HasPrecision(18, 4);
            b.Property(a => a.Description).HasMaxLength(500);
            b.Ignore(a => a.AllowedValueList);
            b.HasIndex(a => a.Code).IsUnique();
        });

        modelBuilder.Entity<CategoryAttribute>(b =>
        {
            b.HasKey(ca => new { ca.CategoryId, ca.AttributeDefinitionId });
            b.HasOne(ca => ca.Category).WithMany(c => c.Attributes).HasForeignKey(ca => ca.CategoryId);
            b.HasOne(ca => ca.Attribute).WithMany().HasForeignKey(ca => ca.AttributeDefinitionId);
        });

        modelBuilder.Entity<ProductAttributeValue>(b =>
        {
            b.HasKey(v => new { v.ProductId, v.AttributeDefinitionId });
            b.Property(v => v.TextValue).HasMaxLength(200);
            b.Property(v => v.NumberValue).HasPrecision(18, 4);
            b.Ignore(v => v.Value);
            b.HasOne(v => v.Product).WithMany(p => p.AttributeValues).HasForeignKey(v => v.ProductId);
            b.HasOne(v => v.Attribute).WithMany().HasForeignKey(v => v.AttributeDefinitionId);
            // Facet filters such as CAPACITY_KW between 8 and 12 become index seeks.
            b.HasIndex(v => new { v.AttributeDefinitionId, v.NumberValue });
            b.HasIndex(v => new { v.AttributeDefinitionId, v.TextValue });
        });

        modelBuilder.Entity<SupplierOffer>(b =>
        {
            b.HasKey(o => o.Id);
            b.Property(o => o.SupplierSku).HasMaxLength(64).IsRequired();
            b.Property(o => o.Currency).HasMaxLength(3).IsRequired();
            b.Property(o => o.Cost).HasPrecision(18, 2);
            b.Property(o => o.Price).HasPrecision(18, 2);
            b.Property(o => o.Availability).HasConversion<string>().HasMaxLength(16);
            b.HasIndex(o => new { o.SupplierId, o.SupplierSku }).IsUnique();
            b.HasIndex(o => o.ProductId);
            // "Products this supplier offers" (catalogue filter, seller portal): seek by supplier in ProductId order.
            b.HasIndex(o => new { o.SupplierId, o.ProductId });
            b.HasOne(o => o.Product).WithMany(p => p.Offers).HasForeignKey(o => o.ProductId);
            b.HasOne(o => o.Supplier).WithMany().HasForeignKey(o => o.SupplierId);
        });

        modelBuilder.Entity<PriceList>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Code).HasMaxLength(32).IsRequired();
            b.Property(p => p.Name).HasMaxLength(128).IsRequired();
            b.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16);
            b.Property(p => p.Currency).HasMaxLength(3);
            b.Property(p => p.DiscountPercent).HasPrecision(5, 2);
            b.HasIndex(p => p.Code).IsUnique();
            b.HasOne(p => p.Parent).WithMany().HasForeignKey(p => p.ParentPriceListId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PriceListEntry>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.UnitPrice).HasPrecision(18, 2);
            b.ToTable(t => t.HasCheckConstraint("CK_PriceListEntry_Price", "[UnitPrice] >= 0 AND [MinQuantity] >= 1"));
            b.HasIndex(e => new { e.PriceListId, e.ProductId, e.MinQuantity });
            b.HasIndex(e => e.ProductId);
            b.HasOne(e => e.PriceList).WithMany().HasForeignKey(e => e.PriceListId);
            b.HasOne(e => e.Product).WithMany().HasForeignKey(e => e.ProductId);
        });

        modelBuilder.Entity<CustomerAccount>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.AccountNumber).HasMaxLength(32).IsRequired();
            b.Property(c => c.Name).HasMaxLength(128).IsRequired();
            b.HasIndex(c => c.AccountNumber).IsUnique();
            b.HasOne(c => c.PriceList).WithMany().HasForeignKey(c => c.PriceListId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ProductChangeRequest>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Kind).HasConversion<string>().HasMaxLength(16);
            b.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(c => c.Currency).HasMaxLength(3);
            b.Property(c => c.OldPrice).HasPrecision(18, 2);
            b.Property(c => c.NewPrice).HasPrecision(18, 2);
            b.Property(c => c.Summary).HasMaxLength(400);
            b.Property(c => c.DecidedBy).HasMaxLength(64);
            b.Property(c => c.DecisionNote).HasMaxLength(500);
            b.Ignore(c => c.ChangePercent);
            b.HasIndex(c => new { c.Status, c.Kind, c.CreatedAtUtc });
            b.HasIndex(c => c.ProductId);
            b.HasOne(c => c.Product).WithMany().HasForeignKey(c => c.ProductId);
            b.HasOne(c => c.Supplier).WithMany().HasForeignKey(c => c.SupplierId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProductAsset>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
            b.Property(a => a.Source).HasConversion<string>().HasMaxLength(16);
            b.Property(a => a.Title).HasMaxLength(200).IsRequired();
            b.Property(a => a.FileName).HasMaxLength(260).IsRequired();
            b.Property(a => a.ContentType).HasMaxLength(64).IsRequired();
            b.Property(a => a.Sha256).HasMaxLength(64).IsUnicode(false).IsRequired();
            b.Property(a => a.StoragePath).HasMaxLength(400).IsRequired();
            b.Property(a => a.ThumbnailPath).HasMaxLength(400);
            b.Property(a => a.SourceUrl).HasMaxLength(1000);
            b.Property(a => a.UploadedBy).HasMaxLength(64);
            b.HasIndex(a => new { a.ProductId, a.Kind, a.IsPrimary });
            b.HasIndex(a => new { a.ProductId, a.Sha256 }).IsUnique();
            b.HasOne(a => a.Product).WithMany().HasForeignKey(a => a.ProductId);
        });

        modelBuilder.Entity<MediaFetchRequest>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(r => r.Url).HasMaxLength(1000).IsRequired();
            b.Property(r => r.LastError).HasMaxLength(500);
            b.HasIndex(r => new { r.Status, r.NextAttemptUtc });
            b.HasIndex(r => r.ProductId);
        });

        modelBuilder.Entity<Organization>(b =>
        {
            b.HasKey(o => o.Id);
            b.Property(o => o.Name).HasMaxLength(200).IsRequired();
            b.Property(o => o.VatNumber).HasMaxLength(32);
            b.Property(o => o.Country).HasMaxLength(2).IsRequired();
            b.Property(o => o.Capabilities).HasConversion<int>();
            b.Property(o => o.Status).HasConversion<string>().HasMaxLength(24);
            b.Property(o => o.ReviewedBy).HasMaxLength(100);
            b.Property(o => o.ReviewNote).HasMaxLength(500);
            b.Ignore(o => o.CanBuy);
            b.Ignore(o => o.CanSell);
            b.HasIndex(o => o.Name);
            b.HasIndex(o => o.Status);
            b.HasOne(o => o.Supplier).WithMany().HasForeignKey(o => o.SupplierId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(o => o.CustomerAccount).WithMany().HasForeignKey(o => o.CustomerAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AppUser>(b =>
        {
            b.ToTable("Users");
            b.HasKey(u => u.Id);
            b.Property(u => u.Email).HasMaxLength(256).IsRequired();
            b.Property(u => u.Username).HasMaxLength(64);
            b.Property(u => u.DisplayName).HasMaxLength(128).IsRequired();
            b.Property(u => u.PasswordHash).HasMaxLength(256).IsRequired();
            b.Property(u => u.Roles).HasMaxLength(200).IsRequired();
            b.Property(u => u.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(u => u.SecurityStamp).HasMaxLength(64).IsRequired();
            b.Ignore(u => u.RoleList);
            b.Ignore(u => u.SignInName);
            b.HasIndex(u => u.Email).IsUnique();
            b.HasIndex(u => u.Username).IsUnique().HasFilter("[Username] IS NOT NULL");
            b.HasOne(u => u.Organization).WithMany().HasForeignKey(u => u.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.TokenHash).HasMaxLength(64).IsUnicode(false).IsRequired();
            b.Property(t => t.SecurityStamp).HasMaxLength(64).IsRequired();
            b.Property(t => t.RevokedReason).HasMaxLength(64);
            b.Property(t => t.CreatedByIp).HasMaxLength(64);
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasIndex(t => new { t.UserId, t.FamilyId });
            b.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId);
        });

        modelBuilder.Entity<SupplierDiscount>(b =>
        {
            b.HasKey(d => d.Id);
            b.Property(d => d.DiscountPercent).HasPrecision(5, 2);
            b.Property(d => d.Note).HasMaxLength(200);
            b.HasIndex(d => new { d.SupplierId, d.CustomerAccountId });
            b.HasOne(d => d.Supplier).WithMany().HasForeignKey(d => d.SupplierId);
            b.HasOne(d => d.CustomerAccount).WithMany().HasForeignKey(d => d.CustomerAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseOrder>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Number).HasMaxLength(32).IsRequired();
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(p => p.Currency).HasMaxLength(3);
            b.Property(p => p.Subtotal).HasPrecision(18, 2);
            b.Property(p => p.ListValue).HasPrecision(18, 2);
            b.Property(p => p.BuyerReference).HasMaxLength(64);
            b.Property(p => p.Note).HasMaxLength(500);
            b.Property(p => p.StatusNote).HasMaxLength(500);
            b.Property(p => p.CreatedBy).HasMaxLength(128);
            b.Property(p => p.PaymentStatus).HasConversion<string>().HasMaxLength(16);
            b.HasIndex(p => p.Number).IsUnique();
            b.HasIndex(p => new { p.BuyerOrganizationId, p.CreatedAtUtc });
            b.HasIndex(p => new { p.SupplierId, p.Status, p.CreatedAtUtc });
            b.HasOne(p => p.BuyerOrganization).WithMany().HasForeignKey(p => p.BuyerOrganizationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(p => p.Supplier).WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(p => p.Lines).WithOne().HasForeignKey(l => l.PurchaseOrderId);
        });

        modelBuilder.Entity<AgentConversation>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.UserName).HasMaxLength(128);
            b.Property(c => c.Title).HasMaxLength(100);
            b.HasIndex(c => new { c.UserId, c.UpdatedAtUtc });
        });

        modelBuilder.Entity<AgentMessage>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.Role).HasMaxLength(16);
            b.Property(m => m.Error).HasMaxLength(1000);
            b.HasIndex(m => new { m.ConversationId, m.Id });
            b.HasOne<AgentConversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentActionProposal>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Kind).HasConversion<string>().HasMaxLength(24);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(p => p.TargetId).HasMaxLength(64);
            b.Property(p => p.TargetLabel).HasMaxLength(200);
            b.Property(p => p.Reason).HasMaxLength(500);
            b.Property(p => p.Source).HasMaxLength(32);
            b.Property(p => p.RequestedBy).HasMaxLength(128);
            b.Property(p => p.DecidedBy).HasMaxLength(128);
            b.Property(p => p.Result).HasMaxLength(500);
            b.HasIndex(p => new { p.Status, p.CreatedAtUtc });
            // One open proposal per action and target: repeated reviews don't pile up duplicates.
            b.HasIndex(p => new { p.Kind, p.TargetId }).IsUnique().HasFilter("[Status] = 'Pending'").HasDatabaseName("IX_AgentActionProposals_OpenPerTarget");
        });

        modelBuilder.Entity<OperationsReview>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Trigger).HasMaxLength(32);
            b.Property(r => r.RequestedBy).HasMaxLength(128);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(r => r.ModelId).HasMaxLength(128);
            b.Property(r => r.Error).HasMaxLength(500);
            b.HasIndex(r => r.StartedAtUtc);
        });

        modelBuilder.Entity<Payment>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Provider).HasConversion<string>().HasMaxLength(16);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(p => p.Amount).HasPrecision(18, 2);
            b.Property(p => p.Currency).HasMaxLength(3);
            b.Property(p => p.CheckoutReference).HasMaxLength(255).IsUnicode(false);
            b.Property(p => p.CheckoutUrl).HasMaxLength(1000);
            b.Property(p => p.PaymentReference).HasMaxLength(255).IsUnicode(false);
            b.Property(p => p.RefundReference).HasMaxLength(255).IsUnicode(false);
            b.Property(p => p.FailureReason).HasMaxLength(300);
            b.Property(p => p.CreatedBy).HasMaxLength(128);
            b.HasIndex(p => new { p.PurchaseOrderId, p.CreatedAtUtc });
            b.HasIndex(p => new { p.Provider, p.CheckoutReference });
            // Database-level guarantee that an order is never paid twice, whatever races between webhooks and syncs.
            b.HasIndex(p => p.PurchaseOrderId).IsUnique().HasFilter("[Status] IN ('Succeeded', 'Refunded')")
                .HasDatabaseName("IX_Payments_OneSuccessPerOrder");
            b.HasOne(p => p.PurchaseOrder).WithMany().HasForeignKey(p => p.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseOrderLine>(b =>
        {
            b.HasKey(l => l.Id);
            b.Property(l => l.SupplierSku).HasMaxLength(64);
            b.Property(l => l.Description).HasMaxLength(256);
            b.Property(l => l.UnitPrice).HasPrecision(18, 2);
            b.Property(l => l.ListUnitPrice).HasPrecision(18, 2);
            b.Property(l => l.LineTotal).HasPrecision(18, 2);
            b.Property(l => l.Currency).HasMaxLength(3);
            b.Property(l => l.PriceExplanation).HasMaxLength(200);
            b.HasIndex(l => l.ProductId);
        });

        modelBuilder.Entity<AuditEvent>(b =>
        {
            b.HasKey(a => a.Id);
            b.HasIndex(a => a.TimestampUtc);
            b.HasIndex(a => new { a.EntityType, a.EntityId });
        });
    }
}
