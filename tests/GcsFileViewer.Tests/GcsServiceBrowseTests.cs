using System.Net;
using System.Text;
using GcsFileViewer.Pricing;
using GcsFileViewer.Storage;
using Google.Apis.Auth.OAuth2;

namespace GcsFileViewer.Tests;

/// <summary>GcsService's paged listing, search, recursive listing, sizing, preview reads, metadata and progress.</summary>
public class GcsServiceBrowseTests
{
    private readonly FakeStorageClient _client = new();
    private readonly GcsService _gcs;

    public GcsServiceBrowseTests()
    {
        _gcs = TestServices.Create(_client);

        _client.Put("b", "readme.txt", "hello world", "text/plain");
        _client.Put("b", "docs/", "", "application/x-directory");
        _client.Put("b", "docs/guide.pdf", "0123456789", "application/pdf");
        _client.Put("b", "docs/old/v1.pdf", "v1", "application/pdf");
        _client.Put("b", "docs/old/notes.txt", "note");
        _client.Put("b", "docs/empty/", "");
        _client.Put("b", "logs/2026/app.log", "log line");
    }

    private static string Summary(BrowserItem item) => $"{item.Kind}:{item.Name}";

    private UsageSnapshot Usage => _gcs.Usage.Snapshot();

    // One page at a time

