using System.Text;
using GcsFileViewer.Pricing;
using GcsFileViewer.Storage;
using GcsFileViewer.ViewModels;
using static GcsFileViewer.Tests.FakeGcsService;

namespace GcsFileViewer.Tests;

/// <summary>The details pane: properties, editing them, and previews.</summary>
public class MainViewModelDetailsTests : ViewModelTestBase
{
    public MainViewModelDetailsTests()
    {
        // Give readme.txt real contents, and list it at the size they actually are.
        Gcs.Contents["alpha/readme.txt"] = Encoding.UTF8.GetBytes("Hello, world.\nSecond line.");
        Gcs.Listings["alpha/"][1] = File("alpha", "readme.txt", 26);
        Gcs.Details["alpha/readme.txt"] = new ObjectDetails(
            "alpha",
            "readme.txt",
            26,
            "text/plain",
            "public, max-age=60",
            "gzip",
            "STANDARD",
            new DateTimeOffset(2025, 6, 7, 8, 9, 10, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            1700000000000000,
            "bWQ1",
            "Y3Jj",
            new Dictionary<string, string> { ["owner"] = "me", ["Build"] = "42" });
    }

    private string? Detail(string label) => Vm.Details.Lines.FirstOrDefault(d => d.Label == label)?.Value;

    private async Task<BrowserItem> SelectAsync(string name, bool show = true)
    {
        if (Vm.Items.Count == 0)
        {
            await OpenAsync("alpha");
        }

        Vm.Details.IsVisible = show;
        var item = Item(name);
        Vm.UpdateSelection([item]);
        return item;
    }

    // Properties

    [Fact]
    public async Task Nothing_is_fetched_while_the_pane_is_closed()
    {
        await SelectAsync("readme.txt", show: false);

        Assert.Empty(Gcs.DetailLookups);
        Assert.Empty(Gcs.Reads);
        Assert.Empty(Vm.Details.Lines);
    }

    [Fact]
    public async Task Opening_the_pane_loads_the_current_selection()
    {
        await SelectAsync("readme.txt", show: false);

        Vm.Details.IsVisible = true;

        Assert.Equal(["alpha/readme.txt"], Gcs.DetailLookups);
        Assert.Equal("readme.txt", Vm.Details.Title);
    }

    [Fact]
    public async Task A_file_shows_its_full_metadata()
    {
        await SelectAsync("readme.txt");

        Assert.Equal("readme.txt", Vm.Details.Title);
        Assert.Equal("File", Detail("Kind"));
        Assert.Equal("gs://alpha/readme.txt", Detail("Location"));
        Assert.Equal("26 B", Detail("Size"));
        Assert.Equal("text/plain", Detail("Content type"));
        Assert.Equal("STANDARD", Detail("Storage class"));
        Assert.Equal("public, max-age=60", Detail("Cache-Control"));
        Assert.Equal("gzip", Detail("Content encoding"));
        Assert.Equal("1700000000000000", Detail("Generation"));
        Assert.Equal("bWQ1", Detail("MD5"));
        Assert.Equal("Y3Jj", Detail("CRC32C"));
        Assert.NotNull(Detail("Created"));
        Assert.NotNull(Detail("Modified"));
        Assert.Equal(["Metadata: Build", "Metadata: owner"], Vm.Details.Lines.Select(d => d.Label).Where(l => l.StartsWith("Metadata")));
        Assert.Equal("42", Detail("Metadata: Build"));
    }

    [Fact]
    public async Task A_large_file_shows_its_exact_size_too()
    {
        Gcs.Listings["alpha/"].Add(File("alpha", "big.bin", 5 * 1024 * 1024));
        Gcs.Contents["alpha/big.bin"] = new byte[5 * 1024 * 1024];
        await SelectAsync("big.bin");

        Assert.Equal("5 MB (5,242,880 bytes)", Detail("Size"));
    }

    [Fact]
    public async Task A_folder_shows_what_is_known_without_any_requests()
    {
        await SelectAsync("docs");

        Assert.Equal("docs", Vm.Details.Title);
        Assert.Equal("Folder", Detail("Kind"));
        Assert.Equal("gs://alpha/docs/", Detail("Location"));
        Assert.Null(Detail("Size"));
        Assert.False(Vm.Details.CanEdit);
        Assert.Empty(Gcs.DetailLookups);
        Assert.Null(Vm.Details.PreviewNote);
    }

    [Fact]
    public async Task A_measured_folder_shows_its_size()
    {
        await OpenAsync("alpha");
        await Vm.MeasureAsync(Item("docs"));

        await SelectAsync("docs");

        Assert.Equal("2 KB (2,058 bytes)", Detail("Size"));
    }

    [Fact]
    public async Task A_bucket_shows_its_storage_class()
    {
        await ConnectAsync();
        Vm.Details.IsVisible = true;

        Vm.UpdateSelection([Item("alpha")]);

        Assert.Equal("Bucket", Detail("Kind"));
        Assert.Equal("gs://alpha/", Detail("Location"));
        Assert.Equal("STANDARD", Detail("Storage class"));
    }

    [Fact]
    public async Task Several_items_show_a_count_and_total()
    {
        await OpenAsync("alpha");
        Vm.Details.IsVisible = true;

        Vm.UpdateSelection([Item("readme.txt"), Item("notes.md")]);

        Assert.Equal("2 items selected", Vm.Details.Title);
        Assert.Equal([new DetailLine("Total size", "950 B")], Vm.Details.Lines);
        Assert.Empty(Gcs.DetailLookups);
    }

    [Fact]
    public async Task Clearing_the_selection_clears_the_pane()
    {
        await SelectAsync("readme.txt");

        Vm.UpdateSelection([]);

        Assert.Equal("Nothing selected", Vm.Details.Title);
        Assert.Empty(Vm.Details.Lines);
        Assert.Null(Vm.Details.PreviewText);
        Assert.False(Vm.Details.CanEdit);
    }

    [Fact]
    public async Task A_failure_to_load_details_is_shown_in_the_pane()
    {
        Gcs.DetailsError = new InvalidOperationException("Access denied.");

        await SelectAsync("readme.txt");

        Assert.Equal("Could not load the details: Access denied.", Vm.Details.PreviewNote);
        Assert.Equal("File", Detail("Kind"));
        Assert.False(Vm.Details.CanEdit);
    }

    [Fact]
    public async Task Viewing_details_is_counted_in_the_cost_estimate()
    {
        await OpenAsync("alpha");
        Gcs.Usage.RecordDownload(1024L * 1024 * 1024);

        await SelectAsync("readme.txt");

        Assert.Equal("Est. cost: $0.12", Vm.Cost.Summary);
    }

    [Fact]
    public async Task Passing_quickly_over_files_only_fetches_the_one_that_stays_selected()
    {
        await OpenAsync("alpha");
        Gcs.Contents["alpha/notes.md"] = Encoding.UTF8.GetBytes("# Notes");
        Vm.Details.Delay = TimeSpan.FromMilliseconds(150);
        Vm.Details.IsVisible = true;

        Vm.UpdateSelection([Item("readme.txt")]);
        Vm.UpdateSelection([Item("notes.md")]);

        // What the list already knows shows at once; the requests wait.
        Assert.Equal("notes.md", Vm.Details.Title);
        Assert.Equal("gs://alpha/notes.md", Detail("Location"));
        Assert.Empty(Gcs.DetailLookups);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Vm.Details.PreviewText is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["alpha/notes.md"], Gcs.DetailLookups);
        Assert.Equal("# Notes", Vm.Details.PreviewText);
    }

