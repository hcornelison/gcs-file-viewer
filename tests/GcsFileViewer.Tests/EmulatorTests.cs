using System.Runtime.CompilerServices;
using System.Text;
using GcsFileViewer.Storage;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

namespace GcsFileViewer.Tests;

/// <summary>
/// Runs a test only when a Cloud Storage emulator is available, which is when STORAGE_EMULATOR_HOST is set.
/// CI starts one; locally, start fake-gcs-server and set the variable to run these. The .NET client library
/// expects the address to include the API path, as in http://localhost:4443/storage/v1/.
/// </summary>
public sealed class EmulatorFactAttribute : FactAttribute
{
    public const string HostVariable = "STORAGE_EMULATOR_HOST";

    public EmulatorFactAttribute(
        [CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(HostVariable)))
        {
            Skip = $"Needs a Cloud Storage emulator; set {HostVariable} to run.";
        }
    }
}

/// <summary>
/// The real GcsService and Google client library against an emulator, to catch anything the in-memory fake
/// gets wrong about how the API behaves.
/// </summary>
public class EmulatorTests
{
    private const string Project = "emulator-project";

    private static StorageClient NewClient() =>
        new StorageClientBuilder { EmulatorDetection = Google.Api.Gax.EmulatorDetection.EmulatorOnly, UnauthenticatedAccess = true }.Build();

    private static GcsService NewService() => new(new GcsEnvironment
    {
        GetDefaultCredential = () => Task.FromResult(GoogleCredential.FromAccessToken("emulator")),
        CreateClient = _ => Task.FromResult(NewClient()),
        CreateAnonymousClient = NewClient,
    });

    // Each test works in its own bucket, so they can run in any order and alongside each other.
    private static async Task<string> NewBucketAsync()
    {
        var bucket = "test-" + Guid.NewGuid().ToString("N");
        await NewClient().CreateBucketAsync(Project, bucket);
        return bucket;
    }

