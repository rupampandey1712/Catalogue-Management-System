using System.Text;
using Catalogue.Application;
using Catalogue.Contracts;
using Catalogue.Domain;
using Catalogue.Infrastructure.Parsing;
using Catalogue.Infrastructure.Streaming;

namespace Catalogue.UnitTests;

public class HvacDomainTests
{
    [Theory]
    [InlineData("5060000000009", true)]     // generator GTIN-13 for item 0
    [InlineData("5060000000016", true)]
    [InlineData("5060000000019", false)]    // wrong check digit
    [InlineData("96385074", true)]          // GTIN-8
    [InlineData("036000291452", true)]      // UPC-A / GTIN-12
    [InlineData("12345", false)]
    [InlineData("50600000000AB", false)]
    public void Gtin_check_digit_is_validated(string gtin, bool valid) => Assert.Equal(valid, Gtin.IsValid(gtin));

    [Fact]
    public void Gtin_is_normalised_to_14_digits_so_13_and_14_digit_forms_match()
        => Assert.Equal(Gtin.Normalize("05060000000009"), Gtin.Normalize("5060000000009"));

    [Fact]
    public void Generator_gtins_are_valid_and_stable_per_item()
    {
        Assert.True(Gtin.IsValid(EdiSampleGenerator.GtinFor(123_456)));
        Assert.Equal(EdiSampleGenerator.GtinFor(42), EdiSampleGenerator.GtinFor(42));
        Assert.NotEqual(EdiSampleGenerator.GtinFor(42), EdiSampleGenerator.GtinFor(43));
    }

    [Fact]
    public void Attribute_values_are_typed_and_validated()
    {
        var kw = new AttributeDefinition("CAPACITY_KW", "Output", AttributeDataType.Number, "kW", min: 0.5m, max: 100m);
        var refrigerant = new AttributeDefinition("REFRIGERANT", "Refrigerant", AttributeDataType.Enum, allowedValues: ["R32", "R290"]);
        var heatPumpReady = new AttributeDefinition("HP_READY", "Heat pump ready", AttributeDataType.Boolean);

        Assert.Null(kw.TryParse("8.5", out var v));
        Assert.Equal(8.5m, v.Number);
        Assert.Contains("above the maximum", kw.TryParse("250", out _));
        Assert.Contains("not a number", kw.TryParse("eight", out _));

        Assert.Null(refrigerant.TryParse("r32", out var r));
        Assert.Equal("R32", r.Text); // canonical spelling
        Assert.Contains("not allowed", refrigerant.TryParse("R22", out _));

        Assert.Null(heatPumpReady.TryParse("yes", out var b));
        Assert.True(b.Bool);
        Assert.Equal("8.5 kW", v.Display("kW"));
    }

    [Fact]
    public void Enum_attribute_requires_allowed_values()
        => Assert.Throws<ArgumentException>(() => new AttributeDefinition("X", "X", AttributeDataType.Enum));

    [Theory]
    [InlineData(500, null, Availability.InStock)]
    [InlineData(5, null, Availability.LowStock)]
    [InlineData(0, 14, Availability.OnOrder)]
    [InlineData(null, null, Availability.Unknown)]
    public void Availability_is_derived_from_stock_and_lead_time(int? qty, int? lead, Availability expected)
        => Assert.Equal(expected, AvailabilityRules.Derive(qty, lead));

