using System.Text;
using Catalogue.Contracts;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Catalogue.Application.Streaming;

public sealed record StreamJob(long ImportId, int ChunkSize, string? CorrelationId = null, string? TraceParent = null);

/// <summary>
/// Producer side: reads a (possibly multi-GB) EDI interchange segment by segment, cuts it into
/// self-contained PRICAT chunks of N line items and publishes them to Event Hubs in batches.
/// Memory use is bounded by one batch of chunks regardless of file size.
/// </summary>
public sealed class EdiStreamPublisher(
    ICatalogueDbContext db,
    IFileStorage storage,
    IEdiSourceLibrary library,
    IEventHubProducer producer,
    IBulkCatalogueWriter writer,
    StreamTelemetry telemetry,
    ILogger<EdiStreamPublisher> logger)
{
    public const string LibraryPrefix = "library://";
    private const int EventsPerSend = 16;

    private static readonly HashSet<string> EnvelopeTags =
        ["UNA", "UNB", "UNG", "UNH", "BGM", "DTM", "NAD", "CUX", "RFF", "UNS", "UNT", "UNE", "UNZ"];

    public async Task RunAsync(StreamJob job, CancellationToken ct)
    {
        var import = await db.ImportFiles.Include(i => i.Supplier).FirstOrDefaultAsync(i => i.Id == job.ImportId, ct);
        if (import is null)
        {
            logger.LogWarning("Stream job for missing import {ImportId}.", job.ImportId);
            return;
        }

        using var activity = CatalogueDiagnostics.StartConsumer("eventhub.stream.publish", job.TraceParent, import.CorrelationId);
        activity?.SetTag("catalogue.import_id", import.Id);
        activity?.SetTag("catalogue.file", import.FileName);
        activity?.SetTag("catalogue.chunk_size", job.ChunkSize);
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["ImportId"] = import.Id });

        try
        {
            await writer.ResetStreamAsync(import.Id, ct);
            import.BeginStream();
            await db.SaveChangesAsync(ct);

            await using var raw = await OpenSourceAsync(import.BlobPath, ct);
            await using var counting = new CountingStream(raw);
            long totalBytes = 0;
            try { totalBytes = raw.Length; } catch (NotSupportedException) { }

            telemetry.RunStarted(import.Id, import.FileName, import.Supplier?.Code ?? import.SupplierId.ToString(),
                totalBytes, job.ChunkSize);
            logger.LogInformation("Streaming import {ImportId} ({File}) to Event Hub in chunks of {ChunkSize}.",
                import.Id, import.FileName, job.ChunkSize);

            var total = await PublishAsync(import, job.ChunkSize, counting, ct);

            await writer.SetTotalAndTryCompleteAsync(import.Id, (int)Math.Min(int.MaxValue, total), ct);
            telemetry.RunFinished(import.Id, "Published");
            activity?.SetTag("catalogue.line_items", total);
            logger.LogInformation("Import {ImportId}: published {Rows} line items.", import.Id, total);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Streaming import {ImportId} failed.", import.Id);
            telemetry.RunFinished(import.Id, "Failed", ex.Message);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            import.Fail($"Stream publish failed: {ex.Message}");
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task<long> PublishAsync(ImportFile import, int chunkSize, CountingStream source, CancellationToken ct)
    {
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        var pending = new List<EdiChunkEvent>(EventsPerSend);
        var body = new StringBuilder(chunkSize * 96);
        int chunkIndex = 0, itemsInChunk = 0, segmentsInChunk = 0;
        long lineItems = 0, firstItemOfChunk = 1, pendingRows = 0;
        var inItem = false;

        async Task SendPending()
        {
            if (pending.Count == 0) return;
            await producer.SendAsync(pending, ct);
            CatalogueDiagnostics.ChunksPublished.Add(pending.Count);
            telemetry.Published(import.Id, pending.Count, pendingRows, source.BytesRead);
            pending.Clear();
            pendingRows = 0;
        }

        void CloseChunk()
        {
            if (itemsInChunk == 0) return;
            var reference = $"{import.Id}-{chunkIndex}";
            var payload = $"UNH+{reference}+PRICAT:D:01B:UN'{body}UNT+{segmentsInChunk + 2}+{reference}'";
            pending.Add(new EdiChunkEvent(import.Id, import.SupplierId, chunkIndex, firstItemOfChunk,
                itemsInChunk, import.CorrelationId ?? "", payload, CatalogueDiagnostics.CurrentTraceParent));
            pendingRows += itemsInChunk;
            chunkIndex++;
            firstItemOfChunk += itemsInChunk;
            itemsInChunk = 0;
            segmentsInChunk = 0;
            body.Clear();
        }

        await foreach (var segment in EdiSegmentReader.ReadAsync(reader, ct))
        {
            var tag = EdiSegmentReader.Tag(segment);
            if (tag.SequenceEqual("LIN"))
            {
                if (itemsInChunk >= chunkSize)
                {
                    CloseChunk();
                    if (pending.Count >= EventsPerSend) await SendPending();
                }
                inItem = true;
                itemsInChunk++;
                lineItems++;
            }
            else if (EnvelopeTags.Contains(tag.ToString()))
            {
                inItem = false;
                continue;
            }
            else if (!inItem)
            {
                continue; // header-level segment between messages
            }

            body.Append(segment).Append('\'');
            segmentsInChunk++;
        }

        CloseChunk();
        await SendPending();
        return lineItems;
    }

    private Task<Stream> OpenSourceAsync(string blobPath, CancellationToken ct)
    {
        if (blobPath.StartsWith(LibraryPrefix, StringComparison.Ordinal))
        {
            var path = library.Resolve(blobPath[LibraryPrefix.Length..])
                ?? throw new FileNotFoundException($"Library file '{blobPath}' no longer exists.");
            return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous));
        }
        return storage.OpenReadAsync(blobPath, ct);
    }
}

/// <summary>Read-through stream wrapper that tracks bytes consumed (for publish progress).</summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    public long BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        BytesRead += n;
        return n;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        BytesRead += n;
        return n;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
