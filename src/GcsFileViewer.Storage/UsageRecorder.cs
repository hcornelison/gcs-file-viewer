using GcsFileViewer.Pricing;

namespace GcsFileViewer.Storage;

/// <summary>
/// Turns the requests <see cref="GcsService"/> makes into entries in a <see cref="UsageTracker"/>. It remembers
/// what it has seen that affects the price: each listed bucket's default storage class and location, which
/// buckets are only reached anonymously, and every listed object that is in a cold storage class.
/// Safe to use from several threads.
/// </summary>
internal sealed class UsageRecorder
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (StorageTier Tier, bool MultiRegion)> _buckets = [];
    private readonly HashSet<string> _anonymousBuckets = [];
    private readonly Dictionary<(string Bucket, string Name), (StorageTier Tier, long Size)> _coldObjects = [];

    public UsageTracker Usage { get; } = new();

    /// <summary>Notes a bucket's default storage class and location, which set the price of writes and listings in it.</summary>
    public void SetBucket(string bucket, string? storageClass, string? location)
    {
        lock (_lock)
        {
            _buckets[bucket] = (StorageTiers.Parse(storageClass), GcsPricing.IsMultiRegion(location));
        }
    }

    /// <summary>
    /// Says whether a bucket is reached without credentials. Requests to such a bucket are billed to its owner,
    /// so none of them are tallied.
    /// </summary>
    public void SetAnonymous(string bucket, bool anonymous)
    {
        lock (_lock)
        {
            if (anonymous)
            {
                _anonymousBuckets.Add(bucket);
            }
            else
            {
                _anonymousBuckets.Remove(bucket);
            }
        }
    }

    public bool IsAnonymous(string bucket)
    {
        lock (_lock)
        {
            return _anonymousBuckets.Contains(bucket);
        }
    }

    /// <summary>Notes an object seen in a listing, so later reads and removals of it can be priced.</summary>
    public void Remember(string bucket, string objectName, string? storageClass, long size)
    {
        var tier = StorageTiers.Parse(storageClass);
        lock (_lock)
        {
            if (tier.IsCold())
            {
                _coldObjects[(bucket, objectName)] = (tier, size);
            }
            else
            {
                _coldObjects.Remove((bucket, objectName));
            }
        }
    }

    /// <summary>Listing the buckets of a project, which is always charged at the Standard rate.</summary>
    public void BucketsListed() => Usage.RecordClassA(StorageTier.Standard, multiRegion: false);

    /// <summary>One page of a listing, or any other Class A request priced by the bucket's defaults.</summary>
    public void Written(string bucket)
    {
        (StorageTier Tier, bool MultiRegion) defaults;
        lock (_lock)
        {
            if (_anonymousBuckets.Contains(bucket))
            {
                return;
            }

            defaults = _buckets.GetValueOrDefault(bucket);
        }

        Usage.RecordClassA(defaults.Tier, defaults.MultiRegion);
    }

    /// <summary>Creating or overwriting an object, which removes whatever was there under that name.</summary>
    public void ObjectWritten(string bucket, string objectName)
    {
        Written(bucket);
        Replaced(bucket, objectName);
    }

    /// <summary>Reading an object's metadata.</summary>
    public void MetadataRead(string bucket, string? storageClass)
    {
        if (!IsAnonymous(bucket))
        {
            Usage.RecordClassB(StorageTiers.Parse(storageClass));
        }
    }

    /// <summary>Reading an object's data: a Class B request, the bytes sent, and a retrieval fee if it is cold.</summary>
    public void DataRead(string bucket, string objectName, long bytes)
    {
        StorageTier tier;
        lock (_lock)
        {
            if (_anonymousBuckets.Contains(bucket))
            {
                return;
            }

            tier = _coldObjects.TryGetValue((bucket, objectName), out var cold)
                ? cold.Tier
                : _buckets.GetValueOrDefault(bucket).Tier;
        }

        Usage.RecordClassB(tier);
        Usage.RecordDownload(bytes);
        Usage.RecordRetrieval(tier, bytes);
    }

    /// <summary>Copying an object: a write at the destination, plus a retrieval fee if the source is cold.</summary>
    public void Copied(string sourceBucket, string sourceObject, string bucket, string objectName)
    {
        if (IsAnonymous(bucket))
        {
            return;
        }

        (StorageTier Tier, long Size)? source;
        lock (_lock)
        {
            source = _coldObjects.TryGetValue((sourceBucket, sourceObject), out var cold) ? cold : null;
        }

        if (source is { } retrieved)
        {
            Usage.RecordRetrieval(retrieved.Tier, retrieved.Size);
        }

        ObjectWritten(bucket, objectName);
    }

    /// <summary>Deleting an object, which is free unless it was cold.</summary>
    public void Deleted(string bucket, string objectName)
    {
        if (!IsAnonymous(bucket))
        {
            Usage.RecordFree();
            Replaced(bucket, objectName);
        }
    }

    // Removing or overwriting a cold object can carry an early deletion fee.
    private void Replaced(string bucket, string objectName)
    {
        bool wasCold;
        lock (_lock)
        {
            wasCold = _coldObjects.Remove((bucket, objectName)) && !_anonymousBuckets.Contains(bucket);
        }

        if (wasCold)
        {
            Usage.RecordColdRemoval();
        }
    }
}