    [Fact]
    public async Task Closing_the_pane_abandons_a_fetch_that_has_not_started()
    {
        await OpenAsync("alpha");
        Vm.Details.Delay = TimeSpan.FromMilliseconds(100);
        Vm.Details.IsVisible = true;
        Vm.UpdateSelection([Item("readme.txt")]);

        Vm.Details.IsVisible = false;
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Empty(Gcs.DetailLookups);
    }

    // Editing

    [Fact]
    public async Task The_editor_starts_from_the_current_values()
    {
        await SelectAsync("readme.txt");

        Assert.True(Vm.Details.CanEdit);
        Assert.True(Vm.Details.SaveCommand.CanExecute(null));
        Assert.Equal("text/plain", Vm.Details.EditContentType);
        Assert.Equal("public, max-age=60", Vm.Details.EditCacheControl);
    }

    [Fact]
    public async Task Saving_writes_the_trimmed_values_and_shows_the_result()
    {
        await SelectAsync("readme.txt");
        Vm.Details.EditContentType = " text/markdown ";
        Vm.Details.EditCacheControl = " no-store ";

        await Vm.Details.SaveCommand.ExecuteAsync(null);

        Assert.Equal("text/markdown", Gcs.Details["alpha/readme.txt"].ContentType);
        Assert.Equal("no-store", Gcs.Details["alpha/readme.txt"].CacheControl);
        Assert.Equal("text/markdown", Detail("Content type"));
        Assert.Equal("no-store", Detail("Cache-Control"));
        Assert.Equal("Saved details for readme.txt", Vm.Status);
        Assert.Equal("Hello, world.\nSecond line.", Vm.Details.PreviewText);
    }