    [Fact]
    public async Task A_page_holds_the_folders_and_files_of_one_level()
    {
        var page = await _gcs.ListPageAsync("b", "docs/", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Folder:empty", "Folder:old", "File:guide.pdf"], page.Items.Select(Summary));
        Assert.Null(page.NextPageToken);
        Assert.Equal(1, Usage.ClassAOperations);
    }

    [Fact]
    public async Task Pages_are_followed_by_token_until_there_are_no_more()
    {
        for (var i = 1; i <= 5; i++)
        {
            _client.Put("big", $"file{i}.txt");
        }

        _client.Put("big", "sub/inner.txt");
        _client.PageSize = 2;

        var first = await _gcs.ListPageAsync("big", "", ct: TestContext.Current.CancellationToken);
        var second = await _gcs.ListPageAsync("big", "", first.NextPageToken, TestContext.Current.CancellationToken);
        var third = await _gcs.ListPageAsync("big", "", second.NextPageToken, TestContext.Current.CancellationToken);

        Assert.Equal(["Folder:sub", "File:file1.txt", "File:file2.txt"], first.Items.Select(Summary));
        Assert.Equal(["File:file3.txt", "File:file4.txt"], second.Items.Select(Summary));
        Assert.Equal(["File:file5.txt"], third.Items.Select(Summary));
        Assert.NotNull(first.NextPageToken);
        Assert.NotNull(second.NextPageToken);
        Assert.Null(third.NextPageToken);
        Assert.Equal(3, Usage.ClassAOperations);
    }

    [Fact]
    public async Task A_page_of_an_empty_folder_is_empty()
    {
        var page = await _gcs.ListPageAsync("b", "nothing/", ct: TestContext.Current.CancellationToken);

        Assert.Empty(page.Items);
        Assert.Null(page.NextPageToken);
    }

    // Search

    [Theory]
    [InlineData("", "pdf", "**pdf**")]
    [InlineData("docs/", "v1", "docs/**v1**")]
    [InlineData("docs/", "*.pdf", "docs/*.pdf")]
    [InlineData("docs/", "**/*.pdf", "docs/**/*.pdf")]
    [InlineData("we[i]rd*/", "x", "we[[]i]rd[*]/**x**")]
    [InlineData("what?/{a}/", "data?.csv", "what[?]/[{]a}/data?.csv")]
    public void The_search_pattern_wraps_plain_terms_and_passes_wildcards_through(string prefix, string term, string expected) =>
        Assert.Equal(expected, GcsService.SearchGlob(prefix, term));

    [Fact]
    public async Task Search_finds_matches_at_any_depth_and_says_which_folder_each_is_in()
    {
        var page = await _gcs.SearchPageAsync("b", "", "pdf", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["guide.pdf", "v1.pdf"], page.Items.Select(i => i.Name));
        Assert.Equal(["docs/", "docs/old/"], page.Items.Select(i => i.ParentPath));
        Assert.Equal(["docs/guide.pdf", "docs/old/v1.pdf"], page.Items.Select(i => i.Path));
        Assert.All(page.Items, i => Assert.Equal(ItemKind.File, i.Kind));
        Assert.Null(page.NextPageToken);
    }

    [Fact]
    public async Task Search_matches_folder_names_too_but_never_returns_folder_placeholders()
    {
        var page = await _gcs.SearchPageAsync("b", "", "old", ct: TestContext.Current.CancellationToken);
        var inDocs = await _gcs.SearchPageAsync("b", "", "docs", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["notes.txt", "v1.pdf"], page.Items.Select(i => i.Name));
        Assert.Equal(["guide.pdf", "notes.txt", "v1.pdf"], inDocs.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Search_stays_under_the_folder_it_is_given()
    {
        var page = await _gcs.SearchPageAsync("b", "docs/old/", "pdf", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["v1.pdf"], page.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Search_is_case_sensitive_like_the_service()
    {
        var page = await _gcs.SearchPageAsync("b", "", "PDF", ct: TestContext.Current.CancellationToken);

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task A_wildcard_term_is_a_pattern_relative_to_the_folder()
    {
        var direct = await _gcs.SearchPageAsync("b", "docs/", "*.pdf", ct: TestContext.Current.CancellationToken);
        var deep = await _gcs.SearchPageAsync("b", "docs/", "**.pdf", ct: TestContext.Current.CancellationToken);
        var single = await _gcs.SearchPageAsync("b", "docs/old/", "v?.pdf", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["guide.pdf"], direct.Items.Select(i => i.Name));
        Assert.Equal(["guide.pdf", "v1.pdf"], deep.Items.Select(i => i.Name));
        Assert.Equal(["v1.pdf"], single.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Search_results_come_a_page_at_a_time()
    {
        for (var i = 1; i <= 5; i++)
        {
            _client.Put("big", $"logs/app{i}.log");
        }

        _client.PageSize = 2;

        var first = await _gcs.SearchPageAsync("big", "", "app", ct: TestContext.Current.CancellationToken);
        var second = await _gcs.SearchPageAsync("big", "", "app", first.NextPageToken, TestContext.Current.CancellationToken);

        Assert.Equal(["app1.log", "app2.log"], first.Items.Select(i => i.Name));
        Assert.Equal(["app3.log", "app4.log"], second.Items.Select(i => i.Name));
        Assert.NotNull(second.NextPageToken);
        Assert.Equal(2, Usage.ClassAOperations);
    }

    // Everything under a folder

    [Fact]
    public async Task ListAll_returns_every_file_with_its_path_relative_to_the_folder()
    {
        var items = await _gcs.ListRecursiveAsync("b", "docs/", ct: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Folder:empty", "File:guide.pdf", "File:old/notes.txt", "File:old/v1.pdf"],
            items.Select(Summary));
        Assert.Equal("docs/old/v1.pdf", items[^1].Path);
        Assert.Equal(2, items[^1].Size);
    }

    [Fact]
    public async Task ListAll_reports_progress_and_counts_a_request_per_page()
    {
        _client.PageSize = 2;
        var reported = new List<int>();

        await _gcs.ListRecursiveAsync("b", "docs/", new InlineProgress<int>(reported.Add), TestContext.Current.CancellationToken);

        Assert.Equal([1, 3, 4], reported);
        Assert.Equal(3, Usage.ClassAOperations);
    }

    [Fact]
    public async Task Measure_counts_files_and_bytes_but_not_folder_placeholders()
    {
        var size = await _gcs.MeasureAsync("b", "docs/", ct: TestContext.Current.CancellationToken);
        var whole = await _gcs.MeasureAsync("b", "", ct: TestContext.Current.CancellationToken);

        Assert.Equal(new FolderSize(3, 16), size);
        Assert.Equal(new FolderSize(5, 35), whole);
    }

    [Fact]
    public async Task Measure_reports_a_running_total()
    {
        _client.PageSize = 2;
        var reported = new List<FolderSize>();

        await _gcs.MeasureAsync("b", "docs/", new InlineProgress<FolderSize>(reported.Add), TestContext.Current.CancellationToken);

        Assert.Equal(new FolderSize(3, 16), reported[^1]);
        Assert.True(reported.Count >= 2);
        Assert.Equal(reported.Count, Usage.ClassAOperations);
    }

    [Fact]
    public async Task Measure_of_nothing_is_zero() =>
        Assert.Equal(new FolderSize(0, 0), await _gcs.MeasureAsync("b", "missing/", ct: TestContext.Current.CancellationToken));

    // Looking for particular names

    [Fact]
    public async Task Find_returns_the_files_and_folders_that_exist_with_the_given_names()
    {
        var found = await _gcs.FindAsync("b", "docs/", ["guide.pdf", "old", "missing.txt", "empty"], TestContext.Current.CancellationToken);

        Assert.Equal(["Folder:empty", "File:guide.pdf", "Folder:old"], found.Select(Summary));
        Assert.Equal("docs/old/", found.Single(i => i.Name == "old").Path);
        Assert.Equal(10, found.Single(i => i.Name == "guide.pdf").Size);
    }

    [Fact]
    public async Task Find_matches_whole_names_only()
    {
        _client.Put("b", "docs/guide.pdf.bak", "x");
        _client.Put("b", "docs/older/x.txt", "x");

        var found = await _gcs.FindAsync("b", "docs/", ["guide", "guide.pdf.b", "ol"], TestContext.Current.CancellationToken);

        Assert.Empty(found);
    }

    [Fact]
    public async Task Find_reports_both_when_a_file_and_a_folder_share_a_name()
    {
        _client.Put("b", "docs/old", "a file called old");

        var found = await _gcs.FindAsync("b", "docs/", ["old"], TestContext.Current.CancellationToken);

        Assert.Equal(["File:old", "Folder:old"], found.Select(Summary).Order());
    }

    [Fact]
    public async Task Find_at_the_top_of_a_bucket_and_with_no_names()
    {
        Assert.Equal(["Folder:docs", "File:readme.txt"], (await _gcs.FindAsync("b", "", ["readme.txt", "docs"], TestContext.Current.CancellationToken)).Select(Summary));
        Assert.Empty(await _gcs.FindAsync("b", "", [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Find_costs_two_requests_a_name_whether_or_not_it_exists()
    {
        await _gcs.FindAsync("b", "docs/", ["guide.pdf", "missing.txt", "guide.pdf"], TestContext.Current.CancellationToken);

        // Each distinct name: one metadata read for the file, one listing for the folder.
        Assert.Equal(2, Usage.ClassBOperations);
        Assert.Equal(2, Usage.ClassAOperations);
    }

    [Fact]
    public async Task Find_passes_on_a_real_failure()
    {
        _client.Error = FakeStorageClient.ApiError(HttpStatusCode.Forbidden, "Access denied.");

        var error = await Assert.ThrowsAsync<GcsException>(() => _gcs.FindAsync("b", "docs/", ["guide.pdf"], TestContext.Current.CancellationToken));

        Assert.Equal(GcsError.PermissionDenied, error.Error);
        Assert.Equal("Access denied.", error.Message);
    }

    // Errors

    [Theory]
    [InlineData(HttpStatusCode.NotFound, GcsError.NotFound, "No such thing.")]
    [InlineData(HttpStatusCode.Forbidden, GcsError.PermissionDenied, "No such thing.")]
    [InlineData(HttpStatusCode.Unauthorized, GcsError.PermissionDenied, "No such thing.")]
    [InlineData(HttpStatusCode.PreconditionFailed, GcsError.AlreadyExists, "An item with that name already exists.")]
    [InlineData(HttpStatusCode.InternalServerError, GcsError.Other, "No such thing.")]
    public async Task Failures_are_translated_into_storage_errors(HttpStatusCode status, GcsError expected, string message)
    {
        _client.Error = FakeStorageClient.ApiError(status, "No such thing.");

        var error = await Assert.ThrowsAsync<GcsException>(() => _gcs.DeleteObjectAsync("b", "readme.txt", TestContext.Current.CancellationToken));

        Assert.Equal(expected, error.Error);
        Assert.Equal(message, error.Message);
        Assert.IsType<Google.GoogleApiException>(error.InnerException);
    }

    [Fact]
    public async Task Every_kind_of_request_reports_failure_the_same_way()
    {
        _client.Error = FakeStorageClient.ApiError(HttpStatusCode.Forbidden, "Access denied.");
        Func<Task>[] requests =
        [
            () => _gcs.ListBucketsAsync("proj"),
            () => _gcs.ListPageAsync("b", ""),
            () => _gcs.SearchPageAsync("b", "", "x"),
            () => _gcs.ListRecursiveAsync("b", ""),
            () => _gcs.MeasureAsync("b", ""),
            () => _gcs.ReadAsync("b", "readme.txt", 10),
            () => _gcs.GetDetailsAsync("b", "readme.txt"),
            () => _gcs.UpdateDetailsAsync("b", "readme.txt", "text/plain", ""),
            () => _gcs.DownloadAsync("b", "readme.txt", new MemoryStream()),
            () => _gcs.UploadAsync("b", "new.txt", new MemoryStream()),
            () => _gcs.CreateFolderAsync("b", "new/"),
            () => _gcs.CopyObjectAsync("b", "readme.txt", "b", "copy.txt", overwrite: false),
            () => _gcs.MoveObjectAsync("b", "readme.txt", "b", "moved.txt", overwrite: false),
            () => _gcs.CopyFolderAsync("b", "docs/", "b", "docs2/", overwrite: false),
            () => _gcs.MoveFolderAsync("b", "docs/", "b", "docs3/", overwrite: false),
            () => _gcs.DeleteFolderAsync("b", "docs/"),
        ];

        foreach (var request in requests)
        {
            var error = await Assert.ThrowsAsync<GcsException>(request);
            Assert.Equal("Access denied.", error.Message);
        }
    }

    [Fact]
    public async Task A_timestamp_that_cannot_be_read_is_left_blank_instead_of_failing_the_listing()
    {
        _client.CorruptTimestamps("b", "readme.txt");

        var page = await _gcs.ListPageAsync("b", "", ct: TestContext.Current.CancellationToken);
        var details = await _gcs.GetDetailsAsync("b", "readme.txt", TestContext.Current.CancellationToken);

        Assert.Null(page.Items.Single(i => i.Name == "readme.txt").Modified);
        Assert.Null(details.Created);
        Assert.Null(details.Updated);
        Assert.Equal(11, details.Size);
    }

    // Preview reads

    [Fact]
    public async Task Read_does_not_ask_for_a_partial_download_to_be_hash_checked()
    {
        await _gcs.ReadAsync("b", "readme.txt", 5, TestContext.Current.CancellationToken);

        Assert.Equal(Google.Cloud.Storage.V1.DownloadValidationMode.Never, _client.LastRangeValidation);
    }

    [Fact]
    public async Task Read_returns_only_the_requested_start_of_an_object()
    {
        var head = await _gcs.ReadAsync("b", "readme.txt", 5, TestContext.Current.CancellationToken);

        Assert.Equal("hello", Encoding.UTF8.GetString(head));
        Assert.Equal(1, Usage.ClassBOperations);
        Assert.Equal(5, Usage.BytesDownloaded);
    }

    [Fact]
    public async Task Read_returns_a_short_object_whole()
    {
        var all = await _gcs.ReadAsync("b", "readme.txt", 1000, TestContext.Current.CancellationToken);

        Assert.Equal("hello world", Encoding.UTF8.GetString(all));
        Assert.Equal(11, Usage.BytesDownloaded);
    }

    [Fact]
    public async Task Read_of_a_missing_object_fails_and_counts_nothing()
    {
        var error = await Assert.ThrowsAsync<GcsException>(
            () => _gcs.ReadAsync("b", "missing", 10, TestContext.Current.CancellationToken));

        Assert.Equal(GcsError.NotFound, error.Error);
        Assert.Equal(0, Usage.ClassBOperations);
    }

    // Metadata

    [Fact]
    public async Task GetDetails_maps_an_objects_metadata()
    {
        _client.Put("b", "tagged.txt", "abc", "text/plain", metadata: new() { ["owner"] = "me" });

        var details = await _gcs.GetDetailsAsync("b", "tagged.txt", TestContext.Current.CancellationToken);

        Assert.Equal("b", details.Bucket);
        Assert.Equal("tagged.txt", details.Name);
        Assert.Equal(3, details.Size);
        Assert.Equal("text/plain", details.ContentType);
        Assert.Null(details.CacheControl);
        Assert.Equal("STANDARD", details.StorageClass);
        Assert.Equal(new DateTimeOffset(2025, 6, 7, 8, 9, 10, TimeSpan.Zero), details.Created);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), details.Updated);
        Assert.Equal(1700000000000000, details.Generation);
        Assert.Equal("bWQ1", details.Md5);
        Assert.Equal("Y3Jj", details.Crc32c);
        Assert.Equal("me", details.Metadata["owner"]);
        Assert.Equal(1, Usage.ClassBOperations);
    }

    [Fact]
    public async Task GetDetails_gives_an_empty_metadata_set_when_there_is_none()
    {
        var details = await _gcs.GetDetailsAsync("b", "readme.txt", TestContext.Current.CancellationToken);

        Assert.Empty(details.Metadata);
    }

    [Fact]
    public async Task UpdateDetails_changes_the_content_type_and_cache_control_only()
    {
        var details = await _gcs.UpdateDetailsAsync("b", "readme.txt", "text/markdown", "no-store", TestContext.Current.CancellationToken);

        Assert.Equal("text/markdown", details.ContentType);
        Assert.Equal("no-store", details.CacheControl);
        Assert.Equal("text/markdown", _client.ContentType("b", "readme.txt"));
        Assert.Equal("no-store", _client.CacheControl("b", "readme.txt"));
        Assert.Equal("hello world", _client.Content("b", "readme.txt"));
        Assert.Equal(1, Usage.ClassAOperations);
    }

    [Fact]
    public async Task UpdateDetails_of_a_missing_object_fails()
    {
        await Assert.ThrowsAsync<GcsException>(() => _gcs.UpdateDetailsAsync("b", "missing", "text/plain", "", TestContext.Current.CancellationToken));

        Assert.Equal(0, Usage.ClassAOperations);
    }

    // Progress

    [Fact]
    public async Task Download_reports_the_bytes_written()
    {
        var reported = new List<long>();

        await _gcs.DownloadAsync("b", "readme.txt", new MemoryStream(), new InlineProgress<long>(reported.Add), TestContext.Current.CancellationToken);

        Assert.Equal(11, reported[^1]);
    }

    [Fact]
    public async Task Upload_reports_the_bytes_sent()
    {
        var reported = new List<long>();

        await _gcs.UploadAsync("b", "new.bin", new MemoryStream(new byte[42]), new InlineProgress<long>(reported.Add), TestContext.Current.CancellationToken);

        Assert.Equal([42], reported);
    }

    [Fact]
    public async Task Deleting_a_folder_reports_each_object_removed()
    {
        var progress = new CollectingProgress<OperationProgress>();

        await _gcs.DeleteFolderAsync("b", "docs/old/", progress, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], progress.Reports.Select(p => p.Done).Order());
        Assert.All(progress.Reports, p => Assert.Equal(2, p.Total));
    }

    [Fact]
    public async Task Copying_a_folder_reports_one_step_per_object()
    {
        var progress = new CollectingProgress<OperationProgress>();

        await _gcs.CopyFolderAsync("b", "docs/old/", "b", "copy/", overwrite: false, progress, ct: TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], progress.Reports.Select(p => p.Done).Order());
        Assert.All(progress.Reports, p => Assert.Equal(2, p.Total));
    }

    [Fact]
    public async Task Moving_a_folder_reports_a_copy_and_a_delete_per_object()
    {
        var progress = new CollectingProgress<OperationProgress>();

        await _gcs.MoveFolderAsync("b", "docs/old/", "b", "moved/", overwrite: false, progress, ct: TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3, 4], progress.Reports.Select(p => p.Done).Order());
        Assert.All(progress.Reports, p => Assert.Equal(4, p.Total));
    }

    [Fact]
    public async Task A_cancelled_folder_delete_stops()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _gcs.DeleteFolderAsync("b", "docs/", null, cts.Token));

        Assert.True(_client.Exists("b", "docs/guide.pdf"));
    }
}
