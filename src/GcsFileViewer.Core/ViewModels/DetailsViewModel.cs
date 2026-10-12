using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GcsFileViewer.Pricing;
using GcsFileViewer.Storage;

namespace GcsFileViewer.ViewModels;

/// <summary>The details pane: the selected item's properties, an editor for two of them, and a preview of its contents.</summary>
/// <param name="requestsMade">Called after the pane has made requests, so the cost estimate can catch up.</param>
/// <param name="notify">Shows a message in the status bar.</param>
public partial class DetailsViewModel(IGcsService gcs, Action requestsMade, Action<string> notify) : ObservableObject
{
    /// <summary>Text previews show at most this much of a file.</summary>
    internal const int PreviewBytes = 64 * 1024;

    /// <summary>Images larger than this aren't previewed.</summary>
    internal const long MaxImageBytes = 10 * 1024 * 1024;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    private IReadOnlyList<BrowserItem> _selection = [];
    private CancellationTokenSource? _loadCts;

    /// <summary>Whether the pane is open. Nothing is fetched for it while it is closed.</summary>
    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _title = "Nothing selected";

    [ObservableProperty]
    private string? _previewText;

    /// <summary>The bytes of an image file to show as its preview.</summary>
    [ObservableProperty]
    private byte[]? _previewImage;

    /// <summary>Why there is no preview, or that the preview is only part of the file.</summary>
    [ObservableProperty]
    private string? _previewNote;

    /// <summary>True when one file is selected and its metadata has loaded, so it can be edited.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _canEdit;

    [ObservableProperty]
    private string _editContentType = "";

    [ObservableProperty]
    private string _editCacheControl = "";

    /// <summary>
    /// How long a file must stay selected before its metadata and preview are fetched, so that moving quickly
    /// through a list doesn't make requests for every row passed over.
    /// </summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(250);

    public ObservableCollection<DetailLine> Lines { get; } = [];

    /// <summary>True if the bytes read as text: nothing that only binary data contains.</summary>
    internal static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            // Tab, line feed, form feed and carriage return are the control characters text files use.
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0C or 0x0D))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Says what is selected. The pane shows it straight away if it is open, or when it is next opened.</summary>
    public void Show(IReadOnlyList<BrowserItem> selection)
    {
        _selection = selection;
        if (IsVisible)
        {
            _ = LoadAsync();
        }
    }

    partial void OnIsVisibleChanged(bool value)
    {
        if (value)
        {
            _ = LoadAsync();
        }
        else
        {
            _loadCts?.Cancel();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task SaveAsync()
    {
        if (_selection is not [{ Kind: ItemKind.File } item])
        {
            return;
        }

        var contentType = EditContentType.Trim();
        if (contentType.Length == 0)
        {
            notify("Enter a content type.");
            return;
        }

        try
        {
            ShowMetadata(await gcs.UpdateDetailsAsync(item.Bucket, item.Path, contentType, EditCacheControl.Trim()));
            notify($"Saved details for {item.Name}");
        }
        catch (Exception ex)
        {
            notify("Could not save the details: " + ex.Message);
        }

        requestsMade();
    }

    // Fills the pane for the current selection. Files get their full metadata and a preview, which cost a request each.
    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        Lines.Clear();
        PreviewText = null;
        PreviewImage = null;
        PreviewNote = null;
        CanEdit = false;
        EditContentType = "";
        EditCacheControl = "";

        if (_selection.Count == 0)
        {
            Title = "Nothing selected";
            return;
        }

        if (_selection.Count > 1)
        {
            Title = $"{_selection.Count:N0} items selected";
            Lines.Add(new DetailLine("Total size", BrowserItem.FormatSize(_selection.Sum(i => i.Size ?? 0))));
            return;
        }

        // What the list already knows is shown at once; the rest follows if the item stays selected.
        var item = _selection[0];
        Title = item.Name;
        Lines.Add(new DetailLine("Kind", item.Kind.ToString()));
        Lines.Add(new DetailLine("Location", item.Uri));
        Add("Size", item.Size is { } size ? DescribeSize(size) : null);
        Add("Type", item.Type);
        Add("Modified", item.Modified is { } modified ? DescribeTime(modified) : null);
        Add("Storage class", item.StorageClass);
        if (item.Kind != ItemKind.File)
        {
            return;
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cts.Token);
            }

            var details = await gcs.GetDetailsAsync(item.Bucket, item.Path, cts.Token);
            if (cts != _loadCts)
            {
                return;
            }

            ShowMetadata(details);
            await LoadPreviewAsync(item, cts);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cts == _loadCts)
            {
                PreviewNote = "Could not load the details: " + ex.Message;
            }
        }
        finally
        {
            if (cts == _loadCts)
            {
                requestsMade();
            }
        }
    }

    private void ShowMetadata(ObjectDetails details)
    {
        Lines.Clear();
        Lines.Add(new DetailLine("Kind", "File"));
        Lines.Add(new DetailLine("Location", $"gs://{details.Bucket}/{details.Name}"));
        Lines.Add(new DetailLine("Size", DescribeSize(details.Size)));
        Add("Content type", details.ContentType);
        Add("Storage class", details.StorageClass);
        Add("Created", details.Created is { } created ? DescribeTime(created) : null);
        Add("Modified", details.Updated is { } updated ? DescribeTime(updated) : null);
        Add("Cache-Control", details.CacheControl);
        Add("Content encoding", details.ContentEncoding);
        Add("Generation", details.Generation?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("MD5", details.Md5);
        Add("CRC32C", details.Crc32c);
        foreach (var (key, value) in details.Metadata.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
        {
            Lines.Add(new DetailLine($"Metadata: {key}", value));
        }

        EditContentType = details.ContentType ?? "";
        EditCacheControl = details.CacheControl ?? "";
        CanEdit = true;
    }

    private async Task LoadPreviewAsync(BrowserItem item, CancellationTokenSource cts)
    {
        var size = item.Size ?? 0;
        if (item.Tier.IsCold())
        {
            PreviewNote = "Preview is off for Nearline, Coldline and Archive storage, where reading data has a " +
                          "retrieval fee. Download the file to view it.";
            return;
        }

        if (size == 0)
        {
            PreviewNote = "This file is empty.";
            return;
        }

        if (ImageExtensions.Contains(Path.GetExtension(item.Name), StringComparer.OrdinalIgnoreCase))
        {
            if (size > MaxImageBytes)
            {
                PreviewNote = $"This image is too large to preview (over {BrowserItem.FormatSize(MaxImageBytes)}).";
                return;
            }

            var image = await gcs.ReadAsync(item.Bucket, item.Path, size, cts.Token);
            if (cts == _loadCts)
            {
                PreviewImage = image;
            }

            return;
        }

        var head = await gcs.ReadAsync(item.Bucket, item.Path, PreviewBytes, cts.Token);
        if (cts != _loadCts)
        {
            return;
        }

        if (!LooksLikeText(head))
        {
            PreviewNote = "No preview for this type of file.";
            return;
        }

        PreviewText = Encoding.UTF8.GetString(head);
        if (size > head.Length)
        {
            PreviewNote = $"Showing the first {BrowserItem.FormatSize(head.Length)} of {item.SizeDisplay}.";
        }
    }

    private void Add(string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Lines.Add(new DetailLine(label, value));
        }
    }

    private static string DescribeSize(long bytes) =>
        bytes < 1024 ? BrowserItem.FormatSize(bytes) : $"{BrowserItem.FormatSize(bytes)} ({bytes:N0} bytes)";

    private static string DescribeTime(DateTimeOffset time) => time.ToLocalTime().ToString("f");
}
