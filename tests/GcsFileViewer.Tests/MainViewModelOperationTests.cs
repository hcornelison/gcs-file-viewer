using System.Text;
using GcsFileViewer.Pricing;
using GcsFileViewer.Storage;
using GcsFileViewer.ViewModels;
using static GcsFileViewer.Tests.FakeGcsService;

namespace GcsFileViewer.Tests;

/// <summary>Progress, cancelling, whole-folder downloads and uploads, and folder sizes.</summary>
public class MainViewModelOperationTests : ViewModelTestBase
{
    public MainViewModelOperationTests()
    {
        Gcs.Contents["alpha/readme.txt"] = Encoding.UTF8.GetBytes(new string('r', 100));
        Gcs.Contents["alpha/notes.md"] = Encoding.UTF8.GetBytes(new string('n', 924));
        Gcs.Contents["alpha/docs/guide.pdf"] = Encoding.UTF8.GetBytes(new string('g', 2048));
        Gcs.Contents["alpha/docs/old/v1.pdf"] = Encoding.UTF8.GetBytes("version1!!");
    }

    private static UploadSource Source(string name, string content, bool sized = true) => new(
        name,
        () => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content))),
        sized ? content.Length : null);

    // Progress and cancel

    [Fact]
    public void Nothing_is_cancellable_when_idle()
    {
        Assert.False(Vm.IsCancellable);
        Assert.False(Vm.CancelCommand.CanExecute(null));
        Assert.True(Vm.IsProgressIndeterminate);
        Assert.Equal(0, Vm.ProgressValue);
    }

    [Fact]
    public async Task Download_progress_follows_the_bytes_written_across_files()
    {
        await OpenAsync("alpha");
        var seen = new List<(string File, double? Progress, bool Cancellable, bool Busy)>();
        Gcs.BeforeEach = key => seen.Add((key, Vm.Progress, Vm.IsCancellable, Vm.IsBusy));
        var fractions = new List<double>();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Progress) && Vm.Progress is { } value)
            {
                fractions.Add(Math.Round(value, 3));
            }
        };

        await Vm.DownloadAsync([Item("readme.txt"), Item("notes.md")], new FakeDownloadTarget());

        // 100 bytes then 924 bytes: each arrives in two halves.
        Assert.Equal([0.049, 0.098, 0.549, 1], fractions.Distinct());
        Assert.Equal([("alpha/readme.txt", null, true, true), ("alpha/notes.md", 100 / 1024.0, true, true)], seen);
        Assert.Null(Vm.Progress);
        Assert.False(Vm.IsCancellable);
        Assert.False(Vm.IsBusy);
    }

    [Fact]
    public async Task Cancel_stops_before_the_next_file_and_says_so()
    {
        await OpenAsync("alpha");
        var target = new FakeDownloadTarget();
        Gcs.BeforeEach = key =>
        {
            if (key == "alpha/notes.md")
            {
                Vm.CancelCommand.Execute(null);
            }
        };

        await Vm.DownloadAsync([Item("readme.txt"), Item("notes.md"), Item("docs")], target);

        Assert.Equal("Cancelled", Vm.Status);
        Assert.Equal(100, target.Text("readme.txt").Length);
        Assert.Equal("", target.Text("notes.md"));
        Assert.DoesNotContain("docs/guide.pdf", target.Paths);
        Assert.False(Vm.IsBusy);
        Assert.False(Vm.IsCancellable);
        Assert.Null(Vm.Progress);
    }

    [Fact]
    public async Task Cancelling_a_delete_refreshes_the_folder_since_some_of_it_may_be_gone()
    {
        await OpenAsync("alpha");
        var before = Gcs.PageRequests;
        Gcs.BeforeEach = key =>
        {
            if (key == "alpha/notes.md")
            {
                Vm.CancelCommand.Execute(null);
            }
        };

        var delete = Vm.DeleteAsync([Item("readme.txt"), Item("notes.md")]);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await delete;

        Assert.Equal(["alpha/readme.txt"], Gcs.DeletedObjects);
        Assert.Equal("Cancelled", Vm.Status);
        Assert.Equal(before + 1, Gcs.PageRequests);
    }

    [Fact]
    public async Task Only_one_operation_runs_at_a_time()
    {
        await OpenAsync("alpha");
        Task? second = null;
        Gcs.BeforeEach = _ => second ??= Vm.MeasureAsync(Item("docs"));

        await Vm.DownloadAsync([Item("readme.txt")], new FakeDownloadTarget());
        await second!;

        // The refusal was shown while the download ran; the download's own result replaced it afterwards.
        Assert.Equal("Downloaded readme.txt", Vm.Status);
        Assert.Null(Item("docs").Size);
    }

    [Fact]
    public async Task Folder_operations_report_their_steps()
    {
        await OpenAsync("alpha");
        var statuses = new List<string>();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Status))
            {
                statuses.Add(Vm.Status);
            }
        };

        var delete = Vm.DeleteAsync([Item("docs")]);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await delete;

        Assert.Contains("Deleting docs… 1 of 3", statuses);
        Assert.Contains("Deleting docs… 3 of 3", statuses);
        Assert.Equal("Deleted 3 objects", Vm.Status);
    }

    [Fact]
    public async Task Moving_a_folder_reports_its_steps()
    {
        await OpenAsync("alpha");
        var statuses = new List<string>();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Status))
            {
                statuses.Add(Vm.Status);
            }
        };

        await Vm.DropAsync([Item("docs")], new Location("beta", ""), move: true);

        Assert.Contains("Moving docs… 2 of 3", statuses);
        Assert.Equal("Moved 3 objects", Vm.Status);
    }

    // Downloading folders

    [Fact]
    public async Task Downloading_a_folder_recreates_its_structure()
    {
        await OpenAsync("alpha");
        var target = new FakeDownloadTarget();

        await Vm.DownloadAsync([Item("docs"), Item("readme.txt")], target);

        Assert.Equal(["docs", "docs/old"], target.Folders);
        Assert.Equal(["docs/guide.pdf", "docs/old/v1.pdf", "readme.txt"], target.Paths.Order());
        Assert.Equal("version1!!", target.Text("docs/old/v1.pdf"));
        Assert.Equal("Downloaded 3 files", Vm.Status);
    }

    [Fact]
    public async Task Downloading_an_empty_folder_still_creates_it()
    {
        Gcs.Listings["alpha/"].Add(Folder("alpha", "empty/"));
        await OpenAsync("alpha");
        var target = new FakeDownloadTarget();

        await Vm.DownloadAsync([Item("empty")], target);

        Assert.Equal(["empty"], target.Folders);
        Assert.Empty(target.Paths);
        Assert.Equal("Downloaded 0 files", Vm.Status);
    }

    [Fact]
    public async Task Buckets_are_not_downloaded()
    {
        await ConnectAsync();
        var target = new FakeDownloadTarget();

        await Vm.DownloadAsync([Item("alpha")], target);

        Assert.Empty(target.Paths);
        Assert.Empty(target.Folders);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_listed_fails_the_download()
    {
        await OpenAsync("alpha");
        var docs = Item("docs");
        Gcs.ListError = new InvalidOperationException("Access denied.");

        await Vm.DownloadAsync([docs], new FakeDownloadTarget());

        Assert.Equal("Could not download: Access denied.", Vm.Status);
    }

    [Fact]
    public async Task A_file_that_cannot_be_created_locally_fails_the_download()
    {
        await OpenAsync("alpha");

        await Vm.DownloadAsync([Item("readme.txt")], new FakeDownloadTarget { OpenError = new IOException("Disk full.") });

        Assert.Equal("Could not download: Disk full.", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("docs/old/v1.pdf", "docs/old/v1.pdf")]
    [InlineData("../../etc/passwd", "etc/passwd")]
    [InlineData("a/../../b", "a/b")]
    [InlineData("/absolute/path", "absolute/path")]
    [InlineData("a//b/./c", "a/b/c")]
    [InlineData("C:\\Windows\\system.ini", "C__Windows_system.ini")]
    [InlineData("  spaced / name ", "spaced/name")]
    [InlineData("..", "unnamed")]
    [InlineData("", "unnamed")]
    public void Object_names_become_safe_local_paths(string objectName, string expected) =>
        Assert.Equal(expected, MainViewModel.LocalPath(objectName));

    [Fact]
    public async Task An_object_named_to_escape_the_download_folder_stays_inside_it()
    {
        Gcs.Listings["beta/"] = [Folder("beta", "evil/")];
        Gcs.Listings["beta/evil/"] = [File("beta", "evil/../../outside.txt", 4)];
        Gcs.Contents["beta/evil/../../outside.txt"] = Encoding.UTF8.GetBytes("data");
        await OpenAsync("beta");
        var target = new FakeDownloadTarget();

        await Vm.DownloadAsync([Item("evil")], target);

        Assert.Equal(["evil/outside.txt"], target.Paths);
    }

    // Uploading folders

    [Fact]
    public async Task Uploading_a_folder_keeps_its_structure_under_the_current_folder()
    {
        await OpenAsync("alpha", "docs/");

        await Vm.UploadAsync([Source("photos/a.jpg", "aaa"), Source("photos/2026/b.jpg", "bb"), Source("top.txt", "t")]);

        Assert.Equal(
            ["alpha/docs/photos/2026/b.jpg", "alpha/docs/photos/a.jpg", "alpha/docs/top.txt"],
            Gcs.Uploads.Keys.Order());
        Assert.False(Vm.Dialogs.IsConfirmVisible);
        Assert.Equal("Uploaded 3 files", Vm.Status);
    }

    [Fact]
    public async Task Uploading_into_an_existing_folder_asks_before_merging()
    {
        await OpenAsync("alpha");

        var upload = Vm.UploadAsync([Source("docs/new.txt", "x"), Source("docs/more.txt", "y")]);

        Assert.True(Vm.Dialogs.IsConfirmVisible);
        Assert.Equal("Replace \"docs\"?", Vm.Dialogs.ConfirmTitle);
        Assert.Contains("folders are merged", Vm.Dialogs.ConfirmMessage);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await upload;

        Assert.Equal(2, Gcs.Uploads.Count);
    }

    [Fact]
    public async Task A_file_and_a_folder_may_share_a_name()
    {
        await OpenAsync("alpha");

        await Vm.UploadAsync([Source("docs", "a file called docs"), Source("readme.txt/inner.txt", "in a folder called readme.txt")]);

        Assert.False(Vm.Dialogs.IsConfirmVisible);
        Assert.Equal(2, Gcs.Uploads.Count);
    }

    [Fact]
    public async Task Upload_progress_goes_by_bytes_when_sizes_are_known()
    {
        await OpenAsync("beta");
        var before = new List<double?>();
        Gcs.BeforeEach = _ => before.Add(Vm.Progress);

        await Vm.UploadAsync([Source("a.bin", new string('a', 25)), Source("b.bin", new string('b', 75))]);

        Assert.Equal([null, 0.25], before);
    }

    [Fact]
    public async Task Upload_progress_goes_by_files_when_a_size_is_unknown()
    {
        await OpenAsync("beta");
        var before = new List<double?>();
        Gcs.BeforeEach = _ => before.Add(Vm.Progress);

        await Vm.UploadAsync([Source("a.bin", "aaaaaaaa", sized: false), Source("b.bin", "b"), Source("c.bin", "c"), Source("d.bin", "d")]);

        Assert.Equal([null, 0.25, 0.5, 0.75], before);
    }

    [Fact]
    public async Task Cancelling_an_upload_keeps_what_was_already_sent()
    {
        await OpenAsync("beta");
        Gcs.BeforeEach = key =>
        {
            if (key == "beta/b.bin")
            {
                Vm.CancelCommand.Execute(null);
            }
        };

        await Vm.UploadAsync([Source("a.bin", "a"), Source("b.bin", "b"), Source("c.bin", "c")]);

        Assert.Equal(["beta/a.bin"], Gcs.Uploads.Keys);
        Assert.Equal("Cancelled", Vm.Status);
    }

    // Folder sizes

    [Fact]
    public async Task Measuring_a_folder_counts_everything_inside_and_shows_the_size_in_the_list()
    {
        await OpenAsync("alpha");

        await Vm.MeasureAsync(Item("docs"));

        Assert.Equal("docs: 2 objects, 2 KB", Vm.Status);
        Assert.Equal(2058, Item("docs").Size);
        Assert.Equal("2 KB", Item("docs").SizeDisplay);
        Assert.Equal(["docs", "readme.txt", "notes.md"], ItemNames);
        Assert.False(Vm.IsBusy);
    }

    [Fact]
    public async Task Measuring_a_bucket_covers_the_whole_bucket()
    {
        await ConnectAsync();

        await Vm.MeasureAsync(Item("alpha"));

        Assert.Equal("alpha: 4 objects, 3 KB", Vm.Status);
        Assert.Equal(3082, Item("alpha").Size);
    }

    [Fact]
    public async Task Measuring_a_folder_with_one_object_says_so()
    {
        await OpenAsync("alpha", "docs/");

        await Vm.MeasureAsync(Item("old"));

        Assert.Equal("old: 1 object, 10 B", Vm.Status);
    }

    [Fact]
    public async Task Files_are_not_measured()
    {
        await OpenAsync("alpha");

        await Vm.MeasureAsync(Item("readme.txt"));

        Assert.Equal("3 items  ·  1 KB", Vm.Status);
    }

    [Fact]
    public async Task A_failed_measure_is_reported()
    {
        await OpenAsync("alpha");
        var docs = Item("docs");
        Gcs.ListError = new InvalidOperationException("Access denied.");

        await Vm.MeasureAsync(docs);

        Assert.Equal("Could not calculate the size: Access denied.", Vm.Status);
        Assert.False(Vm.IsBusy);
    }
}
