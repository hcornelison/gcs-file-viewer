using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using GcsFileViewer.GcloudAuth;
using GcsFileViewer.Pricing;
using Google;
using Google.Apis.Storage.v1.Data;
using Google.Cloud.Storage.V1;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace GcsFileViewer.Storage;

public sealed class GcsService : IGcsService
{
    /// <summary>How many items one page of a folder holds. This is also the most Cloud Storage will return at once.</summary>
    internal const int PageSize = 1000;

    /// <summary>How many objects a folder copy, move or delete works on at once.</summary>
    private const int MaxParallelRequests = 8;

    private static readonly string[] ProjectVariables = ["GOOGLE_CLOUD_PROJECT", "CLOUDSDK_CORE_PROJECT", "GCLOUD_PROJECT"];

    private readonly GcsEnvironment _environment;
    private readonly CredentialProvider _credentials;
    private readonly UsageRecorder _usage = new();
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private StorageClient? _client;
    private StorageClient? _anonymousClient;

    public GcsService()
        : this(new GcsEnvironment())
    {
    }

    internal GcsService(GcsEnvironment environment)
    {
        _environment = environment;
        _credentials = new CredentialProvider(environment);
    }

    public UsageTracker Usage => _usage.Usage;

    public string? CredentialDescription => _credentials.Description;

    public void UseKeyFile(string path)
    {
        _credentials.UseKeyFile(path);
        _client = null;
    }

    public string? GuessDefaultProject()
    {
        foreach (var variable in ProjectVariables)
        {
            if (_environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                return value;
            }
        }

        return _environment.ReadGcloudConfig() is { } lines ? GcloudCli.ParseConfiguredProject(lines) : null;
    }

    public Task<List<string>> ListProjectsAsync(CancellationToken ct = default) => Translate(async () =>
    {
        var client = await _environment.CreateProjectsClient(await _credentials.GetAsync(ct));
        return await ProjectLister.ListActiveAsync(client, ct);
    });

    public Task<List<BrowserItem>> ListBucketsAsync(string projectId, CancellationToken ct = default) => Translate(async () =>
    {
        var client = await GetClientAsync(ct);
        var buckets = new List<BrowserItem>();
        _usage.BucketsListed();
        await foreach (var bucket in client.ListBucketsAsync(projectId).WithCancellation(ct))
        {
            _usage.SetBucket(bucket.Name, bucket.StorageClass, bucket.Location);
            var description = string.IsNullOrEmpty(bucket.Location) ? "Bucket" : $"Bucket ({bucket.Location.ToLowerInvariant()})";
            var created = Timestamp(() => bucket.TimeCreatedDateTimeOffset);
            buckets.Add(BrowserItem.ForBucket(bucket.Name, description, created, bucket.StorageClass));
        }

        SortByName(buckets);
        return buckets;
    });

    public async Task<BrowserItem> OpenBucketAsync(string bucket, CancellationToken ct = default)
    {
        Exception signedInError;
        try
        {
            await ProbeAsync(await GetClientAsync(ct), bucket, ct);
            _usage.SetAnonymous(bucket, false);
            _usage.Written(bucket);
            return BrowserItem.ForBucket(bucket);
        }
        catch (Exception ex) when (ex is GoogleApiException or GcsException)
        {
            signedInError = ex;
        }

        try
        {
            await ProbeAsync(_anonymousClient ??= _environment.CreateAnonymousClient(), bucket, ct);
            _usage.SetAnonymous(bucket, true);
            return BrowserItem.ForBucket(bucket, "Bucket (public)");
        }
        catch (GoogleApiException anonymousError)
        {
            // When signed in, that account's error says more than "anonymous caller has no access".
            var cause = signedInError as GoogleApiException ?? anonymousError;
            var translated = GcsException.From(cause);
            throw new GcsException($"Can't open bucket \"{bucket}\": {translated.Message}", translated.Error, cause);
        }
    }

