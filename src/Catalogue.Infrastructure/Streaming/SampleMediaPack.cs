using System.Globalization;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace Catalogue.Infrastructure.Streaming;

/// <summary>
/// Builds a demo supplier media pack (zip) for the same items the EDI generator emits: rendered
/// product images named <c>&lt;GTIN&gt;.png</c> / <c>&lt;GTIN&gt;_2.png</c>, a one-page PDF datasheet
/// <c>&lt;GTIN&gt;_datasheet.pdf</c> and, for appliances, <c>&lt;MPN&gt;_manual.pdf</c>. This mirrors how real
/// manufacturers ship media: a separate pack keyed by GTIN/MPN, not inside the EDI price file.
/// </summary>
public static class SampleMediaPack
{
    private static readonly Dictionary<string, (SKColor A, SKColor B)> Palette = new()
    {
        ["HVAC"] = (new SKColor(0x1d, 0x63, 0xc7), new SKColor(0x6a, 0xa6, 0xff)),
        ["BOILERS"] = (new SKColor(0xb4, 0x45, 0x1c), new SKColor(0xf3, 0x8f, 0x5e)),
        ["RADIATORS"] = (new SKColor(0x6b, 0x2f, 0xb3), new SKColor(0xb0, 0x8c, 0xf0)),
        ["CONTROLS"] = (new SKColor(0x0a, 0x7d, 0x5c), new SKColor(0x4c, 0xd6, 0xa8)),
        ["PLUMBING"] = (new SKColor(0x8a, 0x5a, 0x10), new SKColor(0xe0, 0xb0, 0x50)),
        ["VENTILATION"] = (new SKColor(0x1b, 0x7f, 0x95), new SKColor(0x6f, 0xd3, 0xe6)),
        ["SOLAR"] = (new SKColor(0x1b, 0x2a, 0x5c), new SKColor(0xf2, 0xb2, 0x2b)),
        ["WATER-HEATING"] = (new SKColor(0x0e, 0x5a, 0x8a), new SKColor(0x9c, 0xd4, 0xf7)),
        ["ELECTRICAL"] = (new SKColor(0x3d, 0x3d, 0x46), new SKColor(0xf2, 0xc0, 0x2b)),
        ["TOOLS"] = (new SKColor(0x9b, 0x1c, 0x1c), new SKColor(0xf0, 0x6b, 0x6b)),
    };

