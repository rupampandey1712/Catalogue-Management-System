using System.Text;
using Catalogue.Application;
using Catalogue.Application.Streaming;
using Catalogue.Contracts;
using Catalogue.Domain;
using Catalogue.Infrastructure.Parsing;
using Catalogue.Infrastructure.Persistence;
using Catalogue.Infrastructure.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Catalogue.UnitTests;

public class StreamingTests
{
    [Fact]
    public async Task Segment_reader_streams_segments_and_honours_release_character()
    {
        var text = "UNA:+.? '\nLIN+A?'B+Name'\r\nPRI+AAA:1.00:GBP'  QTY+47:3";
        var segments = new List<string>();
        await foreach (var s in EdiSegmentReader.ReadAsync(new StringReader(text)))
            segments.Add(s);

        Assert.Equal(["UNA:+.?", "LIN+A?'B+Name", "PRI+AAA:1.00:GBP", "QTY+47:3"], segments);
    }

    [Fact]
    public async Task Parser_reports_the_items_own_LIN_segment_and_reads_descriptions()
    {
        var edi = "UNH+1+PRICAT'LIN+A-1+Pump+HVAC'IMD+F++:::Quiet pump'PRI+AAA:10.50:GBP'QTY+47:4'" +
                  "LIN+B-2+Valve+CONTROLS'PRI+AAA:abc:GBP'UNT+8+1'";
        var rows = new List<ParsedRow>();
        await foreach (var r in new EdiSupplierParser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(edi)), default))
            rows.Add(r);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].RowNumber);                 // LIN is segment #2
        Assert.Equal("Quiet pump", rows[0].Row.Description);
        Assert.StartsWith("LIN+A-1", rows[0].RawValue);
        Assert.Equal(6, rows[1].RowNumber);
        Assert.Equal(-1, rows[1].Row.Price);                // unparseable price surfaces as a validation error
    }

    [Fact]
    public async Task Generator_output_parses_to_the_requested_number_of_line_items()
    {
        var sb = new StringWriter();
        await EdiSampleGenerator.WriteAsync(sb, 1234, errorRate: 0.05, seed: 3, "SUPB", null, default);

        var count = 0;
        var invalid = 0;
        await foreach (var r in new EdiSupplierParser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())), default))
        {
            count++;
            if (RowValidator.Validate(r.Row).Count > 0 || r.Row.CategoryCode!.StartsWith("LEGACY")) invalid++;
        }
        Assert.Equal(1234, count);
        Assert.InRange(invalid, 20, 110); // ~5% injected
    }

    [Fact]
    public async Task Publisher_cuts_interchange_into_self_contained_chunks_and_sets_total()
    {
        var options = new DbContextOptionsBuilder<CatalogueDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CatalogueDbContext(options);
        var supplier = new Supplier("SUPB", "B", IntegrationType.Edi);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        var import = new ImportFile(supplier.Id, "mem", "big.edi", "h", ImportFormats.EdiStream, "corr");
        db.ImportFiles.Add(import);
        await db.SaveChangesAsync();

        var sw = new StringWriter();
        await EdiSampleGenerator.WriteAsync(sw, 2_500, 0, 1, "SUPB", null, default);
        var producer = new CapturingProducer();
        var writer = new RecordingWriter();
        var publisher = new EdiStreamPublisher(db, new StaticStorage(sw.ToString()), new EdiSourceLibrary(Path.GetTempPath()),
            producer, writer, new StreamTelemetry(), NullLogger<EdiStreamPublisher>.Instance);

        await publisher.RunAsync(new StreamJob(import.Id, ChunkSize: 1000), default);

        Assert.Equal([1000, 1000, 500], producer.Events.Select(e => e.LineItems));
        Assert.Equal([1L, 1001L, 2001L], producer.Events.Select(e => e.FirstLineItem));
        Assert.All(producer.Events, e =>
        {
            Assert.StartsWith($"UNH+{import.Id}-{e.ChunkIndex}+PRICAT", e.Payload);
            Assert.EndsWith($"+{import.Id}-{e.ChunkIndex}'", e.Payload);
            Assert.DoesNotContain("UNB+", e.Payload);
        });
        Assert.Equal(2_500, writer.Total);
        Assert.True(writer.Reset);
        Assert.Equal(ImportStatus.Processing, (await db.ImportFiles.FindAsync(import.Id))!.Status);

        // Each chunk parses back to exactly its declared line items.
        foreach (var e in producer.Events)
        {
            var n = 0;
            await foreach (var _ in new EdiSupplierParser().ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(e.Payload)), default)) n++;
            Assert.Equal(e.LineItems, n);
        }
    }

    [Fact]
    public async Task Chunk_processor_validates_rows_and_accounts_for_every_line_item()
    {
        var writer = new RecordingWriter();
        var telemetry = new StreamTelemetry();
        var processor = new EdiChunkProcessor([new EdiSupplierParser()], writer, new StaticReferenceData(), telemetry, NullLogger<EdiChunkProcessor>.Instance);
        var payload = "UNH+9-0+PRICAT'LIN+A-1+Pump+HVAC'PRI+AAA:5:GBP'LIN+A-2+Pump+NOPE'LIN+A-3++HVAC'LIN'UNT+6+9-0'";

        await processor.ProcessAsync(new EdiChunkEvent(9, 1, 0, 101, 4, "c", payload), "2", 7, DateTimeOffset.UtcNow, default);

        var chunk = Assert.Single(writer.Chunks);
        Assert.Single(chunk.Rows);                                   // only A-1 is valid
        Assert.Equal(3, chunk.ErrorRowCount);                        // unknown category, missing name, malformed LIN
        Assert.Contains(chunk.Errors, e => e.Field == "Category" && e.RowNumber == 102);
        Assert.Contains(chunk.Errors, e => e.Field == "Segment");

        var snap = telemetry.Snapshot([new PartitionInfo("2", 7, DateTimeOffset.UtcNow, false)]);
        Assert.Equal(1, snap.Totals.ProcessedRows);
        Assert.Equal(3, snap.Totals.ErrorRows);
        Assert.Equal(0, snap.Partitions.Single().Lag);
    }

    private sealed class CapturingProducer : IEventHubProducer
    {
        public List<EdiChunkEvent> Events { get; } = [];
        public string Mode => "Test";
        public string HubName => "hub";
        public string ConsumerGroup => "cg";
        public Task SendAsync(IReadOnlyList<EdiChunkEvent> events, CancellationToken ct) { Events.AddRange(events); return Task.CompletedTask; }
        public Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PartitionInfo>>([]);
    }

    private sealed class RecordingWriter : IBulkCatalogueWriter
    {
        public List<BulkChunk> Chunks { get; } = [];
        public int Total { get; private set; }
        public bool Reset { get; private set; }
        public Task<bool> WriteChunkAsync(BulkChunk chunk, CancellationToken ct) { Chunks.Add(chunk); return Task.FromResult(true); }
        public Task<bool> SetTotalAndTryCompleteAsync(long importId, int totalRows, CancellationToken ct) { Total = totalRows; return Task.FromResult(false); }
        public Task ResetStreamAsync(long importId, CancellationToken ct) { Reset = true; return Task.CompletedTask; }
    }

    internal sealed class StaticReferenceData : IReferenceDataProvider
    {
        public static readonly AttributeDefinition Capacity = WithId(new AttributeDefinition("CAPACITY_KW", "Output", AttributeDataType.Number, "kW", min: 0.5m, max: 100m), 10);
        public static readonly AttributeDefinition Refrigerant = WithId(new AttributeDefinition("REFRIGERANT", "Refrigerant", AttributeDataType.Enum, allowedValues: ["R32", "R290", "R410A"]), 11);
        public static readonly AttributeDefinition PipeSize = WithId(new AttributeDefinition("PIPE_SIZE_MM", "Pipe size", AttributeDataType.Enum, "mm", ["15", "22"]), 12);

        public static CatalogueReferenceData Data { get; } = new()
        {
            CategoryIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["HVAC"] = 1, ["PLUMBING"] = 2 },
            AttributesByCode = new Dictionary<string, AttributeDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                [Capacity.Code] = Capacity, [Refrigerant.Code] = Refrigerant, [PipeSize.Code] = PipeSize
            },
            CategoryTemplates = new Dictionary<long, IReadOnlyDictionary<long, bool>>
            {
                [1] = new Dictionary<long, bool> { [10] = true, [11] = true },
                [2] = new Dictionary<long, bool> { [12] = true }
            },
            SupplierPolicies = new Dictionary<long, SupplierPolicy> { [7] = new(true, 10m) }
        };

        public Task<CatalogueReferenceData> GetAsync(CancellationToken ct) => Task.FromResult(Data);
        public void Invalidate() { }

        private static AttributeDefinition WithId(AttributeDefinition d, long id)
        {
            typeof(AttributeDefinition).GetProperty(nameof(AttributeDefinition.Id))!.SetValue(d, id);
            return d;
        }
    }

    private sealed class StaticStorage(string content) : IFileStorage
    {
        public Task<string> SaveAsync(Stream c, string fileName, CancellationToken ct) => Task.FromResult("mem");
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }
}
