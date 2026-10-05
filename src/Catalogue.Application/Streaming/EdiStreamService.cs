using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Application.Streaming;

/// <summary>Hand-off between the API (accepts a stream request) and the background publisher.</summary>
public sealed class StreamJobQueue
{
    private readonly Channel<StreamJob> _channel = Channel.CreateUnbounded<StreamJob>();
    public ValueTask EnqueueAsync(StreamJob job, CancellationToken ct) => _channel.Writer.WriteAsync(job, ct);
    public IAsyncEnumerable<StreamJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Use cases for Event Hubs streamed imports: start from library/upload, re-stream.</summary>
public sealed class EdiStreamService(
    ICatalogueDbContext db,
    IFileStorage storage,
    IEdiSourceLibrary library,
    StreamJobQueue jobs,
    IActorContext actor)
{
    public const int DefaultChunkSize = 1000;
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    public sealed record StartResult(ImportFile Import, bool Existing);

    /// <summary>Streams a file that already sits in the server-side EDI library (no upload needed).</summary>
    public async Task<StartResult> StartFromLibraryAsync(long supplierId, string sourceName, int? chunkSize, CancellationToken ct)
    {
        var supplier = await RequireSupplier(supplierId, ct);
        var path = library.Resolve(sourceName)
            ?? throw new KeyNotFoundException($"Source '{sourceName}' was not found in the EDI library.");
        var info = new FileInfo(path);
        var relative = Path.GetRelativePath(library.Root, path).Replace('\\', '/');

        // Fingerprint by identity (path/size/mtime) instead of hashing hundreds of MB on the request path.
        var hash = "SRC-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{relative}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")));
        return await CreateAndQueue(supplier, EdiStreamPublisher.LibraryPrefix + relative, info.Name, hash, chunkSize, ct);
    }

    /// <summary>Streams an uploaded file to storage while hashing it (never buffered in memory).</summary>
    public async Task<StartResult> StartFromUploadAsync(long supplierId, Stream content, string fileName, int? chunkSize, CancellationToken ct)
    {
        var supplier = await RequireSupplier(supplierId, ct);
        if (!Path.GetExtension(fileName).Equals(".edi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only .edi files can be streamed through Event Hubs.");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var hashing = new HashingStream(content, sha, MaxUploadBytes);
        var blobPath = await storage.SaveAsync(hashing, fileName, ct);
        var hash = Convert.ToHexString(sha.GetHashAndReset());
        return await CreateAndQueue(supplier, blobPath, Path.GetFileName(fileName), hash, chunkSize, ct);
    }

    public async Task<ImportFile> RestreamAsync(long importId, int? chunkSize, CancellationToken ct)
    {
        var import = await db.ImportFiles.FirstOrDefaultAsync(i => i.Id == importId, ct)
            ?? throw new KeyNotFoundException($"Import {importId} not found.");
        if (!import.IsStream)
            throw new InvalidOperationException("Only Event Hub streamed imports can be re-streamed.");
        if (import.Status is ImportStatus.Processing)
            throw new InvalidOperationException("Import is still streaming.");
        db.AuditEvents.Add(new AuditEvent(actor.ActorId, "StreamRestarted", "ImportFile",
            import.Id.ToString(), null, actor.CorrelationId));
        await db.SaveChangesAsync(ct);
        await jobs.EnqueueAsync(new StreamJob(import.Id, Clamp(chunkSize), actor.CorrelationId, CatalogueDiagnostics.CurrentTraceParent), ct);
        return import;
    }

    private async Task<StartResult> CreateAndQueue(Supplier supplier, string blobPath, string fileName,
        string hash, int? chunkSize, CancellationToken ct)
    {
        var existing = await db.ImportFiles.FirstOrDefaultAsync(i => i.SupplierId == supplier.Id && i.FileHash == hash, ct);
        if (existing is not null) return new StartResult(existing, true);

        var import = new ImportFile(supplier.Id, blobPath, fileName, hash, ImportFormats.EdiStream, actor.CorrelationId);
        db.ImportFiles.Add(import);
        db.AuditEvents.Add(new AuditEvent(actor.ActorId, "StreamRequested", "ImportFile",
            fileName, $"supplier={supplier.Code} chunk={Clamp(chunkSize)}", actor.CorrelationId));
        await db.SaveChangesAsync(ct);
        await jobs.EnqueueAsync(new StreamJob(import.Id, Clamp(chunkSize), actor.CorrelationId, CatalogueDiagnostics.CurrentTraceParent), ct);
        return new StartResult(import, false);
    }

    private async Task<Supplier> RequireSupplier(long supplierId, CancellationToken ct)
    {
        var supplier = await db.Suppliers.FindAsync([supplierId], ct)
            ?? throw new KeyNotFoundException($"Supplier {supplierId} not found.");
        if (!supplier.IsActive) throw new InvalidOperationException($"Supplier {supplier.Code} is inactive.");
        return supplier;
    }

    // Event Hubs caps an event at 1 MB; ~90 bytes per line item keeps 5,000 items well under it.
    private static int Clamp(int? chunkSize) => Math.Clamp(chunkSize ?? DefaultChunkSize, 50, 5000);
}

internal sealed class HashingStream(Stream inner, IncrementalHash hash, long maxBytes) : Stream
{
    private long _read;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Track(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        return Track(buffer.Span[..n]);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    private int Track(ReadOnlySpan<byte> data)
    {
        _read += data.Length;
        if (_read > maxBytes) throw new InvalidOperationException("File exceeds the 2 GB streaming limit.");
        hash.AppendData(data);
        return data.Length;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
