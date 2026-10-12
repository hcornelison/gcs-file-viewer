using GcsFileViewer.Pricing;
using GcsFileViewer.Storage;
using GcsFileViewer.ViewModels;
using static GcsFileViewer.Tests.FakeGcsService;

namespace GcsFileViewer.Tests;

/// <summary>Long folders loaded a page at a time, and searching under a folder.</summary>
public class MainViewModelBrowsingTests : ViewModelTestBase
{
    private void MakeBigFolder(int folders = 3, int files = 7)
    {
        var items = new List<BrowserItem>();
        items.AddRange(Enumerable.Range(1, folders).Select(i => Folder("big", $"dir{i}/")));
        items.AddRange(Enumerable.Range(1, files).Select(i => File("big", $"file{i}.txt", 10)));

        // Real listings mix folders and files across pages, so put one folder at the very end.
        items.Add(Folder("big", "zz-last/"));
        Gcs.Listings["big/"] = items;
        Gcs.Buckets["proj"].Add(Bucket("big"));
        Gcs.PageSize = 4;
    }

    // Paging

    [Fact]
    public async Task A_long_folder_shows_its_first_page_and_says_there_is_more()
    {
        MakeBigFolder();

        await OpenAsync("big");

        Assert.Equal(["dir1", "dir2", "dir3", "file1.txt"], ItemNames);
        Assert.True(Vm.HasMore);
        Assert.True(Vm.LoadMoreCommand.CanExecute(null));
        Assert.Equal("4+ items  ·  10 B  ·  more to load", Vm.Status);
        Assert.Equal(1, Gcs.PageRequests);
    }

    [Fact]
    public async Task A_short_folder_has_nothing_more_to_load()
    {
        await OpenAsync("alpha");

        Assert.False(Vm.HasMore);
        Assert.False(Vm.LoadMoreCommand.CanExecute(null));
        Assert.Equal("3 items  ·  1 KB", Vm.Status);
    }

    [Fact]
    public async Task Load_more_adds_the_next_page_after_what_is_shown()
    {
        MakeBigFolder();
        await OpenAsync("big");

        await Vm.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(["dir1", "dir2", "dir3", "file1.txt", "file2.txt", "file3.txt", "file4.txt", "file5.txt"], ItemNames);
        Assert.True(Vm.HasMore);
    }

    [Fact]
    public async Task Folders_from_later_pages_join_the_folders_at_the_top()
    {
        MakeBigFolder();
        await OpenAsync("big");

        await Vm.LoadMoreCommand.ExecuteAsync(null);
        await Vm.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(
            ["dir1", "dir2", "dir3", "zz-last", "file1.txt", "file2.txt", "file3.txt", "file4.txt", "file5.txt", "file6.txt", "file7.txt"],
            ItemNames);
        Assert.False(Vm.HasMore);
        Assert.False(Vm.LoadMoreCommand.CanExecute(null));
        Assert.Equal("11 items  ·  70 B", Vm.Status);
    }

    [Fact]
    public async Task The_filter_applies_to_later_pages_as_they_arrive()
    {
        MakeBigFolder();
        await OpenAsync("big");
        Vm.Filter = "file";
        Assert.Equal("1+ items (of 4+)  ·  10 B  ·  more to load", Vm.Status);

        await Vm.LoadMoreCommand.ExecuteAsync(null);
        await Vm.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(7, Vm.Items.Count);
        Assert.All(Vm.Items, i => Assert.StartsWith("file", i.Name));
        Assert.Equal("7 items (of 11)  ·  70 B", Vm.Status);
    }

    [Fact]
    public async Task Navigating_away_forgets_the_pages_still_to_load()
    {
        MakeBigFolder();
        await OpenAsync("big");

        await Vm.NavigateAsync(new Location("alpha", ""));

        Assert.False(Vm.HasMore);
        Assert.Equal(3, Vm.Items.Count);
    }

    [Fact]
    public async Task A_failure_loading_more_is_reported_and_keeps_what_is_shown()
    {
        MakeBigFolder();
        await OpenAsync("big");
        Gcs.ListError = new InvalidOperationException("Access denied.");

        await Vm.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal("Could not load more: Access denied.", Vm.Status);
        Assert.Equal(4, Vm.Items.Count);
        Assert.True(Vm.HasMore);
    }

