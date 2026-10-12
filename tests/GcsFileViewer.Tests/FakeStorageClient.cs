using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Google;
using Google.Api.Gax;
using Google.Apis.Requests;
using Google.Apis.Storage.v1.Data;
using Google.Cloud.Storage.V1;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace GcsFileViewer.Tests;

/// <summary>An in-memory Cloud Storage client that behaves like the real one for the calls GcsService makes.</summary>
internal sealed class FakeStorageClient : StorageClient
{
    private readonly ConcurrentDictionary<(string Bucket, string Name), Stored> _objects = new();
    private int _calls;

    public Dictionary<string, List<Bucket>> BucketsByProject { get; } = [];

    /// <summary>How many objects a listing returns per page.</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>When set, every call fails with this error, as it would without permission.</summary>
    public GoogleApiException? Error { get; set; }

    /// <summary>Number of calls of any kind made to this client.</summary>
    public int Calls => _calls;

    public static GoogleApiException ApiError(HttpStatusCode status, string message) =>
        new("storage", message) { HttpStatusCode = status, Error = new RequestError { Code = (int)status, Message = message } };

    public void Put(
        string bucket,
        string name,
        string content = "",
        string? contentType = null,
        string storageClass = "STANDARD",
        Dictionary<string, string>? metadata = null) =>
        _objects[(bucket, name)] = new Stored(Encoding.UTF8.GetBytes(content), contentType, storageClass)
        {
            Metadata = metadata,
        };

    /// <summary>Makes an object report timestamps the client library can't parse, as a faulty server might.</summary>
    public void CorruptTimestamps(string bucket, string name) =>
        _objects[(bucket, name)] = _objects[(bucket, name)] with { BadTimestamps = true };

    /// <summary>Whether the last ranged download asked for its data to be checked against the object's hash.</summary>
    public DownloadValidationMode? LastRangeValidation { get; private set; }

    public bool Exists(string bucket, string name) => _objects.ContainsKey((bucket, name));

    public string Content(string bucket, string name) => Encoding.UTF8.GetString(_objects[(bucket, name)].Content);

    public string? ContentType(string bucket, string name) => _objects[(bucket, name)].ContentType;

    public string? CacheControl(string bucket, string name) => _objects[(bucket, name)].CacheControl;

    public IEnumerable<string> Names(string bucket) =>
        _objects.Keys.Where(k => k.Bucket == bucket).Select(k => k.Name).Order(StringComparer.Ordinal);

    public override PagedAsyncEnumerable<Buckets, Bucket> ListBucketsAsync(string projectId, ListBucketsOptions? options = null)
    {
        Called();
        var buckets = BucketsByProject.GetValueOrDefault(projectId, []);
        return new Paged<Buckets, Bucket>(Error, [new Buckets { Items = buckets }], page => page.Items);
    }

    public override PagedAsyncEnumerable<Objects, StorageObject> ListObjectsAsync(
        string bucket, string? prefix = null, ListObjectsOptions? options = null)
    {
        Called();
        prefix ??= "";
        var glob = options?.MatchGlob is { } pattern ? GlobToRegex(pattern) : null;
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        var items = new List<StorageObject>();
        foreach (var name in Names(bucket).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            if (glob is not null && !glob.IsMatch(name))
            {
                continue;
            }

            var slash = options?.Delimiter is null ? -1 : name.IndexOf('/', prefix.Length);
            if (slash >= 0)
            {
                folders.Add(name[..(slash + 1)]);
            }
            else
            {
                items.Add(Describe(bucket, name));
            }
        }

        // Each page carries a token saying where the next one starts. Folders arrive with the first page, as they
        // do from the real service for small listings.
        var start = options?.PageToken is { } token ? int.Parse(token, CultureInfo.InvariantCulture) : 0;
        var pages = new List<Objects>();
        for (var offset = start; offset < items.Count || pages.Count == 0; offset += PageSize)
        {
            var next = offset + PageSize;
            pages.Add(new Objects
            {
                Items = items.Skip(offset).Take(PageSize).ToList(),
                NextPageToken = next < items.Count ? next.ToString(CultureInfo.InvariantCulture) : null,
            });
        }

        if (start == 0)
        {
            pages[0].Prefixes = folders.ToList();
        }

        return new Paged<Objects, StorageObject>(Error, pages, page => page.Items ?? []);
    }