    public static async Task<(int Files, long Bytes)> WriteAsync(string zipPath, long startIndex, int count, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        var files = 0;
        await using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            for (var i = startIndex; i < startIndex + count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = EdiSampleGenerator.Describe(i);
                var gtin = EdiSampleGenerator.GtinFor(i);
                Add(zip, $"{gtin}.png", Render(item, variant: 0)); files++;
                if (i % 3 == 0) { Add(zip, $"{gtin}_2.png", Render(item, variant: 1)); files++; }
                Add(zip, $"{gtin}_datasheet.pdf", Pdf($"{item.Brand} {item.Name}", "Product datasheet", DatasheetLines(item, gtin))); files++;
                if (item.Category is "HVAC" or "BOILERS" or "WATER-HEATING")
                {
                    Add(zip, $"{item.Mpn}_manual.pdf", Pdf($"{item.Name}", "Installation & user manual",
                    [
                        "1. Safety: installation must be carried out by a competent, registered installer.",
                        "2. Siting: allow service clearances as shown on the dimension drawing.",
                        "3. Connection: flush the system and fit a magnetic filter before commissioning.",
                        "4. Commissioning: record settings in the benchmark checklist.",
                        $"Manufacturer part number: {item.Mpn}"
                    ]));
                    files++;
                }
            }
        }
        return (files, new FileInfo(zipPath).Length);
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var s = entry.Open();
        s.Write(bytes);
    }

    private static IEnumerable<string> DatasheetLines(EdiSampleGenerator.Item item, string gtin)
    {
        yield return $"Brand: {item.Brand}";
        yield return $"Manufacturer part number: {item.Mpn}";
        yield return $"GTIN: {gtin}";
        yield return $"Category: {item.Category}";
        yield return item.Description;
        foreach (var (k, v) in item.Specs) yield return $"{k}: {v}";
    }

    /// <summary>Stylised product shot: gradient backdrop, product silhouette by category, brand + key spec.</summary>
    public static byte[] Render(EdiSampleGenerator.Item item, int variant)
    {
        const int size = 800;
        var (a, b) = Palette.GetValueOrDefault(item.Category, (SKColors.SlateGray, SKColors.LightGray));
        using var surface = SKSurface.Create(new SKImageInfo(size, size));
        var c = surface.Canvas;
        using (var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(size, size),
                   variant == 0 ? [a, b] : [b, a], SKShaderTileMode.Clamp) })
            c.DrawRect(0, 0, size, size, bg);

        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var soft = new SKPaint { Color = SKColors.White.WithAlpha(60), IsAntialias = true };
        using var line = new SKPaint { Color = a, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10 };
        using var fill = new SKPaint { Color = a.WithAlpha(40), IsAntialias = true };
        c.DrawCircle(size * 0.82f, size * 0.15f, 160, soft);

        // Product silhouette
        var body = new SKRect(200, 190, 600, 560);
        switch (item.Category)
        {
            case "HVAC":
                c.DrawRoundRect(new SKRect(140, 230, 660, 560), 28, 28, white);
                c.DrawCircle(330, 395, 120, fill); c.DrawCircle(330, 395, 120, line);
                for (var k = 0; k < 3; k++) c.DrawLine(330, 395, 330 + 110 * MathF.Cos(k * 2.09f), 395 + 110 * MathF.Sin(k * 2.09f), line);
                for (var y = 290; y < 520; y += 34) c.DrawLine(500, y, 620, y, line);
                break;
            case "BOILERS":
            case "WATER-HEATING":
                c.DrawRoundRect(new SKRect(250, 150, 550, 600), 30, 30, white);
                c.DrawRoundRect(new SKRect(300, 470, 500, 540), 12, 12, fill);
                c.DrawCircle(400, 300, 60, line);
                break;
            case "RADIATORS":
                c.DrawRoundRect(new SKRect(130, 230, 670, 540), 16, 16, white);
                for (var x = 160; x < 650; x += 40) c.DrawLine(x, 250, x, 520, line);
                break;
            case "SOLAR":
                c.DrawRoundRect(new SKRect(160, 200, 640, 560), 10, 10, white);
                for (var x = 160; x <= 640; x += 80) c.DrawLine(x, 200, x, 560, line);
                for (var y = 200; y <= 560; y += 72) c.DrawLine(160, y, 640, y, line);
                break;
            default:
                c.DrawRoundRect(body, 40, 40, white);
                c.DrawCircle(400, 375, 90, fill); c.DrawCircle(400, 375, 90, line);
                break;
        }

        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var brandFont = new SKFont(typeface, 44);
        using var nameFont = new SKFont(SKTypeface.Default, 30);
        using var chipFont = new SKFont(typeface, 28);
        c.DrawText(item.Brand.ToUpperInvariant(), 60, 90, SKTextAlign.Left, brandFont, white);
        c.DrawRoundRect(new SKRect(40, 640, 760, 760), 22, 22, white);
        using var ink = new SKPaint { Color = new SKColor(0x11, 0x18, 0x27), IsAntialias = true };
        var name = item.Name.Length > 46 ? item.Name[..45] + "…" : item.Name;
        c.DrawText(name, 70, 690, SKTextAlign.Left, nameFont, ink);
        var key = string.Join("  ·  ", item.Specs.Take(3).Select(kv => kv.Value));
        using var accent = new SKPaint { Color = a, IsAntialias = true };
        c.DrawText(key, 70, 738, SKTextAlign.Left, chipFont, accent);

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }

    /// <summary>Minimal single-page PDF (Helvetica text) - enough to be a real, openable document.</summary>
    public static byte[] Pdf(string title, string subtitle, IEnumerable<string> lines)
    {
        static string Esc(string s) => new string(s.Select(ch => ch < 128 ? ch : '?').ToArray())
            .Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var content = new StringBuilder();
        content.Append("BT /F1 20 Tf 50 780 Td (").Append(Esc(title)).Append(") Tj ET\n");
        content.Append("BT /F1 12 Tf 50 758 Td (").Append(Esc(subtitle)).Append(") Tj ET\n");
        content.Append("0.8 0.8 0.8 RG 50 748 m 545 748 l S\n");
        var y = 720;
        foreach (var l in lines)
        {
            content.Append(CultureInfo.InvariantCulture, $"BT /F1 11 Tf 50 {y} Td (").Append(Esc(l)).Append(") Tj ET\n");
            y -= 18;
        }
        var stream = content.ToString();
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
