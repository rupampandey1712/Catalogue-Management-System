using System.Globalization;
using System.Text;
using System.Text.Json;
using Catalogue.Application.Streaming;

namespace Catalogue.Infrastructure.Streaming;

/// <summary>
/// Generates large, realistic HVAC PRICAT interchanges for load-testing the Event Hub pipeline.
/// Each catalogue item (brand, MPN, GTIN, specifications) is a deterministic function of its index,
/// so the same index generated for two suppliers yields the same GTIN - which is how the catalogue
/// consolidates one boiler offered by several distributors. Commercial data (price, cost, stock,
/// lead time) varies per supplier seed. A configurable share of rows is deliberately invalid.
/// </summary>
public static class EdiSampleGenerator
{
    public const int ItemsPerMessage = 50_000;

    public sealed record Item(string Category, string Prefix, string Name, string Brand, string Mpn, decimal BasePrice,
        Dictionary<string, string> Specs, int PackSize, int Moq, string Description);

    private static readonly string[] Categories =
        ["HVAC", "BOILERS", "RADIATORS", "CONTROLS", "PLUMBING", "VENTILATION", "SOLAR", "WATER-HEATING", "ELECTRICAL", "TOOLS"];
    // Weighted so fittings/controls (high SKU counts in real merchants) dominate.
    private static readonly int[] CategoryWeights = [10, 8, 12, 12, 25, 6, 5, 6, 10, 6];

    public static IReadOnlyList<string> CategoryCodes => Categories;

    public static async Task<long> WriteAsync(string path, long lineItems, double errorRate = 0.004,
        int seed = 42, string supplierCode = "SUPB", IProgress<long>? progress = null, CancellationToken ct = default,
        long startIndex = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
        await using (var w = new StreamWriter(fs, new UTF8Encoding(false), 1 << 20))
        {
            await WriteAsync(w, lineItems, errorRate, seed, supplierCode, progress, ct, startIndex);
        }
        File.Move(tmp, path, overwrite: true);
        var info = new FileInfo(path);
        await File.WriteAllTextAsync(path + ".meta.json", JsonSerializer.Serialize(new
        {
            lineItems, errorRate, seed, supplierCode, startIndex, sizeBytes = info.Length, generatedUtc = DateTime.UtcNow, format = "hvac-v2"
        }), ct);
        return info.Length;
    }

