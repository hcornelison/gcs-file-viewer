namespace GcsFileViewer.Pricing;

/// <summary>Running tally of billable Cloud Storage activity. Safe to use from several threads.</summary>
public sealed class UsageTracker
{
    private readonly Lock _lock = new();
    private UsageSnapshot _usage = UsageSnapshot.Empty;

    public UsageSnapshot Snapshot()
    {
        lock (_lock)
        {
            return _usage;
        }
    }

    public void Reset() => Update(_ => UsageSnapshot.Empty);

    /// <summary>Listing, uploading, copying and creating folders.</summary>
    public void RecordClassA(StorageTier tier, bool multiRegion, long operations = 1) => Update(u => u with
    {
        ClassAOperations = u.ClassAOperations + operations,
        ClassACost = u.ClassACost + GcsPricing.ClassA(tier, multiRegion, operations),
    });

    /// <summary>Downloading and reading metadata.</summary>
    public void RecordClassB(StorageTier tier, long operations = 1) => Update(u => u with
    {
        ClassBOperations = u.ClassBOperations + operations,
        ClassBCost = u.ClassBCost + GcsPricing.ClassB(tier, operations),
    });

    /// <summary>Deleting, which costs nothing per request.</summary>
    public void RecordFree(long operations = 1) => Update(u => u with { FreeOperations = u.FreeOperations + operations });

    /// <summary>Bytes downloaded to this computer, charged as network usage.</summary>
    public void RecordDownload(long bytes) => Update(u => u with
    {
        BytesDownloaded = u.BytesDownloaded + bytes,
        DownloadCost = u.DownloadCost + GcsPricing.Download(bytes),
    });

    /// <summary>Bytes read or copied out of Nearline, Coldline or Archive storage. Does nothing for Standard storage.</summary>
    public void RecordRetrieval(StorageTier tier, long bytes)
    {
        if (tier.IsCold())
        {
            Update(u => u with
            {
                BytesRetrieved = u.BytesRetrieved + bytes,
                RetrievalCost = u.RetrievalCost + GcsPricing.Retrieval(tier, bytes),
            });
        }
    }

    /// <summary>Objects in a cold storage tier that were deleted, moved or replaced.</summary>
    public void RecordColdRemoval(long objects = 1) =>
        Update(u => u with { ColdObjectsRemoved = u.ColdObjectsRemoved + objects });

    private void Update(Func<UsageSnapshot, UsageSnapshot> change)
    {
        lock (_lock)
        {
            _usage = change(_usage);
        }
    }
}
