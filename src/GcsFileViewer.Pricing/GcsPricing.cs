namespace GcsFileViewer.Pricing;

/// <summary>
/// Cloud Storage list prices in US dollars, taken from https://cloud.google.com/storage/pricing on
/// <see cref="AsOf"/>. Used only to estimate what a session costs; real bills depend on region, free tier
/// and discounts.
/// </summary>
public static class GcsPricing
{
    public static readonly DateOnly AsOf = new(2026, 10, 12);

    /// <summary>Data transfer from a bucket to the internet (worldwide excluding China and Australia, first 10 TiB), per GiB.</summary>
    public const decimal DownloadPerGiB = 0.12m;

    private const decimal GiB = 1024m * 1024 * 1024;

    // Per 1,000 operations, for buckets with a flat namespace in a single region.
    private static readonly Dictionary<StorageTier, (decimal ClassA, decimal ClassB, decimal RetrievalPerGiB)> Rates = new()
    {
        [StorageTier.Standard] = (0.005m, 0.0004m, 0m),
        [StorageTier.Nearline] = (0.01m, 0.001m, 0.01m),
        [StorageTier.Coldline] = (0.02m, 0.01m, 0.02m),
        [StorageTier.Archive] = (0.05m, 0.05m, 0.05m),
    };

    /// <summary>True for dual-region and multi-region locations such as "US" or "NAM4"; single regions look like "us-east1".</summary>
    public static bool IsMultiRegion(string? location) => !string.IsNullOrEmpty(location) && !location.Contains('-');

    /// <summary>Cost of Class A operations (listing, uploading, copying). They cost double in dual- and multi-region buckets.</summary>
    public static decimal ClassA(StorageTier tier, bool multiRegion, long operations) =>
        Rates[tier].ClassA * (multiRegion ? 2 : 1) * operations / 1000;

    /// <summary>Cost of Class B operations (downloading, reading metadata).</summary>
    public static decimal ClassB(StorageTier tier, long operations) => Rates[tier].ClassB * operations / 1000;

    /// <summary>Retrieval fee for reading or copying data held in Nearline, Coldline or Archive storage.</summary>
    public static decimal Retrieval(StorageTier tier, long bytes) => Rates[tier].RetrievalPerGiB * bytes / GiB;

    /// <summary>Network charge for downloading data to this computer.</summary>
    public static decimal Download(long bytes) => DownloadPerGiB * bytes / GiB;
}
