using System.Diagnostics;
using Catalogue.Infrastructure.Streaming;

// Usage: dotnet run --project tools/Catalogue.EdiGenerator -- <lineItems> [output.edi] [errorRate] [seed] [startIndex]
//   startIndex: first catalogue item. Two suppliers' files over the same range share GTINs (one product,
//   two offers); a disjoint range yields new products.
//   e.g. dotnet run -c Release --project tools/Catalogue.EdiGenerator -- 5000000 samples/edi/large/pricat-5m.edi
// Media pack: Catalogue.EdiGenerator media <count> [output.zip] [startIndex]
if (args.Length > 0 && args[0] == "media")
{
    var count = args.Length > 1 ? int.Parse(args[1]) : 200;
    var zipPath = args.Length > 2 ? args[2] : $"samples/media/media-pack-{count}.zip";
    var start = args.Length > 3 ? long.Parse(args[3]) : 0;
    var swm = Stopwatch.StartNew();
    var (files, size) = await SampleMediaPack.WriteAsync(zipPath, start, count);
    Console.WriteLine($"Media pack: {files} files for items {start}..{start + count - 1} -> {Path.GetFullPath(zipPath)} " +
                      $"({size / 1024d / 1024d:N1} MB, {swm.Elapsed.TotalSeconds:N1}s)");
    return 0;
}

if (args.Length == 0 || !long.TryParse(args[0].Replace("_", "").Replace(",", ""), out var lineItems) || lineItems < 1)
{
    Console.WriteLine("usage: Catalogue.EdiGenerator <lineItems> [output.edi] [errorRate=0.004] [seed=42] [startIndex=0]");
    return 1;
}

var output = args.Length > 1 ? args[1] : $"samples/edi/large/pricat-{lineItems}.edi";
var errorRate = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0.004;
var seed = args.Length > 3 ? int.Parse(args[3]) : 42;
var startIndex = args.Length > 4 ? long.Parse(args[4]) : 0;

var sw = Stopwatch.StartNew();
var lastPrint = TimeSpan.Zero;
var progress = new Progress<long>(n =>
{
    if (sw.Elapsed - lastPrint < TimeSpan.FromSeconds(1) && n < lineItems) return;
    lastPrint = sw.Elapsed;
    Console.Write($"\r  {n,12:N0} / {lineItems:N0} line items ({100.0 * n / lineItems:0.0}%)");
});

Console.WriteLine($"Generating {lineItems:N0} PRICAT line items -> {Path.GetFullPath(output)}");
var bytes = await EdiSampleGenerator.WriteAsync(output, lineItems, errorRate, seed, progress: progress, startIndex: startIndex);
Console.WriteLine($"\nDone: {bytes / 1024d / 1024d:N1} MB in {sw.Elapsed.TotalSeconds:N1}s " +
                  $"(~{lineItems * errorRate:N0} intentionally invalid rows).");
return 0;
