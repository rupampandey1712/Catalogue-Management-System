using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;
using AT = Catalogue.Domain.AttributeDataType;

namespace Catalogue.Infrastructure.Persistence;

/// <summary>
/// HVAC reference data: category tree, specification attributes with per-category templates,
/// trade price lists, customer accounts and supplier approval policies. Idempotent - only adds
/// what is missing and never overrides changes made through the admin screens.
/// </summary>
public static class HvacSeed
{
    private static readonly (string Code, string Name, AT Type, string? Unit, string[]? Values, decimal? Min, decimal? Max, string? Desc)[] Attributes =
    [
        ("CAPACITY_KW", "Output", AT.Number, "kW", null, 0.5m, 100m, "Rated heating or cooling output"),
        ("REFRIGERANT", "Refrigerant", AT.Enum, null, ["R32", "R290", "R410A", "R454B", "R744", "R134a"], null, null, "F-gas regulations apply; R22 is banned"),
        ("SCOP", "SCOP", AT.Number, null, null, 1m, 8m, "Seasonal coefficient of performance"),
        ("ENERGY_CLASS", "ErP energy class", AT.Enum, null, ["A+++", "A++", "A+", "A", "B", "C", "D"], null, null, null),
        ("PHASE", "Electrical supply", AT.Enum, null, ["Single", "Three"], null, null, "Single or three phase"),
        ("SOUND_DB", "Sound power", AT.Number, "dB(A)", null, 10m, 90m, null),
        ("FUEL", "Fuel", AT.Enum, null, ["Natural gas", "LPG", "Oil", "Electric", "Hydrogen-ready"], null, null, null),
        ("BOILER_TYPE", "Boiler type", AT.Enum, null, ["Combi", "System", "Heat only"], null, null, null),
        ("EFFICIENCY_PCT", "Seasonal efficiency", AT.Number, "%", null, 50m, 120m, "ErP seasonal space heating efficiency"),
        ("HEIGHT_MM", "Height", AT.Number, "mm", null, 100m, 3000m, null),
        ("WIDTH_MM", "Width", AT.Number, "mm", null, 100m, 3000m, null),
        ("OUTPUT_W", "Heat output (ΔT50)", AT.Number, "W", null, 50m, 10000m, null),
        ("PANEL_TYPE", "Panel type", AT.Enum, null, ["Type 11", "Type 21", "Type 22", "Type 33", "Column", "Towel rail"], null, null, null),
        ("FINISH", "Finish", AT.Enum, null, ["White", "Anthracite", "Chrome", "Black", "Brushed steel"], null, null, null),
        ("CONNECTIVITY", "Connectivity", AT.Enum, null, ["Wired", "Wireless", "WiFi", "Zigbee", "OpenTherm"], null, null, null),
        ("POWER_SUPPLY", "Power supply", AT.Enum, null, ["Mains", "Battery", "Low voltage"], null, null, null),
        ("PIPE_SIZE_MM", "Pipe size", AT.Enum, "mm", ["10", "15", "22", "28", "35", "42"], null, null, null),
        ("MATERIAL", "Material", AT.Enum, null, ["Copper", "Brass", "PEX", "Multilayer", "Plastic", "Steel"], null, null, null),
        ("CONNECTION", "Connection", AT.Enum, null, ["Compression", "Push-fit", "Solder", "Press", "Threaded"], null, null, null),
        ("DUCT_DIAMETER_MM", "Duct diameter", AT.Enum, "mm", ["100", "125", "150", "160", "200"], null, null, null),
        ("AIRFLOW_M3H", "Airflow", AT.Number, "m³/h", null, 10m, 2000m, null),
        ("PV_POWER_W", "Panel power", AT.Number, "W", null, 50m, 1000m, null),
        ("STORAGE_KWH", "Storage capacity", AT.Number, "kWh", null, 0m, 50m, null),
        ("VOLUME_L", "Volume", AT.Number, "L", null, 5m, 1000m, null),
        ("CURRENT_A", "Rated current", AT.Number, "A", null, 1m, 125m, null),
        ("IP_RATING", "IP rating", AT.Enum, null, ["IP20", "IP44", "IP54", "IP65", "IP66", "IP67"], null, null, null),
        ("POWER_SOURCE", "Power source", AT.Enum, null, ["Corded", "Cordless 18V", "Cordless 12V", "Manual", "Gas"], null, null, null),
        ("WARRANTY_YEARS", "Warranty", AT.Number, "years", null, 0m, 15m, null),
    ];

    /// <summary>Category → attributes (* = required). Shared with the EDI sample generator.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Templates = new Dictionary<string, string[]>
    {
        ["HVAC"] = ["CAPACITY_KW*", "REFRIGERANT*", "PHASE*", "SCOP", "ENERGY_CLASS", "SOUND_DB", "WARRANTY_YEARS"],
        ["BOILERS"] = ["CAPACITY_KW*", "FUEL*", "BOILER_TYPE*", "EFFICIENCY_PCT", "ENERGY_CLASS", "WARRANTY_YEARS"],
        ["RADIATORS"] = ["HEIGHT_MM*", "WIDTH_MM*", "OUTPUT_W*", "PANEL_TYPE", "FINISH"],
        ["CONTROLS"] = ["CONNECTIVITY*", "POWER_SUPPLY"],
        ["PLUMBING"] = ["PIPE_SIZE_MM*", "MATERIAL*", "CONNECTION"],
        ["VENTILATION"] = ["DUCT_DIAMETER_MM*", "AIRFLOW_M3H", "SOUND_DB"],
        ["SOLAR"] = ["PV_POWER_W", "STORAGE_KWH", "CAPACITY_KW"],
        ["WATER-HEATING"] = ["VOLUME_L*", "CAPACITY_KW", "ENERGY_CLASS"],
        ["ELECTRICAL"] = ["CURRENT_A", "IP_RATING"],
        ["TOOLS"] = ["POWER_SOURCE"],
    };

