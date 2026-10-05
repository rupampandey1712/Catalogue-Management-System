using System.Text;
using Catalogue.Application;
using Catalogue.Contracts;
using Catalogue.Domain;
using Catalogue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.UnitTests;

public class ImportServiceTests
{
    private static CatalogueDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<CatalogueDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new CatalogueDbContext(options);
    }

    private sealed class MemStorage : IFileStorage
    {
        public Task<string> SaveAsync(Stream content, string fileName, CancellationToken ct)
            => Task.FromResult($"/mem/{fileName}");
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
            => Task.FromResult<Stream>(Stream.Null);
    }

    private sealed class CapturingQueue : IImportQueue
    {
        public List<ImportRequested> Messages { get; } = [];
        public ValueTask PublishAsync(ImportRequested message, CancellationToken ct)
        { Messages.Add(message); return ValueTask.CompletedTask; }
        public ValueTask<ImportRequested?> ReceiveAsync(CancellationToken ct)
            => ValueTask.FromResult<ImportRequested?>(null);
    }

    private sealed class Actor : IActorContext
    {
        public string ActorId => "test";
        public string CorrelationId => "corr-1";
    }

    [Fact]
    public async Task Same_file_twice_is_idempotent()
    {
        await using var db = NewDb();
        var supplier = new Supplier("SUPA", "Supplier A", IntegrationType.Csv);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var queue = new CapturingQueue();
        var sut = new ImportService(db, new MemStorage(), queue, new Actor());
        var csv = new MemoryStream(Encoding.UTF8.GetBytes("sku,name\nA-1,Widget\n"));

        var first = await sut.StartImportAsync(supplier.Id, csv, "products.csv", default);
        csv.Position = 0;
        var second = await sut.StartImportAsync(supplier.Id, csv, "products.csv", default);

        Assert.Equal(first.Id, second.Id);      // same import returned
        Assert.Single(queue.Messages);          // queued only once
        Assert.Equal("corr-1", first.CorrelationId);
    }

    [Fact]
    public async Task Rejects_disallowed_file_types()
    {
        await using var db = NewDb();
        var supplier = new Supplier("SUPA", "Supplier A", IntegrationType.Csv);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var sut = new ImportService(db, new MemStorage(), new CapturingQueue(), new Actor());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.StartImportAsync(supplier.Id, new MemoryStream([1, 2, 3]), "evil.exe", default));
    }
}
