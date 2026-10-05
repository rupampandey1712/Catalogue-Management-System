using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Catalogue.Application.Media;

namespace Catalogue.Infrastructure.Storage;

/// <summary>Product images and documents in a private Blob container (Azurite locally).</summary>
public sealed class BlobMediaStorage : IMediaStorage
{
    private readonly BlobContainerClient _container;
    private readonly Lazy<Task> _ensure;

    public BlobMediaStorage(string connectionString, string container)
    {
        _container = new BlobContainerClient(connectionString, container, BlobOptions.Create());
        // Private container: files are only ever served through the API's signed URLs.
        _ensure = new Lazy<Task>(() => _container.CreateIfNotExistsAsync(PublicAccessType.None));
    }

    public string Mode => "Blob";
    public string Location => _container.Uri.ToString();

    public async Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct)
    {
        await _ensure.Value;
        await _container.GetBlobClient(path).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType, CacheControl = "private, max-age=86400" }
        }, ct);
    }

    public async Task<MediaContent?> OpenAsync(string path, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(path);
        try
        {
            var props = await blob.GetPropertiesAsync(cancellationToken: ct);
            return new MediaContent(await blob.OpenReadAsync(cancellationToken: ct), props.Value.ContentType, props.Value.ContentLength);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public Task DeleteAsync(string path, CancellationToken ct) => _container.DeleteBlobIfExistsAsync(path, cancellationToken: ct);
}

/// <summary>Disk fallback under the project's .storage/media folder.</summary>
public sealed class LocalMediaStorage(string root) : IMediaStorage
{
    public string Mode => "LocalDisk";
    public string Location => root;

    private string Full(string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path escapes the media root.");
        return full;
    }

    public async Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var fs = File.Create(full);
        await content.CopyToAsync(fs, ct);
        await File.WriteAllTextAsync(full + ".type", contentType, ct);
    }

    public Task<MediaContent?> OpenAsync(string path, CancellationToken ct)
    {
        var full = Full(path);
        if (!File.Exists(full)) return Task.FromResult<MediaContent?>(null);
        var type = File.Exists(full + ".type") ? File.ReadAllText(full + ".type") : "application/octet-stream";
        var fs = File.OpenRead(full);
        return Task.FromResult<MediaContent?>(new MediaContent(fs, type, fs.Length));
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        var full = Full(path);
        if (File.Exists(full)) File.Delete(full);
        if (File.Exists(full + ".type")) File.Delete(full + ".type");
        return Task.CompletedTask;
    }
}
