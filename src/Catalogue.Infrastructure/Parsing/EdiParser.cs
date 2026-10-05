using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Catalogue.Application;
using Catalogue.Application.Streaming;
using Catalogue.Contracts;

namespace Catalogue.Infrastructure.Parsing;

/// <summary>
/// Simplified EDIFACT PRICAT parser used to simulate EDI in this learning project.
/// Segments (terminator ', element separator +, component separator :):
///   UNH+ref+PRICAT'                message header
///   LIN+sku+name+category'         starts a line item
///   PIA+1+value:MF'                manufacturer part number      (PIA+1+value:SRV = GTIN)
///   IMD+F+BRN+:::brand'            brand                         (IMD+F++:::text = description)
///   PRI+AAA:amount:currency'       list price                    (PRI+AAB = supplier cost)
///   QTY+47:n'                      stock on hand                 (QTY+53 = min order qty, QTY+52 = pack size)
///   DTM+169:days:804'              lead time in days
///   CAV+CODE:value'                specification value, e.g. CAV+CAPACITY_KW:8 (simplified CCI/CAV)
///   IMD+F+IMG+:::url'              product image link        (IMD+F+DOC = datasheet link)
///   STS+DIS'                       item discontinued
///   UNT+count+ref'                 message trailer (flushes the last line item)
/// Segments are streamed, so file size does not affect memory use. Isolated so a real EDI
/// library can replace it later.
/// </summary>
public sealed class EdiSupplierParser : ISupplierFileParser
{
    public bool CanHandle(string format) => format.Equals("EDI", StringComparison.OrdinalIgnoreCase);

    private sealed class Item
    {
        public required string Sku;
        public string? Name, Category, Currency, Description, Brand, Mpn, Gtin;
        public decimal? Price, Cost;
        public int? Qty, Moq, Pack, LeadTime;
        public bool Discontinued;
        public Dictionary<string, string>? Attributes;
        public List<MediaReference>? Media;
        public int SegmentNo;
        public readonly StringBuilder Raw = new();

        public ParsedRow ToRow() => new(SegmentNo,
            new CanonicalProductImport(Sku, Name ?? "", Description, Category, Price, Currency, Qty)
            {
                Brand = Brand, ManufacturerPartNumber = Mpn, Gtin = Gtin, Cost = Cost, LeadTimeDays = LeadTime,
                MinOrderQuantity = Moq, PackSize = Pack, Discontinued = Discontinued,
                Attributes = (IReadOnlyDictionary<string, string>?)Attributes ?? new Dictionary<string, string>(),
                Media = (IReadOnlyList<MediaReference>?)Media ?? []
            }, Raw.ToString());
    }

    public async IAsyncEnumerable<ParsedRow> ParseAsync(Stream source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        Item? item = null;
        var segmentNo = 0;

        await foreach (var segment in EdiSegmentReader.ReadAsync(reader, ct))
        {
            segmentNo++;
            // Elements keep their escapes so ':' inside data survives the component split below.
            var parts = EdiSegmentReader.Split(segment, '+', unescape: false);
            var tag = parts[0];

            if (tag == "LIN")
            {
                if (item is not null) yield return item.ToRow();
                var sku = parts.ElementAtOrDefault(1)?.Trim();
                item = sku is null ? null : new Item
                {
                    Sku = sku,
                    Name = Unescape(parts.ElementAtOrDefault(2)?.Trim()),
                    Category = Unescape(parts.ElementAtOrDefault(3)?.Trim()),
                    SegmentNo = segmentNo
                };
                item?.Raw.Append(segment).Append('\'');
                continue;
            }
            if (tag == "UNT")
            {
                if (item is not null) yield return item.ToRow();
                item = null;
                continue;
            }
            if (item is null) continue;

            item.Raw.Append(segment).Append('\'');
            var c1 = parts.Length > 1 ? EdiSegmentReader.Split(parts[1], ':', unescape: true) : [];
            switch (tag)
            {
                case "IMD" when parts.Length > 3 && parts[2] is "IMG" or "DOC":
                    // IMD+F+IMG+:::https?://... - a link to media hosted by the supplier. Components after
                    // the third are re-joined so an unescaped "https://" still survives.
                    var link = string.Join(':', EdiSegmentReader.Split(parts[3], ':', unescape: true).Skip(3)).Trim();
                    if (link.Length > 0) (item.Media ??= []).Add(new MediaReference(parts[2] == "IMG" ? "Image" : "Datasheet", link));
                    break;
                case "IMD" when parts.Length > 3:
                    var text = EdiSegmentReader.Split(parts[3], ':', unescape: true).LastOrDefault();
                    if (string.IsNullOrWhiteSpace(text)) break;
                    if (parts[2] == "BRN") item.Brand = text; else item.Description = text;
                    break;
                case "PIA" when parts.Length > 2:
                    var id = EdiSegmentReader.Split(parts[2], ':', unescape: true);
                    if (id.Length < 2) break;
                    if (id[1] == "MF") item.Mpn = id[0];
                    else if (id[1] is "SRV" or "EN") item.Gtin = id[0];
                    break;
                case "PRI" when c1.Length >= 2:
                    var amount = decimal.TryParse(c1[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var a) ? a : -1; // -1 surfaces a validation error
                    if (c1[0] == "AAB") item.Cost = amount;
                    else
                    {
                        item.Price = amount;
                        if (c1.Length >= 3 && c1[2].Length > 0) item.Currency = c1[2];
                    }
                    break;
                case "QTY" when c1.Length >= 2:
                    var q = int.TryParse(c1[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
                    switch (c1[0])
                    {
                        case "53": item.Moq = q; break;
                        case "52": item.Pack = q; break;
                        default: item.Qty = q; break;
                    }
                    break;
                case "DTM" when c1.Length >= 2 && c1[0] == "169":
                    item.LeadTime = int.TryParse(c1[1], out var lt) ? lt : -1;
                    break;
                case "CAV" when c1.Length >= 2 && c1[0].Length > 0:
                    (item.Attributes ??= new(StringComparer.OrdinalIgnoreCase))[c1[0].Trim().ToUpperInvariant()] =
                        string.Join(':', c1.Skip(1)).Trim();
                    break;
                case "STS" when parts.Length > 1 && parts[1] == "DIS":
                    item.Discontinued = true;
                    break;
            }
        }

        if (item is not null) yield return item.ToRow(); // tolerate a missing UNT
    }

    private static string? Unescape(string? value)
        => value is null || !value.Contains('?') ? value : string.Concat(EdiSegmentReader.Split(value, '\0', unescape: true));
}
