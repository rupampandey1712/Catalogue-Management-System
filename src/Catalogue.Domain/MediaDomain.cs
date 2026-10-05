namespace Catalogue.Domain;

public enum AssetKind { Image, Datasheet, Manual, EnergyLabel, Certificate, Drawing, Other }
public enum AssetSource { Upload, BulkZip, SupplierUrl }

/// <summary>
/// An image or document attached to a product. Bytes live in Blob storage (or the disk fallback);
/// this row is the catalogue's record of it: kind, title, size, hash and where it came from.
/// </summary>
public sealed class ProductAsset
{
    public long Id { get; private set; }
    public long ProductId { get; private set; }
    public Product? Product { get; private set; }
    public AssetKind Kind { get; private set; }
    public string Title { get; private set; } = null!;
    public string FileName { get; private set; } = null!;
    public string ContentType { get; private set; } = null!;
    public long SizeBytes { get; private set; }
    /// <summary>SHA-256 of the bytes: de-duplicates re-uploads and doubles as an ETag.</summary>
    public string Sha256 { get; private set; } = null!;
    public string StoragePath { get; private set; } = null!;
    public string? ThumbnailPath { get; private set; }
    public int? Width { get; private set; }
    public int? Height { get; private set; }
    public bool IsPrimary { get; private set; }
    public int SortOrder { get; private set; }
    public AssetSource Source { get; private set; }
    public string? SourceUrl { get; private set; }
    public long? SupplierId { get; private set; }
    public string UploadedBy { get; private set; } = "system";
    public DateTime UploadedAtUtc { get; private set; } = DateTime.UtcNow;

    private ProductAsset() { }

    public ProductAsset(long productId, AssetKind kind, string title, string fileName, string contentType, long sizeBytes,
        string sha256, string storagePath, string? thumbnailPath, int? width, int? height, AssetSource source,
        string uploadedBy, string? sourceUrl = null, long? supplierId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        if (sizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        ProductId = productId;
        Kind = kind;
        Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName) : title.Trim();
        FileName = Path.GetFileName(fileName);
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StoragePath = storagePath;
        ThumbnailPath = thumbnailPath;
        Width = width;
        Height = height;
        Source = source;
        SourceUrl = sourceUrl;
        SupplierId = supplierId;
        UploadedBy = uploadedBy;
    }

    public void SetPrimary(bool primary) => IsPrimary = primary;
    public void Update(string title, AssetKind kind, int sortOrder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title.Trim();
        // An image cannot become a document (and vice versa) - the bytes decide what it is.
        if ((Kind == AssetKind.Image) == (kind == AssetKind.Image)) Kind = kind;
        SortOrder = sortOrder;
    }
}

public enum MediaFetchStatus { Pending, Done, Failed }

/// <summary>
/// A media URL referenced by a supplier feed (CSV image_url / EDI IMD+F+IMG). Downloaded in the
/// background with retries; EDI itself only carries the link, never the bytes.
/// </summary>
public sealed class MediaFetchRequest
{
    public long Id { get; private set; }
    public long ProductId { get; private set; }
    public long? SupplierId { get; private set; }
    public AssetKind Kind { get; private set; }
    public string Url { get; private set; } = null!;
    public MediaFetchStatus Status { get; private set; } = MediaFetchStatus.Pending;
    public int Attempts { get; private set; }
    public DateTime NextAttemptUtc { get; private set; } = DateTime.UtcNow;
    public string? LastError { get; private set; }
    public long? AssetId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;

    private MediaFetchRequest() { }

    public MediaFetchRequest(long productId, long? supplierId, AssetKind kind, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ProductId = productId;
        SupplierId = supplierId;
        Kind = kind;
        Url = url.Trim();
    }

    public const int MaxAttempts = 5;

    public void Succeeded(long assetId) { Status = MediaFetchStatus.Done; AssetId = assetId; LastError = null; }

    /// <summary>Permanent failures stop immediately; transient ones back off exponentially.</summary>
    public void Failed(string error, bool permanent)
    {
        Attempts++;
        LastError = error.Length > 500 ? error[..500] : error;
        if (permanent || Attempts >= MaxAttempts) Status = MediaFetchStatus.Failed;
        else NextAttemptUtc = DateTime.UtcNow.AddSeconds(Math.Pow(4, Attempts) * 15); // 1m, 4m, 16m, 64m
    }
}
