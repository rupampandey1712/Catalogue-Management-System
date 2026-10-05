using Catalogue.Application;
using Catalogue.Application.Streaming;
using Catalogue.Infrastructure.Messaging;
using Catalogue.Infrastructure.Parsing;
using Catalogue.Infrastructure.Persistence;
using Catalogue.Infrastructure.Storage;
using Catalogue.Infrastructure.Streaming;
using Catalogue.Infrastructure.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Catalogue.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCatalogueInfrastructure(this IServiceCollection services, IConfiguration config,
        string? contentRoot = null)
    {
        var connStr = config.GetConnectionString("CatalogueDb")
            ?? "Server=(localdb)\\mssqllocaldb;Database=CatalogueDb;Trusted_Connection=True;TrustServerCertificate=True";
        services.AddDbContext<CatalogueDbContext>(o => o.UseSqlServer(connStr, sql =>
        {
            // Transient faults (deadlock victim, failover, throttling, network blips) are retried by EF.
            sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: [1205]);
            sql.CommandTimeout(60);
        }));
        services.AddScoped<ICatalogueDbContext>(sp => sp.GetRequiredService<CatalogueDbContext>());

        // Storage: Azure Blob / Azurite when reachable, otherwise the project's own .storage folder.
        // Either way the bytes stay in the project location (Azurite's data is bind-mounted to .azurite/).
        var storageRoot = StoragePaths.Root(config, contentRoot);
        services.AddSingleton(sp => StorageSelection.Resolve(config, storageRoot, sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<StorageSelection>() is { UseBlob: true } s
            ? new BlobFileStorage(s.ConnectionString!, config["FileStorage:Container"] ?? "catalogue-raw")
            : new LocalFileStorage(Path.Combine(storageRoot, "files")));
        services.AddSingleton<Application.Media.IMediaStorage>(sp => sp.GetRequiredService<StorageSelection>() is { UseBlob: true } s
            ? new BlobMediaStorage(s.ConnectionString!, config["Media:Container"] ?? "catalogue-media")
            : new LocalMediaStorage(Path.Combine(storageRoot, "media")));
        services.AddSingleton<Application.Media.IImageProcessor, Media.SkiaImageProcessor>();
        services.AddSingleton(new Application.Media.MediaUrlSigner(System.Text.Encoding.UTF8.GetBytes(
            config["Media:SigningKey"] ?? config["Jwt:SigningKey"] ?? "dev-only-signing-key-change-me-0123456789abcdef")));
        services.AddScoped<Application.Media.MediaService>();
        services.AddScoped<Application.Media.BulkMediaImporter>();
        services.AddHttpClient("media", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(90);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("CatalogueMediaFetcher/1.0");
        });

        // Queue: in-memory channel locally, Service Bus (or the SB emulator) when configured.
        services.AddSingleton<IImportQueue>(sp =>
            config.GetValue<bool>("ServiceBus:Enabled")
                ? new ServiceBusImportQueue(config)
                : new InMemoryImportQueue());

        services.AddSingleton<IReferenceDataProvider, ReferenceDataProvider>();
        services.AddSingleton<ISupplierFileParser, CsvSupplierParser>();
        services.AddSingleton<ISupplierFileParser, EdiSupplierParser>();

        services.AddHttpClient<DummyJsonSupplierProductClient>();
        services.AddTransient<FakeSupplierProductClient>();
        services.AddTransient<ISupplierProductClient>(sp =>
            config.GetValue<bool>("SupplierApis:UseDummyJson")
                ? sp.GetRequiredService<DummyJsonSupplierProductClient>()
                : sp.GetRequiredService<FakeSupplierProductClient>());

        return services;
    }

    /// <summary>
    /// Event Hubs streaming pipeline: producer (Azure/emulator or in-memory hub), set-based bulk
    /// writer, live telemetry, background publisher and - optionally - the partition consumers.
    /// </summary>
    public static IServiceCollection AddCatalogueEventHub(this IServiceCollection services, IConfiguration config,
        string contentRoot, bool publisher = true)
    {
        var options = config.GetSection("EventHub").Get<EventHubOptions>() ?? new EventHubOptions();
        services.AddSingleton(options);
        services.AddSingleton<StreamTelemetry>();
        services.AddSingleton<StreamJobQueue>();

        var sqlConn = config.GetConnectionString("CatalogueDb")
            ?? "Server=(localdb)\\mssqllocaldb;Database=CatalogueDb;Trusted_Connection=True;TrustServerCertificate=True";
        services.AddSingleton<IBulkCatalogueWriter>(sp =>
            new SqlBulkCatalogueWriter(sqlConn, sp.GetRequiredService<ILogger<SqlBulkCatalogueWriter>>()));

        var sourceDir = options.SourceDirectory is { Length: > 0 } dir
            ? Path.GetFullPath(Path.Combine(contentRoot, dir))
            : Path.Combine(contentRoot, "edi-library");
        services.AddSingleton<IEdiSourceLibrary>(new EdiSourceLibrary(sourceDir));

        if (options.Enabled)
        {
            var eh = config.GetConnectionString("EventHub")
                ?? throw new InvalidOperationException("ConnectionStrings:EventHub is required when EventHub:Enabled=true.");
            var checkpoint = config.GetConnectionString("CheckpointStorage") ?? "UseDevelopmentStorage=true";
            services.AddSingleton(new EventHubConnection(eh, checkpoint));
            services.AddSingleton<IEventHubProducer>(_ => new AzureEventHubProducer(eh, options));
        }
        else
        {
            services.AddSingleton<InMemoryEventHub>();
            services.AddSingleton<IEventHubProducer>(sp => sp.GetRequiredService<InMemoryEventHub>());
        }

        services.AddSingleton<EdiChunkProcessor>();
        services.AddScoped<EdiStreamPublisher>();
        services.AddScoped<EdiStreamService>();

        if (publisher)
            services.AddHostedService<EdiStreamPublisherService>();
        // The in-memory hub only exists inside this process, so its consumers must run here too.
        if (options.ConsumerInProcess || !options.Enabled)
            services.AddHostedService<EventHubIngestionService>();
        return services;
    }

    /// <summary>
    /// Payment gateways: the simulator when Payments:Simulated:Enabled (Development), Stripe Checkout when
    /// Payments:Stripe:SecretKey is set (test keys only unless AllowLiveKeys). Neither => orders settle on account.
    /// </summary>
    public static IServiceCollection AddCataloguePayments(this IServiceCollection services, IConfiguration config)
    {
        var options = Payments.PaymentOptions.From(config);
        services.AddSingleton(options);
        if (options.SimulatedEnabled)
            services.AddSingleton<IPaymentGateway, Payments.SimulatedPaymentGateway>();
        if (options.StripeEnabled)
        {
            services.AddHttpClient(Payments.StripePaymentGateway.HttpClientName, c =>
            {
                c.BaseAddress = new Uri("https://api.stripe.com/");
                c.Timeout = TimeSpan.FromSeconds(30);
            });
            services.AddTransient<IPaymentGateway>(sp => new Payments.StripePaymentGateway(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(Payments.StripePaymentGateway.HttpClientName), options));
        }
        services.AddScoped<PaymentService>();
        return services;
    }

    /// <summary>Background download of media URLs referenced by supplier feeds.</summary>
    public static IServiceCollection AddCatalogueMediaFetcher(this IServiceCollection services)
        => services.AddHostedService<Media.MediaFetchService>();

    /// <summary>Applies migrations (EnsureCreated for the local path) and seeds demo data.</summary>
    public static async Task SeedAsync(this CatalogueDbContext db, CancellationToken ct = default)
    {
        await db.Database.EnsureCreatedAsync(ct);
        await SchemaEvolver.EvolveAsync(db, ct);
        await ApplySchemaPatchesAsync(db, ct);
        await EnsureCategoriesAsync(db, ct);

        if (!await db.Suppliers.AnyAsync(ct))
        {
            var supplierCsv = new Domain.Supplier("SUPA", "Supplier A (CSV)", Domain.IntegrationType.Csv, "SUPA-v3");
            var supplierEdi = new Domain.Supplier("SUPB", "Supplier B (EDI)", Domain.IntegrationType.Edi, "SUPB-v1");
            var supplierRest = new Domain.Supplier("DEMO01", "Supplier-Demo-01 (REST)", Domain.IntegrationType.Rest, "DJ-v1");
            db.Suppliers.AddRange(supplierCsv, supplierEdi, supplierRest);
            await db.SaveChangesAsync(ct);

            var hvac = await db.Categories.FirstAsync(c => c.Code == "HVAC", ct);
            var pump = new Domain.Product("HP-8KW-DEMO", "Thermora Aero 8kW Air Source Heat Pump", "Monobloc air source heat pump, R32", supplierCsv.Id);
            pump.SetIdentity("Thermora", "TH-AERO-8", "5060000000008"[..12] + Domain.Gtin.CheckDigit("506000000000"));
            pump.Activate();
            pump.Prices.Add(Domain.ProductPrice.For(pump, "GBP", 1299.99m, DateTime.UtcNow));
            pump.InventoryRecords.Add(Domain.Inventory.For(pump, supplierCsv.Id, 12));
            pump.ProductCategories.Add(new Domain.ProductCategory { CategoryId = hvac.Id });
            db.Products.Add(pump);
            await db.SaveChangesAsync(ct);
        }

        await HvacSeed.SeedAsync(db, ct);
        await BackfillOffersAsync(db, ct);
    }

    /// <summary>
    /// One-time backfill: products imported before supplier offers existed get an offer for their
    /// source supplier (SKU, availability from stock). Batched so multi-million catalogues stay responsive.
    /// </summary>
    private static async Task BackfillOffersAsync(CatalogueDbContext db, CancellationToken ct)
    {
        // A completion marker (not "any offers exist") so an interrupted backfill resumes on next start.
        if (!db.Database.IsSqlServer()) return;
        var done = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS [Value] FROM dbo.SchemaMarkers WHERE Name = 'offers-backfill'").SingleAsync(ct);
        if (done > 0) return;
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        int inserted;
        do
        {
            inserted = await db.Database.ExecuteSqlRawAsync("""
                INSERT dbo.SupplierOffers (ProductId, SupplierId, SupplierSku, Cost, Currency, LeadTimeDays, MinOrderQuantity, PackSize, Availability, IsPreferred, UpdatedAtUtc)
                SELECT TOP (500000) p.Id, p.SupplierId, p.Sku, NULL, 'GBP', NULL, 1, 1,
                       CASE WHEN i.Quantity IS NULL THEN 'Unknown' WHEN i.Quantity > 20 THEN 'InStock' WHEN i.Quantity > 0 THEN 'LowStock' ELSE 'OnOrder' END,
                       1, SYSUTCDATETIME()
                FROM dbo.Products p
                LEFT JOIN dbo.Inventories i ON i.ProductId = p.Id AND i.SupplierId = p.SupplierId
                WHERE p.SupplierId IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM dbo.SupplierOffers o WHERE o.SupplierId = p.SupplierId AND o.SupplierSku = p.Sku)
                """, ct);
        } while (inserted > 0);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT dbo.SchemaMarkers (Name, AppliedAtUtc) VALUES ('offers-backfill', SYSUTCDATETIME())", ct);
    }

    /// <summary>
    /// Idempotent DDL for objects added after a database was first created (the learning build uses
    /// EnsureCreated rather than migrations, which never alters an existing schema).
    /// </summary>
    private static async Task ApplySchemaPatchesAsync(CatalogueDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return;
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10)); // index builds on multi-million row tables
        await db.Database.ExecuteSqlRawAsync("""
            IF OBJECT_ID(N'dbo.SchemaMarkers', N'U') IS NULL
                CREATE TABLE dbo.SchemaMarkers (Name nvarchar(100) NOT NULL PRIMARY KEY, AppliedAtUtc datetime2 NOT NULL);
            IF OBJECT_ID(N'dbo.ImportStreamChunks', N'U') IS NULL
                CREATE TABLE dbo.ImportStreamChunks (
                    ImportId bigint NOT NULL,
                    ChunkIndex int NOT NULL,
                    Rows int NOT NULL,
                    Errors int NOT NULL,
                    AppliedAtUtc datetime2 NOT NULL,
                    CONSTRAINT PK_ImportStreamChunks PRIMARY KEY (ImportId, ChunkIndex));
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Products_Status' AND object_id = OBJECT_ID(N'dbo.Products'))
                CREATE INDEX IX_Products_Status ON dbo.Products (Status);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Products_Sku' AND object_id = OBJECT_ID(N'dbo.Products'))
                CREATE INDEX IX_Products_Sku ON dbo.Products (Sku);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Products_UpdatedAtUtc' AND object_id = OBJECT_ID(N'dbo.Products'))
                CREATE INDEX IX_Products_UpdatedAtUtc ON dbo.Products (UpdatedAtUtc);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProductPrices_Active' AND object_id = OBJECT_ID(N'dbo.ProductPrices'))
                CREATE INDEX IX_ProductPrices_Active ON dbo.ProductPrices (ProductId, Currency) INCLUDE (Amount) WHERE ValidToUtc IS NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Inventories_Product_Supplier' AND object_id = OBJECT_ID(N'dbo.Inventories'))
                CREATE INDEX IX_Inventories_Product_Supplier ON dbo.Inventories (ProductId, SupplierId) INCLUDE (Quantity);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ImportItemErrors_Import_Row' AND object_id = OBJECT_ID(N'dbo.ImportItemErrors'))
                CREATE INDEX IX_ImportItemErrors_Import_Row ON dbo.ImportItemErrors (ImportId, RowNumber);
            """, ct);

        // Concurrent Event Hub partitions insert adjacent SKU ranges. With page locks, one chunk's IX
        // page lock blocks another chunk's FK check (S) on the same page and they deadlock; forcing
        // row locks (and no escalation to table locks) lets the partitions write in parallel.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER INDEX ALL ON dbo.Products SET (ALLOW_PAGE_LOCKS = OFF);
            ALTER INDEX ALL ON dbo.ProductPrices SET (ALLOW_PAGE_LOCKS = OFF);
            ALTER INDEX ALL ON dbo.Inventories SET (ALLOW_PAGE_LOCKS = OFF);
            ALTER INDEX ALL ON dbo.ProductCategories SET (ALLOW_PAGE_LOCKS = OFF);
            ALTER TABLE dbo.Products SET (LOCK_ESCALATION = DISABLE);
            ALTER TABLE dbo.ProductPrices SET (LOCK_ESCALATION = DISABLE);
            ALTER TABLE dbo.Inventories SET (LOCK_ESCALATION = DISABLE);
            ALTER TABLE dbo.ProductCategories SET (LOCK_ESCALATION = DISABLE);
            ALTER TABLE dbo.ImportItemErrors SET (LOCK_ESCALATION = DISABLE);
            ALTER INDEX ALL ON dbo.ProductAttributeValues SET (ALLOW_PAGE_LOCKS = OFF);
            ALTER INDEX ALL ON dbo.SupplierOffers SET (ALLOW_PAGE_LOCKS = OFF);
            ALTER INDEX ALL ON dbo.ProductChangeRequests SET (ALLOW_PAGE_LOCKS = OFF);
            ALTER TABLE dbo.ProductAttributeValues SET (LOCK_ESCALATION = DISABLE);
            -- Every chunk appends new ProductIds to the end of these clustered indexes; on SQL 2019+
            -- this relieves last-page insert latch contention between concurrent partitions.
            IF CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 15
            BEGIN
                ALTER INDEX PK_ProductAttributeValues ON dbo.ProductAttributeValues SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);
                ALTER INDEX PK_SupplierOffers ON dbo.SupplierOffers SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);
                ALTER INDEX PK_Products ON dbo.Products SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);
                ALTER INDEX PK_ProductPrices ON dbo.ProductPrices SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);
                ALTER INDEX PK_Inventories ON dbo.Inventories SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);
                ALTER INDEX PK_ProductCategories ON dbo.ProductCategories SET (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);
            END
            ALTER TABLE dbo.SupplierOffers SET (LOCK_ESCALATION = DISABLE);
            ALTER TABLE dbo.ProductChangeRequests SET (LOCK_ESCALATION = DISABLE);
            """, ct);
    }

    /// <summary>Ensures the category codes used by the EDI generator exist (adds only missing ones).</summary>
    private static async Task EnsureCategoriesAsync(CatalogueDbContext db, CancellationToken ct)
    {
        var wanted = new Dictionary<string, string>
        {
            ["HVAC"] = "Heating, Ventilation & Air Conditioning",
            ["CONTROLS"] = "Controls & Thermostats",
            ["BOILERS"] = "Boilers",
            ["RADIATORS"] = "Radiators & Towel Rails",
            ["PLUMBING"] = "Plumbing & Fittings",
            ["ELECTRICAL"] = "Electrical",
            ["VENTILATION"] = "Ventilation",
            ["SOLAR"] = "Solar & Storage",
            ["WATER-HEATING"] = "Water Heating",
            ["TOOLS"] = "Tools & Equipment",
        };
        var existing = await db.Categories.Select(c => c.Code).ToListAsync(ct);
        var missing = wanted.Where(w => !existing.Contains(w.Key)).ToList();
        if (missing.Count == 0) return;
        db.Categories.AddRange(missing.Select(m => new Domain.Category(m.Key, m.Value)));
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>The storage backend chosen at startup (logged so it is obvious where bytes go).</summary>
public sealed record StorageSelection(bool UseBlob, string? ConnectionString, string LocalRoot)
{
    public static StorageSelection Resolve(IConfiguration config, string localRoot, ILoggerFactory logs)
    {
        var log = logs.CreateLogger("Storage");
        var conn = config.GetConnectionString("BlobStorage");
        var want = config.GetValue("FileStorage:UseBlob", true) && !string.IsNullOrWhiteSpace(conn);
        if (want && BlobOptions.IsReachable(conn!, TimeSpan.FromSeconds(3)))
        {
            log.LogInformation("Storage: Azure Blob ({Endpoint}). Local Azurite keeps its data in the project's .azurite folder.",
                conn!.Contains("UseDevelopmentStorage") ? "Azurite 127.0.0.1:10000" : "configured account");
            return new StorageSelection(true, conn, localRoot);
        }
        if (want)
            log.LogWarning("Storage: Blob endpoint unreachable - falling back to local disk at {Root}. Start Azurite with 'docker compose up -d azurite'.", localRoot);
        else
            log.LogInformation("Storage: local disk at {Root}.", localRoot);
        return new StorageSelection(false, null, localRoot);
    }
}
