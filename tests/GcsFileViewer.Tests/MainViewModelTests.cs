using System.Globalization;
using System.Text;
using GcsFileViewer.Pricing;
using GcsFileViewer.Storage;
using GcsFileViewer.ViewModels;
using static GcsFileViewer.Tests.FakeGcsService;

namespace GcsFileViewer.Tests;

public class MainViewModelTests : ViewModelTestBase
{
    // Project prompt

    [Fact]
    public async Task Initialize_prefills_the_default_project_and_lists_projects()
    {
        Gcs.DefaultProject = "proj";

        await Vm.Connect.InitializeAsync();

        Assert.Equal("proj", Vm.Connect.ProjectInput);
        Assert.Equal(["proj", "other"], Vm.Connect.Projects);
        Assert.Equal("Using fake credentials", Vm.Connect.CredentialSource);
        Assert.Null(Vm.Connect.Error);
        Assert.True(Vm.Connect.IsVisible);
    }

    [Fact]
    public async Task Initialize_reports_missing_credentials_up_front()
    {
        Gcs.CredentialDescription = null;
        Gcs.ProjectsError = new GcsException("No Google credentials found.", GcsError.NoCredentials);

        await Vm.Connect.InitializeAsync();

        Assert.Equal("No Google credentials found.", Vm.Connect.Error);
        Assert.Equal("No Google credentials found", Vm.Connect.CredentialSource);
    }

    [Fact]
    public async Task Initialize_ignores_a_failure_to_list_projects()
    {
        Gcs.ProjectsError = new HttpRequestException("403");

        await Vm.Connect.InitializeAsync();

        Assert.Null(Vm.Connect.Error);
        Assert.Empty(Vm.Connect.Projects);
    }

    [Fact]
    public async Task UseKeyFile_switches_credentials_and_reloads_projects()
    {
        Vm.Connect.Error = "old error";

        await Vm.Connect.UseKeyFileAsync("/keys/sa.json");

        Assert.Equal("/keys/sa.json", Gcs.KeyFile);
        Assert.Null(Vm.Connect.Error);
        Assert.Equal(["proj", "other"], Vm.Connect.Projects);
    }

    [Fact]
    public async Task Connect_requires_a_project_id()
    {
        await ConnectAsync("   ");

        Assert.Equal("Enter a project ID.", Vm.Connect.Error);
        Assert.True(Vm.Connect.IsVisible);
        Assert.Equal(0, Gcs.BucketListCalls);
    }

    [Fact]
    public async Task Connect_shows_the_projects_buckets()
    {
        await ConnectAsync("  proj ");

        Assert.Equal("proj", Vm.ProjectId);
        Assert.False(Vm.Connect.IsVisible);
        Assert.Equal(["alpha", "beta"], Vm.Buckets.Select(b => b.Name));
        Assert.Equal(["alpha", "beta"], ItemNames);
        Assert.Equal(["proj"], CrumbLabels);
        Assert.Equal("2 items", Vm.Status);
        Assert.False(Vm.IsBusy);
        Assert.False(Vm.UpCommand.CanExecute(null));
    }

    [Fact]
    public async Task Connect_failure_keeps_the_prompt_open_with_the_error()
    {
        Gcs.BucketsError = new InvalidOperationException("Permission denied.");

        await ConnectAsync();

        Assert.Equal("Permission denied.", Vm.Connect.Error);
        Assert.True(Vm.Connect.IsVisible);
        Assert.Null(Vm.ProjectId);
        Assert.False(Vm.Connect.IsConnecting);
    }

    [Fact]
    public async Task The_prompt_can_only_be_cancelled_once_a_project_is_open()
    {
        Assert.False(Vm.Connect.CancelCommand.CanExecute(null));

        await ConnectAsync();
        Vm.Connect.ShowCommand.Execute(null);

        Assert.True(Vm.Connect.IsVisible);
        Assert.Equal("proj", Vm.Connect.ProjectInput);
        Assert.True(Vm.Connect.CancelCommand.CanExecute(null));

        Vm.Connect.CancelCommand.Execute(null);

        Assert.False(Vm.Connect.IsVisible);
    }

    [Fact]
    public async Task Connecting_to_another_project_resets_history()
    {
        Gcs.Buckets["other"] = [Bucket("gamma")];
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        await ConnectAsync("other");

        Assert.Equal(["gamma"], ItemNames);
        Assert.Equal(["other"], CrumbLabels);
        Assert.False(Vm.BackCommand.CanExecute(null));
    }

    // Opening a bucket by name

