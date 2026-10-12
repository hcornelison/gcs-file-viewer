using GcsFileViewer.Pricing;
using GcsFileViewer.Storage;
using GcsFileViewer.ViewModels;

namespace GcsFileViewer.Tests;

/// <summary>In-memory stand-in for Cloud Storage, keyed by project and by "bucket/prefix".</summary>
internal sealed class FakeGcsService : IGcsService
{
    public UsageTracker Usage { get; } = new();

    public string? CredentialDescription { get; set; } = "Using fake credentials";

    public string? DefaultProject { get; set; }

    public string? KeyFile { get; private set; }

    public List<string> Projects { get; } = [];

    public Dictionary<string, List<BrowserItem>> Buckets { get; } = [];

    /// <summary>What each folder directly contains, by "bucket/prefix".</summary>
    public Dictionary<string, List<BrowserItem>> Listings { get; } = [];

    /// <summary>Listings that stay in flight until the test completes them.</summary>
    public Dictionary<string, TaskCompletionSource<List<BrowserItem>>> PendingListings { get; } = [];

    /// <summary>How many items one page of a folder or search holds.</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>Object contents by "bucket/object", for downloads and previews.</summary>
    public Dictionary<string, byte[]> Contents { get; } = [];

    /// <summary>Metadata by "bucket/object"; objects without an entry get plain defaults.</summary>
    public Dictionary<string, ObjectDetails> Details { get; } = [];

    public Exception? ProjectsError { get; set; }

    public Exception? BucketsError { get; set; }

    public Exception? ListError { get; set; }

    public Exception? DownloadError { get; set; }

    public Exception? OpenBucketError { get; set; }

    public Exception? UploadError { get; set; }

    public Exception? DeleteError { get; set; }

    public Exception? TransferError { get; set; }

    public Exception? CreateFolderError { get; set; }

    public Exception? DetailsError { get; set; }

    public Exception? FindError { get; set; }

    /// <summary>Lookups of particular names as "bucket/prefix: name, name".</summary>
    public List<string> Lookups { get; } = [];

    /// <summary>Runs just before each object is downloaded, uploaded, deleted or transferred, with its "bucket/object".</summary>
    public Action<string>? BeforeEach { get; set; }

    /// <summary>Uploaded content by "bucket/object".</summary>
    public Dictionary<string, byte[]> Uploads { get; } = [];

    public List<string> CreatedFolders { get; } = [];

    /// <summary>Copies and moves as "copy|move bucket/source -> bucket/target", for both objects and folders.</summary>
    public List<string> Transfers { get; } = [];

    public List<string> DeletedObjects { get; } = [];

    public List<string> DeletedFolders { get; } = [];

    /// <summary>Preview reads as "bucket/object:maxBytes".</summary>
    public List<string> Reads { get; } = [];

    /// <summary>Metadata lookups by "bucket/object".</summary>
    public List<string> DetailLookups { get; } = [];

    /// <summary>How many requests for a page of a folder or search have been made.</summary>
    public int PageRequests { get; private set; }

    /// <summary>What folder operations report as the number of objects in any folder.</summary>
    public int ObjectsPerFolder { get; set; } = 3;

    public int BucketListCalls { get; private set; }

    public static BrowserItem Bucket(string name) => BrowserItem.ForBucket(name, storageClass: "STANDARD");

    public static BrowserItem Folder(string bucket, string path) => BrowserItem.ForFolder(bucket, path);

    public static BrowserItem File(string bucket, string path, long size = 0) =>
        BrowserItem.ForFile(bucket, path, size, modified: DateTimeOffset.UnixEpoch, storageClass: "STANDARD");

    public void UseKeyFile(string path) => KeyFile = path;

    public string? GuessDefaultProject() => DefaultProject;

    public Task<List<string>> ListProjectsAsync(CancellationToken ct = default) =>
        ProjectsError is null ? Task.FromResult(Projects.ToList()) : Task.FromException<List<string>>(ProjectsError);

    public Task<List<BrowserItem>> ListBucketsAsync(string projectId, CancellationToken ct = default)
    {
        BucketListCalls++;
        if (BucketsError is not null)
        {
            return Task.FromException<List<BrowserItem>>(BucketsError);
        }

        return Task.FromResult(Buckets.TryGetValue(projectId, out var buckets) ? buckets.ToList() : []);
    }

    public async Task<ListPage> ListPageAsync(
        string bucket, string prefix, string? pageToken = null, CancellationToken ct = default)
    {
        PageRequests++;
        return Page(await LevelAsync(bucket, prefix), pageToken);
    }