    public Task<ListPage> ListPageAsync(
        string bucket, string prefix, string? pageToken = null, CancellationToken ct = default) => Translate(async () =>
    {
        var client = await GetClientAsync(bucket, ct);
        var options = new ListObjectsOptions { Delimiter = "/", PageToken = pageToken, PageSize = PageSize };
        await foreach (var page in PagesAsync(client, bucket, prefix, options, ct))
        {
            var folders = (page.Prefixes ?? []).Select(folder => BrowserItem.ForFolder(bucket, folder)).ToList();

            // Skip the zero-byte placeholder object some tools create for the folder itself.
            var files = (page.Items ?? []).Where(obj => obj.Name != prefix).Select(obj => ToFile(bucket, obj)).ToList();
            SortByName(folders);
            SortByName(files);
            return new ListPage([.. folders, .. files], NullIfEmpty(page.NextPageToken));
        }

        return new ListPage([], null);
    });

    public Task<ListPage> SearchPageAsync(
        string bucket, string prefix, string term, string? pageToken = null, CancellationToken ct = default) => Translate(async () =>
    {
        var client = await GetClientAsync(bucket, ct);
        var matches = new List<BrowserItem>();
        var token = pageToken;

        // The service can return a page with no matches but more to scan, so keep going until there is something to show.
        do
        {
            var options = new ListObjectsOptions { MatchGlob = SearchGlob(prefix, term), PageToken = token };
            token = null;
            await foreach (var page in PagesAsync(client, bucket, prefix, options, ct))
            {
                matches.AddRange((page.Items ?? []).Where(obj => !IsPlaceholder(obj)).Select(obj => ToFile(bucket, obj)));
                token = NullIfEmpty(page.NextPageToken);
                break;
            }
        }
        while (matches.Count == 0 && token is not null);

        return new ListPage(matches, token);
    });

    public Task<List<BrowserItem>> ListRecursiveAsync(
        string bucket, string prefix, IProgress<int>? progress = null, CancellationToken ct = default) => Translate(async () =>
    {
        var client = await GetClientAsync(bucket, ct);
        var items = new List<BrowserItem>();
        await foreach (var page in PagesAsync(client, bucket, prefix, null, ct))
        {
            foreach (var obj in page.Items ?? [])
            {
                var relative = obj.Name[prefix.Length..];
                if (!IsPlaceholder(obj))
                {
                    items.Add(ToFile(bucket, obj, relative));
                }
                else if (relative.Length > 1)
                {
                    // A placeholder for a subfolder, possibly an empty one. The one for the prefix itself has no name left.
                    items.Add(BrowserItem.ForFolder(bucket, obj.Name, relative.TrimEnd('/')));
                }
            }

            progress?.Report(items.Count);
        }

        return items;
    });

    public Task<List<BrowserItem>> FindAsync(
        string bucket, string prefix, IReadOnlyCollection<string> names, CancellationToken ct = default) => Translate(async () =>
    {
        var client = await GetClientAsync(bucket, ct);
        var found = new List<BrowserItem>();
        await ForEachAsync(names.Distinct().ToList(), ct, async (name, token) =>
        {
            var file = await TryGetObjectAsync(client, bucket, prefix + name, token);
            var folder = await HasObjectsUnderAsync(client, bucket, prefix + name + "/", token);
            lock (found)
            {
                if (file is not null)
                {
                    found.Add(ToFile(bucket, file));
                }

                if (folder)
                {
                    found.Add(BrowserItem.ForFolder(bucket, prefix + name + "/"));
                }
            }
        });

        SortByName(found);
        return found;
    });

    public Task<FolderSize> MeasureAsync(
        string bucket, string prefix, IProgress<FolderSize>? progress = null, CancellationToken ct = default) => Translate(async () =>
    {
        var client = await GetClientAsync(bucket, ct);
        var size = new FolderSize(0, 0);
        await foreach (var page in PagesAsync(client, bucket, prefix, null, ct))
        {
            // Folder placeholders aren't files, so they don't count.
            foreach (var obj in (page.Items ?? []).Where(obj => !IsPlaceholder(obj)))
            {
                size = new FolderSize(size.Objects + 1, size.Bytes + SizeOf(obj));
            }

            progress?.Report(size);
        }

        return size;
    });

    public Task<byte[]> ReadAsync(string bucket, string objectName, long maxBytes, CancellationToken ct = default) => Translate(async () =>
    {
        var client = await GetClientAsync(bucket, ct);
        using var buffer = new MemoryStream();
        // Part of an object can't be checked against the hash of the whole.
        var options = new DownloadObjectOptions
        {
            Range = new RangeHeaderValue(0, maxBytes - 1),
            DownloadValidationMode = DownloadValidationMode.Never,
        };
        await client.DownloadObjectAsync(bucket, objectName, buffer, options, ct);
        _usage.DataRead(bucket, objectName, buffer.Length);
        return buffer.ToArray();
    });

