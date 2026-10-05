using System.Globalization;
using System.Runtime.CompilerServices;
using Catalogue.Application;
using Catalogue.Contracts;

namespace Catalogue.Infrastructure.Parsing;

/// <summary>
/// Parses supplier CSV files with header: sku,name,description,category,price,currency,quantity
/// plus optional HVAC columns: brand,mpn,gtin,cost,lead_time_days,moq,pack_size,discontinued,attr.&lt;CODE&gt;
/// </summary>
public sealed class CsvSupplierParser : ISupplierFileParser
{
    public bool CanHandle(string format) => format.Equals("CSV", StringComparison.OrdinalIgnoreCase);

    public async IAsyncEnumerable<ParsedRow> ParseAsync(Stream source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(source);
        var header = await reader.ReadLineAsync(ct);
        if (header is null) yield break;

        var columns = Split(header).Select(c => c.Trim().ToLowerInvariant()).ToArray();
        int rowNumber = 1; // header is row 1
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            ct.ThrowIfCancellationRequested();

            var fields = Split(line);
            string Get(string name) =>
                Array.IndexOf(columns, name) is var i && i >= 0 && i < fields.Length
                    ? fields[i].Trim()
                    : "";

            // Unparseable numbers become -1 so they surface as validation errors instead of vanishing.
            decimal? Dec(string col) => Get(col) is { Length: > 0 } v
                ? decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : -1
                : null;
            int? Int(string col) => Get(col) is { Length: > 0 } v
                ? int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1
                : null;

            // Specification columns: "attr.CAPACITY_KW" or "attr:CAPACITY_KW".
            Dictionary<string, string>? attributes = null;
            for (var i = 0; i < columns.Length && i < fields.Length; i++)
            {
                if (!columns[i].StartsWith("attr.") && !columns[i].StartsWith("attr:")) continue;
                var value = fields[i].Trim();
                if (value.Length == 0) continue;
                (attributes ??= new(StringComparer.OrdinalIgnoreCase))[columns[i][5..].ToUpperInvariant()] = value;
            }

            var row = new CanonicalProductImport(
                Sku: Get("sku"),
                Name: Get("name"),
                Description: NullIfEmpty(Get("description")),
                CategoryCode: NullIfEmpty(Get("category")),
                Price: Dec("price"),
                Currency: NullIfEmpty(Get("currency")),
                Quantity: Int("quantity"))
            {
                Brand = NullIfEmpty(Get("brand")),
                ManufacturerPartNumber = NullIfEmpty(Get("mpn")),
                Gtin = NullIfEmpty(Get("gtin")),
                Cost = Dec("cost"),
                LeadTimeDays = Int("lead_time_days"),
                MinOrderQuantity = Int("moq"),
                PackSize = Int("pack_size"),
                Discontinued = Get("discontinued").Equals("true", StringComparison.OrdinalIgnoreCase) || Get("discontinued") == "1",
                Attributes = (IReadOnlyDictionary<string, string>?)attributes ?? new Dictionary<string, string>(),
                Media = MediaColumns(Get)
            };

            yield return new ParsedRow(rowNumber, row, line);
        }
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary>image_url / datasheet_url / manual_url columns; several links separated by '|'.</summary>
    private static IReadOnlyList<MediaReference> MediaColumns(Func<string, string> get)
    {
        List<MediaReference>? list = null;
        foreach (var (col, kind) in new[] { ("image_url", "Image"), ("datasheet_url", "Datasheet"), ("manual_url", "Manual") })
            foreach (var url in get(col).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                (list ??= []).Add(new MediaReference(kind, url));
        return (IReadOnlyList<MediaReference>?)list ?? [];
    }

    /// <summary>Minimal RFC-4180-style splitter (quoted fields with commas/double quotes).</summary>
    public static string[] Split(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}
