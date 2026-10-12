namespace GcsFileViewer.Storage;

/// <summary>One page of a listing, with the token for fetching the next page if there is one.</summary>
public sealed record ListPage(List<BrowserItem> Items, string? NextPageToken);