    public override Task<StorageObject> GetObjectAsync(
        string bucket, string objectName, GetObjectOptions? options = null, CancellationToken cancellationToken = default)
    {
        Called();
        ThrowIfFailing();
        Require(bucket, objectName);
        return Task.FromResult(Describe(bucket, objectName));
    }

    public override Task<StorageObject> PatchObjectAsync(
        StorageObject obj, PatchObjectOptions? options = null, CancellationToken cancellationToken = default)
    {
        Called();
        ThrowIfFailing();
        var stored = Require(obj.Bucket, obj.Name);

        // A patch changes only the fields it carries.
        _objects[(obj.Bucket, obj.Name)] = stored with
        {
            ContentType = obj.ContentType ?? stored.ContentType,
            CacheControl = obj.CacheControl ?? stored.CacheControl,
        };
        return Task.FromResult(Describe(obj.Bucket, obj.Name));
    }

    public override Task<StorageObject> UploadObjectAsync(
        string bucket,
        string objectName,
        string? contentType,
        Stream source,
        UploadObjectOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<Google.Apis.Upload.IUploadProgress>? progress = null)
    {
        Called();
        ThrowIfFailing();
        cancellationToken.ThrowIfCancellationRequested();
        if (options?.IfGenerationMatch == 0 && Exists(bucket, objectName))
        {
            throw ApiError(HttpStatusCode.PreconditionFailed, "At least one of the pre-conditions you specified did not hold.");
        }

        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        _objects[(bucket, objectName)] = new Stored(buffer.ToArray(), contentType, "STANDARD");
        progress?.Report(new Sent(buffer.Length));
        return Task.FromResult(Describe(bucket, objectName));
    }

    public override Task<StorageObject> DownloadObjectAsync(
        string bucket,
        string objectName,
        Stream destination,
        DownloadObjectOptions? options = null,
        CancellationToken cancellationToken = default,
        IProgress<Google.Apis.Download.IDownloadProgress>? progress = null)
    {
        Called();
        ThrowIfFailing();
        cancellationToken.ThrowIfCancellationRequested();
        var content = Require(bucket, objectName).Content;
        if (options?.Range is { } range)
        {
            LastRangeValidation = options.DownloadValidationMode;
            // Asking for a range of an empty object is an error, as it is on the real service.
            if (content.Length == 0)
            {
                throw ApiError(HttpStatusCode.RequestedRangeNotSatisfiable, "Requested range not satisfiable");
            }

            var bounds = range.Ranges.First();
            var from = (int)(bounds.From ?? 0);
            var to = (int)Math.Min(bounds.To ?? content.Length - 1, content.Length - 1);
            content = content[from..(to + 1)];
        }

        destination.Write(content);
        return Task.FromResult(Describe(bucket, objectName));
    }

    public override Task<StorageObject> CopyObjectAsync(
        string sourceBucket,
        string sourceObjectName,
        string? destinationBucket = null,
        string? destinationObjectName = null,
        CopyObjectOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Called();
        ThrowIfFailing();
        cancellationToken.ThrowIfCancellationRequested();
        var source = Require(sourceBucket, sourceObjectName);
        var target = (destinationBucket ?? sourceBucket, destinationObjectName ?? sourceObjectName);
        if (options?.IfGenerationMatch == 0 && _objects.ContainsKey(target))
        {
            throw ApiError(HttpStatusCode.PreconditionFailed, "At least one of the pre-conditions you specified did not hold.");
        }

        _objects[target] = source;
        return Task.FromResult(Describe(target.Item1, target.Item2));
    }