    [Fact]
    public async Task Cache_control_can_be_cleared()
    {
        await SelectAsync("readme.txt");
        Vm.Details.EditCacheControl = "";

        await Vm.Details.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", Gcs.Details["alpha/readme.txt"].CacheControl);
        Assert.Null(Detail("Cache-Control"));
    }

    [Fact]
    public async Task A_content_type_is_required()
    {
        await SelectAsync("readme.txt");
        Vm.Details.EditContentType = "  ";

        await Vm.Details.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Enter a content type.", Vm.Status);
        Assert.Equal("text/plain", Gcs.Details["alpha/readme.txt"].ContentType);
    }

    [Fact]
    public async Task A_failed_save_is_reported()
    {
        await SelectAsync("readme.txt");
        Gcs.DetailsError = new InvalidOperationException("Permission denied.");

        await Vm.Details.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Could not save the details: Permission denied.", Vm.Status);
    }

    [Fact]
    public async Task Folders_cannot_be_edited()
    {
        await SelectAsync("docs");

        Assert.False(Vm.Details.SaveCommand.CanExecute(null));
    }

    // Preview

    [Fact]
    public async Task A_small_text_file_is_previewed_whole()
    {
        await SelectAsync("readme.txt");

        Assert.Equal("Hello, world.\nSecond line.", Vm.Details.PreviewText);
        Assert.Null(Vm.Details.PreviewNote);
        Assert.Null(Vm.Details.PreviewImage);
        Assert.Equal([$"alpha/readme.txt:{DetailsViewModel.PreviewBytes}"], Gcs.Reads);
    }

    [Fact]
    public async Task A_long_text_file_shows_only_its_start()
    {
        var content = new string('x', DetailsViewModel.PreviewBytes + 5000);
        Gcs.Listings["alpha/"].Add(File("alpha", "long.log", content.Length));
        Gcs.Contents["alpha/long.log"] = Encoding.UTF8.GetBytes(content);

        await SelectAsync("long.log");

        Assert.Equal(DetailsViewModel.PreviewBytes, Vm.Details.PreviewText!.Length);
        Assert.Equal("Showing the first 64 KB of 68.9 KB.", Vm.Details.PreviewNote);
    }

