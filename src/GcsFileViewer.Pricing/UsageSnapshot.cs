namespace GcsFileViewer.Pricing;

/// <summary>What has been asked of Cloud Storage so far, and roughly what that costs at list prices.</summary>
/// <param name="ColdObjectsRemoved">
/// Objects in Nearline, Coldline or Archive storage that were deleted, moved or replaced. These may carry
/// early deletion fees, which are not part of the estimate.
/// </param>
public sealed record UsageSnapshot(
    long ClassAOperations,
    long ClassBOperations,
    long FreeOperations,
    long BytesDownloaded,
    long BytesRetrieved,
    long ColdObjectsRemoved,
    decimal ClassACost,
    decimal ClassBCost,
    decimal DownloadCost,
    decimal RetrievalCost)
{
    public static UsageSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public decimal TotalCost => ClassACost + ClassBCost + DownloadCost + RetrievalCost;
}