    public override Task DeleteObjectAsync(
        string bucket, string objectName, DeleteObjectOptions? options = null, CancellationToken cancellationToken = default)
    {
        Called();
        ThrowIfFailing();
        cancellationToken.ThrowIfCancellationRequested();
        Require(bucket, objectName);
        _objects.TryRemove((bucket, objectName), out _);
        return Task.CompletedTask;
    }

    // Cloud Storage globs: ** crosses folders, * and ? stay within one, [..] is a character class.
    private static Regex GlobToRegex(string glob)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            switch (glob[i])
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    regex.Append(".*");
                    i++;
                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '[':
                    var close = glob.IndexOf(']', i + 2);
                    regex.Append('[').Append(Regex.Escape(glob[(i + 1)..close]).Replace("]", "\\]")).Append(']');
                    i = close;
                    break;
                default:
                    regex.Append(Regex.Escape(glob[i].ToString()));
                    break;
            }
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    private void Called() => Interlocked.Increment(ref _calls);

    private void ThrowIfFailing()
    {
        if (Error is not null)
        {
            throw Error;
        }
    }

    private Stored Require(string bucket, string name) =>
        _objects.TryGetValue((bucket, name), out var found)
            ? found
            : throw ApiError(HttpStatusCode.NotFound, $"No such object: {bucket}/{name}");

    private StorageObject Describe(string bucket, string name)
    {
        var stored = _objects[(bucket, name)];
        return new StorageObject
        {
            Bucket = bucket,
            Name = name,
            Size = (ulong)stored.Content.Length,
            ContentType = stored.ContentType,
            CacheControl = stored.CacheControl,
            StorageClass = stored.StorageClass,
            Metadata = stored.Metadata,
            Generation = 1700000000000000,
            Md5Hash = "bWQ1",
            Crc32c = "Y3Jj",
            TimeCreatedDateTimeOffset = new DateTimeOffset(2025, 6, 7, 8, 9, 10, TimeSpan.Zero),
            UpdatedDateTimeOffset = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        }.Also(obj =>
        {
            if (stored.BadTimestamps)
            {
                obj.UpdatedRaw = "not a date";
                obj.TimeCreatedRaw = "not a date";
            }
        });
    }

    private sealed record Stored(byte[] Content, string? ContentType, string StorageClass)
    {
        public string? CacheControl { get; init; }

        public Dictionary<string, string>? Metadata { get; init; }

        public bool BadTimestamps { get; init; }
    }

    private sealed class Sent(long bytes) : Google.Apis.Upload.IUploadProgress
    {
        public Google.Apis.Upload.UploadStatus Status => Google.Apis.Upload.UploadStatus.Completed;

        public long BytesSent => bytes;

        public Exception? Exception => null;
    }

    // A listing that yields its pages lazily, failing on first read like a real request would.
    private sealed class Paged<TResponse, TResource>(
        GoogleApiException? error, IReadOnlyList<TResponse> pages, Func<TResponse, IEnumerable<TResource>> resources)
        : PagedAsyncEnumerable<TResponse, TResource>
    {
        public override IAsyncEnumerable<TResponse> AsRawResponses() => Pages();

        public override IAsyncEnumerator<TResource> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            Resources().GetAsyncEnumerator(cancellationToken);

        private async IAsyncEnumerable<TResponse> Pages([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (error is not null)
            {
                throw error;
            }

            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return page;
            }
        }

        private async IAsyncEnumerable<TResource> Resources([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var page in Pages(cancellationToken))
            {
                foreach (var resource in resources(page) ?? [])
                {
                    yield return resource;
                }
            }
        }
    }
}

internal static class ObjectExtensions
{
    /// <summary>Applies a change to an object and returns it, for adjusting something built in one expression.</summary>
    public static T Also<T>(this T value, Action<T> change)
    {
        change(value);
        return value;
    }
}
