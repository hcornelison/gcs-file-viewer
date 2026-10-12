namespace GcsFileViewer.Pricing;

/// <summary>The four Cloud Storage classes that are priced differently.</summary>
public enum StorageTier
{
    Standard,
    Nearline,
    Coldline,
    Archive,
}

public static class StorageTiers
{
    /// <summary>
    /// Maps a storage class name from the API, including legacy ones, to its priced tier. Unknown and missing
    /// names count as Standard.
    /// </summary>
    public static StorageTier Parse(string? storageClass) => storageClass?.ToUpperInvariant() switch
    {
        "NEARLINE" or "DURABLE_REDUCED_AVAILABILITY" => StorageTier.Nearline,
        "COLDLINE" => StorageTier.Coldline,
        "ARCHIVE" => StorageTier.Archive,
        _ => StorageTier.Standard,
    };

    /// <summary>True for the tiers that charge for retrieval and for deleting data early.</summary>
    public static bool IsCold(this StorageTier tier) => tier != StorageTier.Standard;
}
