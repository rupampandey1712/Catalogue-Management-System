using System.Text;
using Catalogue.Application;
using Catalogue.Contracts;
using Catalogue.Domain;
using Catalogue.Infrastructure.Parsing;
using Catalogue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Catalogue.WorkerTests;

public class ImportProcessorTests
{
    private static CatalogueDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<CatalogueDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new CatalogueDbContext(options);
    }

    private sealed class StaticStorage(string content) : IFileStorage
    {
        public Task<string> SaveAsync(Stream c, string fileName, CancellationToken ct)
            => Task.FromResult("mem");
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public async Task Processes_csv_and_completes_with_errors()
    {
        await using var db = NewDb();
        var category = new Category("HVAC", "Heating");
        var supplier = new Supplier("SUPA", "Supplier A", IntegrationType.Csv);
        db.Categories.Add(category);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var import = new ImportFile(supplier.Id, "mem", "p.csv", "hash1", "CSV", "corr-x");
        db.ImportFiles.Add(import);
        await db.SaveChangesAsync();

        var csv = "sku,name,category,price,currency,quantity\n" +
                  "HP-1,Heat Pump,HVAC,999.99,GBP,5\n" +
                  ",Missing SKU,HVAC,10,GBP,1\n" +   // row error
                  "TH-1,Thermostat,UNKNOWN,?,GBP,2\n".Replace("?", "20.00"); // unknown category
        var processor = new ImportProcessor(db, new StaticStorage(csv),
            [new CsvSupplierParser()], NullLogger<ImportProcessor>.Instance);

        await processor.ProcessAsync(new ImportRequested(import.Id, supplier.Id, "mem", "CSV", "corr-x"), default);

        var done = await db.ImportFiles.Include(i => i.Errors).FirstAsync(i => i.Id == import.Id);
        Assert.Equal(ImportStatus.CompletedWithErrors, done.Status);
        Assert.Equal(3, done.TotalRows);
        Assert.Equal(1, done.ProcessedRows);
        Assert.Equal(2, done.Errors.Count);
        Assert.Contains(db.Products, p => p.Sku == "HP-1");
    }

    [Fact]
    public async Task Processing_twice_does_not_duplicate_products()
    {
        await using var db = NewDb();
        var supplier = new Supplier("SUPA", "Supplier A", IntegrationType.Csv);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var import = new ImportFile(supplier.Id, "mem", "p.csv", "hash2", "CSV", "corr-y");
        db.ImportFiles.Add(import);
        await db.SaveChangesAsync();

        var csv = "sku,name,price\nHP-1,Heat Pump,100\n";
        var storage = new StaticStorage(csv);
        var processor = new ImportProcessor(db, storage,
            [new CsvSupplierParser()], NullLogger<ImportProcessor>.Instance);

        var msg = new ImportRequested(import.Id, supplier.Id, "mem", "CSV", "corr-y");
        await processor.ProcessAsync(msg, default);
        await processor.ProcessAsync(msg, default); // replay

        Assert.Equal(1, await db.Products.CountAsync());
        Assert.Equal(ImportStatus.Completed,
            (await db.ImportFiles.FirstAsync(i => i.Id == import.Id)).Status);
    }
}
