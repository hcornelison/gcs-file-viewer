using GcsFileViewer.Pricing;

namespace GcsFileViewer.Storage;

public enum ItemKind
{
    Bucket,
    Folder,
    File,
}

/// <summary>A row in the browser: a bucket, a folder (object prefix) or an object.</summary>
/// <param name="Path">Full object name or prefix inside the bucket; empty for buckets.</param>
/// <param name="StorageClass">The storage class name as the service reports it, which may be a legacy one.</param>
public sealed record BrowserItem(
    ItemKind Kind,
    string Name,
    string Bucket,
    string Path,
    long? Size,
    string Type,
    DateTimeOffset? Modified,
    string? StorageClass)
{
    public bool IsContainer => Kind != ItemKind.File;

    /// <summary>The priced tier the item's storage class belongs to.</summary>
    public StorageTier Tier => StorageTiers.Parse(StorageClass);

    /// <summary>The prefix of the folder that holds this item; empty at the top of a bucket.</summary>
    public string ParentPath
    {
        get
        {
            var trimmed = Path.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            return slash < 0 ? "" : trimmed[..(slash + 1)];
        }
    }

    public string Uri => $"gs://{Bucket}/{Path}";

    public string SizeDisplay => Size is { } s ? FormatSize(s) : "";

    public string ModifiedDisplay => Modified?.ToLocalTime().ToString("g") ?? "";

    // Folders and buckets sort ahead of files when sorting by name.
    public string SortKey => (IsContainer ? "0" : "1") + Name.ToLowerInvariant();

    /// <param name="description">What to show in the Type column, such as "Bucket (us-east1)".</param>
    public static BrowserItem ForBucket(
        string name, string description = "Bucket", DateTimeOffset? created = null, string? storageClass = null) =>
        new(ItemKind.Bucket, name, name, "", null, description, created, storageClass);

    /// <param name="path">The folder's prefix, ending in a slash.</param>
    /// <param name="name">The name to show; the last segment of the path unless given.</param>
    public static BrowserItem ForFolder(string bucket, string path, string? name = null)
    {
        var trimmed = path.TrimEnd('/');
        return new(ItemKind.Folder, name ?? trimmed[(trimmed.LastIndexOf('/') + 1)..], bucket, path, null, "Folder", null, null);
    }

    /// <param name="name">The name to show; the last segment of the path unless given.</param>
    public static BrowserItem ForFile(
        string bucket,
        string path,
        long? size,
        string type = "File",
        DateTimeOffset? modified = null,
        string? storageClass = null,
        string? name = null) =>
        new(ItemKind.File, name ?? path[(path.LastIndexOf('/') + 1)..], bucket, path, size, type, modified, storageClass);

    /// <summary>Formats a byte count using binary multiples: 1 KB is 1,024 bytes.</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