    public Task<ListPage> SearchPageAsync(
        string bucket, string prefix, string term, string? pageToken = null, CancellationToken ct = default)
    {
        PageRequests++;
        if (ListError is not null)
        {
            return Task.FromException<ListPage>(ListError);
        }

        var matches = Under(bucket, prefix)
            .Where(i => i.Kind == ItemKind.File && i.Path.Contains(term, StringComparison.Ordinal))
            .ToList();
        return Task.FromResult(Page(matches, pageToken));
    }

    public Task<List<BrowserItem>> ListRecursiveAsync(
        string bucket, string prefix, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (ListError is not null)
        {
            return Task.FromException<List<BrowserItem>>(ListError);
        }

        var items = Under(bucket, prefix)
            .Select(i => i with { Name = i.Path[prefix.Length..].TrimEnd('/') })
            .ToList();
        progress?.Report(items.Count);
        return Task.FromResult(items);
    }

    public Task<List<BrowserItem>> FindAsync(
        string bucket, string prefix, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        Lookups.Add($"{bucket}/{prefix}: {string.Join(", ", names)}");
        if (FindError is not null)
        {
            return Task.FromException<List<BrowserItem>>(FindError);
        }

        var level = Listings.TryGetValue($"{bucket}/{prefix}", out var items) ? items : [];
        return Task.FromResult(level.Where(i => names.Contains(i.Name)).ToList());
    }

    public Task<FolderSize> MeasureAsync(
        string bucket, string prefix, IProgress<FolderSize>? progress = null, CancellationToken ct = default)
    {
        if (ListError is not null)
        {
            return Task.FromException<FolderSize>(ListError);
        }

        var files = Under(bucket, prefix).Where(i => i.Kind == ItemKind.File).ToList();
        var size = new FolderSize(files.Count, files.Sum(f => f.Size ?? 0));
        progress?.Report(size);
        return Task.FromResult(size);
    }

    public Task<byte[]> ReadAsync(string bucket, string objectName, long maxBytes, CancellationToken ct = default)
    {
        Reads.Add($"{bucket}/{objectName}:{maxBytes}");
        if (DownloadError is not null)
        {
            return Task.FromException<byte[]>(DownloadError);
        }

        var content = Contents[$"{bucket}/{objectName}"];
        return Task.FromResult(content[..(int)Math.Min(maxBytes, content.Length)]);
    }

    public Task<ObjectDetails> GetDetailsAsync(string bucket, string objectName, CancellationToken ct = default)
    {
        DetailLookups.Add($"{bucket}/{objectName}");
        return DetailsError is null
            ? Task.FromResult(DetailsOf(bucket, objectName))
            : Task.FromException<ObjectDetails>(DetailsError);
    }

    public Task<ObjectDetails> UpdateDetailsAsync(
        string bucket, string objectName, string contentType, string cacheControl, CancellationToken ct = default)
    {
        if (DetailsError is not null)
        {
            return Task.FromException<ObjectDetails>(DetailsError);
        }

        var updated = DetailsOf(bucket, objectName) with { ContentType = contentType, CacheControl = cacheControl };
        Details[$"{bucket}/{objectName}"] = updated;
        return Task.FromResult(updated);
    }

    public Task<BrowserItem> OpenBucketAsync(string bucket, CancellationToken ct = default) =>
        OpenBucketError is null
            ? Task.FromResult(Bucket(bucket) with { Type = "Bucket (public)" })
            : Task.FromException<BrowserItem>(OpenBucketError);

    public async Task DownloadAsync(
        string bucket, string objectName, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        Begin($"{bucket}/{objectName}", DownloadError, ct);
        var content = Contents[$"{bucket}/{objectName}"];

        // Arrive in two halves so progress part-way through a file can be observed.
        var half = content.Length / 2;
        await destination.WriteAsync(content.AsMemory(0, half), ct);
        progress?.Report(half);
        await destination.WriteAsync(content.AsMemory(half), ct);
        progress?.Report(content.Length);
    }

