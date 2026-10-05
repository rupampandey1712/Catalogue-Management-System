using System.Text;
using Catalogue.Infrastructure.Parsing;

namespace Catalogue.UnitTests;

public class CsvParserTests
{
    [Fact]
    public async Task Parses_valid_rows()
    {
        var csv = "sku,name,description,category,price,currency,quantity\n" +
                  "HP-1,Heat Pump,8kW unit,HVAC,999.50,GBP,10\n" +
                  "TH-1,Thermostat,\"Smart, wifi\",CONTROLS,79.99,GBP,50\n";
        var parser = new CsvSupplierParser();
        var rows = new List<Catalogue.Application.ParsedRow>();
        await foreach (var r in parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), default))
            rows.Add(r);

        Assert.Equal(2, rows.Count);
        Assert.Equal("HP-1", rows[0].Row.Sku);
        Assert.Equal(999.50m, rows[0].Row.Price);
        Assert.Equal(10, rows[0].Row.Quantity);
        Assert.Equal("Smart, wifi", rows[1].Row.Description); // quoted comma
    }

    [Fact]
    public async Task Invalid_price_surfaces_as_negative_for_validation()
    {
        var csv = "sku,name,price\nBAD-1,Broken,not-a-number\n";
        var parser = new CsvSupplierParser();
        var rows = new List<Catalogue.Application.ParsedRow>();
        await foreach (var r in parser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), default))
            rows.Add(r);

        Assert.Single(rows);
        Assert.Equal(-1, rows[0].Row.Price);
        Assert.Contains(Catalogue.Application.RowValidator.Validate(rows[0].Row), e => e.Field == "Price");
    }
}
