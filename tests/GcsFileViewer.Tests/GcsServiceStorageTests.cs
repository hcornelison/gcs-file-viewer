using System.Net;
using System.Text;
using GcsFileViewer.Pricing;
using GcsFileViewer.Storage;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Storage.v1.Data;

namespace GcsFileViewer.Tests;

/// <summary>GcsService against an in-memory storage client: listing, transfers and bucket access.</summary>
public class GcsServiceStorageTests
{
    private readonly FakeStorageClient _client = new();
    private readonly FakeStorageClient _anonymous = new();
    private readonly GcsService _gcs;

    public GcsServiceStorageTests()
    {
        _gcs = TestServices.Create(_client, _anonymous);

        _client.Put("b", "readme.txt", "hello", "text/plain");
        _client.Put("b", "Zeta.bin", "12345678");
        _client.Put("b", "docs/", "", "application/x-directory");
        _client.Put("b", "docs/guide.pdf", "pdf!", "application/pdf");
        _client.Put("b", "docs/old/v1.pdf", "v1", "application/pdf");
        _client.Put("b", "docs/old/v2.pdf", "v2", "application/pdf");
        _client.Put("b", "archive/2025/a.log", "a");
    }

    private static string Summary(BrowserItem item) => $"{item.Kind}:{item.Name}";

    // Listing

    [Fact]
    public async Task ListBuckets_sorts_by_name_and_describes_the_location()
    {
        _client.BucketsByProject["proj"] =
        [
            new Bucket { Name = "zulu", Location = "US-EAST1", StorageClass = "STANDARD" },
            new Bucket { Name = "Alpha", StorageClass = "NEARLINE" },
        ];

        var buckets = await _gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken);