    [Fact]
    public async Task Edi_parser_reads_hvac_segments_including_escaped_values()
    {
        var edi = "UNH+1+PRICAT'" +
                  "LIN+HP-1+Thermora 8kW Heat Pump+HVAC'" +
                  "PIA+1+TH-AERO-8:MF'PIA+1+5060000000009:SRV'" +
                  "IMD+F+BRN+:::Thermora'IMD+F++:::Quiet?: R32 unit'" +
                  "CAV+CAPACITY_KW:8'CAV+ENERGY_CLASS:A?+?+?+'" +
                  "PRI+AAA:4200.00:GBP'PRI+AAB:2900.50:GBP'" +
                  "QTY+47:0'QTY+53:2'QTY+52:1'DTM+169:14:804'STS+DIS'" +
                  "UNT+15+1'";
        var rows = new List<ParsedRow>();
        await foreach (var r in new EdiSupplierParser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(edi)), default))
            rows.Add(r);

        var row = Assert.Single(rows).Row;
        Assert.Equal("Thermora", row.Brand);
        Assert.Equal("TH-AERO-8", row.ManufacturerPartNumber);
        Assert.Equal("5060000000009", row.Gtin);
        Assert.Equal("Quiet: R32 unit", row.Description);
        Assert.Equal("A+++", row.Attributes["ENERGY_CLASS"]);
        Assert.Equal("8", row.Attributes["CAPACITY_KW"]);
        Assert.Equal(4200m, row.Price);
        Assert.Equal(2900.50m, row.Cost);
        Assert.Equal(0, row.Quantity);
        Assert.Equal(2, row.MinOrderQuantity);
        Assert.Equal(14, row.LeadTimeDays);
        Assert.True(row.Discontinued);
    }

    [Fact]
    public async Task Csv_parser_reads_hvac_columns_and_attr_columns()
    {
        var csv = "sku,name,category,price,brand,mpn,gtin,cost,lead_time_days,moq,pack_size,attr.CAPACITY_KW,attr.REFRIGERANT\n" +
                  "HP-9,Heat Pump 9kW,HVAC,3100,Aerivo,AE-9,5060000000016,2100,5,1,1,9,R290\n";
        var rows = new List<ParsedRow>();
        await foreach (var r in new CsvSupplierParser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), default))
            rows.Add(r);
        var row = Assert.Single(rows).Row;
        Assert.Equal("Aerivo", row.Brand);
        Assert.Equal(2100m, row.Cost);
        Assert.Equal(5, row.LeadTimeDays);
        Assert.Equal("R290", row.Attributes["REFRIGERANT"]);
        Assert.Equal("9", row.Attributes["CAPACITY_KW"]);
    }

    [Fact]
    public void Row_validator_checks_specs_against_the_category_template()
    {
        var refData = StreamingTests.StaticReferenceData.Data;
        CanonicalProductImport Row(Dictionary<string, string> attrs, string? gtin = null) =>
            new("HP-1", "Pump", null, "HVAC", 100, "GBP", 1) { Attributes = attrs, Gtin = gtin };

        var ok = HvacRowValidator.Validate(Row(new() { ["CAPACITY_KW"] = "8", ["REFRIGERANT"] = "R32" }), refData, 1, out var attrs);
        Assert.Empty(ok);
        Assert.Equal(2, attrs.Count);

        var bad = HvacRowValidator.Validate(Row(new()
        {
            ["REFRIGERANT"] = "R22",          // not allowed
            ["PIPE_SIZE_MM"] = "15",          // plumbing spec on an HVAC product
            ["COLOUR"] = "Red"                // unknown attribute
        }, gtin: "5060000000019"), refData, 1, out _);
        Assert.Contains(bad, e => e.Field == "attr.REFRIGERANT");
        Assert.Contains(bad, e => e.Field == "attr.PIPE_SIZE_MM" && e.Message.Contains("does not apply"));
        Assert.Contains(bad, e => e.Field == "attr.COLOUR");
        Assert.Contains(bad, e => e.Field == "Gtin");
    }

    [Fact]
    public void Price_approval_threshold_is_percentage_based()
    {
        Assert.False(ProductChangeRequest.NeedsPriceApproval(null, 100, 500));
        Assert.False(ProductChangeRequest.NeedsPriceApproval(15, 100, 114));
        Assert.True(ProductChangeRequest.NeedsPriceApproval(15, 100, 116));
        Assert.True(ProductChangeRequest.NeedsPriceApproval(15, 100, 80));
    }

    [Fact]
    public void Change_requests_can_only_be_decided_once()
    {
        var product = new Product("SKU-1", "Thing");
        var request = ProductChangeRequest.ForPriceChange(product, 1, null, "GBP", 100, 130);
        Assert.Equal(30m, request.ChangePercent);
        request.Decide(true, "admin", null);
        Assert.Equal(ChangeStatus.Approved, request.Status);
        Assert.Throws<InvalidOperationException>(() => request.Decide(false, "admin", null));
    }

    [Fact]
    public void Category_cannot_be_moved_under_its_own_descendant()
    {
        var parent = new Category("HEATING", "Heating");
        typeof(Category).GetProperty(nameof(Category.Id))!.SetValue(parent, 5L);
        Assert.Throws<InvalidOperationException>(() => parent.MoveTo(9, ancestorsOfNewParent: [9, 5]));
        parent.MoveTo(null, []);
        Assert.Null(parent.ParentCategoryId);
    }
}