    [Theory]
    [InlineData("my-bucket", "my-bucket", "")]
    [InlineData("  my-bucket  ", "my-bucket", "")]
    [InlineData("gs://my-bucket", "my-bucket", "")]
    [InlineData("GS://my-bucket/", "my-bucket", "")]
    [InlineData("gs://my-bucket/logs/2026", "my-bucket", "logs/2026/")]
    [InlineData("my-bucket/logs/2026/", "my-bucket", "logs/2026/")]
    public void ParseBucketInput_accepts_names_paths_and_gs_uris(string input, string bucket, string prefix) =>
        Assert.Equal(new Location(bucket, prefix), ConnectViewModel.ParseBucketInput(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gs://")]
    [InlineData("/")]
    public void ParseBucketInput_returns_null_without_a_bucket(string input) =>
        Assert.Null(ConnectViewModel.ParseBucketInput(input));

    [Fact]
    public async Task Open_bucket_requires_a_name()
    {
        Vm.Connect.BucketInput = " ";

        await Vm.Connect.OpenBucketCommand.ExecuteAsync(null);

        Assert.Equal("Enter a bucket name.", Vm.Connect.Error);
        Assert.True(Vm.Connect.IsVisible);
    }

    [Fact]
    public async Task Open_bucket_works_without_a_project()
    {
        Gcs.Listings["public-data/"] = [File("public-data", "index.html", 10)];
        Vm.Connect.BucketInput = "public-data";

        await Vm.Connect.OpenBucketCommand.ExecuteAsync(null);

        Assert.Null(Vm.ProjectId);
        Assert.True(Vm.Connect.IsConnected);
        Assert.False(Vm.Connect.IsVisible);
        Assert.Equal(["public-data"], Vm.Buckets.Select(b => b.Name));
        Assert.Equal("public-data", Vm.SelectedBucket?.Name);
        Assert.Equal(["index.html"], ItemNames);
        Assert.Equal(["Buckets", "public-data"], CrumbLabels);
        Assert.Equal("", Vm.Connect.BucketInput);
        Assert.True(Vm.Connect.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task Open_bucket_with_a_path_goes_straight_to_that_folder()
    {
        Vm.Connect.BucketInput = "gs://alpha/docs/old";

        await Vm.Connect.OpenBucketCommand.ExecuteAsync(null);

        Assert.Equal(["v1.pdf"], ItemNames);
        Assert.Equal(["Buckets", "alpha", "docs", "old"], CrumbLabels);
    }

    [Fact]
    public async Task Open_bucket_failure_keeps_the_prompt_open_with_the_error()
    {
        Gcs.OpenBucketError = new InvalidOperationException("Can't open bucket \"nope\": not found");
        Vm.Connect.BucketInput = "nope";

        await Vm.Connect.OpenBucketCommand.ExecuteAsync(null);

        Assert.Equal("Can't open bucket \"nope\": not found", Vm.Connect.Error);
        Assert.True(Vm.Connect.IsVisible);
        Assert.False(Vm.Connect.IsConnected);
        Assert.Empty(Vm.Buckets);
    }

    [Fact]
    public async Task A_named_bucket_is_listed_after_the_projects_buckets_and_survives_refresh()
    {
        await ConnectAsync();
        Vm.Connect.BucketInput = "public-data";
        await Vm.Connect.OpenBucketCommand.ExecuteAsync(null);

        await Vm.NavigateAsync(Location.Root);
        await Vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["alpha", "beta", "public-data"], ItemNames);
        Assert.Equal(["proj"], CrumbLabels);
    }

    [Fact]
    public async Task Opening_a_project_bucket_by_name_does_not_list_it_twice()
    {
        await ConnectAsync();
        Vm.Connect.BucketInput = "alpha";

        await Vm.Connect.OpenBucketCommand.ExecuteAsync(null);

        Assert.Equal(["alpha", "beta"], Vm.Buckets.Select(b => b.Name));
        Assert.Equal(["proj", "alpha"], CrumbLabels);
    }

    // Navigation

    [Fact]
    public async Task Opening_a_bucket_lists_its_top_level()
    {
        await ConnectAsync();

        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        Assert.Equal(["docs", "readme.txt", "notes.md"], ItemNames);
        Assert.Equal(["proj", "alpha"], CrumbLabels);
        Assert.Equal("alpha", Vm.SelectedBucket?.Name);
        Assert.Equal("3 items  ·  1 KB", Vm.Status);
        Assert.True(Vm.UpCommand.CanExecute(null));
        Assert.True(Vm.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task Opening_nested_folders_builds_the_breadcrumb_trail()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        await Vm.OpenCommand.ExecuteAsync(Item("docs"));

        await Vm.OpenCommand.ExecuteAsync(Item("old"));

        Assert.Equal(["v1.pdf"], ItemNames);
        Assert.Equal(["proj", "alpha", "docs", "old"], CrumbLabels);
        Assert.Equal(new Location("alpha", "docs/"), Vm.Crumbs[2].Location);
    }

    [Fact]
    public async Task Opening_a_file_does_not_navigate()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        await Vm.OpenCommand.ExecuteAsync(Item("readme.txt"));

        Assert.Equal(["proj", "alpha"], CrumbLabels);
    }

    [Fact]
    public async Task Up_walks_from_a_nested_folder_back_to_the_bucket_list()
    {
        await ConnectAsync();
        await Vm.NavigateAsync(new Location("alpha", "docs/old/"));

        await Vm.UpCommand.ExecuteAsync(null);
        Assert.Equal(["proj", "alpha", "docs"], CrumbLabels);

        await Vm.UpCommand.ExecuteAsync(null);
        Assert.Equal(["proj", "alpha"], CrumbLabels);

        await Vm.UpCommand.ExecuteAsync(null);
        Assert.Equal(["proj"], CrumbLabels);
        Assert.Equal(["alpha", "beta"], ItemNames);
        Assert.Null(Vm.SelectedBucket);
        Assert.False(Vm.UpCommand.CanExecute(null));
    }

    [Fact]
    public async Task Back_and_forward_retrace_history()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        await Vm.OpenCommand.ExecuteAsync(Item("docs"));
        Assert.False(Vm.ForwardCommand.CanExecute(null));

        await Vm.BackCommand.ExecuteAsync(null);
        Assert.Equal(["proj", "alpha"], CrumbLabels);
        Assert.True(Vm.ForwardCommand.CanExecute(null));

        await Vm.BackCommand.ExecuteAsync(null);
        Assert.Equal(["proj"], CrumbLabels);
        Assert.False(Vm.BackCommand.CanExecute(null));

        await Vm.ForwardCommand.ExecuteAsync(null);
        await Vm.ForwardCommand.ExecuteAsync(null);
        Assert.Equal(["proj", "alpha", "docs"], CrumbLabels);
        Assert.False(Vm.ForwardCommand.CanExecute(null));
    }

    [Fact]
    public async Task Navigating_somewhere_new_discards_forward_history()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        await Vm.BackCommand.ExecuteAsync(null);

        await Vm.OpenCommand.ExecuteAsync(Item("beta"));

        Assert.False(Vm.ForwardCommand.CanExecute(null));
        Assert.Equal(["data.csv"], ItemNames);
    }

    [Fact]
    public async Task Navigating_to_the_current_location_adds_no_history()
    {
        await ConnectAsync();

        await Vm.NavigateAsync(Location.Root);

        Assert.False(Vm.BackCommand.CanExecute(null));
    }

    [Fact]
    public async Task Selecting_a_bucket_in_the_sidebar_opens_it()
    {
        await ConnectAsync();

        Vm.SelectedBucket = Vm.Buckets[1];

        Assert.Equal(["proj", "beta"], CrumbLabels);
        Assert.Equal(["data.csv"], ItemNames);
    }

    [Fact]
    public async Task Refresh_at_the_root_fetches_the_bucket_list_again()
    {
        await ConnectAsync();
        Gcs.Buckets["proj"].Add(Bucket("gamma"));

        await Vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["alpha", "beta", "gamma"], ItemNames);
        Assert.Equal(["alpha", "beta", "gamma"], Vm.Buckets.Select(b => b.Name));
    }