        Assert.Equal(["Alpha", "zulu"], buckets.Select(b => b.Name));
        Assert.All(buckets, b => Assert.Equal(ItemKind.Bucket, b.Kind));
        Assert.Equal("Bucket", buckets[0].Type);
        Assert.Equal("NEARLINE", buckets[0].StorageClass);
        Assert.Equal("Bucket (us-east1)", buckets[1].Type);
        Assert.Equal("gs://zulu/", buckets[1].Uri);
    }

    [Fact]
    public async Task ListBuckets_of_an_unknown_project_is_empty() =>
        Assert.Empty(await _gcs.ListBucketsAsync("nope", TestContext.Current.CancellationToken));

    [Fact]
    public async Task List_at_the_top_shows_folders_first_then_files_ignoring_case()
    {
        var items = (await _gcs.ListPageAsync("b", "", ct: TestContext.Current.CancellationToken)).Items;

        Assert.Equal(["Folder:archive", "Folder:docs", "File:readme.txt", "File:Zeta.bin"], items.Select(Summary));
    }

    [Fact]
    public async Task List_describes_files_with_size_type_date_and_full_path()
    {
        var items = (await _gcs.ListPageAsync("b", "docs/", ct: TestContext.Current.CancellationToken)).Items;

        var file = Assert.Single(items, i => i.Kind == ItemKind.File);
        Assert.Equal("guide.pdf", file.Name);
        Assert.Equal("docs/guide.pdf", file.Path);
        Assert.Equal("b", file.Bucket);
        Assert.Equal(4, file.Size);
        Assert.Equal("application/pdf", file.Type);
        Assert.Equal("STANDARD", file.StorageClass);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), file.Modified);
    }

    [Fact]
    public async Task List_names_folders_relative_to_the_prefix_and_hides_the_placeholder()
    {
        var items = (await _gcs.ListPageAsync("b", "docs/", ct: TestContext.Current.CancellationToken)).Items;

        Assert.Equal(["Folder:old", "File:guide.pdf"], items.Select(Summary));
        Assert.Equal("docs/old/", items[0].Path);
        Assert.Equal("Folder", items[0].Type);
        Assert.Null(items[0].Size);
    }

    [Fact]
    public async Task List_falls_back_to_the_extension_when_there_is_no_content_type()
    {
        var items = (await _gcs.ListPageAsync("b", "", ct: TestContext.Current.CancellationToken)).Items;

        Assert.Equal("BIN file", items.Single(i => i.Name == "Zeta.bin").Type);
    }

    [Fact]
    public async Task List_of_an_empty_folder_is_empty() =>
        Assert.Empty((await _gcs.ListPageAsync("b", "nothing/", ct: TestContext.Current.CancellationToken)).Items);

    [Fact]
    public async Task List_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _gcs.ListPageAsync("b", "", null, cts.Token));
    }

    // Download and upload

    [Fact]
    public async Task Download_writes_the_objects_bytes()
    {
        using var target = new MemoryStream();

        await _gcs.DownloadAsync("b", "readme.txt", target, ct: TestContext.Current.CancellationToken);

        Assert.Equal("hello", Encoding.UTF8.GetString(target.ToArray()));
    }

    [Fact]
    public async Task Upload_stores_the_content_with_a_type_guessed_from_the_name()
    {
        await _gcs.UploadAsync("b", "docs/notes.md", new MemoryStream(Encoding.UTF8.GetBytes("# hi")), ct: TestContext.Current.CancellationToken);
        await _gcs.UploadAsync("b", "model.bin", new MemoryStream([1, 2]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("# hi", _client.Content("b", "docs/notes.md"));
        Assert.Equal("text/markdown", _client.ContentType("b", "docs/notes.md"));
        Assert.Null(_client.ContentType("b", "model.bin"));
    }

    [Fact]
    public async Task Upload_replaces_an_existing_object()
    {
        await _gcs.UploadAsync("b", "readme.txt", new MemoryStream(Encoding.UTF8.GetBytes("new")), ct: TestContext.Current.CancellationToken);

        Assert.Equal("new", _client.Content("b", "readme.txt"));
    }

    // Folders

    [Fact]
    public async Task CreateFolder_writes_an_empty_placeholder_that_then_lists_as_a_folder()
    {
        await _gcs.CreateFolderAsync("b", "docs/drafts/", TestContext.Current.CancellationToken);

        Assert.Equal("", _client.Content("b", "docs/drafts/"));
        var items = (await _gcs.ListPageAsync("b", "docs/", ct: TestContext.Current.CancellationToken)).Items;
        Assert.Contains("Folder:drafts", items.Select(Summary));
        Assert.Empty((await _gcs.ListPageAsync("b", "docs/drafts/", ct: TestContext.Current.CancellationToken)).Items);
    }

    [Fact]
    public async Task CreateFolder_refuses_to_overwrite_an_existing_placeholder()
    {
        var error = await Assert.ThrowsAsync<GcsException>(() => _gcs.CreateFolderAsync("b", "docs/", TestContext.Current.CancellationToken));

        Assert.Equal(GcsError.AlreadyExists, error.Error);
    }

    [Fact]
    public async Task DeleteObject_removes_just_that_object()
    {
        await _gcs.DeleteObjectAsync("b", "readme.txt", TestContext.Current.CancellationToken);

        Assert.False(_client.Exists("b", "readme.txt"));
        Assert.True(_client.Exists("b", "Zeta.bin"));
    }

    [Fact]
    public async Task DeleteFolder_removes_everything_under_the_prefix_and_counts_it()
    {
        var deleted = await _gcs.DeleteFolderAsync("b", "docs/", ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, deleted);
        Assert.Equal(["Zeta.bin", "archive/2025/a.log", "readme.txt"], _client.Names("b"));
    }

    [Fact]
    public async Task DeleteFolder_of_nothing_deletes_nothing() =>
        Assert.Equal(0, await _gcs.DeleteFolderAsync("b", "missing/", ct: TestContext.Current.CancellationToken));

    // Copy and move

    [Fact]
    public async Task CopyObject_keeps_the_original_and_can_cross_buckets()
    {
        await _gcs.CopyObjectAsync("b", "readme.txt", "other", "copies/readme.txt", overwrite: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal("hello", _client.Content("other", "copies/readme.txt"));
        Assert.Equal("text/plain", _client.ContentType("other", "copies/readme.txt"));
        Assert.True(_client.Exists("b", "readme.txt"));
    }

    [Fact]
    public async Task CopyObject_without_overwrite_refuses_a_taken_name()
    {
        var error = await Assert.ThrowsAsync<GcsException>(
            () => _gcs.CopyObjectAsync("b", "readme.txt", "b", "Zeta.bin", overwrite: false, ct: TestContext.Current.CancellationToken));

        Assert.Equal(GcsError.AlreadyExists, error.Error);
        Assert.Equal("12345678", _client.Content("b", "Zeta.bin"));
    }

    [Fact]
    public async Task CopyObject_with_overwrite_replaces_the_target()
    {
        await _gcs.CopyObjectAsync("b", "readme.txt", "b", "Zeta.bin", overwrite: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal("hello", _client.Content("b", "Zeta.bin"));
    }

    [Fact]
    public async Task MoveObject_copies_then_removes_the_original()
    {
        await _gcs.MoveObjectAsync("b", "readme.txt", "b", "docs/README.md", overwrite: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal("hello", _client.Content("b", "docs/README.md"));
        Assert.False(_client.Exists("b", "readme.txt"));
    }

    [Fact]
    public async Task MoveObject_that_cannot_copy_leaves_the_original_in_place()
    {
        await Assert.ThrowsAsync<GcsException>(
            () => _gcs.MoveObjectAsync("b", "readme.txt", "b", "Zeta.bin", overwrite: false, ct: TestContext.Current.CancellationToken));

        Assert.True(_client.Exists("b", "readme.txt"));
    }

    [Fact]
    public async Task CopyFolder_copies_the_whole_tree_under_the_new_prefix()
    {
        var copied = await _gcs.CopyFolderAsync("b", "docs/", "other", "backup/docs/", overwrite: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, copied);
        Assert.Equal(
            ["backup/docs/", "backup/docs/guide.pdf", "backup/docs/old/v1.pdf", "backup/docs/old/v2.pdf"],
            _client.Names("other"));
        Assert.True(_client.Exists("b", "docs/old/v1.pdf"));
    }

    [Fact]
    public async Task MoveFolder_moves_the_whole_tree_and_leaves_siblings_alone()
    {
        var moved = await _gcs.MoveFolderAsync("b", "docs/old/", "b", "docs/archive/", overwrite: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, moved);
        Assert.Equal(
            ["Zeta.bin", "archive/2025/a.log", "docs/", "docs/archive/v1.pdf", "docs/archive/v2.pdf", "docs/guide.pdf", "readme.txt"],
            _client.Names("b"));
    }

    [Fact]
    public async Task MoveFolder_that_hits_a_taken_name_deletes_no_originals()
    {
        _client.Put("b", "docs/archive/v2.pdf", "existing");

        await Assert.ThrowsAsync<GcsException>(
            () => _gcs.MoveFolderAsync("b", "docs/old/", "b", "docs/archive/", overwrite: false, ct: TestContext.Current.CancellationToken));

        Assert.True(_client.Exists("b", "docs/old/v1.pdf"));
        Assert.True(_client.Exists("b", "docs/old/v2.pdf"));
        Assert.Equal("existing", _client.Content("b", "docs/archive/v2.pdf"));
    }

    [Fact]
    public async Task MoveFolder_with_overwrite_merges_into_an_existing_folder()
    {
        _client.Put("b", "docs/archive/v2.pdf", "existing");
        _client.Put("b", "docs/archive/keep.txt", "keep");

        await _gcs.MoveFolderAsync("b", "docs/old/", "b", "docs/archive/", overwrite: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal("v2", _client.Content("b", "docs/archive/v2.pdf"));
        Assert.Equal("keep", _client.Content("b", "docs/archive/keep.txt"));
        Assert.False(_client.Exists("b", "docs/old/v1.pdf"));
    }

    // Opening a bucket by name

    [Fact]
    public async Task OpenBucket_uses_the_signed_in_account_when_it_can_list_the_bucket()
    {
        var bucket = await _gcs.OpenBucketAsync("b", TestContext.Current.CancellationToken);

        Assert.Equal(ItemKind.Bucket, bucket.Kind);
        Assert.Equal("b", bucket.Name);
        Assert.Equal("Bucket", bucket.Type);
        Assert.Equal(0, _anonymous.Calls);
    }

    [Fact]
    public async Task OpenBucket_falls_back_to_anonymous_access_and_keeps_using_it()
    {
        _client.Error = FakeStorageClient.ApiError(HttpStatusCode.Forbidden, "me@example.com does not have storage.objects.list access.");
        _anonymous.Put("public", "index.html", "<html>", "text/html");

        var bucket = await _gcs.OpenBucketAsync("public", TestContext.Current.CancellationToken);
        var items = (await _gcs.ListPageAsync("public", "", ct: TestContext.Current.CancellationToken)).Items;
        using var target = new MemoryStream();
        await _gcs.DownloadAsync("public", "index.html", target, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Bucket (public)", bucket.Type);
        Assert.Equal(["File:index.html"], items.Select(Summary));
        Assert.Equal("<html>", Encoding.UTF8.GetString(target.ToArray()));
    }

    [Fact]
    public async Task OpenBucket_goes_back_to_the_signed_in_account_once_it_works()
    {
        _client.Error = FakeStorageClient.ApiError(HttpStatusCode.Forbidden, "denied");
        await _gcs.OpenBucketAsync("b", TestContext.Current.CancellationToken);
        _client.Error = null;

        var bucket = await _gcs.OpenBucketAsync("b", TestContext.Current.CancellationToken);
        var items = (await _gcs.ListPageAsync("b", "docs/old/", ct: TestContext.Current.CancellationToken)).Items;

        Assert.Equal("Bucket", bucket.Type);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task OpenBucket_reports_the_signed_in_accounts_error_when_nothing_works()
    {
        _client.Error = FakeStorageClient.ApiError(HttpStatusCode.Forbidden, "me@example.com does not have storage.objects.list access.");
        _anonymous.Error = FakeStorageClient.ApiError(HttpStatusCode.Unauthorized, "Anonymous caller does not have access.");

        var error = await Assert.ThrowsAsync<GcsException>(() => _gcs.OpenBucketAsync("secret", TestContext.Current.CancellationToken));

        Assert.Equal("Can't open bucket \"secret\": me@example.com does not have storage.objects.list access.", error.Message);
    }

    [Fact]
    public async Task OpenBucket_without_any_credentials_reports_the_anonymous_error()
    {
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = () => throw new InvalidOperationException("no default credentials"),
            GetGcloudAccessToken = _ => throw new InvalidOperationException("gcloud is not installed"),
            CreateAnonymousClient = () => _anonymous,
        });
        _anonymous.Error = FakeStorageClient.ApiError(HttpStatusCode.NotFound, "The specified bucket does not exist.");

        var error = await Assert.ThrowsAsync<GcsException>(() => gcs.OpenBucketAsync("nope", TestContext.Current.CancellationToken));

        Assert.Equal("Can't open bucket \"nope\": The specified bucket does not exist.", error.Message);
    }

    [Fact]
    public async Task OpenBucket_without_any_credentials_still_opens_a_public_bucket()
    {
        var gcs = new GcsService(new GcsEnvironment
        {
            GetDefaultCredential = () => throw new InvalidOperationException("no default credentials"),
            GetGcloudAccessToken = _ => throw new InvalidOperationException("gcloud is not installed"),
            CreateAnonymousClient = () => _anonymous,
        });
        _anonymous.Put("public", "data/a.csv", "1,2");

        var bucket = await gcs.OpenBucketAsync("public", TestContext.Current.CancellationToken);
        var items = (await gcs.ListPageAsync("public", "", ct: TestContext.Current.CancellationToken)).Items;

        Assert.Equal("Bucket (public)", bucket.Type);
        Assert.Equal(["Folder:data"], items.Select(Summary));
    }
}