public class PriceResolverTests
{
    private static PriceList List(long id, string code, PriceListKind kind, decimal discount, long? parent = null)
    {
        var l = new PriceList(code, code, kind, discount, parent);
        typeof(PriceList).GetProperty(nameof(PriceList.Id))!.SetValue(l, id);
        return l;
    }

    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly PriceList Trade = List(1, "TRADE", PriceListKind.Trade, 12);
    private static readonly PriceList Gold = List(2, "GOLD", PriceListKind.Tier, 20, 1);
    private static readonly PriceList Customer = List(3, "CUST", PriceListKind.Customer, 0, 2);
    private static readonly Dictionary<long, PriceList> All = new() { [1] = Trade, [2] = Gold, [3] = Customer };

    [Fact]
    public void Chain_follows_parents_and_stops_on_cycles()
    {
        Assert.Equal(["CUST", "GOLD", "TRADE"], PriceResolver.Chain(3, All).Select(l => l.Code));
        var a = List(10, "A", PriceListKind.Tier, 5, 11);
        var b = List(11, "B", PriceListKind.Tier, 5, 10);
        Assert.Equal(2, PriceResolver.Chain(10, new Dictionary<long, PriceList> { [10] = a, [11] = b }).Count);
    }

    [Fact]
    public void Explicit_quantity_break_wins_with_the_highest_qualifying_break()
    {
        var entries = new List<PriceListEntry>
        {
            new(3, 99, 1, 1049m, Now.AddDays(-1)),
            new(3, 99, 5, 995m, Now.AddDays(-1)),
            new(3, 99, 10, 949m, Now.AddDays(-1)),
        };
        var chain = PriceResolver.Chain(3, All);
        Assert.Equal(1049m, PriceResolver.Resolve(99, 1299.99m, "GBP", chain, entries, 1, Now).NetUnitPrice);
        Assert.Equal(995m, PriceResolver.Resolve(99, 1299.99m, "GBP", chain, entries, 7, Now).NetUnitPrice);
        var q = PriceResolver.Resolve(99, 1299.99m, "GBP", chain, entries, 12, Now);
        Assert.Equal(949m, q.NetUnitPrice);
        Assert.Equal("CUST", q.PriceListCode);
        Assert.Equal(3, q.Breaks.Count);
        Assert.Equal(949m * 12, q.LineTotal);
    }

    [Fact]
    public void Falls_back_through_the_chain_to_the_first_discount()
    {
        // Customer list has no entry for this product and 0% discount -> Gold's 20% applies.
        var q = PriceResolver.Resolve(7, 100m, "GBP", PriceResolver.Chain(3, All), [], 1, Now);
        Assert.Equal(80m, q.NetUnitPrice);
        Assert.Equal("GOLD", q.PriceListCode);
        Assert.Equal(20m, q.SavingPercent);
    }

    [Fact]
    public void Expired_entries_and_breaks_above_the_quantity_are_ignored()
    {
        var entries = new List<PriceListEntry>
        {
            new(2, 7, 1, 50m, Now.AddDays(-30), Now.AddDays(-1)), // expired
            new(2, 7, 100, 40m, Now.AddDays(-1)),                 // break above qty
        };
        var q = PriceResolver.Resolve(7, 100m, "GBP", PriceResolver.Chain(2, All), entries, 5, Now);
        Assert.Equal(80m, q.NetUnitPrice); // Gold 20% discount
    }

    [Fact]
    public void Without_a_price_list_the_list_price_stands()
    {
        var q = PriceResolver.Resolve(7, 100m, "GBP", [], [], 3, Now);
        Assert.Equal(100m, q.NetUnitPrice);
        Assert.Equal("List price", q.Source);
        Assert.Null(q.PriceListCode);
    }
}