    [Fact]
    public async Task A_name_is_checked_against_the_whole_folder_without_loading_it_all()
    {
        MakeBigFolder();
        await OpenAsync("big");
        var requests = Gcs.PageRequests;

        var create = Vm.NewFolderCommand.ExecuteAsync(null);
        Vm.Dialogs.PromptText = "zz-last";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);

        // "zz-last" is on a page that hasn't been loaded, and still isn't: it was asked about by name.
        Assert.Equal("\"zz-last\" already exists here.", Vm.Dialogs.PromptError);
        Assert.True(Vm.HasMore);
        Assert.Equal(requests, Gcs.PageRequests);
        Assert.Equal(["big/: zz-last"], Gcs.Lookups);
        Vm.Dialogs.RejectPromptCommand.Execute(null);
        await create;
    }

    [Fact]
    public async Task A_fully_loaded_folder_is_checked_from_what_is_on_screen()
    {
        await OpenAsync("alpha");

        var create = Vm.NewFolderCommand.ExecuteAsync(null);
        Vm.Dialogs.PromptText = "docs";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);

        Assert.Equal("\"docs\" already exists here.", Vm.Dialogs.PromptError);
        Assert.Empty(Gcs.Lookups);
        Vm.Dialogs.RejectPromptCommand.Execute(null);
        await create;
    }

    [Fact]
    public async Task A_name_that_cannot_be_checked_is_not_accepted()
    {
        MakeBigFolder();
        await OpenAsync("big");
        Gcs.FindError = new InvalidOperationException("Access denied.");

        var create = Vm.NewFolderCommand.ExecuteAsync(null);
        Vm.Dialogs.PromptText = "new";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);

        Assert.Equal("Could not check that name: Access denied.", Vm.Dialogs.PromptError);
        Assert.True(Vm.Dialogs.IsPromptVisible);
        Vm.Dialogs.RejectPromptCommand.Execute(null);
        await create;
        Assert.Empty(Gcs.CreatedFolders);
    }

    [Fact]
    public async Task Uploading_a_few_files_asks_about_just_those_names()
    {
        MakeBigFolder();
        await OpenAsync("big");

        var upload = Vm.UploadAsync([new UploadSource("file7.txt", () => Task.FromResult<Stream>(new MemoryStream()))]);

        Assert.True(Vm.Dialogs.IsConfirmVisible);
        Assert.Equal("Replace \"file7.txt\"?", Vm.Dialogs.ConfirmTitle);
        Assert.True(Vm.HasMore);
        Assert.Equal(["big/: file7.txt"], Gcs.Lookups);
        Vm.Dialogs.RejectConfirmCommand.Execute(null);
        await upload;
        Assert.Empty(Gcs.Uploads);
    }

    [Fact]
    public async Task Uploading_many_files_loads_the_rest_of_the_folder_instead()
    {
        MakeBigFolder();
        await OpenAsync("big");
        var files = Enumerable.Range(1, MainViewModel.MaxNameLookups + 1)
            .Select(i => new UploadSource($"new{i}.txt", () => Task.FromResult<Stream>(new MemoryStream())))
            .ToList();

        await Vm.UploadAsync(files);

        Assert.Empty(Gcs.Lookups);
        Assert.Equal(files.Count, Gcs.Uploads.Count);
    }

    [Fact]
    public async Task A_page_wanted_twice_at_once_is_fetched_and_shown_once()
    {
        MakeBigFolder();
        await OpenAsync("big");

        // Hold the next page in flight, then ask for it again the way an upload of many files does.
        var slow = new TaskCompletionSource<List<BrowserItem>>();
        var all = Gcs.Listings["big/"];
        Gcs.PendingListings["big/"] = slow;
        var scrolled = Vm.LoadMoreCommand.ExecuteAsync(null);
        var files = Enumerable.Range(1, MainViewModel.MaxNameLookups + 1)
            .Select(i => new UploadSource($"new{i}.txt", () => Task.FromResult<Stream>(new MemoryStream())))
            .ToList();
        List<BrowserItem>? listedWhenUploadBegan = null;
        Gcs.BeforeEach = _ => listedWhenUploadBegan ??= [.. Vm.Items];
        var upload = Vm.UploadAsync(files);

        Gcs.PendingListings.Remove("big/");
        slow.SetResult(all);
        await scrolled;
        await upload;

        // Everything in the folder, each item once.
        Assert.NotNull(listedWhenUploadBegan);
        Assert.Equal(11, listedWhenUploadBegan.Count);
        Assert.Equal(11, listedWhenUploadBegan.Distinct().Count());
    }

    // Search

    [Fact]
    public async Task Search_looks_through_every_folder_under_the_current_one()
    {
        await OpenAsync("alpha");
        Vm.Filter = " pdf ";

        await Vm.SearchCommand.ExecuteAsync(null);

        Assert.True(Vm.IsSearch);
        Assert.Equal(["guide.pdf", "v1.pdf"], ItemNames);
        Assert.Equal(["docs/", "docs/old/"], Vm.Items.Select(i => i.ParentPath));
        Assert.Equal(["proj", "alpha", "Search: pdf"], CrumbLabels);
        Assert.Equal("", Vm.Filter);
        Assert.Equal("2 items  ·  2 KB", Vm.Status);
    }

    [Fact]
    public async Task Search_is_limited_to_the_folder_it_started_from()
    {
        await OpenAsync("alpha", "docs/old/");
        Vm.Filter = "pdf";

        await Vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["v1.pdf"], ItemNames);
        Assert.Equal(["proj", "alpha", "docs", "old", "Search: pdf"], CrumbLabels);
    }

    [Fact]
    public async Task Search_needs_a_term_and_a_bucket()
    {
        await ConnectAsync();
        Vm.Filter = "alpha";
        await Vm.SearchCommand.ExecuteAsync(null);
        Assert.False(Vm.IsSearch);

        await Vm.NavigateAsync(new Location("alpha", ""));
        Vm.Filter = "   ";
        await Vm.SearchCommand.ExecuteAsync(null);

        Assert.False(Vm.IsSearch);
        Assert.Equal(["proj", "alpha"], CrumbLabels);
    }

    [Fact]
    public async Task Search_results_are_not_a_folder_so_nothing_can_be_put_there()
    {
        await OpenAsync("alpha");
        Vm.CopyToClipboard([Item("readme.txt")]);
        Vm.Filter = "pdf";

        await Vm.SearchCommand.ExecuteAsync(null);

        Assert.False(Vm.IsInFolder);
        Assert.False(Vm.PasteCommand.CanExecute(null));
        await Vm.UploadAsync([new UploadSource("x.txt", () => Task.FromResult<Stream>(new MemoryStream()))]);
        await Vm.NewFolderCommand.ExecuteAsync(null);
        Assert.Empty(Gcs.Uploads);
        Assert.False(Vm.Dialogs.IsPromptVisible);
    }

    [Fact]
    public async Task Up_and_back_leave_the_search_for_the_folder_it_came_from()
    {
        await OpenAsync("alpha", "docs/");
        Vm.Filter = "pdf";
        await Vm.SearchCommand.ExecuteAsync(null);

        await Vm.UpCommand.ExecuteAsync(null);

        Assert.False(Vm.IsSearch);
        Assert.True(Vm.IsInFolder);
        Assert.Equal(["proj", "alpha", "docs"], CrumbLabels);

        await Vm.BackCommand.ExecuteAsync(null);
        Assert.True(Vm.IsSearch);
        await Vm.BackCommand.ExecuteAsync(null);
        Assert.False(Vm.IsSearch);
    }

    [Fact]
    public async Task Searching_again_from_results_searches_the_same_folder()
    {
        await OpenAsync("alpha");
        Vm.Filter = "pdf";
        await Vm.SearchCommand.ExecuteAsync(null);
        Vm.Filter = "readme";

        await Vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(["readme.txt"], ItemNames);
        Assert.Equal(["proj", "alpha", "Search: readme"], CrumbLabels);
    }

    [Fact]
    public async Task A_result_can_be_followed_to_the_folder_that_holds_it()
    {
        await OpenAsync("alpha");
        Vm.Filter = "v1";
        await Vm.SearchCommand.ExecuteAsync(null);

        await Vm.OpenContainingFolderAsync(Item("v1.pdf"));

        Assert.False(Vm.IsSearch);
        Assert.Equal(["proj", "alpha", "docs", "old"], CrumbLabels);
        Assert.Equal(["v1.pdf"], ItemNames);
    }

    [Fact]
    public async Task Long_search_results_load_a_page_at_a_time()
    {
        Gcs.Listings["beta/logs/"] = Enumerable.Range(1, 5).Select(i => File("beta", $"logs/app{i}.log", 1)).ToList();
        Gcs.PageSize = 2;
        await OpenAsync("beta");
        Vm.Filter = "app";

        await Vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, Vm.Items.Count);
        Assert.True(Vm.HasMore);

        await Vm.LoadMoreCommand.ExecuteAsync(null);
        await Vm.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(5, Vm.Items.Count);
        Assert.False(Vm.HasMore);
    }

    [Fact]
    public async Task A_search_with_no_matches_is_shown_as_empty()
    {
        await OpenAsync("alpha");
        Vm.Filter = "nothing-like-this";

        await Vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(Vm.Items);
        Assert.True(Vm.IsEmpty);
        Assert.Equal("0 items", Vm.Status);
    }

    [Fact]
    public async Task A_failed_search_is_reported()
    {
        await OpenAsync("alpha");
        Gcs.ListError = new InvalidOperationException("Access denied.");
        Vm.Filter = "pdf";

        await Vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Could not search: Access denied.", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    [Fact]
    public async Task Renaming_a_search_result_keeps_it_in_its_own_folder()
    {
        await OpenAsync("alpha");
        Vm.Filter = "v1";
        await Vm.SearchCommand.ExecuteAsync(null);

        var rename = Vm.RenameAsync(Item("v1.pdf"));
        Vm.Dialogs.PromptText = "v1-final.pdf";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        await rename;

        Assert.Equal(["move alpha/docs/old/v1.pdf -> alpha/docs/old/v1-final.pdf"], Gcs.Transfers);
    }

    [Fact]
    public async Task A_search_result_cannot_take_a_name_used_in_its_own_folder()
    {
        Gcs.Listings["alpha/docs/old/"].Add(File("alpha", "docs/old/taken.pdf"));
        await OpenAsync("alpha");
        Vm.Filter = "v1";
        await Vm.SearchCommand.ExecuteAsync(null);

        var rename = Vm.RenameAsync(Item("v1.pdf"));
        Vm.Dialogs.PromptText = "taken.pdf";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);

        Assert.Equal("\"taken.pdf\" already exists here.", Vm.Dialogs.PromptError);
        Assert.Equal(["alpha/docs/old/: taken.pdf"], Gcs.Lookups);
        Vm.Dialogs.RejectPromptCommand.Execute(null);
        await rename;
        Assert.Empty(Gcs.Transfers);
    }

    [Fact]
    public async Task A_name_clash_the_service_refuses_is_reported_as_it_describes_it()
    {
        await OpenAsync("alpha");
        Gcs.TransferError = new GcsException("An item with that name already exists.", GcsError.AlreadyExists);

        var rename = Vm.RenameAsync(Item("readme.txt"));
        Vm.Dialogs.PromptText = "raced.txt";
        Vm.Dialogs.AcceptPromptCommand.Execute(null);
        await rename;

        Assert.Equal("Could not rename: An item with that name already exists.", Vm.Status);
    }

    [Fact]
    public async Task Search_results_can_be_copied_out_to_a_folder()
    {
        await OpenAsync("alpha");
        Vm.Filter = "pdf";
        await Vm.SearchCommand.ExecuteAsync(null);
        Vm.CopyToClipboard([.. Vm.Items]);
        await Vm.NavigateAsync(new Location("beta", ""));

        await Vm.PasteCommand.ExecuteAsync(null);

        Assert.Equal(
            ["copy alpha/docs/guide.pdf -> beta/guide.pdf", "copy alpha/docs/old/v1.pdf -> beta/v1.pdf"],
            Gcs.Transfers);
    }
}
