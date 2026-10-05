using System.Text;
using Catalogue.Infrastructure.Parsing;

namespace Catalogue.UnitTests;

public class EdiParserTests
{
    [Fact]
    public async Task Parses_two_line_items_with_price_and_quantity()
    {
        var edi = "UNH+1+PRICAT'" +
                  "LIN+HP-100+Heat Pump 8kW+HVAC'" +
                  "PRI+AAA:1299.99:GBP'" +
                  "QTY+47:12'" +
                  "LIN+TH-200+Smart Thermostat+CONTROLS'" +
                  "PRI+AAA:89.50:GBP'" +
                  "QTY+47:100'" +
                  "UNT+7+1'";
        var parser = new EdiSupplierParser();
        var rows = new List<Catalogue.Application.ParsedRow>();
        await foreach (var r in parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(edi)), default))
            rows.Add(r);

        Assert.Equal(2, rows.Count);
        Assert.Equal("HP-100", rows[0].Row.Sku);
        Assert.Equal(1299.99m, rows[0].Row.Price);
        Assert.Equal("GBP", rows[0].Row.Currency);
        Assert.Equal(12, rows[0].Row.Quantity);
        Assert.Equal("TH-200", rows[1].Row.Sku);
    }
}