    public Task<ObjectDetails> GetDetailsAsync(string bucket, string objectName, CancellationToken ct = default) => Translate(async () =>
    {
        var client = await GetClientAsync(bucket, ct);
        var obj = await client.GetObjectAsync(bucket, objectName, cancellationToken: ct);
        _usage.MetadataRead(bucket, obj.StorageClass);
        return ToDetails(obj);
    });

    public Task<ObjectDetails> UpdateDetailsAsync(
        string bucket, string objectName, string contentType, string cacheControl, CancellationToken ct = default) => Translate(async () =>
    {
        var client = await GetClientAsync(bucket, ct);
        var patch = new StorageObject { Bucket = bucket, Name = objectName, ContentType = contentType, CacheControl = cacheControl };
        var obj = await client.PatchObjectAsync(patch, cancellationToken: ct);
        _usage.Written(bucket);
        return ToDetails(obj);
    });

    public Task DownloadAsync(
        string bucket, string objectName, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default) =>
        Translate(async () =>
        {
            var client = await GetClientAsync(bucket, ct);
            var counted = new CountingStream(destination, progress);
            var completed = false;
            try
            {
                await client.DownloadObjectAsync(bucket, objectName, counted, cancellationToken: ct);
                completed = true;
            }
            finally
            {
                // A download that fails part-way is still charged for what was sent.
                if (completed || counted.BytesWritten > 0)
                {
                    _usage.DataRead(bucket, objectName, counted.BytesWritten);
                }
            }
        });

    public Task UploadAsync(
        string bucket, string objectName, Stream source, IProgress<long>? progress = null, CancellationToken ct = default) =>
        Translate(async () =>
        {
            var client = await GetClientAsync(bucket, ct);
            await client.UploadObjectAsync(bucket, objectName, ContentTypes.Guess(objectName), source, cancellationToken: ct,
                progress: progress is null ? null : new UploadProgress(progress));
            _usage.ObjectWritten(bucket, objectName);
        });

    public Task CreateFolderAsync(string bucket, string prefix, CancellationToken ct = default) => Translate(async () =>
    {
        var client = await GetClientAsync(bucket, ct);
        await client.UploadObjectAsync(bucket, prefix, "application/x-directory", Stream.Null,
            new UploadObjectOptions { IfGenerationMatch = 0 }, ct);
        _usage.Written(bucket);
    });

    public Task CopyObjectAsync(
        string sourceBucket, string sourceObject, string destinationBucket, string destinationObject, bool overwrite,
        CancellationToken ct = default) => Translate(async () =>
        {
            var client = await GetClientAsync(destinationBucket, ct);
            await CopyAsync(client, sourceBucket, sourceObject, destinationBucket, destinationObject, CopyOptions(overwrite), ct);
        });

    public Task MoveObjectAsync(
        string sourceBucket, string sourceObject, string destinationBucket, string destinationObject, bool overwrite,
        CancellationToken ct = default) => Translate(async () =>
        {
            var client = await GetClientAsync(destinationBucket, ct);
            await CopyAsync(client, sourceBucket, sourceObject, destinationBucket, destinationObject, CopyOptions(overwrite), ct);
            await DeleteAsync(client, sourceBucket, sourceObject, ct);
        });

