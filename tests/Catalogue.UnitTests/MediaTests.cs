using System.IO.Compression;
using System.Net;
using System.Text;
using Catalogue.Application;
using Catalogue.Application.Media;
using Catalogue.Domain;
using Catalogue.Infrastructure.Media;
using Catalogue.Infrastructure.Parsing;
using Catalogue.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.UnitTests;

public class MediaTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly byte[] Pdf = "%PDF-1.4\n%fake datasheet"u8.ToArray();

    [Fact]
    public void Files_are_identified_by_bytes_not_names()
    {
        Assert.Equal("image/png", MediaSniffer.Detect(Png)?.ContentType);
        Assert.Equal("image/jpeg", MediaSniffer.Detect([0xFF, 0xD8, 0xFF, 0xE0])?.ContentType);
        Assert.Equal("application/pdf", MediaSniffer.Detect(Pdf)?.ContentType);
        Assert.Null(MediaSniffer.Detect("<svg onload=alert(1)>"u8));   // script-capable formats are refused
        Assert.Null(MediaSniffer.Detect("<html>"u8));
    }

    [Theory]
    [InlineData("TH-PLUS-5_manual.pdf", AssetKind.Manual)]
    [InlineData("5060000000009_datasheet.pdf", AssetKind.Datasheet)]
    [InlineData("erp-energy-label.pdf", AssetKind.EnergyLabel)]
    [InlineData("ce_declaration.pdf", AssetKind.Certificate)]
    public void Document_kind_is_guessed_from_the_file_name(string name, AssetKind kind)
        => Assert.Equal(kind, MediaSniffer.GuessKind(name, isImage: false));

    [Fact]
    public void Signed_media_urls_verify_and_reject_tampering()
    {
        var signer = new MediaUrlSigner("a-test-signing-key-of-at-least-32-bytes!"u8.ToArray());
        var url = signer.Url(42, thumbnail: true);
        var query = System.Web.HttpUtility.ParseQueryString(url[(url.IndexOf('?') + 1)..]);
        var exp = long.Parse(query["exp"]!);
        Assert.True(signer.Verify(42, "t", exp, query["sig"]));
        Assert.False(signer.Verify(43, "t", exp, query["sig"]));          // other asset
        Assert.False(signer.Verify(42, "o", exp, query["sig"]));          // thumbnail link reused for the original
        Assert.False(signer.Verify(42, "t", exp + 300, query["sig"]));    // extended expiry
        var expired = signer.Url(42, true, TimeSpan.FromMinutes(-10));
        var eq = System.Web.HttpUtility.ParseQueryString(expired[(expired.IndexOf('?') + 1)..]);
        Assert.False(signer.Verify(42, "t", long.Parse(eq["exp"]!), eq["sig"]));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.0.10", true)]
    [InlineData("169.254.169.254", true)]   // cloud metadata endpoint
    [InlineData("::1", true)]
    [InlineData("8.8.8.8", false)]
    public void Feed_links_to_private_addresses_are_blocked(string ip, bool isPrivate)
        => Assert.Equal(isPrivate, UrlGuard.IsPrivate(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("100.100.100.200", true)]   // carrier-grade NAT range (Alibaba metadata, many cloud internals)
    [InlineData("224.0.0.1", true)]
    [InlineData("::", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("2606:4700:4700::1111", false)]
    public void Less_obvious_internal_ranges_are_blocked(string ip, bool isPrivate)
        => Assert.Equal(isPrivate, UrlGuard.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public async Task Non_http_links_are_rejected()
        => Assert.NotNull(await UrlGuard.RejectReasonAsync("file:///etc/passwd", allowPrivate: false, default));

    // The pre-flight check only sees the first URL; the handler must refuse internal targets on every
    // connection, which is what stops a public URL redirecting (or re-resolving) to an internal host.
    [Theory]
    [InlineData("http://127.0.0.1:9/")]
    [InlineData("http://localhost:9/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task Media_client_refuses_to_connect_to_internal_addresses(string url)
    {
        using var client = new HttpClient(UrlGuard.CreateHandler(allowPrivate: false));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
        Assert.IsType<BlockedDestinationException>(ex.InnerException);
    }

    [Fact]
    public async Task Media_client_allows_internal_addresses_only_when_configured()
    {
        using var client = new HttpClient(UrlGuard.CreateHandler(allowPrivate: true)) { Timeout = TimeSpan.FromSeconds(10) };
        // Nothing listens on port 9 (discard), so the connect itself fails - but not because it was blocked.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/"));
        Assert.IsNotType<BlockedDestinationException>(ex.InnerException);
    }

    [Fact]
    public void Media_client_does_not_use_a_proxy()
    {
        // A proxy would make the connect callback see the proxy's address instead of the real target.
        using var handler = UrlGuard.CreateHandler(allowPrivate: false);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }

    private sealed class MemoryStorage : IMediaStorage
    {
        public readonly Dictionary<string, byte[]> Files = [];
        public string Mode => "Memory";
        public string Location => "memory";
        public async Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Files[path] = ms.ToArray();
        }
        public Task<MediaContent?> OpenAsync(string path, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(path, out var b) ? new MediaContent(new MemoryStream(b), "application/octet-stream", b.Length) : null);
        public Task DeleteAsync(string path, CancellationToken ct) { Files.Remove(path); return Task.CompletedTask; }
    }

    private sealed class FakeImages : IImageProcessor
    {
        public ImageInfo? Probe(byte[] bytes) => new(800, 800);
        public byte[] Thumbnail(byte[] bytes, int maxEdge) => [0xFF, 0xD8, 0xFF, 0xD9];
    }

    [Fact]
    public async Task Media_pack_matches_by_gtin_then_mpn_then_sku_and_deduplicates()
    {
        var db = new CatalogueDbContext(new DbContextOptionsBuilder<CatalogueDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var boiler = new Product("BLR-1", "Combi boiler");
        boiler.SetIdentity("Vaillant", "ECOTEC-30", "5060000000009");
        var valve = new Product("TRV-9", "Radiator valve");
        db.Products.AddRange(boiler, valve);
        await db.SaveChangesAsync();

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] bytes) { using var s = archive.CreateEntry(name).Open(); s.Write(bytes); }
            Add("5060000000009.png", Png);
            Add("5060000000009_2.png", Png);                 // same bytes again -> duplicate
            Add("ECOTEC-30_manual.pdf", Pdf);
            Add("trv-9_datasheet.pdf", [.. Pdf, 1]);
            Add("UNKNOWN-1.png", Png);
            Add("5060000000009_notes.txt", "hello"u8.ToArray());
        }
        zip.Position = 0;

        var storage = new MemoryStorage();
        var media = new MediaService(db, storage, new FakeImages());
        var report = await new BulkMediaImporter(db, media).ImportAsync(zip, "tester", default);

        Assert.Equal((3, 1, 1, 1), (report.Attached, report.Duplicates, report.Unmatched, report.Rejected));
        var boilerAssets = await db.ProductAssets.Where(a => a.ProductId == boiler.Id).ToListAsync();
        Assert.Contains(boilerAssets, a => a.Kind == AssetKind.Image && a.IsPrimary && a.ThumbnailPath != null);
        Assert.Contains(boilerAssets, a => a.Kind == AssetKind.Manual);
        Assert.Contains(await db.ProductAssets.Where(a => a.ProductId == valve.Id).ToListAsync(), a => a.Kind == AssetKind.Datasheet);
        Assert.All(storage.Files.Keys, k => Assert.StartsWith("products/", k));
    }

    [Fact]
    public async Task Pdf_cannot_be_stored_as_a_product_image()
    {
        var db = new CatalogueDbContext(new DbContextOptionsBuilder<CatalogueDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var media = new MediaService(db, new MemoryStorage(), new FakeImages());
        await Assert.ThrowsAsync<MediaRejectedException>(() =>
            media.AddAsync(1, new MemoryStream(Pdf), "photo.pdf", AssetKind.Image, null, AssetSource.Upload, "t", default));
    }

    [Fact]
    public async Task Csv_and_edi_feeds_carry_media_links()
    {
        var csv = "sku,name,price,currency,quantity,image_url,datasheet_url\n" +
                  "HP-1,Heat Pump,999.50,GBP,10,https://cdn.example.com/hp1.jpg|https://cdn.example.com/hp1-b.jpg,https://cdn.example.com/hp1.pdf\n";
        var csvRows = new List<ParsedRow>();
        await foreach (var r in new CsvSupplierParser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), default)) csvRows.Add(r);
        var links = Assert.Single(csvRows).Row.Media;
        Assert.Equal(2, links.Count(m => m.Kind == "Image"));
        Assert.Equal("https://cdn.example.com/hp1.pdf", links.Single(m => m.Kind == "Datasheet").Url);

        var edi = "UNH+1+PRICAT'LIN+HP-100+Heat Pump 8kW+HVAC'PRI+AAA:1299.99:GBP'" +
                  "IMD+F+IMG+:::https://cdn.example.com/hp100.jpg'IMD+F+DOC+:::https://cdn.example.com/hp100.pdf'UNT+5+1'";
        var ediRows = new List<ParsedRow>();
        await foreach (var r in new EdiSupplierParser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(edi)), default)) ediRows.Add(r);
        var ediLinks = Assert.Single(ediRows).Row.Media;
        Assert.Contains(ediLinks, m => m.Kind == "Image" && m.Url == "https://cdn.example.com/hp100.jpg");
        Assert.Contains(ediLinks, m => m.Kind == "Datasheet" && m.Url == "https://cdn.example.com/hp100.pdf");
    }
}
