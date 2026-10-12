using System.Net;
using GcsFileViewer.Pricing;
using GcsFileViewer.Storage;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Storage.v1.Data;

namespace GcsFileViewer.Tests;

/// <summary>What GcsService tallies as billable usage for each kind of request.</summary>
public class GcsServiceUsageTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private readonly FakeStorageClient _client = new();
    private readonly FakeStorageClient _anonymous = new();
    private readonly GcsService _gcs;

    public GcsServiceUsageTests()
    {
        _gcs = TestServices.Create(_client, _anonymous);

        _client.BucketsByProject["proj"] =
        [
            new Bucket { Name = "b", Location = "us-east1", StorageClass = "STANDARD" },
            new Bucket { Name = "multi", Location = "US", StorageClass = "STANDARD" },
            new Bucket { Name = "cold", Location = "us-east1", StorageClass = "COLDLINE" },
        ];
        _client.Put("b", "readme.txt", "hello");
        _client.Put("b", "docs/a.txt", "aaaa");
        _client.Put("b", "docs/b.txt", "bb");
        _client.Put("b", "old/report.pdf", "0123456789", storageClass: "COLDLINE");
        _client.Put("b", "old/notes.txt", "12345", storageClass: "NEARLINE");
    }

    private UsageSnapshot Usage => _gcs.Usage.Snapshot();

    [Fact]
    public void Nothing_is_tallied_before_any_request()
    {
        Assert.Equal(0, Usage.ClassAOperations);
        Assert.Equal(0m, Usage.TotalCost);
    }

    [Fact]
    public async Task Listing_buckets_is_one_standard_class_A_request()
    {
        await _gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken);

        Assert.Equal(1, Usage.ClassAOperations);
        Assert.Equal(0.000005m, Usage.ClassACost);
    }

    [Fact]
    public async Task Listing_a_folder_is_one_class_A_request_per_page()
    {
        for (var i = 0; i < 7; i++)
        {
            _client.Put("big", $"f{i}");
        }

        _client.PageSize = 3;

        var page = await _gcs.ListPageAsync("big", "", ct: TestContext.Current.CancellationToken);
        while (page.NextPageToken is not null)
        {
            page = await _gcs.ListPageAsync("big", "", page.NextPageToken, TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, Usage.ClassAOperations);
        Assert.Equal(0.000015m, Usage.TotalCost);
    }

    [Fact]
    public async Task Requests_in_a_multi_region_bucket_cost_double()
    {
        await _gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken);
        _gcs.Usage.Reset();

        await _gcs.ListPageAsync("multi", "", ct: TestContext.Current.CancellationToken);

        Assert.Equal(0.00001m, Usage.ClassACost);
    }

    [Fact]
    public async Task Requests_are_priced_at_the_buckets_default_storage_class()
    {
        await _gcs.ListBucketsAsync("proj", TestContext.Current.CancellationToken);
        _gcs.Usage.Reset();

        await _gcs.UploadAsync("cold", "new.txt", new MemoryStream([1]), ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, Usage.ClassAOperations);
        Assert.Equal(0.00002m, Usage.ClassACost);
    }

    [Fact]
    public async Task Opening_a_bucket_by_name_is_one_class_A_request()
    {
        await _gcs.OpenBucketAsync("b", TestContext.Current.CancellationToken);

        Assert.Equal(1, Usage.ClassAOperations);
    }

    [Fact]
    public async Task Download_counts_a_class_B_request_and_the_bytes_sent()
    {
        await _gcs.DownloadAsync("b", "readme.txt", new MemoryStream(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, Usage.ClassBOperations);
        Assert.Equal(5, Usage.BytesDownloaded);
        Assert.Equal(0, Usage.BytesRetrieved);
        Assert.Equal(0.0000004m, Usage.ClassBCost);
        Assert.Equal(GcsPricing.Download(5), Usage.DownloadCost);
    }

    [Fact]
    public async Task Download_of_a_listed_cold_object_adds_a_retrieval_fee_at_its_class_rate()
    {
        await _gcs.ListPageAsync("b", "old/", ct: TestContext.Current.CancellationToken);
        _gcs.Usage.Reset();

        await _gcs.DownloadAsync("b", "old/report.pdf", new MemoryStream(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(10, Usage.BytesDownloaded);
        Assert.Equal(10, Usage.BytesRetrieved);
        Assert.Equal(0.00001m, Usage.ClassBCost);
        Assert.Equal(GcsPricing.Retrieval(StorageTier.Coldline, 10), Usage.RetrievalCost);
    }

    [Fact]
    public async Task A_failed_download_is_not_counted()
    {
        await Assert.ThrowsAsync<GcsException>(() => _gcs.DownloadAsync("b", "missing", new MemoryStream(), ct: TestContext.Current.CancellationToken));

        Assert.Equal(0, Usage.ClassBOperations);
        Assert.Equal(0, Usage.BytesDownloaded);
    }

    [Fact]
    public async Task Upload_and_new_folder_are_class_A_requests()
    {
        await _gcs.UploadAsync("b", "new.txt", new MemoryStream([1, 2, 3]), ct: TestContext.Current.CancellationToken);
        await _gcs.CreateFolderAsync("b", "new-folder/", TestContext.Current.CancellationToken);

        Assert.Equal(2, Usage.ClassAOperations);
        Assert.Equal(0, Usage.BytesDownloaded);
    }

    [Fact]
    public async Task Copy_is_a_class_A_request_with_a_retrieval_fee_for_a_cold_source()
    {
        await _gcs.ListPageAsync("b", "old/", ct: TestContext.Current.CancellationToken);
        _gcs.Usage.Reset();

        await _gcs.CopyObjectAsync("b", "old/report.pdf", "b", "report-copy.pdf", overwrite: false, ct: TestContext.Current.CancellationToken);
        await _gcs.CopyObjectAsync("b", "readme.txt", "b", "readme-copy.txt", overwrite: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, Usage.ClassAOperations);
        Assert.Equal(10, Usage.BytesRetrieved);
        Assert.Equal(0, Usage.ColdObjectsRemoved);
    }

    [Fact]
    public async Task A_refused_copy_is_not_counted()
    {
        await Assert.ThrowsAsync<GcsException>(
            () => _gcs.CopyObjectAsync("b", "readme.txt", "b", "docs/a.txt", overwrite: false, ct: TestContext.Current.CancellationToken));

        Assert.Equal(0, Usage.ClassAOperations);
    }

    [Fact]
    public async Task Move_adds_a_free_delete_and_flags_a_cold_original()
    {
        await _gcs.ListPageAsync("b", "old/", ct: TestContext.Current.CancellationToken);
        _gcs.Usage.Reset();

        await _gcs.MoveObjectAsync("b", "old/notes.txt", "b", "notes.txt", overwrite: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, Usage.ClassAOperations);
        Assert.Equal(1, Usage.FreeOperations);
        Assert.Equal(5, Usage.BytesRetrieved);
        Assert.Equal(1, Usage.ColdObjectsRemoved);
    }

    [Fact]
    public async Task Delete_is_free_and_flags_a_cold_object_once()
    {
        await _gcs.ListPageAsync("b", "old/", ct: TestContext.Current.CancellationToken);
        _gcs.Usage.Reset();

        await _gcs.DeleteObjectAsync("b", "old/report.pdf", TestContext.Current.CancellationToken);
        await _gcs.DeleteObjectAsync("b", "readme.txt", TestContext.Current.CancellationToken);

        Assert.Equal(2, Usage.FreeOperations);
        Assert.Equal(1, Usage.ColdObjectsRemoved);
        Assert.Equal(0m, Usage.TotalCost);
    }

    [Fact]
    public async Task Replacing_a_cold_object_by_upload_flags_it()
    {
        await _gcs.ListPageAsync("b", "old/", ct: TestContext.Current.CancellationToken);

        await _gcs.UploadAsync("b", "old/report.pdf", new MemoryStream([1]), ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, Usage.ColdObjectsRemoved);
    }

    [Fact]
    public async Task Deleting_a_folder_is_one_listing_plus_free_deletes()
    {
        var deleted = await _gcs.DeleteFolderAsync("b", "old/", ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, deleted);
        Assert.Equal(1, Usage.ClassAOperations);
        Assert.Equal(2, Usage.FreeOperations);
        Assert.Equal(2, Usage.ColdObjectsRemoved);
    }

    [Fact]
    public async Task Moving_a_folder_is_a_listing_then_a_copy_and_delete_per_object()
    {
        await _gcs.MoveFolderAsync("b", "old/", "b", "archive/", overwrite: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, Usage.ClassAOperations);
        Assert.Equal(2, Usage.FreeOperations);
        Assert.Equal(15, Usage.BytesRetrieved);
        Assert.Equal(2, Usage.ColdObjectsRemoved);
        Assert.Equal(
            GcsPricing.Retrieval(StorageTier.Coldline, 10) + GcsPricing.Retrieval(StorageTier.Nearline, 5),
            Usage.RetrievalCost);
    }

    [Fact]
    public async Task Copying_a_folder_removes_nothing()
    {
        await _gcs.CopyFolderAsync("b", "docs/", "b", "docs-copy/", overwrite: false, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, Usage.ClassAOperations);
        Assert.Equal(0, Usage.FreeOperations);
        Assert.Equal(0, Usage.BytesRetrieved);
    }

    [Fact]
    public async Task Requests_to_a_public_bucket_opened_anonymously_are_not_counted()
    {
        _client.Error = FakeStorageClient.ApiError(HttpStatusCode.Forbidden, "denied");
        _anonymous.Put("public", "data.csv", "1,2,3");

        await _gcs.OpenBucketAsync("public", TestContext.Current.CancellationToken);
        await _gcs.ListPageAsync("public", "", ct: TestContext.Current.CancellationToken);
        await _gcs.DownloadAsync("public", "data.csv", new MemoryStream(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, Usage.ClassAOperations);
        Assert.Equal(0, Usage.ClassBOperations);
        Assert.Equal(0, Usage.BytesDownloaded);
        Assert.Equal(0m, Usage.TotalCost);
    }

    [Fact]
    public void The_tracker_totals_every_kind_of_charge_and_resets()
    {
        var usage = new UsageTracker();
        usage.RecordClassA(StorageTier.Standard, multiRegion: false, 2000);
        usage.RecordClassB(StorageTier.Standard, 10_000);
        usage.RecordFree(7);
        usage.RecordDownload(GiB);
        usage.RecordRetrieval(StorageTier.Archive, 2 * GiB);
        usage.RecordRetrieval(StorageTier.Standard, GiB);
        usage.RecordColdRemoval(3);

        var snapshot = usage.Snapshot();

        Assert.Equal(0.01m, snapshot.ClassACost);
        Assert.Equal(0.004m, snapshot.ClassBCost);
        Assert.Equal(0.12m, snapshot.DownloadCost);
        Assert.Equal(0.10m, snapshot.RetrievalCost);
        Assert.Equal(0.234m, snapshot.TotalCost);
        Assert.Equal(7, snapshot.FreeOperations);
        Assert.Equal(2 * GiB, snapshot.BytesRetrieved);
        Assert.Equal(3, snapshot.ColdObjectsRemoved);

        usage.Reset();

        Assert.Equal(0m, usage.Snapshot().TotalCost);
        Assert.Equal(0, usage.Snapshot().ClassAOperations);
    }

    [Fact]
    public async Task The_tracker_is_safe_to_update_from_many_threads()
    {
        var usage = new UsageTracker();

        await Parallel.ForEachAsync(Enumerable.Range(0, 2000), (_, _) =>
        {
            usage.RecordClassA(StorageTier.Standard, multiRegion: false);
            usage.RecordFree();
            return ValueTask.CompletedTask;
        });

        Assert.Equal(2000, usage.Snapshot().ClassAOperations);
        Assert.Equal(2000, usage.Snapshot().FreeOperations);
        Assert.Equal(0.01m, usage.Snapshot().ClassACost);
    }
}