    public Task<int> CopyFolderAsync(
        string sourceBucket, string sourcePrefix, string destinationBucket, string destinationPrefix, bool overwrite,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        TransferFolderAsync(sourceBucket, sourcePrefix, destinationBucket, destinationPrefix, overwrite, deleteSource: false, progress, ct);

    public Task<int> MoveFolderAsync(
        string sourceBucket, string sourcePrefix, string destinationBucket, string destinationPrefix, bool overwrite,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        TransferFolderAsync(sourceBucket, sourcePrefix, destinationBucket, destinationPrefix, overwrite, deleteSource: true, progress, ct);

    public Task DeleteObjectAsync(string bucket, string objectName, CancellationToken ct = default) => Translate(async () =>
    {
        var client = await GetClientAsync(bucket, ct);
        await DeleteAsync(client, bucket, objectName, ct);
    });

    public Task<int> DeleteFolderAsync(
        string bucket, string prefix, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
        Translate(async () =>
        {
            var client = await GetClientAsync(bucket, ct);
            var names = await ListNamesAsync(client, bucket, prefix, ct);
            var steps = new StepCounter(progress, names.Count);
            await ForEachAsync(names, ct, async (name, token) =>
            {
                await DeleteAsync(client, bucket, name, token);
                steps.Next();
            });
            return names.Count;
        });

    /// <summary>
    /// The glob that finds <paramref name="term"/> anywhere in a path under <paramref name="prefix"/>. A term that
    /// already has wildcards is taken as a glob relative to the prefix.
    /// </summary>
    internal static string SearchGlob(string prefix, string term) =>
        term.IndexOfAny(['*', '?', '[', '{']) >= 0
            ? EscapeGlob(prefix) + term
            : EscapeGlob(prefix) + "**" + term + "**";

    private Task<int> TransferFolderAsync(
        string sourceBucket, string sourcePrefix, string destinationBucket, string destinationPrefix, bool overwrite,
        bool deleteSource, IProgress<OperationProgress>? progress, CancellationToken ct) => Translate(async () =>
        {
            var client = await GetClientAsync(destinationBucket, ct);
            var names = await ListNamesAsync(client, sourceBucket, sourcePrefix, ct);
            var options = CopyOptions(overwrite);

            // A move makes two requests per object: the copy, then the delete.
            var steps = new StepCounter(progress, deleteSource ? names.Count * 2 : names.Count);

            // Copy everything first so a failure part-way leaves the original folder intact.
            await ForEachAsync(names, ct, async (name, token) =>
            {
                var target = destinationPrefix + name[sourcePrefix.Length..];
                await CopyAsync(client, sourceBucket, name, destinationBucket, target, options, token);
                steps.Next();
            });
            if (deleteSource)
            {
                await ForEachAsync(names, ct, async (name, token) =>
                {
                    await DeleteAsync(client, sourceBucket, name, token);
                    steps.Next();
                });
            }

            return names.Count;
        });

    // Every listing goes through here, so each page is counted as a request and its objects are remembered.
    private async IAsyncEnumerable<Objects> PagesAsync(
        StorageClient client, string bucket, string prefix, ListObjectsOptions? options, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var page in client.ListObjectsAsync(bucket, prefix, options).AsRawResponses().WithCancellation(ct))
        {
            _usage.Written(bucket);
            foreach (var obj in page.Items ?? [])
            {
                _usage.Remember(bucket, obj.Name, obj.StorageClass, SizeOf(obj));
            }

            yield return page;
        }
    }

    private async Task<List<string>> ListNamesAsync(StorageClient client, string bucket, string prefix, CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var page in PagesAsync(client, bucket, prefix, null, ct))
        {
            names.AddRange((page.Items ?? []).Select(obj => obj.Name));
        }

        return names;
    }

    private async Task CopyAsync(
        StorageClient client, string sourceBucket, string sourceObject, string destinationBucket, string destinationObject,
        CopyObjectOptions? options, CancellationToken ct)
    {
        await client.CopyObjectAsync(sourceBucket, sourceObject, destinationBucket, destinationObject, options, ct);
        _usage.Copied(sourceBucket, sourceObject, destinationBucket, destinationObject);
    }

    private async Task DeleteAsync(StorageClient client, string bucket, string objectName, CancellationToken ct)
    {
        await client.DeleteObjectAsync(bucket, objectName, cancellationToken: ct);
        _usage.Deleted(bucket, objectName);
    }

    private async Task<StorageObject?> TryGetObjectAsync(StorageClient client, string bucket, string objectName, CancellationToken ct)
    {
        try
        {
            var obj = await client.GetObjectAsync(bucket, objectName, cancellationToken: ct);
            _usage.MetadataRead(bucket, obj.StorageClass);
            return obj;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Asking about an object that isn't there is still a request.
            _usage.MetadataRead(bucket, null);
            return null;
        }
    }

    private async Task<bool> HasObjectsUnderAsync(StorageClient client, string bucket, string prefix, CancellationToken ct)
    {
        var options = new ListObjectsOptions { PageSize = 1 };
        await foreach (var page in PagesAsync(client, bucket, prefix, options, ct))
        {
            return page.Items is { Count: > 0 };
        }

        return false;
    }

    private static async Task ProbeAsync(StorageClient client, string bucket, CancellationToken ct)
    {
        var options = new ListObjectsOptions { Delimiter = "/", PageSize = 1 };
        await foreach (var _ in client.ListObjectsAsync(bucket, "", options).AsRawResponses().WithCancellation(ct))
        {
            break;
        }
    }

    private static Task ForEachAsync(
        IReadOnlyCollection<string> names, CancellationToken ct, Func<string, CancellationToken, ValueTask> body) =>
        Parallel.ForEachAsync(
            names, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelRequests, CancellationToken = ct }, body);

