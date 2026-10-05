using System.Security.Cryptography;
using Catalogue.Contracts;
using Catalogue.Domain;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Application;

public sealed class ImportService(ICatalogueDbContext db, IFileStorage storage, IImportQueue queue, IActorContext actor)
{
    private static readonly HashSet<string> AllowedExtensions = [".csv", ".edi"];
    private const long MaxFileBytes = 25 * 1024 * 1024;

    /// <summary>Stores the raw file, deduplicates by hash, creates the import record and queues processing.</summary>
    public async Task<ImportFile> StartImportAsync(long supplierId, Stream content, string fileName, CancellationToken ct)
    {
        var supplier = await db.Suppliers.FindAsync([supplierId], ct)
            ?? throw new KeyNotFoundException($"Supplier {supplierId} not found.");
        if (!supplier.IsActive)
            throw new InvalidOperationException($"Supplier {supplier.Code} is inactive.");

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            throw new InvalidOperationException($"File type '{ext}' is not allowed.");
        if (content.CanSeek && content.Length > MaxFileBytes)
            throw new InvalidOperationException("File exceeds the 25 MB limit.");

        // Buffer so we can hash and store idempotently.
        var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (bytes.LongLength > MaxFileBytes)
            throw new InvalidOperationException("File exceeds the 25 MB limit.");

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var existing = await db.ImportFiles
            .FirstOrDefaultAsync(i => i.SupplierId == supplierId && i.FileHash == hash, ct);
        if (existing is not null)
            return existing; // idempotent: same file already received

        ms.Position = 0;
        var blobPath = await storage.SaveAsync(ms, fileName, ct);

        var format = ext == ".edi" ? "EDI" : "CSV";
        var import = new ImportFile(supplierId, blobPath, fileName, hash, format, actor.CorrelationId);
        db.ImportFiles.Add(import);
        db.AuditEvents.Add(new AuditEvent(actor.ActorId, "ImportReceived", "ImportFile",
            "pending", $"{fileName} ({bytes.Length} bytes)", actor.CorrelationId));
        await db.SaveChangesAsync(ct);

        await queue.PublishAsync(new ImportRequested(import.Id, supplierId, blobPath, format, actor.CorrelationId, CatalogueDiagnostics.CurrentTraceParent), ct);
        return import;
    }
}