    [Fact]
    public async Task A_binary_file_has_no_preview()
    {
        Gcs.Listings["alpha/"].Add(File("alpha", "data.bin", 4));
        Gcs.Contents["alpha/data.bin"] = [0x00, 0xFF, 0x10, 0x80];

        await SelectAsync("data.bin");

        Assert.Null(Vm.Details.PreviewText);
        Assert.Equal("No preview for this type of file.", Vm.Details.PreviewNote);
    }

    [Fact]
    public async Task An_image_is_read_whole_and_offered_as_a_picture()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Gcs.Listings["alpha/"].Add(File("alpha", "logo.PNG", png.Length));
        Gcs.Contents["alpha/logo.PNG"] = png;

        await SelectAsync("logo.PNG");

        Assert.Equal(png, Vm.Details.PreviewImage);
        Assert.Null(Vm.Details.PreviewText);
        Assert.Equal(["alpha/logo.PNG:8"], Gcs.Reads);
    }

    [Fact]
    public async Task A_huge_image_is_not_downloaded_for_preview()
    {
        Gcs.Listings["alpha/"].Add(File("alpha", "scan.jpg", DetailsViewModel.MaxImageBytes + 1));

        await SelectAsync("scan.jpg");

        Assert.Null(Vm.Details.PreviewImage);
        Assert.Equal("This image is too large to preview (over 10 MB).", Vm.Details.PreviewNote);
        Assert.Empty(Gcs.Reads);
    }

    [Fact]
    public async Task An_empty_file_says_so_without_reading_it()
    {
        Gcs.Listings["alpha/"].Add(File("alpha", "empty.txt"));

        await SelectAsync("empty.txt");

        Assert.Equal("This file is empty.", Vm.Details.PreviewNote);
        Assert.Empty(Gcs.Reads);
    }

    [Theory]
    [InlineData("NEARLINE")]
    [InlineData("COLDLINE")]
    [InlineData("ARCHIVE")]
    public async Task Cold_storage_files_are_not_read_for_preview(string storageClass)
    {
        Gcs.Listings["alpha/"].Add(File("alpha", "backup.txt", 50) with { StorageClass = storageClass });
        Gcs.Contents["alpha/backup.txt"] = new byte[50];

        await SelectAsync("backup.txt");

        Assert.StartsWith("Preview is off for Nearline, Coldline and Archive storage", Vm.Details.PreviewNote);
        Assert.Empty(Gcs.Reads);
        Assert.Equal(["alpha/backup.txt"], Gcs.DetailLookups);
    }

    [Fact]
    public async Task A_preview_that_cannot_be_read_is_explained()
    {
        await OpenAsync("alpha");
        Gcs.DownloadError = new InvalidOperationException("Access denied.");

        await SelectAsync("readme.txt");

        Assert.Equal("Could not load the details: Access denied.", Vm.Details.PreviewNote);
        Assert.Equal("text/plain", Detail("Content type"));
    }

    [Fact]
    public async Task Selecting_something_else_replaces_the_preview()
    {
        await SelectAsync("readme.txt");

        await SelectAsync("docs");

        Assert.Null(Vm.Details.PreviewText);
        Assert.Equal("docs", Vm.Details.Title);
        Assert.Equal("", Vm.Details.EditContentType);
    }

    [Theory]
    [InlineData("plain text", true)]
    [InlineData("tabs\tand\r\nnew lines\f", true)]
    [InlineData("", true)]
    [InlineData("caf\u00e9 \u4e2d\u6587 \ud83d\ude00", true)]
    public void Text_is_recognised(string text, bool expected) =>
        Assert.Equal(expected, DetailsViewModel.LooksLikeText(Encoding.UTF8.GetBytes(text)));

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x48, 0x69, 0x00, 0x21 })]
    [InlineData(new byte[] { 0x1B, 0x5B })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    public void Binary_data_is_not_mistaken_for_text(byte[] bytes) =>
        Assert.False(DetailsViewModel.LooksLikeText(bytes));
}
