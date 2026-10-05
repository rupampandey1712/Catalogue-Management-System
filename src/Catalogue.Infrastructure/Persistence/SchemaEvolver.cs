using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Catalogue.Infrastructure.Persistence;

/// <summary>
/// Additive schema evolution for the learning build (which uses EnsureCreated, not migrations):
/// diffs the EF model against the live database and creates missing tables, columns and indexes.
/// It never drops or alters existing objects, so it is safe to run on every start. A production
/// system would use EF migrations or a DACPAC instead.
/// </summary>
public static partial class SchemaEvolver
{
    public static async Task<IReadOnlyList<string>> EvolveAsync(CatalogueDbContext db, CancellationToken ct = default)
    {
        if (!db.Database.IsSqlServer()) return [];
        var applied = new List<string>();

        var tables = (await Query(db, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo'", ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = (await Query(db, "SELECT TABLE_NAME + '.' + COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo'", ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexes = (await Query(db, "SELECT name FROM sys.indexes WHERE name IS NOT NULL", ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var statements = StatementSplit().Split(db.Database.GenerateCreateScript())
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        // 1. Missing tables (EF orders the script by FK dependency).
        foreach (var sql in statements)
        {
            var m = CreateTable().Match(sql);
            if (!m.Success || tables.Contains(m.Groups[1].Value)) continue;
            await db.Database.ExecuteSqlRawAsync(sql, ct);
            tables.Add(m.Groups[1].Value);
            applied.Add($"table {m.Groups[1].Value}");
            foreach (var c in ColumnList(sql)) columns.Add($"{m.Groups[1].Value}.{c}");
        }

        // 2. Missing columns on existing tables, added NULL or NOT NULL with a safe default.
        var model = db.GetService<IDesignTimeModel>().Model;
        foreach (var entity in model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null || !tables.Contains(table)) continue;
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(store);
                if (column is null || columns.Contains($"{table}.{column}")) continue;
                var type = property.GetColumnType(store);
                var sql = property.IsColumnNullable(store)
                    ? $"ALTER TABLE [dbo].[{table}] ADD [{column}] {type} NULL"
                    : $"ALTER TABLE [dbo].[{table}] ADD [{column}] {type} NOT NULL CONSTRAINT [DF_{table}_{column}] DEFAULT ({DefaultFor(property.ClrType, type)})";
                await db.Database.ExecuteSqlRawAsync(sql, ct);
                columns.Add($"{table}.{column}");
                applied.Add($"column {table}.{column}");
            }
        }

        // 3. Missing indexes (new tables or new indexes on existing tables).
        foreach (var sql in statements)
        {
            var m = CreateIndex().Match(sql);
            if (!m.Success || indexes.Contains(m.Groups[1].Value)) continue;
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql, ct);
                indexes.Add(m.Groups[1].Value);
                applied.Add($"index {m.Groups[1].Value}");
            }
            catch (Exception ex) when (ex.GetType().Name == "SqlException")
            {
                applied.Add($"index {m.Groups[1].Value} FAILED: {ex.Message}");
            }
        }
        return applied;
    }

    private static string DefaultFor(Type clr, string sqlType)
    {
        var t = Nullable.GetUnderlyingType(clr) ?? clr;
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "SYSUTCDATETIME()";
        if (sqlType.Contains("char", StringComparison.OrdinalIgnoreCase)) return t.IsEnum ? $"'{Enum.GetNames(t)[0]}'" : "''";
        return "0";
    }

    private static IEnumerable<string> ColumnList(string createTable) =>
        ColumnName().Matches(createTable).Select(m => m.Groups[1].Value);

    private static async Task<List<string>> Query(CatalogueDbContext db, string sql, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<string>(sql.Replace("SELECT ", "SELECT CAST(", StringComparison.Ordinal)
            .Replace(" FROM ", " AS nvarchar(400)) AS [Value] FROM ", StringComparison.Ordinal)).ToListAsync(ct);

    [GeneratedRegex(@";\s*\r?\n|^\s*GO\s*$", RegexOptions.Multiline)]
    private static partial Regex StatementSplit();

    [GeneratedRegex(@"^CREATE TABLE \[(\w+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"^CREATE (?:UNIQUE )?(?:NONCLUSTERED )?INDEX \[(\w+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex CreateIndex();

    [GeneratedRegex(@"^\s+\[(\w+)\]\s", RegexOptions.Multiline)]
    private static partial Regex ColumnName();
}