    private async Task<StorageClient> GetClientAsync(string bucket, CancellationToken ct) =>
        _usage.IsAnonymous(bucket) ? _anonymousClient! : await GetClientAsync(ct);

    private async Task<StorageClient> GetClientAsync(CancellationToken ct)
    {
        var credential = await _credentials.GetAsync(ct);

        // One caller at a time, so requests that start together share one client.
        await _clientGate.WaitAsync(ct);
        try
        {
            return _client ??= await _environment.CreateClient(credential);
        }
        finally
        {
            _clientGate.Release();
        }
    }

    // Callers only ever see GcsException for a failed request, whichever client library raised it.
    private static async Task<T> Translate<T>(Func<Task<T>> request)
    {
        try
        {
            return await request();
        }
        catch (GoogleApiException ex)
        {
            throw GcsException.From(ex);
        }
        catch (Grpc.Core.RpcException ex)
        {
            throw new GcsException(ex.Status.Detail is { Length: > 0 } detail ? detail : ex.Message, GcsError.Other, ex);
        }
    }

    private static Task Translate(Func<Task> request) => Translate(async () =>
    {
        await request();
        return true;
    });

    // Without overwrite, only copy onto a name that has no live object.
    private static CopyObjectOptions? CopyOptions(bool overwrite) =>
        overwrite ? null : new CopyObjectOptions { IfGenerationMatch = 0 };

    // Makes wildcard characters in a literal path match only themselves.
    private static string EscapeGlob(string text) =>
        string.Concat(text.Select(c => c is '*' or '?' or '[' or '{' ? $"[{c}]" : c.ToString()));

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // A zero-byte object whose name ends in a slash stands in for a folder.
    private static bool IsPlaceholder(StorageObject obj) => obj.Name.EndsWith('/');

    private static long SizeOf(StorageObject obj) => (long)(obj.Size ?? 0);

    // The client library parses timestamps when they are read, and throws if the server sent one it can't parse.
    // A date that can't be shown is no reason to fail a whole listing.
    private static DateTimeOffset? Timestamp(Func<DateTimeOffset?> read)
    {
        try
        {
            return read();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void SortByName(List<BrowserItem> items) =>
        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    private static BrowserItem ToFile(string bucket, StorageObject obj, string? name = null) => BrowserItem.ForFile(
        bucket,
        obj.Name,
        (long?)obj.Size,
        ContentTypes.Describe(obj.Name, obj.ContentType),
        Timestamp(() => obj.UpdatedDateTimeOffset),
        obj.StorageClass,
        name);

    private static ObjectDetails ToDetails(StorageObject obj) => new(
        obj.Bucket,
        obj.Name,
        SizeOf(obj),
        obj.ContentType,
        obj.CacheControl,
        obj.ContentEncoding,
        obj.StorageClass,
        Timestamp(() => obj.TimeCreatedDateTimeOffset),
        Timestamp(() => obj.UpdatedDateTimeOffset),
        obj.Generation,
        obj.Md5Hash,
        obj.Crc32c,
        obj.Metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(obj.Metadata));

    // Counts finished requests across the threads of a parallel loop and reports each one.
    private sealed class StepCounter(IProgress<OperationProgress>? progress, int total)
    {
        private int _done;

        public void Next() => progress?.Report(new OperationProgress(Interlocked.Increment(ref _done), total));
    }

    // Reports just the byte count from the client library's upload progress.
    private sealed class UploadProgress(IProgress<long> progress) : IProgress<Google.Apis.Upload.IUploadProgress>
    {
        public void Report(Google.Apis.Upload.IUploadProgress value) => progress.Report(value.BytesSent);
    }
}