    private static Stream Text(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static async Task<string> DownloadAsync(GcsService gcs, string bucket, string name)
    {
        using var target = new MemoryStream();
        await gcs.DownloadAsync(bucket, name, target);
        return Encoding.UTF8.GetString(target.ToArray());
    }

    private static string Summary(BrowserItem item) => $"{item.Kind}:{item.Name}";

    [EmulatorFact]
    public async Task A_new_bucket_is_listed_for_its_project_and_can_be_opened()
    {
        var gcs = NewService();
        var bucket = await NewBucketAsync();

        var buckets = await gcs.ListBucketsAsync(Project);
        var opened = await gcs.OpenBucketAsync(bucket);

        Assert.Contains(bucket, buckets.Select(b => b.Name));
        Assert.Equal(bucket, opened.Name);
    }

    [EmulatorFact]
    public async Task Uploads_are_listed_by_level_and_download_intact()
    {
        var gcs = NewService();
        var bucket = await NewBucketAsync();
        await gcs.UploadAsync(bucket, "readme.txt", Text("hello"));
        await gcs.UploadAsync(bucket, "docs/guide.txt", Text("guide"));
        await gcs.UploadAsync(bucket, "docs/old/v1.txt", Text("v1"));

        var top = await gcs.ListPageAsync(bucket, "");
        var docs = await gcs.ListPageAsync(bucket, "docs/");

        Assert.Equal(["Folder:docs", "File:readme.txt"], top.Items.Select(Summary));
        Assert.Equal(["Folder:old", "File:guide.txt"], docs.Items.Select(Summary));
        Assert.Equal(5, top.Items.Single(i => i.Name == "readme.txt").Size);
        Assert.Equal("guide", await DownloadAsync(gcs, bucket, "docs/guide.txt"));
    }

    // Three things Cloud Storage does that the emulator doesn't, which are therefore covered by the in-memory
    // tests alone: refusing a write sent with "only if nothing has this name yet", listing a zero-byte object
    // whose name ends in a slash as a folder, and returning long listings a page at a time.

    [EmulatorFact]
    public async Task Names_are_found_as_files_and_folders()
    {
        var gcs = NewService();
        var bucket = await NewBucketAsync();
        await gcs.UploadAsync(bucket, "docs/guide.txt", Text("guide"));
        await gcs.UploadAsync(bucket, "docs/old/v1.txt", Text("v1"));

        var found = await gcs.FindAsync(bucket, "docs/", ["guide.txt", "old", "missing", "gui"]);

        Assert.Equal(["File:guide.txt", "Folder:old"], found.Select(Summary));
    }

    [EmulatorFact]
    public async Task Copy_keeps_the_original_and_can_overwrite()
    {
        var gcs = NewService();
        var bucket = await NewBucketAsync();
        await gcs.UploadAsync(bucket, "a.txt", Text("aaa"));
        await gcs.UploadAsync(bucket, "b.txt", Text("bbb"));

        await gcs.CopyObjectAsync(bucket, "a.txt", bucket, "copy.txt", overwrite: false);
        await gcs.CopyObjectAsync(bucket, "a.txt", bucket, "b.txt", overwrite: true);

        Assert.Equal("aaa", await DownloadAsync(gcs, bucket, "copy.txt"));
        Assert.Equal("aaa", await DownloadAsync(gcs, bucket, "b.txt"));
        Assert.Equal("aaa", await DownloadAsync(gcs, bucket, "a.txt"));
    }

    [EmulatorFact]
    public async Task Moving_a_folder_moves_everything_under_it()
    {
        var gcs = NewService();
        var bucket = await NewBucketAsync();
        await gcs.UploadAsync(bucket, "docs/guide.txt", Text("guide"));
        await gcs.UploadAsync(bucket, "docs/old/v1.txt", Text("v1"));
        await gcs.UploadAsync(bucket, "keep.txt", Text("keep"));

        var moved = await gcs.MoveFolderAsync(bucket, "docs/", bucket, "archive/", overwrite: false);
        var all = await gcs.ListRecursiveAsync(bucket, "");

        Assert.Equal(2, moved);
        Assert.Equal(["archive/guide.txt", "archive/old/v1.txt", "keep.txt"], all.Select(i => i.Name).Order(StringComparer.Ordinal));
        Assert.Equal("v1", await DownloadAsync(gcs, bucket, "archive/old/v1.txt"));
    }

    [EmulatorFact]
    public async Task Deleting_a_folder_removes_only_what_is_under_it()
    {
        var gcs = NewService();
        var bucket = await NewBucketAsync();
        await gcs.UploadAsync(bucket, "docs/a.txt", Text("a"));
        await gcs.UploadAsync(bucket, "docs/deep/b.txt", Text("b"));
        await gcs.UploadAsync(bucket, "docs-other.txt", Text("c"));

        var deleted = await gcs.DeleteFolderAsync(bucket, "docs/");
        var size = await gcs.MeasureAsync(bucket, "");

        Assert.Equal(2, deleted);
        Assert.Equal(new FolderSize(1, 1), size);
        var missing = await Assert.ThrowsAsync<GcsException>(() => gcs.DeleteObjectAsync(bucket, "docs/a.txt"));
        Assert.Equal(GcsError.NotFound, missing.Error);
    }

    [EmulatorFact]
    public async Task The_start_of_an_object_can_be_read_and_its_metadata_changed()
    {
        var gcs = NewService();
        var bucket = await NewBucketAsync();
        await gcs.UploadAsync(bucket, "notes.txt", Text("0123456789"));

        var head = await gcs.ReadAsync(bucket, "notes.txt", 4);
        var before = await gcs.GetDetailsAsync(bucket, "notes.txt");
        var after = await gcs.UpdateDetailsAsync(bucket, "notes.txt", "text/markdown", "no-store");

        Assert.Equal("0123", Encoding.UTF8.GetString(head));
        Assert.Equal(10, before.Size);
        Assert.Equal("text/plain", before.ContentType);
        Assert.Equal("text/markdown", after.ContentType);
        Assert.Equal("no-store", after.CacheControl);
    }
}