    [Fact]
    public async Task Refresh_in_a_folder_picks_up_new_objects()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("beta"));
        Gcs.Listings["beta/"].Add(File("beta", "more.csv"));

        await Vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["data.csv", "more.csv"], ItemNames);
        Assert.Equal(["proj", "beta"], CrumbLabels);
    }

    [Fact]
    public async Task A_listing_error_is_shown_in_the_status_bar()
    {
        await ConnectAsync();
        Gcs.ListError = new InvalidOperationException("Access denied.");

        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        Assert.Equal("Could not load this folder: Access denied.", Vm.Status);
        Assert.Empty(Vm.Items);
        Assert.False(Vm.IsBusy);
    }

    [Fact]
    public async Task An_empty_folder_is_flagged_as_empty()
    {
        Gcs.Listings["beta/"] = [];
        await ConnectAsync();

        await Vm.OpenCommand.ExecuteAsync(Item("beta"));

        Assert.True(Vm.IsEmpty);
        Assert.Equal("0 items", Vm.Status);
    }

    [Fact]
    public async Task A_slow_listing_does_not_overwrite_a_newer_one()
    {
        var slow = new TaskCompletionSource<List<BrowserItem>>();
        Gcs.PendingListings["alpha/"] = slow;
        await ConnectAsync();

        var stale = Vm.NavigateAsync(new Location("alpha", ""));
        Assert.True(Vm.IsBusy);
        await Vm.NavigateAsync(new Location("beta", ""));
        slow.SetResult([File("alpha", "late.txt")]);
        await stale;

        Assert.Equal(["data.csv"], ItemNames);
        Assert.Equal(["proj", "beta"], CrumbLabels);
        Assert.False(Vm.IsBusy);
    }

    // Filter and selection

    [Fact]
    public async Task Filter_narrows_the_listing_ignoring_case()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        Vm.Filter = "README";

        Assert.Equal(["readme.txt"], ItemNames);
        Assert.Equal("1 item (of 3)  ·  100 B", Vm.Status);

        Vm.Filter = "";

        Assert.Equal(3, Vm.Items.Count);
    }

    [Fact]
    public async Task Filter_with_no_matches_flags_the_view_as_empty()
    {
        await ConnectAsync();

        Vm.Filter = "zzz";

        Assert.Empty(Vm.Items);
        Assert.True(Vm.IsEmpty);
    }

    [Fact]
    public async Task Filter_is_cleared_when_navigating()
    {
        await ConnectAsync();
        Vm.Filter = "alp";

        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        Assert.Equal("", Vm.Filter);
        Assert.Equal(3, Vm.Items.Count);
    }

    [Fact]
    public async Task Selection_is_summarised_in_the_status_bar()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        Vm.UpdateSelection([Item("readme.txt")]);
        Assert.Equal("1 item selected  ·  100 B", Vm.Status);

        Vm.UpdateSelection([Item("readme.txt"), Item("notes.md")]);
        Assert.Equal("2 items selected  ·  1 KB", Vm.Status);

        Vm.UpdateSelection([Item("docs")]);
        Assert.Equal("1 item selected", Vm.Status);

        Vm.UpdateSelection([]);
        Assert.Equal("3 items  ·  1 KB", Vm.Status);
    }

    // Uploads

    private static UploadSource Source(string name, string content) =>
        new(name, () => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content))));

    [Fact]
    public async Task Upload_is_only_possible_inside_a_bucket()
    {
        await ConnectAsync();
        Assert.False(Vm.IsInFolder);

        await Vm.UploadAsync([Source("new.txt", "x")]);

        Assert.Empty(Gcs.Uploads);

        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        Assert.True(Vm.IsInFolder);
    }

    [Fact]
    public async Task Upload_puts_files_in_the_current_folder_and_refreshes()
    {
        await ConnectAsync();
        await Vm.NavigateAsync(new Location("alpha", "docs/"));
        Gcs.Listings["alpha/docs/"].Add(File("alpha", "docs/new.txt", 3));

        await Vm.UploadAsync([Source("new.txt", "abc"), Source("more.txt", "defg")]);

        Assert.Equal("abc", Encoding.UTF8.GetString(Gcs.Uploads["alpha/docs/new.txt"]));
        Assert.Equal("defg", Encoding.UTF8.GetString(Gcs.Uploads["alpha/docs/more.txt"]));
        Assert.Contains("new.txt", ItemNames);
        Assert.Equal("Uploaded 2 files", Vm.Status);
        Assert.False(Vm.Dialogs.IsConfirmVisible);
        Assert.False(Vm.IsBusy);
    }

    [Fact]
    public async Task Upload_asks_before_replacing_an_existing_file()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        var upload = Vm.UploadAsync([Source("readme.txt", "new")]);

        Assert.True(Vm.Dialogs.IsConfirmVisible);
        Assert.Equal("Replace \"readme.txt\"?", Vm.Dialogs.ConfirmTitle);
        Assert.Equal("Replace", Vm.Dialogs.ConfirmAction);
        Assert.Empty(Gcs.Uploads);

        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await upload;

        Assert.False(Vm.Dialogs.IsConfirmVisible);
        Assert.Equal("new", Encoding.UTF8.GetString(Gcs.Uploads["alpha/readme.txt"]));
        Assert.Equal("Uploaded readme.txt", Vm.Status);
    }

    [Fact]
    public async Task Declining_to_replace_uploads_nothing()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        var upload = Vm.UploadAsync([Source("readme.txt", "new"), Source("notes.md", "new"), Source("fresh.txt", "new")]);
        Assert.Equal("Replace 2 files?", Vm.Dialogs.ConfirmTitle);
        Vm.Dialogs.RejectConfirmCommand.Execute(null);
        await upload;

        Assert.Empty(Gcs.Uploads);
        Assert.False(Vm.Dialogs.IsConfirmVisible);
    }

    [Fact]
    public async Task Upload_failure_is_reported()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("beta"));
        Gcs.UploadError = new InvalidOperationException("Permission denied.");

        await Vm.UploadAsync([Source("new.txt", "x")]);

        Assert.Equal("Could not upload: Permission denied.", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    // New folder

    [Fact]
    public async Task New_folder_is_only_possible_inside_a_bucket()
    {
        await ConnectAsync();

        await Vm.NewFolderCommand.ExecuteAsync(null);

        Assert.False(Vm.Dialogs.IsPromptVisible);
        Assert.Empty(Gcs.CreatedFolders);
    }

    [Fact]
    public async Task New_folder_asks_for_a_name_and_creates_it_in_the_current_folder()
    {
        await ConnectAsync();
        await Vm.NavigateAsync(new Location("alpha", "docs/"));

        var create = Vm.NewFolderCommand.ExecuteAsync(null);
        Assert.True(Vm.Dialogs.IsPromptVisible);
        Assert.Equal("New folder", Vm.Dialogs.PromptTitle);
        Assert.Equal("", Vm.Dialogs.PromptText);
        Gcs.Listings["alpha/docs/"].Add(Folder("alpha", "docs/drafts/"));
        Vm.Dialogs.PromptText = "  drafts ";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        await create;

        Assert.False(Vm.Dialogs.IsPromptVisible);
        Assert.Equal(["alpha/docs/drafts/"], Gcs.CreatedFolders);
        Assert.Contains("drafts", ItemNames);
        Assert.Equal("Created folder drafts", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    [Theory]
    [InlineData("", "Enter a name.")]
    [InlineData("   ", "Enter a name.")]
    [InlineData("a/b", "A name can't contain a slash.")]
    [InlineData("..", "That name isn't allowed.")]
    [InlineData("docs", "\"docs\" already exists here.")]
    [InlineData("readme.txt", "\"readme.txt\" already exists here.")]
    public async Task An_invalid_name_keeps_the_prompt_open_with_an_error(string name, string error)
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        var create = Vm.NewFolderCommand.ExecuteAsync(null);
        Vm.Dialogs.PromptText = name;
        Vm.Dialogs.AcceptPromptCommand.Execute(null);

        Assert.True(Vm.Dialogs.IsPromptVisible);
        Assert.Equal(error, Vm.Dialogs.PromptError);
        Assert.False(create.IsCompleted);

        Vm.Dialogs.PromptText = "fine";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        await create;

        Assert.Null(Vm.Dialogs.PromptError);
        Assert.Equal(["alpha/fine/"], Gcs.CreatedFolders);
    }

    [Fact]
    public async Task Cancelling_the_prompt_creates_nothing()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        var create = Vm.NewFolderCommand.ExecuteAsync(null);
        Vm.Dialogs.PromptText = "drafts";
        Vm.Dialogs.RejectPromptCommand.Execute(null);
        await create;

        Assert.False(Vm.Dialogs.IsPromptVisible);
        Assert.Empty(Gcs.CreatedFolders);
    }

    [Fact]
    public async Task New_folder_failure_is_reported()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Gcs.CreateFolderError = new InvalidOperationException("Permission denied.");

        var create = Vm.NewFolderCommand.ExecuteAsync(null);
        Vm.Dialogs.PromptText = "drafts";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        await create;

        Assert.Equal("Could not create the folder: Permission denied.", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    // Rename

    [Fact]
    public async Task Rename_starts_from_the_current_name_and_renames_a_file_in_place()
    {
        await ConnectAsync();
        await Vm.NavigateAsync(new Location("alpha", "docs/"));

        var rename = Vm.RenameAsync(Item("guide.pdf"));
        Assert.True(Vm.Dialogs.IsPromptVisible);
        Assert.Equal("Rename \"guide.pdf\"", Vm.Dialogs.PromptTitle);
        Assert.Equal("guide.pdf", Vm.Dialogs.PromptText);
        Assert.Equal("Rename", Vm.Dialogs.PromptAction);
        Vm.Dialogs.PromptText = "manual.pdf";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        await rename;

        Assert.Equal(["move alpha/docs/guide.pdf -> alpha/docs/manual.pdf"], Gcs.Transfers);
        Assert.Equal("Renamed guide.pdf to manual.pdf", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    [Fact]
    public async Task Rename_of_a_folder_moves_its_whole_prefix()
    {
        await ConnectAsync();
        await Vm.NavigateAsync(new Location("alpha", "docs/"));

        var rename = Vm.RenameAsync(Item("old"));
        Vm.Dialogs.PromptText = "archive";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        await rename;

        Assert.Equal(["move alpha/docs/old/ -> alpha/docs/archive/"], Gcs.Transfers);
        Assert.Equal("Renamed old to archive (3 objects)", Vm.Status);
    }

    [Fact]
    public async Task Rename_at_the_top_of_a_bucket_keeps_the_object_at_the_top()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        var rename = Vm.RenameAsync(Item("readme.txt"));
        Vm.Dialogs.PromptText = "README.md";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        await rename;

        Assert.Equal(["move alpha/readme.txt -> alpha/README.md"], Gcs.Transfers);
    }

    [Fact]
    public async Task Rename_to_the_same_name_does_nothing()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        var rename = Vm.RenameAsync(Item("readme.txt"));
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        await rename;

        Assert.False(Vm.Dialogs.IsPromptVisible);
        Assert.Empty(Gcs.Transfers);
    }

    [Fact]
    public async Task Rename_refuses_a_name_that_is_already_taken()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        var rename = Vm.RenameAsync(Item("readme.txt"));
        Vm.Dialogs.PromptText = "notes.md";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);

        Assert.Equal("\"notes.md\" already exists here.", Vm.Dialogs.PromptError);
        Assert.Empty(Gcs.Transfers);

        Vm.Dialogs.RejectPromptCommand.Execute(null);
        await rename;
    }

    [Fact]
    public async Task Buckets_cannot_be_renamed()
    {
        await ConnectAsync();

        await Vm.RenameAsync(Item("alpha"));

        Assert.False(Vm.Dialogs.IsPromptVisible);
        Assert.Empty(Gcs.Transfers);
    }

    [Fact]
    public async Task Rename_failure_is_reported()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Gcs.TransferError = new InvalidOperationException("Permission denied.");

        var rename = Vm.RenameAsync(Item("readme.txt"));
        Vm.Dialogs.PromptText = "other.txt";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        await rename;

        Assert.Equal("Could not rename: Permission denied.", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    // Copy and move

    [Fact]
    public async Task Paste_is_unavailable_until_something_is_copied_and_a_bucket_is_open()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Assert.False(Vm.PasteCommand.CanExecute(null));

        Vm.CopyToClipboard([Item("readme.txt")]);
        Assert.True(Vm.PasteCommand.CanExecute(null));
        Assert.Equal("\"readme.txt\" ready to copy. Open the destination folder and paste.", Vm.Status);

        await Vm.NavigateAsync(Location.Root);
        Assert.False(Vm.PasteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Buckets_cannot_be_copied()
    {
        await ConnectAsync();

        Vm.CopyToClipboard([Item("alpha")]);
        await Vm.OpenCommand.ExecuteAsync(Item("beta"));

        Assert.False(Vm.PasteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Copy_and_paste_copies_files_and_folders_into_another_folder()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Vm.CopyToClipboard([Item("docs"), Item("readme.txt")]);
        await Vm.NavigateAsync(new Location("alpha", "docs/old/"));

        await Vm.PasteCommand.ExecuteAsync(null);

        Assert.Empty(Gcs.Transfers);
        Assert.Equal("Can't copy a folder into itself.", Vm.Status);

        await Vm.NavigateAsync(new Location("beta", ""));
        await Vm.PasteCommand.ExecuteAsync(null);

        Assert.Equal(["copy alpha/docs/ -> beta/docs/", "copy alpha/readme.txt -> beta/readme.txt"], Gcs.Transfers);
        Assert.Equal("Copied 4 objects", Vm.Status);
        Assert.False(Vm.IsBusy);
        Assert.True(Vm.PasteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cut_and_paste_moves_and_then_empties_the_clipboard()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Vm.CutToClipboard([Item("readme.txt"), Item("notes.md")]);
        Assert.Equal("2 items ready to move. Open the destination folder and paste.", Vm.Status);
        await Vm.NavigateAsync(new Location("alpha", "docs/"));

        await Vm.PasteCommand.ExecuteAsync(null);

        Assert.Equal(
            ["move alpha/readme.txt -> alpha/docs/readme.txt", "move alpha/notes.md -> alpha/docs/notes.md"],
            Gcs.Transfers);
        Assert.Equal("Moved 2 objects", Vm.Status);
        Assert.False(Vm.PasteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Pasting_a_copy_beside_the_original_gives_it_a_new_name()
    {
        Gcs.Listings["alpha/"].Add(File("alpha", "readme (copy).txt"));
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Vm.CopyToClipboard([Item("readme.txt"), Item("docs")]);

        await Vm.PasteCommand.ExecuteAsync(null);

        Assert.False(Vm.Dialogs.IsConfirmVisible);
        Assert.Equal(
            ["copy alpha/readme.txt -> alpha/readme (copy 2).txt", "copy alpha/docs/ -> alpha/docs (copy)/"],
            Gcs.Transfers);
    }

    [Fact]
    public async Task Moving_items_to_where_they_already_are_does_nothing()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Vm.CutToClipboard([Item("readme.txt")]);

        await Vm.PasteCommand.ExecuteAsync(null);

        Assert.Empty(Gcs.Transfers);
        Assert.Equal("Those items are already in this folder.", Vm.Status);
        Assert.True(Vm.PasteCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_folder_cannot_be_moved_into_itself()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Vm.CutToClipboard([Item("docs")]);
        await Vm.NavigateAsync(new Location("alpha", "docs/"));

        await Vm.PasteCommand.ExecuteAsync(null);

        Assert.Empty(Gcs.Transfers);
        Assert.Equal("Can't move a folder into itself.", Vm.Status);
    }

    [Fact]
    public async Task Paste_asks_before_replacing_and_then_overwrites()
    {
        Gcs.Listings["beta/"].Add(File("beta", "readme.txt"));
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Vm.CopyToClipboard([Item("readme.txt"), Item("notes.md")]);
        await Vm.NavigateAsync(new Location("beta", ""));

        var paste = Vm.PasteCommand.ExecuteAsync(null);
        Assert.True(Vm.Dialogs.IsConfirmVisible);
        Assert.Equal("Replace \"readme.txt\"?", Vm.Dialogs.ConfirmTitle);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await paste;

        Assert.Equal(
            ["copy alpha/readme.txt -> beta/readme.txt (overwrite)", "copy alpha/notes.md -> beta/notes.md"],
            Gcs.Transfers);
    }

    [Fact]
    public async Task Declining_to_replace_pastes_nothing_and_keeps_the_clipboard()
    {
        Gcs.Listings["beta/"].Add(File("beta", "readme.txt"));
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Vm.CutToClipboard([Item("readme.txt")]);
        await Vm.NavigateAsync(new Location("beta", ""));

        var paste = Vm.PasteCommand.ExecuteAsync(null);
        Vm.Dialogs.RejectConfirmCommand.Execute(null);
        await paste;

        Assert.Empty(Gcs.Transfers);
        Assert.True(Vm.PasteCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failed_move_is_reported_and_keeps_the_clipboard()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Vm.CutToClipboard([Item("readme.txt")]);
        await Vm.NavigateAsync(new Location("beta", ""));
        Gcs.TransferError = new InvalidOperationException("Permission denied.");

        await Vm.PasteCommand.ExecuteAsync(null);

        Assert.Equal("Could not move: Permission denied.", Vm.Status);
        Assert.True(Vm.PasteCommand.CanExecute(null));
        Assert.False(Vm.IsBusy);
    }

    // Drag and drop between folders

    [Fact]
    public async Task Dropping_onto_a_subfolder_moves_the_items_into_it()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        var dragged = new[] { Item("readme.txt"), Item("notes.md") };
        Gcs.Listings["alpha/"].RemoveAll(i => i.Kind == ItemKind.File);

        await Vm.DropAsync(dragged, new Location("alpha", "docs/"), move: true);

        Assert.Equal(
            ["move alpha/readme.txt -> alpha/docs/readme.txt", "move alpha/notes.md -> alpha/docs/notes.md"],
            Gcs.Transfers);
        Assert.Equal(["docs"], ItemNames);
        Assert.Equal(["proj", "alpha"], CrumbLabels);
        Assert.Equal("Moved 2 objects", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    [Fact]
    public async Task Dropping_with_copy_leaves_the_originals()
    {
        await ConnectAsync();
        await Vm.NavigateAsync(new Location("alpha", "docs/"));

        await Vm.DropAsync([Item("old")], new Location("beta", ""), move: false);

        Assert.Equal(["copy alpha/docs/old/ -> beta/old/"], Gcs.Transfers);
        Assert.Equal("Copied 3 objects", Vm.Status);
    }

    [Fact]
    public async Task Dropping_checks_the_destination_for_clashing_names()
    {
        Gcs.Listings["alpha/docs/"].Add(File("alpha", "docs/readme.txt"));
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        var drop = Vm.DropAsync([Item("readme.txt")], new Location("alpha", "docs/"), move: true);
        Assert.True(Vm.Dialogs.IsConfirmVisible);
        Assert.Equal("Replace \"readme.txt\"?", Vm.Dialogs.ConfirmTitle);
        Assert.False(Vm.IsBusy);
        Vm.Dialogs.RejectConfirmCommand.Execute(null);
        await drop;

        Assert.Empty(Gcs.Transfers);
        Assert.Equal("3 items  ·  1 KB", Vm.Status);

        drop = Vm.DropAsync([Item("readme.txt")], new Location("alpha", "docs/"), move: true);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await drop;

        Assert.Equal(["move alpha/readme.txt -> alpha/docs/readme.txt (overwrite)"], Gcs.Transfers);
    }

    [Fact]
    public async Task A_folder_cannot_be_dropped_onto_itself_or_its_children()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        await Vm.DropAsync([Item("docs")], new Location("alpha", "docs/"), move: true);
        await Vm.DropAsync([Item("docs")], new Location("alpha", "docs/old/"), move: true);

        Assert.Empty(Gcs.Transfers);
        Assert.Equal("Can't move a folder into itself.", Vm.Status);
    }

    [Fact]
    public async Task Dropping_items_where_they_already_are_does_nothing()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        await Vm.DropAsync([Item("readme.txt")], new Location("alpha", ""), move: true);

        Assert.Empty(Gcs.Transfers);
        Assert.Equal("Those items are already in this folder.", Vm.Status);
    }

    [Fact]
    public async Task Buckets_cannot_be_dragged_and_the_bucket_list_is_not_a_destination()
    {
        await ConnectAsync();
        await Vm.DropAsync([Item("alpha")], new Location("beta", ""), move: true);

        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        await Vm.DropAsync([Item("readme.txt")], Location.Root, move: true);

        Assert.Empty(Gcs.Transfers);
    }

    [Fact]
    public async Task A_failed_drop_is_reported()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        Gcs.TransferError = new InvalidOperationException("Permission denied.");

        await Vm.DropAsync([Item("readme.txt")], new Location("beta", ""), move: true);

        Assert.Equal("Could not move: Permission denied.", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    // Deletes

    [Fact]
    public async Task Delete_asks_first_and_does_nothing_if_declined()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        var delete = Vm.DeleteAsync([Item("readme.txt")]);

        Assert.True(Vm.Dialogs.IsConfirmVisible);
        Assert.Equal("Delete \"readme.txt\"?", Vm.Dialogs.ConfirmTitle);
        Assert.Equal("Delete", Vm.Dialogs.ConfirmAction);

        Vm.Dialogs.RejectConfirmCommand.Execute(null);
        await delete;

        Assert.Empty(Gcs.DeletedObjects);
        Assert.Equal(3, Vm.Items.Count);
    }

    [Fact]
    public async Task Delete_removes_files_and_refreshes()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        var targets = new[] { Item("readme.txt"), Item("notes.md") };
        Gcs.Listings["alpha/"].RemoveAll(i => i.Kind == ItemKind.File);

        var delete = Vm.DeleteAsync(targets);
        Assert.Equal("Delete 2 items?", Vm.Dialogs.ConfirmTitle);
        Assert.DoesNotContain("Folders", Vm.Dialogs.ConfirmMessage);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await delete;

        Assert.Equal(["alpha/readme.txt", "alpha/notes.md"], Gcs.DeletedObjects);
        Assert.Equal(["docs"], ItemNames);
        Assert.Equal("Deleted 2 objects", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    [Fact]
    public async Task Delete_of_a_folder_warns_and_removes_everything_inside()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        var delete = Vm.DeleteAsync([Item("docs"), Item("readme.txt")]);
        Assert.Contains("everything inside them", Vm.Dialogs.ConfirmMessage);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await delete;

        Assert.Equal(["alpha/docs/"], Gcs.DeletedFolders);
        Assert.Equal(["alpha/readme.txt"], Gcs.DeletedObjects);
        Assert.Equal("Deleted 4 objects", Vm.Status);
    }

    [Fact]
    public async Task Buckets_cannot_be_deleted()
    {
        await ConnectAsync();

        await Vm.DeleteAsync([Item("alpha")]);

        Assert.False(Vm.Dialogs.IsConfirmVisible);
        Assert.Empty(Gcs.DeletedFolders);
        Assert.Empty(Gcs.DeletedObjects);
    }

    [Fact]
    public async Task Delete_failure_is_reported()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("beta"));
        Gcs.DeleteError = new InvalidOperationException("Permission denied.");

        var delete = Vm.DeleteAsync([Item("data.csv")]);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await delete;

        Assert.Equal("Could not delete: Permission denied.", Vm.Status);
        Assert.Equal(["data.csv"], ItemNames);
        Assert.False(Vm.IsBusy);
    }

    // Cost estimate

    private const long GiB = 1024L * 1024 * 1024;

    private static BrowserItem ColdFile(string bucket, string path, string storageClass) =>
        File(bucket, path, 10) with { StorageClass = storageClass };

    [Fact]
    public void The_cost_starts_at_zero() =>
        Assert.Equal("Est. cost: $0.00", Vm.Cost.Summary);

    [Fact]
    public async Task The_cost_summary_is_refreshed_after_each_load()
    {
        await ConnectAsync();
        Gcs.Usage.RecordDownload(GiB);
        Gcs.Usage.RecordClassA(StorageTier.Standard, multiRegion: false, 42);

        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        Assert.Equal("Est. cost: $0.12", Vm.Cost.Summary);
        Assert.Equal(
            [
                new CostLine("List, upload and copy requests", "42", "$0.0002"),
                new CostLine("Download and read requests", "0", "$0.00"),
                new CostLine("Delete requests", "0", "Free"),
                new CostLine("Downloaded", "1 GB", "$0.12"),
                new CostLine("Total", "", "$0.1202"),
            ],
            Vm.Cost.Lines);
        Assert.Null(Vm.Cost.Warning);
    }

    [Fact]
    public async Task Tiny_costs_are_shown_as_less_than_a_cent()
    {
        await ConnectAsync();
        Gcs.Usage.RecordClassA(StorageTier.Standard, multiRegion: false, 3);
        Gcs.Usage.RecordClassB(StorageTier.Standard, 1);

        await Vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("Est. cost: <$0.01", Vm.Cost.Summary);
        Assert.Equal("<$0.0001", Vm.Cost.Lines.Single(l => l.Label == "Download and read requests").Cost);
        Assert.Equal("<$0.0001", Vm.Cost.Lines.Single(l => l.Label == "Total").Cost);
    }

    [Fact]
    public async Task The_cost_is_refreshed_after_a_download()
    {
        Gcs.Contents["alpha/readme.txt"] = [1];
        Gcs.Usage.RecordDownload(10 * GiB);

        await Vm.DownloadAsync([File("alpha", "readme.txt")], new FakeDownloadTarget());

        Assert.Equal("Est. cost: $1.20", Vm.Cost.Summary);
    }

    [Fact]
    public async Task Retrieval_and_early_deletion_show_up_only_when_they_happen()
    {
        await ConnectAsync();
        Gcs.Usage.RecordRetrieval(StorageTier.Coldline, 5 * GiB);
        Gcs.Usage.RecordColdRemoval(3);

        await Vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains(new CostLine("Retrieved from cold storage", "5 GB", "$0.10"), Vm.Cost.Lines);
        Assert.StartsWith("3 objects in Nearline, Coldline or Archive storage were deleted, moved or replaced.", Vm.Cost.Warning);

        Gcs.Usage.Reset();
        Gcs.Usage.RecordColdRemoval();
        await Vm.RefreshCommand.ExecuteAsync(null);

        Assert.StartsWith("1 object in Nearline, Coldline or Archive storage was deleted", Vm.Cost.Warning);
    }

    [Fact]
    public async Task Reset_clears_the_tally()
    {
        await ConnectAsync();
        Gcs.Usage.RecordDownload(GiB);
        Gcs.Usage.RecordColdRemoval();
        await Vm.RefreshCommand.ExecuteAsync(null);

        Vm.Cost.ResetCommand.Execute(null);

        Assert.Equal("Est. cost: $0.00", Vm.Cost.Summary);
        Assert.Null(Vm.Cost.Warning);
        Assert.Equal(0, Gcs.Usage.Snapshot().BytesDownloaded);
    }

    [Fact]
    public void The_pricing_note_says_when_the_prices_are_from() =>
        Assert.Contains("as of 2026-10-12", Vm.Cost.PricingNote);

    // Cold storage warnings

    [Fact]
    public async Task Deleting_cold_files_warns_about_fees_in_the_confirmation()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));

        var delete = Vm.DeleteAsync([ColdFile("alpha", "a.bak", "COLDLINE"), ColdFile("alpha", "b.bak", "ARCHIVE"), Item("readme.txt")]);

        Assert.Contains("This involves Archive and Coldline storage", Vm.Dialogs.ConfirmMessage);
        Assert.Contains("early deletion fee", Vm.Dialogs.ConfirmMessage);
        Vm.Dialogs.RejectConfirmCommand.Execute(null);
        await delete;
    }

    [Fact]
    public async Task Deleting_a_folder_in_a_cold_bucket_warns_too()
    {
        Gcs.Buckets["proj"].Add(Bucket("vault") with { StorageClass = "NEARLINE" });
        await ConnectAsync();

        var delete = Vm.DeleteAsync([Folder("vault", "2020/")]);

        Assert.Contains("This involves Nearline storage", Vm.Dialogs.ConfirmMessage);
        Vm.Dialogs.RejectConfirmCommand.Execute(null);
        await delete;
    }

    [Fact]
    public async Task Renaming_a_cold_file_asks_before_going_ahead()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        var cold = ColdFile("alpha", "backup.tar", "COLDLINE");

        var rename = Vm.RenameAsync(cold);
        Vm.Dialogs.PromptText = "backup-old.tar";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        Assert.True(Vm.Dialogs.IsConfirmVisible);
        Assert.Equal("Rename in cold storage?", Vm.Dialogs.ConfirmTitle);
        Vm.Dialogs.RejectConfirmCommand.Execute(null);
        await rename;

        Assert.Empty(Gcs.Transfers);

        rename = Vm.RenameAsync(cold);
        Vm.Dialogs.PromptText = "backup-old.tar";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await rename;

        Assert.Equal(["move alpha/backup.tar -> alpha/backup-old.tar"], Gcs.Transfers);
    }

    [Fact]
    public async Task Moving_or_copying_cold_files_asks_before_going_ahead()
    {
        await ConnectAsync();
        await Vm.OpenCommand.ExecuteAsync(Item("alpha"));
        var cold = ColdFile("alpha", "backup.tar", "ARCHIVE");

        var drop = Vm.DropAsync([cold], new Location("beta", ""), move: true);
        Assert.Equal("Move from cold storage?", Vm.Dialogs.ConfirmTitle);
        Assert.Equal("Move", Vm.Dialogs.ConfirmAction);
        Vm.Dialogs.RejectConfirmCommand.Execute(null);
        await drop;
        Assert.Empty(Gcs.Transfers);

        drop = Vm.DropAsync([cold], new Location("beta", ""), move: false);
        Assert.Equal("Copy from cold storage?", Vm.Dialogs.ConfirmTitle);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await drop;

        Assert.Equal(["copy alpha/backup.tar -> beta/backup.tar"], Gcs.Transfers);
    }

    // Downloads

    [Fact]
    public async Task Download_writes_each_file_to_its_target()
    {
        Gcs.Contents["alpha/readme.txt"] = Encoding.UTF8.GetBytes("hello");
        Gcs.Contents["alpha/notes.md"] = Encoding.UTF8.GetBytes("# notes");
        var target = new FakeDownloadTarget();

        await Vm.DownloadAsync([File("alpha", "readme.txt", 5), File("alpha", "notes.md", 7)], target);

        Assert.Equal("hello", target.Text("readme.txt"));
        Assert.Equal("# notes", target.Text("notes.md"));
        Assert.Equal("Downloaded 2 files", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    [Fact]
    public async Task Download_of_one_file_names_it_in_the_status()
    {
        Gcs.Contents["alpha/readme.txt"] = [1, 2, 3];

        await Vm.DownloadAsync([File("alpha", "readme.txt")], new FakeDownloadTarget());

        Assert.Equal("Downloaded readme.txt", Vm.Status);
    }

    [Fact]
    public async Task Download_failure_is_reported_and_clears_the_busy_state()
    {
        Gcs.DownloadError = new IOException("Disk full.");

        await Vm.DownloadAsync([File("alpha", "readme.txt")], new FakeDownloadTarget());

        Assert.Equal("Could not download: Disk full.", Vm.Status);
        Assert.False(Vm.IsBusy);
    }
}
