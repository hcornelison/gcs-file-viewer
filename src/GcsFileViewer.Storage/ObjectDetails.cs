namespace GcsFileViewer.Storage;

/// <summary>An object's metadata, as shown in the details pane.</summary>
public sealed record ObjectDetails(
    string Bucket,
    string Name,
    long Size,
    string? ContentType,
    string? CacheControl,
    string? ContentEncoding,
    string? StorageClass,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    long? Generation,
    string? Md5,
    string? Crc32c,
    IReadOnlyDictionary<string, string> Metadata);