    private static readonly (string Code, string Name, string? Parent, int Sort)[] Tree =
    [
        ("HEATING", "Heating", null, 10),
        ("COOLING", "Cooling & Ventilation", null, 20),
        ("RENEWABLES", "Renewables", null, 30),
        ("INSTALL", "Installation & Tools", null, 40),
        ("BOILERS", "Boilers", "HEATING", 1),
        ("RADIATORS", "Radiators & Towel Rails", "HEATING", 2),
        ("WATER-HEATING", "Water Heating", "HEATING", 3),
        ("CONTROLS", "Controls & Thermostats", "HEATING", 4),
        ("HVAC", "Heat Pumps & Air Conditioning", "COOLING", 1),
        ("VENTILATION", "Ventilation", "COOLING", 2),
        ("SOLAR", "Solar & Storage", "RENEWABLES", 1),
        ("PLUMBING", "Plumbing & Fittings", "INSTALL", 1),
        ("ELECTRICAL", "Electrical", "INSTALL", 2),
        ("TOOLS", "Tools & Equipment", "INSTALL", 3),
    ];

    public static async Task SeedAsync(CatalogueDbContext db, CancellationToken ct = default)
    {
        // --- category tree ---
        var categories = await db.Categories.ToDictionaryAsync(c => c.Code, ct);
        foreach (var (code, name, _, sort) in Tree.Where(t => !categories.ContainsKey(t.Code)))
        {
            var c = new Category(code, name);
            c.Rename(name, sort);
            db.Categories.Add(c);
            categories[code] = c;
        }
        await db.SaveChangesAsync(ct);
        foreach (var (code, name, parent, sort) in Tree.Where(t => t.Parent is not null))
        {
            var c = categories[code];
            if (c.ParentCategoryId is null) // only place top-level orphans; never undo an admin move
            {
                c.MoveTo(categories[parent!].Id, []);
                if (c.SortOrder == 0) c.Rename(c.Name == code ? name : c.Name, sort);
            }
        }
        if (categories.TryGetValue("HVAC", out var hvac) && hvac.Name == "Heating, Ventilation & Air Conditioning")
            hvac.Rename("Heat Pumps & Air Conditioning", hvac.SortOrder);
        await db.SaveChangesAsync(ct);

        // --- attribute definitions + templates ---
        var firstRun = !await db.AttributeDefinitions.AnyAsync(ct);
        var defs = await db.AttributeDefinitions.ToDictionaryAsync(a => a.Code, ct);
        var order = 0;
        foreach (var a in Attributes)
        {
            order += 10;
            if (defs.ContainsKey(a.Code)) continue;
            var def = new AttributeDefinition(a.Code, a.Name, a.Type, a.Unit, a.Values, a.Min, a.Max, true, order, a.Desc);
            db.AttributeDefinitions.Add(def);
            defs[a.Code] = def;
        }
        await db.SaveChangesAsync(ct);

        if (firstRun)
        {
            foreach (var (catCode, attrs) in Templates)
            {
                if (!categories.TryGetValue(catCode, out var cat)) continue;
                var sort = 0;
                foreach (var spec in attrs)
                {
                    var code = spec.TrimEnd('*');
                    db.CategoryAttributes.Add(new CategoryAttribute
                    {
                        CategoryId = cat.Id, AttributeDefinitionId = defs[code].Id, IsRequired = spec.EndsWith('*'), SortOrder = sort += 10
                    });
                }
            }

            // Approval policies: SUPA holds big price moves; the REST demo supplier needs new-product approval.
            foreach (var s in await db.Suppliers.ToListAsync(ct))
            {
                if (s.Code == "SUPA") s.SetApprovalPolicy(false, 15m);
                if (s.Code == "DEMO01") s.SetApprovalPolicy(true, 10m);
            }
            await db.SaveChangesAsync(ct);
        }

        // --- price lists + customers ---
        if (!await db.PriceLists.AnyAsync(ct))
        {
            var trade = new PriceList("TRADE", "Trade price list", PriceListKind.Trade, 12m);
            db.PriceLists.Add(trade);
            await db.SaveChangesAsync(ct);
            var gold = new PriceList("INSTALLER-GOLD", "Installer Gold tier", PriceListKind.Tier, 20m, trade.Id);
            db.PriceLists.Add(gold);
            await db.SaveChangesAsync(ct);
            var briteheat = new PriceList("CUST-1001", "BriteHeat negotiated prices", PriceListKind.Customer, 22m, gold.Id);
            db.PriceLists.Add(briteheat);
            await db.SaveChangesAsync(ct);

            db.CustomerAccounts.AddRange(
                new CustomerAccount("ACC-1001", "BriteHeat Installers Ltd", briteheat.Id),
                new CustomerAccount("ACC-1002", "Northern Plumbing Merchants", trade.Id),
                new CustomerAccount("ACC-1003", "Coastal Heating & Gas", gold.Id));

            // Negotiated quantity breaks on the demo heat pump.
            var demo = await db.Products.FirstOrDefaultAsync(p => p.Sku == "HP-8KW-DEMO", ct);
            if (demo is not null)
            {
                var from = DateTime.UtcNow.Date;
                db.PriceListEntries.AddRange(
                    new PriceListEntry(briteheat.Id, demo.Id, 1, 1049m, from),
                    new PriceListEntry(briteheat.Id, demo.Id, 5, 995m, from),
                    new PriceListEntry(briteheat.Id, demo.Id, 10, 949m, from),
                    new PriceListEntry(gold.Id, demo.Id, 3, 1019m, from));
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
