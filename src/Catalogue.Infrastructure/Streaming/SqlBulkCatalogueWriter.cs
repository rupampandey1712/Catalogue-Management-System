using System.Data;
using Catalogue.Application.Streaming;
using Microsoft.Data.SqlClient;
using Catalogue.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Polly;

namespace Catalogue.Infrastructure.Streaming;

/// <summary>
/// High-volume writer: one round trip per chunk. Rows are SqlBulkCopy'd into a temp table and
/// applied with set-based MERGE/UPDATE/INSERT, all inside one transaction together with a
/// (ImportId, ChunkIndex) ledger row - so a redelivered Event Hubs event is a no-op.
/// EF change tracking is bypassed entirely; that is what lets millions of rows flow through.
/// </summary>
public sealed class SqlBulkCatalogueWriter(string connectionString, ILogger<SqlBulkCatalogueWriter> logger)
    : IBulkCatalogueWriter
{
    private const int ProductStatusActive = 1;          // ProductStatus.Active (stored as int)
    private const int ProductStatusPendingApproval = 3; // ProductStatus.PendingApproval

    private static readonly string[] RowColumns =
        ["Sku", "Name", "Description", "CategoryId", "Price", "Currency", "Quantity",
         "Brand", "Mpn", "Gtin", "Cost", "LeadTimeDays", "Moq", "PackSize", "Availability"];
    private readonly ResiliencePipeline _pipeline =
        TransientFaults.SqlWritePipeline(msg => logger.LogWarning("Bulk writer resilience: {Event}", msg));

    public Task<bool> WriteChunkAsync(BulkChunk chunk, CancellationToken ct)
        // No HOLDLOCK (key-range locks serialise and deadlock concurrent partitions); the unique index
        // guards correctness instead. A rare deadlock or same-SKU insert race is retried by the pipeline -
        // the whole transaction, ledger row included, was rolled back. Outages trip the circuit breaker.
        => _pipeline.ExecuteAsync(async token => await WriteOnceAsync(chunk, token), ct).AsTask();

    private async Task<bool> WriteOnceAsync(BulkChunk chunk, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // 1. Ledger first: a duplicate key means this chunk was already applied.
        try
        {
            await Exec(conn, tx, "INSERT dbo.ImportStreamChunks (ImportId, ChunkIndex, Rows, Errors, AppliedAtUtc) " +
                                 "VALUES (@i, @c, @r, @e, SYSUTCDATETIME())", ct,
                ("@i", chunk.ImportId), ("@c", chunk.ChunkIndex), ("@r", chunk.Rows.Count), ("@e", chunk.ErrorRowCount));
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        // 2. Stage rows + specification values.
        if (chunk.Rows.Count > 0)
        {
            await Exec(conn, tx, """
                CREATE TABLE #rows (
                    Sku nvarchar(64) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
                    Name nvarchar(256) NOT NULL,
                    Description nvarchar(max) NULL,
                    CategoryId bigint NULL,
                    Price decimal(18,2) NULL,
                    Currency nvarchar(3) COLLATE DATABASE_DEFAULT NOT NULL,
                    Quantity int NULL,
                    Brand nvarchar(100) NULL,
                    Mpn nvarchar(64) NULL,
                    Gtin varchar(14) COLLATE DATABASE_DEFAULT NULL,
                    Cost decimal(18,2) NULL,
                    LeadTimeDays int NULL,
                    Moq int NULL,
                    PackSize int NULL,
                    Availability nvarchar(16) NOT NULL,
                    ProductId bigint NULL,
                    IsForeign bit NOT NULL DEFAULT 0,
                    IsNew bit NOT NULL DEFAULT 0,
                    PriceHeld bit NOT NULL DEFAULT 0);
                CREATE TABLE #attrs (
                    Sku nvarchar(64) COLLATE DATABASE_DEFAULT NOT NULL,
                    AttributeDefinitionId bigint NOT NULL,
                    TextValue nvarchar(200) NULL,
                    NumberValue decimal(18,4) NULL,
                    BoolValue bit NULL,
                    PRIMARY KEY (Sku, AttributeDefinitionId));
                CREATE TABLE #media (
                    Sku nvarchar(64) COLLATE DATABASE_DEFAULT NOT NULL,
                    Kind nvarchar(16) NOT NULL,
                    Url nvarchar(1000) COLLATE DATABASE_DEFAULT NOT NULL);
                """, ct);

            using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, tx)
                   { DestinationTableName = "#rows", BatchSize = 0, BulkCopyTimeout = 120 })
            {
                foreach (var col in RowColumns) bulk.ColumnMappings.Add(col, col);
                await bulk.WriteToServerAsync(ToTable(chunk.Rows), ct);
            }
            if (chunk.Rows.Any(r => r.Attributes.Count > 0))
            {
                using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, tx)
                    { DestinationTableName = "#attrs", BatchSize = 0, BulkCopyTimeout = 120 };
                foreach (var col in new[] { "Sku", "AttributeDefinitionId", "TextValue", "NumberValue", "BoolValue" })
                    bulk.ColumnMappings.Add(col, col);
                await bulk.WriteToServerAsync(ToAttributeTable(chunk.Rows), ct);
            }

            if (chunk.Rows.Any(r => r.Media.Count > 0))
            {
                using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, tx)
                    { DestinationTableName = "#media", BatchSize = 0, BulkCopyTimeout = 120 };
                foreach (var col in new[] { "Sku", "Kind", "Url" }) bulk.ColumnMappings.Add(col, col);
                await bulk.WriteToServerAsync(ToMediaTable(chunk.Rows), ct);
            }

            // 3. Apply. Matching order: this supplier's existing offer → same GTIN offered by another
            //    supplier (consolidation: attach an offer, don't duplicate the product) → own product by SKU.
            //    Product content (name, specs, list price) is owned by the supplier that created it.
            await Exec(conn, tx, """
                DECLARE @now datetime2 = SYSUTCDATETIME();
                DECLARE @ids TABLE (ProductId bigint NOT NULL, Sku nvarchar(64) COLLATE DATABASE_DEFAULT NOT NULL, Act nvarchar(10) NOT NULL);

                UPDATE r SET r.ProductId = o.ProductId,
                             r.IsForeign = CASE WHEN p.SupplierId = @supplierId THEN 0 ELSE 1 END
                FROM #rows r
                JOIN dbo.SupplierOffers o ON o.SupplierId = @supplierId AND o.SupplierSku = r.Sku
                JOIN dbo.Products p ON p.Id = o.ProductId;

                UPDATE r SET r.ProductId = g.Id, r.IsForeign = 1
                FROM #rows r
                CROSS APPLY (SELECT TOP (1) p.Id FROM dbo.Products p
                             WHERE p.Gtin = r.Gtin AND (p.SupplierId IS NULL OR p.SupplierId <> @supplierId)
                             ORDER BY p.Id) g
                WHERE r.ProductId IS NULL AND r.Gtin IS NOT NULL;

                -- Owned products. Status is preserved on update (a feed never re-activates a product an
                -- admin deactivated); new products start Active or PendingApproval per supplier policy.
                MERGE dbo.Products AS t
                USING (SELECT * FROM #rows WHERE IsForeign = 0) AS s ON t.SupplierId = @supplierId AND t.Sku = s.Sku
                WHEN MATCHED THEN UPDATE SET
                    t.Name = s.Name,
                    t.Description = COALESCE(s.Description, t.Description),
                    t.Brand = COALESCE(s.Brand, t.Brand),
                    t.ManufacturerPartNumber = COALESCE(s.Mpn, t.ManufacturerPartNumber),
                    t.Gtin = COALESCE(s.Gtin, t.Gtin),
                    t.UpdatedAtUtc = @now
                WHEN NOT MATCHED THEN
                    INSERT (Sku, Name, Description, Status, SupplierId, CreatedAtUtc, UpdatedAtUtc, Brand, ManufacturerPartNumber, Gtin)
                    VALUES (s.Sku, s.Name, s.Description, @newStatus, @supplierId, @now, @now, s.Brand, s.Mpn, s.Gtin)
                OUTPUT inserted.Id, inserted.Sku, $action INTO @ids;

                UPDATE r SET r.ProductId = i.ProductId, r.IsNew = CASE WHEN i.Act = 'INSERT' THEN 1 ELSE 0 END
                FROM #rows r JOIN @ids i ON i.Sku = r.Sku;

                IF @requireNewApproval = 1
                    INSERT dbo.ProductChangeRequests (ProductId, SupplierId, ImportId, Kind, Summary, Status, CreatedAtUtc)
                    SELECT r.ProductId, @supplierId, @importId, 'NewProduct', LEFT(CONCAT(N'New product ', r.Sku, N' – ', r.Name), 400), 'Pending', @now
                    FROM #rows r WHERE r.IsNew = 1;

                -- List-price moves beyond the supplier's threshold are held for approval instead of applied.
                IF @priceThreshold IS NOT NULL
                BEGIN
                    UPDATE r SET r.PriceHeld = 1
                    FROM #rows r
                    JOIN dbo.ProductPrices pp ON pp.ProductId = r.ProductId AND pp.Currency = r.Currency AND pp.ValidToUtc IS NULL
                    WHERE r.IsForeign = 0 AND r.IsNew = 0 AND r.Price IS NOT NULL AND pp.Amount > 0
                      AND ABS(r.Price - pp.Amount) * 100.0 / pp.Amount > @priceThreshold;

                    INSERT dbo.ProductChangeRequests (ProductId, SupplierId, ImportId, Kind, Currency, OldPrice, NewPrice, Summary, Status, CreatedAtUtc)
                    SELECT r.ProductId, @supplierId, @importId, 'PriceChange', r.Currency, pp.Amount, r.Price,
                           CONCAT(N'List price ', FORMAT(pp.Amount, '0.00'), N' → ', FORMAT(r.Price, '0.00'), N' ', r.Currency), 'Pending', @now
                    FROM #rows r
                    JOIN dbo.ProductPrices pp ON pp.ProductId = r.ProductId AND pp.Currency = r.Currency AND pp.ValidToUtc IS NULL
                    WHERE r.PriceHeld = 1 AND NOT EXISTS (
                        SELECT 1 FROM dbo.ProductChangeRequests c
                        WHERE c.ProductId = r.ProductId AND c.Kind = 'PriceChange' AND c.Status = 'Pending' AND c.NewPrice = r.Price);
                END

                UPDATE pp SET pp.ValidToUtc = @now
                FROM dbo.ProductPrices pp
                JOIN #rows r ON pp.ProductId = r.ProductId AND pp.Currency = r.Currency
                WHERE r.IsForeign = 0 AND r.PriceHeld = 0 AND r.Price IS NOT NULL AND pp.ValidToUtc IS NULL AND pp.Amount <> r.Price;

                INSERT dbo.ProductPrices (ProductId, Currency, Amount, ValidFromUtc, ValidToUtc)
                SELECT r.ProductId, r.Currency, r.Price, @now, NULL
                FROM #rows r
                WHERE r.IsForeign = 0 AND r.PriceHeld = 0 AND r.Price IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM dbo.ProductPrices pp
                    WHERE pp.ProductId = r.ProductId AND pp.Currency = r.Currency AND pp.ValidToUtc IS NULL);

                -- Stock is per supplier, so every row (owned or consolidated) updates its own inventory line.
                MERGE dbo.Inventories AS t
                USING (SELECT ProductId, Quantity FROM #rows WHERE Quantity IS NOT NULL) AS s
                    ON t.ProductId = s.ProductId AND t.SupplierId = @supplierId
                WHEN MATCHED AND t.Quantity <> s.Quantity THEN UPDATE SET t.Quantity = s.Quantity, t.UpdatedAtUtc = @now
                WHEN NOT MATCHED THEN INSERT (ProductId, SupplierId, Quantity, UpdatedAtUtc)
                    VALUES (s.ProductId, @supplierId, s.Quantity, @now);

                -- Brand-new products cannot have offers yet: plain INSERT (no match pass). MERGE the rest.
                INSERT dbo.SupplierOffers (ProductId, SupplierId, SupplierSku, Cost, Price, Currency, LeadTimeDays, MinOrderQuantity, PackSize, Availability, IsPreferred, UpdatedAtUtc)
                SELECT s.ProductId, @supplierId, s.Sku, s.Cost, s.Price, s.Currency, s.LeadTimeDays, COALESCE(s.Moq, 1), COALESCE(s.PackSize, 1), s.Availability, 1, @now
                FROM #rows s WHERE s.IsNew = 1;

                MERGE dbo.SupplierOffers AS t
                USING (SELECT * FROM #rows WHERE IsNew = 0) AS s ON t.SupplierId = @supplierId AND t.SupplierSku = s.Sku
                WHEN MATCHED THEN UPDATE SET
                    t.ProductId = s.ProductId,
                    t.Cost = COALESCE(s.Cost, t.Cost),
                    t.Price = COALESCE(s.Price, t.Price),
                    t.Currency = s.Currency,
                    t.LeadTimeDays = COALESCE(s.LeadTimeDays, t.LeadTimeDays),
                    t.MinOrderQuantity = COALESCE(s.Moq, t.MinOrderQuantity),
                    t.PackSize = COALESCE(s.PackSize, t.PackSize),
                    t.Availability = s.Availability,
                    t.UpdatedAtUtc = @now
                WHEN NOT MATCHED THEN
                    INSERT (ProductId, SupplierId, SupplierSku, Cost, Price, Currency, LeadTimeDays, MinOrderQuantity, PackSize, Availability, IsPreferred, UpdatedAtUtc)
                    VALUES (s.ProductId, @supplierId, s.Sku, s.Cost, s.Price, s.Currency, s.LeadTimeDays, COALESCE(s.Moq, 1), COALESCE(s.PackSize, 1),
                            s.Availability, CASE WHEN s.IsForeign = 0 THEN 1 ELSE 0 END, @now);

                INSERT dbo.ProductCategories (ProductId, CategoryId)
                SELECT r.ProductId, r.CategoryId FROM #rows r
                WHERE r.IsForeign = 0 AND r.CategoryId IS NOT NULL AND (r.IsNew = 1 OR NOT EXISTS (
                    SELECT 1 FROM dbo.ProductCategories pc
                    WHERE pc.ProductId = r.ProductId AND pc.CategoryId = r.CategoryId));

                INSERT dbo.ProductAttributeValues (ProductId, AttributeDefinitionId, TextValue, NumberValue, BoolValue)
                SELECT r.ProductId, a.AttributeDefinitionId, a.TextValue, a.NumberValue, a.BoolValue
                FROM #attrs a JOIN #rows r ON r.Sku = a.Sku WHERE r.IsNew = 1;

                MERGE dbo.ProductAttributeValues AS t
                USING (SELECT r.ProductId, a.AttributeDefinitionId, a.TextValue, a.NumberValue, a.BoolValue
                       FROM #attrs a JOIN #rows r ON r.Sku = a.Sku WHERE r.IsForeign = 0 AND r.IsNew = 0) AS s
                    ON t.ProductId = s.ProductId AND t.AttributeDefinitionId = s.AttributeDefinitionId
                WHEN MATCHED AND (ISNULL(t.TextValue, N'') <> ISNULL(s.TextValue, N'')
                               OR ISNULL(t.NumberValue, -1) <> ISNULL(s.NumberValue, -1)
                               OR ISNULL(t.BoolValue, 0) <> ISNULL(s.BoolValue, 0)) THEN
                    UPDATE SET t.TextValue = s.TextValue, t.NumberValue = s.NumberValue, t.BoolValue = s.BoolValue
                WHEN NOT MATCHED THEN
                    INSERT (ProductId, AttributeDefinitionId, TextValue, NumberValue, BoolValue)
                    VALUES (s.ProductId, s.AttributeDefinitionId, s.TextValue, s.NumberValue, s.BoolValue);

                -- Media links are queued for the background fetcher (EDI carries links, not bytes).
                INSERT dbo.MediaFetchRequests (ProductId, SupplierId, Kind, Url, Status, Attempts, NextAttemptUtc, CreatedAtUtc)
                SELECT DISTINCT r.ProductId, @supplierId, m.Kind, m.Url, 'Pending', 0, @now, @now
                FROM #media m JOIN #rows r ON r.Sku = m.Sku
                WHERE NOT EXISTS (SELECT 1 FROM dbo.MediaFetchRequests f WHERE f.ProductId = r.ProductId AND f.Url = m.Url);

                DROP TABLE #media;
                DROP TABLE #attrs;
                DROP TABLE #rows;
                """, ct,
                ("@supplierId", chunk.SupplierId),
                ("@importId", chunk.ImportId),
                ("@newStatus", chunk.Policy.RequireApprovalForNewProducts ? ProductStatusPendingApproval : ProductStatusActive),
                ("@requireNewApproval", chunk.Policy.RequireApprovalForNewProducts),
                ("@priceThreshold", (object?)chunk.Policy.PriceChangeThresholdPercent ?? DBNull.Value));
        }

        // 4. Row-level diagnostics.
        if (chunk.Errors.Count > 0)
        {
            using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
                { DestinationTableName = "dbo.ImportItemErrors", BulkCopyTimeout = 120 };
            foreach (var col in new[] { "ImportId", "RowNumber", "Field", "Message", "RawValue" })
                bulk.ColumnMappings.Add(col, col);
            await bulk.WriteToServerAsync(ToTable(chunk.ImportId, chunk.Errors), ct);
        }

        // 5. Progress counters + completion (set-based, so concurrent partitions never lose updates).
        await Exec(conn, tx, """
            UPDATE dbo.ImportFiles
               SET ProcessedRows = ProcessedRows + @ok, ErrorRows = ErrorRows + @err
             WHERE Id = @importId;
            """ + CompleteSql, ct,
            ("@ok", chunk.Rows.Count), ("@err", chunk.ErrorRowCount), ("@importId", chunk.ImportId));

        await tx.CommitAsync(ct);
        return true;
    }

    private const string CompleteSql = """

        UPDATE dbo.ImportFiles
           SET Status = CASE WHEN ErrorRows = 0 THEN 'Completed' ELSE 'CompletedWithErrors' END,
               CompletedAtUtc = SYSUTCDATETIME()
         WHERE Id = @importId AND Status = 'Processing' AND TotalRows > 0
           AND ProcessedRows + ErrorRows >= TotalRows;
        SELECT @@ROWCOUNT;
        """;

    public async Task<bool> SetTotalAndTryCompleteAsync(long importId, int totalRows, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        if (totalRows == 0)
        {
            await Exec(conn, null, "UPDATE dbo.ImportFiles SET Status = 'Rejected', FailureReason = 'No LIN line items found in the interchange.', CompletedAtUtc = SYSUTCDATETIME() WHERE Id = @importId", ct,
                ("@importId", importId));
            return true;
        }
        await using var cmd = Command(conn, null,
            "UPDATE dbo.ImportFiles SET TotalRows = @total WHERE Id = @importId;" + CompleteSql,
            ("@total", totalRows), ("@importId", importId));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    public async Task ResetStreamAsync(long importId, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await Exec(conn, null, """
            DELETE FROM dbo.ImportStreamChunks WHERE ImportId = @importId;
            WHILE 1 = 1
            BEGIN
                DELETE TOP (50000) FROM dbo.ImportItemErrors WHERE ImportId = @importId;
                IF @@ROWCOUNT = 0 BREAK;
            END
            """, ct, ("@importId", importId));
    }

    private static async Task Exec(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = Command(conn, tx, sql, parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static SqlCommand Command(SqlConnection conn, SqlTransaction? tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private static DataTable ToTable(IReadOnlyList<BulkRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("Sku", typeof(string));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("Description", typeof(string));
        t.Columns.Add("CategoryId", typeof(long));
        t.Columns.Add("Price", typeof(decimal));
        t.Columns.Add("Currency", typeof(string));
        t.Columns.Add("Quantity", typeof(int));
        t.Columns.Add("Brand", typeof(string));
        t.Columns.Add("Mpn", typeof(string));
        t.Columns.Add("Gtin", typeof(string));
        t.Columns.Add("Cost", typeof(decimal));
        t.Columns.Add("LeadTimeDays", typeof(int));
        t.Columns.Add("Moq", typeof(int));
        t.Columns.Add("PackSize", typeof(int));
        t.Columns.Add("Availability", typeof(string));
        static object N(object? v) => v ?? DBNull.Value;
        foreach (var r in rows)
            t.Rows.Add(r.Sku, r.Name, N(r.Description), N(r.CategoryId), N(r.Price), r.Currency, N(r.Quantity),
                N(r.Brand), N(r.Mpn), N(r.Gtin), N(r.Cost), N(r.LeadTimeDays), N(r.MinOrderQuantity), N(r.PackSize), r.Availability);
        return t;
    }

    private static DataTable ToMediaTable(IReadOnlyList<BulkRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("Sku", typeof(string));
        t.Columns.Add("Kind", typeof(string));
        t.Columns.Add("Url", typeof(string));
        foreach (var r in rows)
            foreach (var m in r.Media.DistinctBy(m => m.Url))
                t.Rows.Add(r.Sku, m.Kind, m.Url);
        return t;
    }

    private static DataTable ToAttributeTable(IReadOnlyList<BulkRow> rows)
    {
        var t = new DataTable();
        t.Columns.Add("Sku", typeof(string));
        t.Columns.Add("AttributeDefinitionId", typeof(long));
        t.Columns.Add("TextValue", typeof(string));
        t.Columns.Add("NumberValue", typeof(decimal));
        t.Columns.Add("BoolValue", typeof(bool));
        foreach (var r in rows)
            foreach (var a in r.Attributes.DistinctBy(a => a.AttributeId))
                t.Rows.Add(r.Sku, a.AttributeId, (object?)a.Value.Text ?? DBNull.Value,
                    (object?)a.Value.Number ?? DBNull.Value, (object?)a.Value.Bool ?? DBNull.Value);
        return t;
    }

    private static DataTable ToTable(long importId, IReadOnlyList<BulkError> errors)
    {
        var t = new DataTable();
        t.Columns.Add("ImportId", typeof(long));
        t.Columns.Add("RowNumber", typeof(int));
        t.Columns.Add("Field", typeof(string));
        t.Columns.Add("Message", typeof(string));
        t.Columns.Add("RawValue", typeof(string));
        foreach (var e in errors)
            t.Rows.Add(importId, e.RowNumber, e.Field, e.Message, (object?)e.RawValue ?? DBNull.Value);
        return t;
    }
}
