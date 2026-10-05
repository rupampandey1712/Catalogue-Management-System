using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Catalogue.Application;
using Microsoft.Extensions.Configuration;

namespace Catalogue.Infrastructure.Storage;

/// <summary>
/// Where storage lives on disk. Everything defaults to folders inside the repository
/// (<c>.storage/</c> for the disk fallback, <c>.azurite/</c> for the Azurite emulator), never %TEMP%,
/// so all data of the learning project stays in the project location.
/// </summary>
public static class StoragePaths
{
    /// <summary>Resolves <c>Storage:Root</c> (default <c>../../.storage</c>) against the host's content root.</summary>
    public static string Root(IConfiguration config, string? contentRoot)
    {
        var configured = config["Storage:Root"] ?? Path.Combine("..", "..", ".storage");
        var basePath = contentRoot ?? AppContext.BaseDirectory;
        return Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(basePath, configured));
    }
}

/// <summary>Local disk storage (fallback when no Blob endpoint is reachable).</summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;

    public LocalFileStorage(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public async Task<string> SaveAsync(Stream content, string fileName, CancellationToken ct)
    {
        var safeName = Path.GetFileName(fileName);
        var path = Path.Combine(_root, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}-{safeName}");
        await using var fs = File.Create(path);
        await content.CopyToAsync(fs, ct);
        return path;
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        => Task.FromResult<Stream>(File.OpenRead(path));
}

/// <summary>Azure Blob / Azurite storage for raw supplier files.</summary>
public sealed class BlobFileStorage : IFileStorage
{
    private readonly BlobContainerClient _container;
    private readonly Lazy<Task> _ensure;

    public BlobFileStorage(string connectionString, string container)
    {
        _container = new BlobContainerClient(connectionString, container, BlobOptions.Create());
        _ensure = new Lazy<Task>(() => _container.CreateIfNotExistsAsync(PublicAccessType.None));
    }

    public async Task<string> SaveAsync(Stream content, string fileName, CancellationToken ct)
    {
        await _ensure.Value;
        var blobName = $"raw/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}-{Path.GetFileName(fileName)}";
        await _container.UploadBlobAsync(blobName, content, ct);
        return blobName;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        => await _container.GetBlobClient(path).OpenReadAsync(cancellationToken: ct);
}

internal static class BlobOptions
{
    public static BlobClientOptions Create()
    {
        var options = new BlobClientOptions();
        options.Retry.Mode = Azure.Core.RetryMode.Exponential;
        options.Retry.MaxRetries = 6;
        options.Retry.Delay = TimeSpan.FromMilliseconds(500);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(30);
        options.Retry.NetworkTimeout = TimeSpan.FromMinutes(5); // multi-hundred-MB uploads
        return options;
    }

    /// <summary>Quick reachability probe used at startup to decide Blob vs disk fallback.</summary>
    public static bool IsReachable(string connectionString, TimeSpan timeout)
    {
        try
        {
            var options = new BlobClientOptions();
            options.Retry.MaxRetries = 0;
            options.Retry.NetworkTimeout = timeout;
            var service = new BlobServiceClient(connectionString, options);
            using var cts = new CancellationTokenSource(timeout);
            service.GetProperties(cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
