using GcsFileViewer.Pricing;

namespace GcsFileViewer.Storage;

/// <summary>
/// Access to Cloud Storage for the browser. A request that fails throws <see cref="GcsException"/>; a cancelled
/// one throws <see cref="OperationCanceledException"/>.
/// </summary>
public interface IGcsService
{
    /// <summary>Tally of the billable requests and downloads made through this service.</summary>
    UsageTracker Usage { get; }

    /// <summary>Which credentials are in use, once they have been resolved.</summary>
    string? CredentialDescription { get; }

    /// <summary>Switches from the automatically discovered credentials to a service account key file.</summary>
    void UseKeyFile(string path);

    /// <summary>Best guess at the project the user wants, from the environment or gcloud's config.</summary>
    string? GuessDefaultProject();

    /// <summary>Lists the IDs of active projects the credentials can see.</summary>
    Task<List<string>> ListProjectsAsync(CancellationToken ct = default);

    Task<List<BrowserItem>> ListBucketsAsync(string projectId, CancellationToken ct = default);

    /// <summary>
    /// Checks that a bucket can be listed, using the signed-in account or, failing that, anonymous access
    /// (which works for public buckets). Later calls for the bucket use whichever one worked.
    /// </summary>
    Task<BrowserItem> OpenBucketAsync(string bucket, CancellationToken ct = default);

    /// <summary>Lists one page of the folders and objects directly under <paramref name="prefix"/>.</summary>
    Task<ListPage> ListPageAsync(string bucket, string prefix, string? pageToken = null, CancellationToken ct = default);

    /// <summary>
    /// Finds objects anywhere under <paramref name="prefix"/> whose path contains <paramref name="term"/>, one
    /// page at a time. A term with wildcards (* ? [ {) is used as a glob relative to the prefix instead.
    /// </summary>
    Task<ListPage> SearchPageAsync(
        string bucket, string prefix, string term, string? pageToken = null, CancellationToken ct = default);

    /// <summary>
    /// Lists every object under <paramref name="prefix"/> at any depth. Names are paths relative to the prefix;
    /// folder placeholder objects come back as folders.
    /// </summary>
    Task<List<BrowserItem>> ListRecursiveAsync(
        string bucket, string prefix, IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the files and folders directly under <paramref name="prefix"/> that have one of the given names.
    /// Costs two requests per name, so it suits checking a few names in a folder too large to list.
    /// </summary>
    Task<List<BrowserItem>> FindAsync(
        string bucket, string prefix, IReadOnlyCollection<string> names, CancellationToken ct = default);

    /// <summary>Counts the objects under <paramref name="prefix"/> and adds up their sizes.</summary>
    Task<FolderSize> MeasureAsync(
        string bucket, string prefix, IProgress<FolderSize>? progress = null, CancellationToken ct = default);

    /// <summary>Reads up to the first <paramref name="maxBytes"/> of an object, for previewing it.</summary>
    Task<byte[]> ReadAsync(string bucket, string objectName, long maxBytes, CancellationToken ct = default);

    Task<ObjectDetails> GetDetailsAsync(string bucket, string objectName, CancellationToken ct = default);

    /// <summary>Sets an object's content type and Cache-Control header and returns its metadata afterwards.</summary>
    Task<ObjectDetails> UpdateDetailsAsync(
        string bucket, string objectName, string contentType, string cacheControl, CancellationToken ct = default);

    /// <summary>Downloads an object, reporting the number of bytes written so far.</summary>
    Task DownloadAsync(
        string bucket, string objectName, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>Creates the object, replacing any existing object with the same name, reporting the bytes sent so far.</summary>
    Task UploadAsync(
        string bucket, string objectName, Stream source, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>Creates an empty folder by writing the zero-byte placeholder object named <paramref name="prefix"/>.</summary>
    Task CreateFolderAsync(string bucket, string prefix, CancellationToken ct = default);

    /// <summary>Copies an object. Unless <paramref name="overwrite"/> is set, fails if the destination name is taken.</summary>
    Task CopyObjectAsync(
        string sourceBucket, string sourceObject, string destinationBucket, string destinationObject, bool overwrite,
        CancellationToken ct = default);

    /// <summary>Copies an object as <see cref="CopyObjectAsync"/> does, then deletes the original.</summary>
    Task MoveObjectAsync(
        string sourceBucket, string sourceObject, string destinationBucket, string destinationObject, bool overwrite,
        CancellationToken ct = default);

    /// <summary>Copies every object under one prefix to another and returns how many there were.</summary>
    Task<int> CopyFolderAsync(
        string sourceBucket, string sourcePrefix, string destinationBucket, string destinationPrefix, bool overwrite,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Copies a folder as <see cref="CopyFolderAsync"/> does, then deletes the originals. All copies are made
    /// before any original is deleted.
    /// </summary>
    Task<int> MoveFolderAsync(
        string sourceBucket, string sourcePrefix, string destinationBucket, string destinationPrefix, bool overwrite,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default);

    Task DeleteObjectAsync(string bucket, string objectName, CancellationToken ct = default);

    /// <summary>Deletes every object under <paramref name="prefix"/> and returns how many there were.</summary>
    Task<int> DeleteFolderAsync(
        string bucket, string prefix, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
}