    /// <param name="startIndex">First catalogue item index. Overlapping ranges for two suppliers share GTINs
    /// (consolidated into one product); disjoint ranges produce new products.</param>
    public static async Task WriteAsync(TextWriter w, long lineItems, double errorRate, int seed,
        string supplierCode, IProgress<long>? progress, CancellationToken ct, long startIndex = 0)
    {
        var rng = new Random(seed);
        var now = DateTime.UtcNow;
        var interchangeRef = $"CAT{now:yyMMddHHmm}";
        var gln = "50" + (supplierCode.Aggregate(17L, (h, c) => (h * 31 + c) % 100_000_000_000L)).ToString("D11", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(8192);

        await w.WriteAsync($"UNA:+.? '\nUNB+UNOC:3+{gln}:14+CATALOGUE:14+{now:yyMMdd}:{now:HHmm}+{interchangeRef}'\n");

        long item = startIndex, last = startIndex + lineItems;
        var messages = 0;
        while (item < last)
        {
            ct.ThrowIfCancellationRequested();
            messages++;
            var msgRef = $"M{messages:D6}";
            var segments = 0;
            sb.Clear();
            sb.Append($"UNH+{msgRef}+PRICAT:D:01B:UN:EAN008'\n"); segments++;
            sb.Append($"BGM+9+{interchangeRef}-{messages}+9'\n"); segments++;
            sb.Append($"DTM+137:{now:yyyyMMdd}:102'\n"); segments++;
            sb.Append($"NAD+SU+{gln}::9'\n"); segments++;
            sb.Append("CUX+2:GBP:8'\n"); segments++;
            await w.WriteAsync(sb); sb.Clear();

            var end = Math.Min(last, item + ItemsPerMessage);
            for (; item < end; item++)
            {
                segments += AppendItem(sb, rng, item, errorRate);
                if (sb.Length > 7000) { await w.WriteAsync(sb); sb.Clear(); }
                if ((item & 0x3FFF) == 0) progress?.Report(item - startIndex);
            }
            sb.Append("UNS+S'\n"); segments++;
            sb.Append($"UNT+{segments + 1}+{msgRef}'\n");
            await w.WriteAsync(sb); sb.Clear();
        }

        await w.WriteAsync($"UNZ+{messages}+{interchangeRef}'\n");
        progress?.Report(lineItems);
    }

    /// <summary>GS1-style GTIN-13 for a catalogue item: fictional company prefix 5060 + item reference.</summary>
    public static string GtinFor(long index)
    {
        var payload = "5060" + (index % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
        return payload + Catalogue.Domain.Gtin.CheckDigit(payload);
    }

    private static int AppendItem(StringBuilder sb, Random rng, long index, double errorRate)
    {
        var it = Describe(index);
        var sku = $"{it.Prefix}-{index + 1:D8}";
        var name = it.Name;
        var category = it.Category;
        var gtin = GtinFor(index);
        var specs = new Dictionary<string, string>(it.Specs);

        // Supplier-specific commercials.
        var price = Math.Round(it.BasePrice * (decimal)(0.92 + rng.NextDouble() * 0.16), 2);
        var cost = Math.Round(price * (decimal)(0.55 + rng.NextDouble() * 0.2), 2);
        var currency = rng.Next(40) == 0 ? (rng.Next(2) == 0 ? "EUR" : "USD") : "GBP";
        var qtyValue = rng.Next(10) == 0 ? 0 : rng.Next(1, 600);
        var qty = qtyValue.ToString(CultureInfo.InvariantCulture);
        var leadTime = qtyValue == 0 ? rng.Next(5, 28) : rng.Next(1, 4);
        var priceText = price.ToString("0.00", CultureInfo.InvariantCulture);

        if (rng.NextDouble() < errorRate)
        {
            switch (rng.Next(9))
            {
                case 0: priceText = "-" + priceText; break;                               // negative price
                case 1: currency = "JPY"; break;                                           // unsupported currency
                case 2: category = "LEGACY-" + rng.Next(1, 9); break;                      // unknown category
                case 3: name = ""; break;                                                  // missing name
                case 4: qty = "-" + rng.Next(1, 50); break;                                // negative quantity
                case 5: gtin = gtin[..^1] + (char)('0' + (gtin[^1] - '0' + 1) % 10); break; // bad GTIN check digit
                case 6 when category == "HVAC": specs["REFRIGERANT"] = "R22"; break;      // banned refrigerant
                case 7 when specs.ContainsKey("CAPACITY_KW"): specs["CAPACITY_KW"] = "250"; break; // out of range
                default: specs["VOLUME_L"] = "999"; if (category == "WATER-HEATING") specs["DUCT_DIAMETER_MM"] = "100"; break; // spec not in template
            }
        }

        var segments = 0;
        void Seg(string s) { sb.Append(s).Append("'\n"); segments++; }
        static string E(string v) => EdiSegmentReader.Escape(v);
        Seg($"LIN+{sku}+{E(name)}+{category}");
        Seg($"PIA+1+{E(it.Mpn)}:MF");
        Seg($"PIA+1+{gtin}:SRV");
        Seg($"IMD+F+BRN+:::{E(it.Brand)}");
        if ((index & 3) != 0) Seg($"IMD+F++:::{E(it.Description)}");
        foreach (var (code, value) in specs) Seg($"CAV+{code}:{E(value)}");
        Seg($"PRI+AAA:{priceText}:{currency}");
        Seg($"PRI+AAB:{cost.ToString("0.00", CultureInfo.InvariantCulture)}:{currency}");
        Seg($"QTY+47:{qty}");
        if (it.Moq > 1) Seg($"QTY+53:{it.Moq}");
        if (it.PackSize > 1) Seg($"QTY+52:{it.PackSize}");
        Seg($"DTM+169:{leadTime}:804");
        if (index % 997 == 0) Seg("STS+DIS");
        return segments;
    }

    private static string Pick(Random r, params string[] values) => values[r.Next(values.Length)];
    private static T Pick<T>(Random r, params T[] values) => values[r.Next(values.Length)];
    private static string N(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Intrinsic item data: a pure function of the index (same for every supplier).</summary>
    public static Item Describe(long index)
    {
        var r = new Random(unchecked((int)(index * 2654435761L % int.MaxValue)) ^ 0x5EED);
        var roll = r.Next(CategoryWeights.Sum());
        var c = 0;
        while (roll >= CategoryWeights[c]) roll -= CategoryWeights[c++];
        var specs = new Dictionary<string, string>();
        var series = Pick(r, "Pro", "Eco", "Plus", "Max", "Neo", "Prime");

        switch (Categories[c])
        {
            case "HVAC":
            {
                var brand = Pick(r, "Thermora", "Aerivo", "Klimaro", "Nordair");
                var kw = Pick(r, 5m, 6m, 8m, 10m, 12m, 14m, 16m);
                var model = Pick(r, "Air Source Heat Pump", "Monobloc Heat Pump", "Split AC Unit", "Hybrid Heat Pump");
                var scop = Math.Round(3.2m + (decimal)r.NextDouble() * 1.8m, 1);
                specs["CAPACITY_KW"] = N(kw);
                specs["REFRIGERANT"] = r.Next(10) < 6 ? "R32" : r.Next(10) < 8 ? "R290" : "R410A";
                specs["PHASE"] = kw <= 12 ? "Single" : "Three";
                specs["SCOP"] = N(scop);
                specs["ENERGY_CLASS"] = scop > 4.3m ? "A+++" : "A++";
                specs["SOUND_DB"] = N(52 + r.Next(15));
                specs["WARRANTY_YEARS"] = N(Pick(r, 5m, 7m, 10m));
                return new("HVAC", "HP", $"{brand} {series} {N(kw)}kW {model}", brand, $"{brand[..2].ToUpperInvariant()}-{series.ToUpperInvariant()}-{N(kw)}", 600 + kw * 420,
                    specs, 1, 1, $"{model} with {specs["REFRIGERANT"]} refrigerant, SCOP {N(scop)}");
            }
            case "BOILERS":
            {
                var brand = Pick(r, "Caldex", "Hearthline", "Vulkan");
                var type = Pick(r, "Combi", "Combi", "System", "Heat only");
                var kw = type == "Heat only" ? Pick(r, 12m, 15m, 18m, 24m, 30m) : Pick(r, 24m, 28m, 30m, 35m, 40m);
                var fuel = r.Next(10) < 8 ? "Natural gas" : r.Next(2) == 0 ? "LPG" : "Oil";
                specs["CAPACITY_KW"] = N(kw);
                specs["FUEL"] = fuel;
                specs["BOILER_TYPE"] = type;
                specs["EFFICIENCY_PCT"] = N(89 + r.Next(6));
                specs["ENERGY_CLASS"] = "A";
                specs["WARRANTY_YEARS"] = N(Pick(r, 7m, 10m, 12m));
                return new("BOILERS", "BLR", $"{brand} {series} {N(kw)}kW {type} Boiler{(fuel == "Natural gas" ? "" : " " + fuel)}", brand,
                    $"{brand[..2].ToUpperInvariant()}{type[0]}{N(kw)}-{series[..1]}", 650 + kw * 35, specs, 1, 1, $"{type} boiler, {fuel}, ErP A");
            }
            case "RADIATORS":
            {
                var brand = Pick(r, "Radiant Works", "Nordheat", "Calora");
                var type = Pick(r, "Type 11", "Type 21", "Type 22", "Type 22", "Type 33", "Column", "Towel rail");
                var h = Pick(r, 300m, 450m, 600m, 700m, type == "Towel rail" ? 1200m : 600m);
                var wd = type == "Towel rail" ? 500m : 400m + r.Next(9) * 200m;
                var factor = type switch { "Type 11" => 0.0012m, "Type 21" => 0.0017m, "Type 22" => 0.0022m, "Type 33" => 0.003m, "Column" => 0.0026m, _ => 0.0011m };
                var output = Math.Round(h * wd * factor);
                var finish = type is "Column" or "Towel rail" ? Pick(r, "White", "Anthracite", "Chrome", "Black") : "White";
                specs["HEIGHT_MM"] = N(h); specs["WIDTH_MM"] = N(wd); specs["OUTPUT_W"] = N(output);
                specs["PANEL_TYPE"] = type; specs["FINISH"] = finish;
                return new("RADIATORS", "RAD", $"{brand} {type} {N(h)}x{N(wd)} {finish}", brand, $"RW{type.Replace("Type ", "T").Replace(" ", "")[..2]}{N(h)}{N(wd)}{finish[..1]}",
                    30 + output * 0.045m, specs, 1, 1, $"{N(output)}W at ΔT50");
            }
            case "CONTROLS":
            {
                var brand = Pick(r, "Smartherm", "Hivex", "Tempo");
                var kind = Pick(r, "Smart Thermostat", "Room Sensor", "Programmer", "TRV Head", "Zone Valve", "Receiver");
                var conn = Pick(r, "Wired", "Wireless", "WiFi", "Zigbee", "OpenTherm");
                specs["CONNECTIVITY"] = conn;
                specs["POWER_SUPPLY"] = conn is "Wired" or "OpenTherm" ? "Mains" : Pick(r, "Battery", "Mains");
                return new("CONTROLS", "CTL", $"{brand} {kind} {series} {conn}", brand, $"{brand[..3].ToUpperInvariant()}-{kind[..3].ToUpperInvariant()}{r.Next(100, 999)}",
                    kind switch { "Smart Thermostat" => 149m, "Zone Valve" => 68m, "TRV Head" => 42m, "Programmer" => 89m, _ => 35m },
                    specs, 1, 1, $"{conn} {kind.ToLowerInvariant()}");
            }
            case "PLUMBING":
            {
                var brand = Pick(r, "FlowFit", "Brassco", "Pipeline Pro");
                var kind = Pick(r, "Compression Elbow", "Compression Tee", "Push-fit Elbow", "Push-fit Tee", "Ball Valve", "Isolation Valve", "Copper Tube 3m", "Press Coupler");
                var size = Pick(r, "15", "15", "22", "22", "28");
                var connection = kind.StartsWith("Push") ? "Push-fit" : kind.StartsWith("Press") ? "Press" : kind.StartsWith("Copper") ? "Solder" : "Compression";
                var material = kind.StartsWith("Push") ? "Plastic" : kind.StartsWith("Copper") ? "Copper" : "Brass";
                specs["PIPE_SIZE_MM"] = size; specs["MATERIAL"] = material; specs["CONNECTION"] = connection;
                var pack = kind.Contains("Elbow") || kind.Contains("Tee") || kind.Contains("Coupler") ? Pick(r, 10, 25) : 1;
                return new("PLUMBING", "PLB", $"{brand} {kind} {size}mm{(pack > 1 ? $" (pack of {pack})" : "")}", brand,
                    $"{brand[..2].ToUpperInvariant()}{kind[..2].ToUpperInvariant()}{size}{(pack > 1 ? "P" + pack : "")}",
                    (kind.StartsWith("Copper") ? 18m : kind.Contains("Valve") ? 9m : 1.4m) * (size == "28" ? 1.8m : size == "22" ? 1.4m : 1m) * pack,
                    specs, pack, pack > 1 ? pack : 1, $"{size}mm {connection.ToLowerInvariant()} {material.ToLowerInvariant()}");
            }
            case "VENTILATION":
            {
                var brand = Pick(r, "Ventaro", "Airline", "Breathe");
                var kind = Pick(r, "Extractor Fan", "Inline Fan", "MVHR Unit");
                var duct = kind == "MVHR Unit" ? Pick(r, "150", "160", "200") : Pick(r, "100", "125", "150");
                var airflow = kind == "MVHR Unit" ? 250 + r.Next(250) : 60 + r.Next(240);
                specs["DUCT_DIAMETER_MM"] = duct; specs["AIRFLOW_M3H"] = N(airflow); specs["SOUND_DB"] = N(22 + r.Next(20));
                return new("VENTILATION", "VNT", $"{brand} {series} {kind} {duct}mm", brand, $"{brand[..2].ToUpperInvariant()}V{duct}{kind[..1]}",
                    kind == "MVHR Unit" ? 1450m : 45m + airflow * 0.3m, specs, 1, 1, $"{N(airflow)} m³/h");
            }
            case "SOLAR":
            {
                var brand = Pick(r, "Solari", "Sunvolt");
                var kind = Pick(r, "PV Panel", "PV Panel", "Hybrid Inverter", "Battery Storage");
                if (kind == "PV Panel") { var w = Pick(r, 400m, 410m, 430m, 450m); specs["PV_POWER_W"] = N(w); return new("SOLAR", "SOL", $"{brand} {N(w)}W {kind} Black", brand, $"SV{N(w)}B", w * 0.32m, specs, 1, 1, "All-black monocrystalline"); }
                if (kind == "Battery Storage") { var kwh = Pick(r, 5m, 10m, 13.5m); specs["STORAGE_KWH"] = N(kwh); return new("SOLAR", "SOL", $"{brand} {N(kwh)}kWh {kind}", brand, $"SB{N(kwh)}", 900 + kwh * 380, specs, 1, 1, "LiFePO4 battery"); }
                var ikw = Pick(r, 3.6m, 5m, 6m); specs["CAPACITY_KW"] = N(ikw);
                return new("SOLAR", "SOL", $"{brand} {N(ikw)}kW {kind}", brand, $"SI{N(ikw)}", 700 + ikw * 150, specs, 1, 1, "Hybrid inverter");
            }
            case "WATER-HEATING":
            {
                var brand = Pick(r, "Aquaflo", "Cylindra");
                var kind = Pick(r, "Unvented Cylinder", "Unvented Cylinder", "Heat Pump Cylinder", "Immersion Heater");
                if (kind == "Immersion Heater") { specs["VOLUME_L"] = "5"; specs["CAPACITY_KW"] = "3"; return new("WATER-HEATING", "WHT", $"{brand} 3kW Immersion Heater 11\"", brand, "AQ-IMM3", 24m, specs, 1, 1, "3kW element"); }
                var vol = Pick(r, 150m, 180m, 210m, 250m, 300m);
                specs["VOLUME_L"] = N(vol); specs["ENERGY_CLASS"] = Pick(r, "B", "C");
                return new("WATER-HEATING", "WHT", $"{brand} {N(vol)}L {kind}", brand, $"{brand[..2].ToUpperInvariant()}{N(vol)}{kind[..1]}", 380 + vol * 2.4m, specs, 1, 1, "Indirect, stainless steel");
            }
            case "ELECTRICAL":
            {
                var brand = Pick(r, "Voltek", "Circuitry");
                var kind = Pick(r, "Fused Spur", "RCBO", "Isolator Switch", "Junction Box");
                var amps = kind switch { "Fused Spur" => 13m, "RCBO" => Pick(r, 16m, 32m, 40m), "Isolator Switch" => Pick(r, 20m, 32m, 63m), _ => 20m };
                specs["CURRENT_A"] = N(amps); specs["IP_RATING"] = kind == "Isolator Switch" ? Pick(r, "IP65", "IP66") : "IP20";
                return new("ELECTRICAL", "ELC", $"{brand} {N(amps)}A {kind}", brand, $"VK{kind[..2].ToUpperInvariant()}{N(amps)}", 6m + amps * 0.4m, specs, 1, 1, $"{N(amps)}A rated");
            }
            default:
            {
                var brand = Pick(r, "Torqx", "Pipemaster");
                var kind = Pick(r, "Press Tool 15-28mm", "Pipe Cutter 15-22mm", "Blowtorch", "Pipe Bender");
                specs["POWER_SOURCE"] = kind.StartsWith("Press") ? "Cordless 18V" : kind == "Blowtorch" ? "Gas" : "Manual";
                return new("TOOLS", "TLS", $"{brand} {kind}", brand, $"TQ{kind[..2].ToUpperInvariant()}{r.Next(10, 99)}",
                    kind.StartsWith("Press") ? 1250m : kind == "Blowtorch" ? 39m : 24m, specs, 1, 1, "Trade quality");
            }
        }
    }
}

/// <summary>Server-side folder of streamable EDI interchanges (too large to upload through a browser).</summary>
public sealed class EdiSourceLibrary(string root) : IEdiSourceLibrary
{
    private const double BytesPerItemEstimate = 280;

    public string Root { get; } = Path.GetFullPath(root);

    public IReadOnlyList<EdiSourceFile> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateFiles(Root, "*.edi", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p))
            .Select(f => new EdiSourceFile(
                Path.GetRelativePath(Root, f.FullName).Replace('\\', '/'),
                f.Length, f.LastWriteTimeUtc, ReadLineItems(f) ?? (long)Math.Max(1, f.Length / BytesPerItemEstimate)))
            .OrderBy(f => f.SizeBytes)
            .ToList();
    }

    public string? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var full = Path.GetFullPath(Path.Combine(Root, name));
        var rootWithSep = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)) return null; // no traversal
        return File.Exists(full) && full.EndsWith(".edi", StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static long? ReadLineItems(FileInfo f)
    {
        try
        {
            var meta = f.FullName + ".meta.json";
            if (!File.Exists(meta)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            return doc.RootElement.TryGetProperty("lineItems", out var li) ? li.GetInt64() : null;
        }
        catch { return null; }
    }
}
