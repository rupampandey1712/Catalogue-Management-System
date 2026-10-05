using Catalogue.Application;
using Catalogue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Catalogue.Functions;

/// <summary>Short-lived DbContext factory for stateless activity functions.
/// Singleton-safe: builds its own options from configuration (Functions DI validates
/// lifetimes, and scoped EF options can't flow into singletons).</summary>
public sealed class CatalogueDbContextFactory
{
    private readonly DbContextOptions<CatalogueDbContext> _options;

    public CatalogueDbContextFactory(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var connStr = config.GetSection("ConnectionStrings")["CatalogueDb"]
            ?? "Server=(localdb)\\mssqllocaldb;Database=CatalogueDb;Trusted_Connection=True;TrustServerCertificate=True";
        _options = new DbContextOptionsBuilder<CatalogueDbContext>()
            .UseSqlServer(connStr).Options;
    }

    public CatalogueDbContext Create()
    {
        var db = new CatalogueDbContext(_options);
        try
        {
            db.Database.EnsureCreated(); // matches the API's dev convenience; use migrations in prod
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Functions host could not reach CatalogueDb (conn='{_options.Extensions}').", ex);
        }
        return db;
    }
}

/// <summary>Resolves the registered supplier client (fake or DummyJSON, per config).</summary>
public sealed class SupplierClientResolver(IServiceProvider services)
{
    public ISupplierProductClient Resolve() => services.GetRequiredService<ISupplierProductClient>();
}