    public async Task UploadAsync(
        string bucket, string objectName, Stream source, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        Begin($"{bucket}/{objectName}", UploadError, ct);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct);
        Uploads[$"{bucket}/{objectName}"] = buffer.ToArray();
        progress?.Report(buffer.Length);
    }

    public Task CreateFolderAsync(string bucket, string prefix, CancellationToken ct = default)
    {
        if (CreateFolderError is not null)
        {
            return Task.FromException(CreateFolderError);
        }

        CreatedFolders.Add($"{bucket}/{prefix}");
        return Task.CompletedTask;
    }

    public Task CopyObjectAsync(
        string sourceBucket, string sourceName, string bucket, string name, bool overwrite, CancellationToken ct = default) =>
        Transfer("copy", sourceBucket, sourceName, bucket, name, overwrite, 1, null, ct);

    public Task MoveObjectAsync(
        string sourceBucket, string sourceName, string bucket, string name, bool overwrite, CancellationToken ct = default) =>
        Transfer("move", sourceBucket, sourceName, bucket, name, overwrite, 1, null, ct);

    public Task<int> CopyFolderAsync(
        string sourceBucket, string sourcePrefix, string bucket, string prefix, bool overwrite,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Transfer("copy", sourceBucket, sourcePrefix, bucket, prefix, overwrite, ObjectsPerFolder, progress, ct);

    public Task<int> MoveFolderAsync(
        string sourceBucket, string sourcePrefix, string bucket, string prefix, bool overwrite,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Transfer("move", sourceBucket, sourcePrefix, bucket, prefix, overwrite, ObjectsPerFolder, progress, ct);

    public Task DeleteObjectAsync(string bucket, string objectName, CancellationToken ct = default)
    {
        try
        {
            Begin($"{bucket}/{objectName}", DeleteError, ct);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }

        DeletedObjects.Add($"{bucket}/{objectName}");
        return Task.CompletedTask;
    }

    public Task<int> DeleteFolderAsync(
        string bucket, string prefix, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            Begin($"{bucket}/{prefix}", DeleteError, ct);
        }
        catch (Exception ex)
        {
            return Task.FromException<int>(ex);
        }

        DeletedFolders.Add($"{bucket}/{prefix}");
        ReportSteps(progress);
        return Task.FromResult(ObjectsPerFolder);
    }

    private Task<int> Transfer(
        string kind, string sourceBucket, string source, string bucket, string target, bool overwrite, int count,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        try
        {
            Begin($"{sourceBucket}/{source}", TransferError, ct);
        }
        catch (Exception ex)
        {
            return Task.FromException<int>(ex);
        }

        Transfers.Add($"{kind} {sourceBucket}/{source} -> {bucket}/{target}{(overwrite ? " (overwrite)" : "")}");
        ReportSteps(progress);
        return Task.FromResult(count);
    }

    private void Begin(string key, Exception? error, CancellationToken ct)
    {
        BeforeEach?.Invoke(key);
        ct.ThrowIfCancellationRequested();
        if (error is not null)
        {
            throw error;
        }
    }

    private void ReportSteps(IProgress<OperationProgress>? progress)
    {
        for (var done = 1; done <= ObjectsPerFolder; done++)
        {
            progress?.Report(new OperationProgress(done, ObjectsPerFolder));
        }
    }

    // Everything at any depth under a prefix, gathered from the per-folder listings.
    private IEnumerable<BrowserItem> Under(string bucket, string prefix) =>
        Listings
            .Where(l => l.Key.StartsWith($"{bucket}/{prefix}", StringComparison.Ordinal))
            .OrderBy(l => l.Key, StringComparer.Ordinal)
            .SelectMany(l => l.Value);

    // What a folder directly contains, or the error or pending result a test has set up for it.
    private Task<List<BrowserItem>> LevelAsync(string bucket, string prefix)
    {
        var key = $"{bucket}/{prefix}";
        if (ListError is not null)
        {
            return Task.FromException<List<BrowserItem>>(ListError);
        }

        if (PendingListings.TryGetValue(key, out var pending))
        {
            return pending.Task;
        }

        return Task.FromResult(Listings.TryGetValue(key, out var items) ? items.ToList() : []);
    }

    private ListPage Page(List<BrowserItem> items, string? pageToken)
    {
        var start = pageToken is null ? 0 : int.Parse(pageToken);
        var next = start + PageSize;
        return new ListPage(items.Skip(start).Take(PageSize).ToList(), next < items.Count ? next.ToString() : null);
    }

    private ObjectDetails DetailsOf(string bucket, string objectName) =>
        Details.TryGetValue($"{bucket}/{objectName}", out var details)
            ? details
            : new ObjectDetails(
                bucket,
                objectName,
                Contents.TryGetValue($"{bucket}/{objectName}", out var content) ? content.Length : 0,
                "application/octet-stream",
                null,
                null,
                "STANDARD",
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                1,
                "bWQ1",
                "Y3Jj",
                new Dictionary<string, string>());
}

/// <summary>Collects downloads in memory, by the relative path they were written to.</summary>
internal sealed class FakeDownloadTarget : IDownloadTarget
{
    private readonly Dictionary<string, MemoryStream> _files = [];

    public List<string> Folders { get; } = [];

    public IEnumerable<string> Paths => _files.Keys;

    public Exception? OpenError { get; set; }

    public string Text(string path) => System.Text.Encoding.UTF8.GetString(_files[path].ToArray());

    public Task<Stream> OpenFileAsync(string relativePath) =>
        OpenError is null
            ? Task.FromResult<Stream>(_files[relativePath] = new MemoryStream())
            : Task.FromException<Stream>(OpenError);

    public Task CreateFolderAsync(string relativePath)
    {
        Folders.Add(relativePath);
        return Task.CompletedTask;
    }
}
