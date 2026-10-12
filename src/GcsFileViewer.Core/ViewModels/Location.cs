namespace GcsFileViewer.ViewModels;

/// <summary>Where the browser is: the bucket list (no bucket), or a prefix inside a bucket.</summary>
/// <param name="Search">When set, the location is the results of searching under the prefix for this term.</param>
public sealed record Location(string? Bucket, string Prefix, string? Search = null)
{
    public static readonly Location Root = new(null, "");
}
