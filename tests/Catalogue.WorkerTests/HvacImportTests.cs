using System.Text;
using Catalogue.Application;
using Catalogue.Contracts;
using Catalogue.Domain;
using Catalogue.Infrastructure.Parsing;
using Catalogue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Catalogue.WorkerTests;

/// <summary>File-import path: specs, supplier offers, GTIN consolidation and approval policies.</summary>
public class HvacImportTests
{
    private sealed class StaticStorage(string content) : IFileStorage
    {
        public Task<string> SaveAsync(Stream c, string fileName, CancellationToken ct) => Task.FromResult("mem");
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    private sealed record Setup(CatalogueDbContext Db, Supplier A, Supplier B);

    private static async Task<Setup> NewDbAsync()
    {
        var db = new CatalogueDbContext(new DbContextOptionsBuilder<CatalogueDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var hvac = new Category("HVAC", "Heat Pumps");
        var kw = new AttributeDefinition("CAPACITY_KW", "Output", AttributeDataType.Number, "kW", min: 0.5m, max: 100m);
        var refrigerant = new AttributeDefinition("REFRIGERANT", "Refrigerant", AttributeDataType.Enum, allowedValues: ["R32", "R290"]);
        var a = new Supplier("SUPA", "A", IntegrationType.Csv);
        var b = new Supplier("SUPB", "B", IntegrationType.Csv);
        db.AddRange(hvac, kw, refrigerant, a, b);
        await db.SaveChangesAsync();
        db.CategoryAttributes.AddRange(
            new CategoryAttribute { CategoryId = hvac.Id, AttributeDefinitionId = kw.Id, IsRequired = true },
            new CategoryAttribute { CategoryId = hvac.Id, AttributeDefinitionId = refrigerant.Id, IsRequired = true });
        await db.SaveChangesAsync();
        return new Setup(db, a, b);
    }

    private static async Task<ImportFile> ImportAsync(CatalogueDbContext db, Supplier supplier, string csv, string hash)
    {
        var import = new ImportFile(supplier.Id, "mem", $"{hash}.csv", hash, "CSV", "corr");
        db.ImportFiles.Add(import);
        await db.SaveChangesAsync();
        var processor = new ImportProcessor(db, new StaticStorage(csv), [new CsvSupplierParser()], NullLogger<ImportProcessor>.Instance);
        await processor.ProcessAsync(new ImportRequested(import.Id, supplier.Id, "mem", "CSV", "corr"), default);
        return await db.ImportFiles.Include(i => i.Errors).FirstAsync(i => i.Id == import.Id);
    }

    private const string Header = "sku,name,category,price,currency,quantity,brand,mpn,gtin,cost,lead_time_days,attr.CAPACITY_KW,attr.REFRIGERANT\n";

    [Fact]
    public async Task Imports_specs_identity_and_an_offer_and_rejects_invalid_specs()
    {
        var (db, a, _) = await NewDbAsync();
        var done = await ImportAsync(db, a, Header +
            "A-HP-8,Thermora 8kW,HVAC,4200,GBP,12,Thermora,TH-8,5060000000009,2900,2,8,R32\n" +
            "A-HP-9,Bad refrigerant,HVAC,4300,GBP,5,Thermora,TH-9,5060000000016,3000,2,9,R22\n", "h1");

        Assert.Equal(ImportStatus.CompletedWithErrors, done.Status);
        Assert.Contains(done.Errors, e => e.Field == "attr.REFRIGERANT");
        var p = await db.Products.Include(x => x.AttributeValues).Include(x => x.Offers).SingleAsync();
        Assert.Equal("Thermora", p.Brand);
        Assert.Equal("05060000000009", p.Gtin);
        Assert.Equal(2, p.AttributeValues.Count);
        var offer = Assert.Single(p.Offers);
        Assert.Equal(2900m, offer.Cost);
        Assert.Equal(Availability.LowStock, offer.Availability); // 12 <= 20
        Assert.True(offer.IsPreferred);
    }

    [Fact]
    public async Task Same_gtin_from_a_second_supplier_adds_an_offer_instead_of_a_duplicate_product()
    {
        var (db, a, b) = await NewDbAsync();
        await ImportAsync(db, a, Header + "A-HP-8,Thermora 8kW,HVAC,4200,GBP,12,Thermora,TH-8,5060000000009,2900,2,8,R32\n", "h1");
        await ImportAsync(db, b, Header + "B-77812,Thermora Aero 8,HVAC,3999,GBP,40,Thermora,TH-8,5060000000009,2800,1,8,R32\n", "h2");

        var product = await db.Products.Include(p => p.Offers).Include(p => p.InventoryRecords).SingleAsync();
        Assert.Equal(2, product.Offers.Count);
        Assert.Equal("Thermora 8kW", product.Name);               // content owner (A) unchanged
        Assert.Contains(product.Offers, o => o.SupplierId == b.Id && o.SupplierSku == "B-77812" && !o.IsPreferred);
        Assert.Equal(52, product.InventoryRecords.Sum(i => i.Quantity)); // stock per supplier, summed
    }

    [Fact]
    public async Task Approval_policy_holds_new_products_and_large_price_moves()
    {
        var (db, a, _) = await NewDbAsync();
        a.SetApprovalPolicy(requireApprovalForNewProducts: true, priceChangeThresholdPercent: 10);
        await db.SaveChangesAsync();

        await ImportAsync(db, a, Header + "A-HP-8,Thermora 8kW,HVAC,1000,GBP,12,Thermora,TH-8,5060000000009,700,2,8,R32\n", "h1");
        var product = await db.Products.SingleAsync();
        Assert.Equal(ProductStatus.PendingApproval, product.Status);
        Assert.Single(db.ProductChangeRequests, r => r.Kind == ChangeKind.NewProduct);

        // +25% price move from the same supplier: held, the old price stays current.
        await ImportAsync(db, a, Header + "A-HP-8,Thermora 8kW,HVAC,1250,GBP,12,Thermora,TH-8,5060000000009,700,2,8,R32\n", "h2");
        var held = await db.ProductChangeRequests.SingleAsync(r => r.Kind == ChangeKind.PriceChange);
        Assert.Equal(1000m, held.OldPrice);
        Assert.Equal(1250m, held.NewPrice);
        Assert.Equal(1000m, (await db.ProductPrices.SingleAsync(p => p.ValidToUtc == null)).Amount);

        // Approve both: product goes live, new price becomes current and the old one history.
        var approvals = new ApprovalService(db, new TestActor());
        var decided = await approvals.DecideAsync(await db.ProductChangeRequests.Select(r => r.Id).ToListAsync(), true, "ok", default);
        Assert.Equal(2, decided);
        Assert.Equal(ProductStatus.Active, (await db.Products.SingleAsync()).Status);
        Assert.Equal(1250m, (await db.ProductPrices.SingleAsync(p => p.ValidToUtc == null)).Amount);
        Assert.Equal(2, await db.ProductPrices.CountAsync());
    }

    [Fact]
    public async Task Small_price_moves_apply_directly_and_close_the_previous_price()
    {
        var (db, a, _) = await NewDbAsync();
        a.SetApprovalPolicy(false, 10);
        await db.SaveChangesAsync();
        await ImportAsync(db, a, Header + "A-HP-8,Thermora 8kW,HVAC,1000,GBP,12,Thermora,TH-8,5060000000009,700,2,8,R32\n", "h1");
        await ImportAsync(db, a, Header + "A-HP-8,Thermora 8kW,HVAC,1050,GBP,12,Thermora,TH-8,5060000000009,700,2,8,R32\n", "h2");

        Assert.Empty(db.ProductChangeRequests);
        var prices = await db.ProductPrices.OrderBy(p => p.ValidFromUtc).ToListAsync();
        Assert.Equal(2, prices.Count);
        Assert.NotNull(prices[0].ValidToUtc);           // closed (was a bug: several "current" prices)
        Assert.Equal(1050m, prices.Single(p => p.ValidToUtc == null).Amount);
    }

    private sealed class TestActor : IActorContext
    {
        public string ActorId => "admin";
        public string CorrelationId => "test";
    }
}
