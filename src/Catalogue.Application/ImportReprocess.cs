using Catalogue.Application.Streaming;
using Catalogue.Contracts;
using Catalogue.Domain;

namespace Catalogue.Application;

/// <summary>Re-runs an import (used by the imports endpoint and by approved agent actions).</summary>
public sealed class ImportReprocessService(ICatalogueDbContext db, IImportQueue queue, EdiStreamService streams)
{
    /// <returns>False when the import does not exist.</returns>
    /// <exception cref="InvalidOperationException">The import is still running, or a stream cannot be replayed.</exception>
    public async Task<bool> ReprocessAsync(long id, string actorId, string? correlationId, CancellationToken ct)
    {
        var import = await db.ImportFiles.FindAsync([id], ct);
        if (import is null) return false;
        if (import.IsStream)
        {
            // Streamed imports are replayed through Event Hubs, not the file queue.
            await streams.RestreamAsync(id, null, ct);
            return true;
        }
        if (import.Status is ImportStatus.Received or ImportStatus.Processing)
            throw new InvalidOperationException($"Import is currently {import.Status}.");
        // Don't pre-set Processing here: the queue consumer owns that transition.
        db.AuditEvents.Add(new AuditEvent(actorId, "ImportReprocessRequested", "ImportFile", id.ToString(), null, correlationId));
        await db.SaveChangesAsync(ct);
        await queue.PublishAsync(new ImportRequested(import.Id, import.SupplierId,
            import.BlobPath, import.Format, correlationId, CatalogueDiagnostics.CurrentTraceParent), ct);
        return true;
    }
}
