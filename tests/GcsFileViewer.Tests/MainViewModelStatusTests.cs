using System.Text;
using GcsFileViewer.Storage;
using GcsFileViewer.ViewModels;
using static GcsFileViewer.Tests.FakeGcsService;

namespace GcsFileViewer.Tests;

/// <summary>What the status line shows when several things want to say something, and the selection commands.</summary>
public class MainViewModelStatusTests : ViewModelTestBase
{
    public MainViewModelStatusTests()
    {
        Gcs.Contents["alpha/readme.txt"] = Encoding.UTF8.GetBytes(new string('r', 100));
        Gcs.Contents["alpha/notes.md"] = Encoding.UTF8.GetBytes(new string('n', 924));
    }

    [Fact]
    public async Task Selecting_rows_during_an_operation_does_not_hide_its_progress()
    {
        await OpenAsync("alpha");
        string? duringOperation = null;
        Gcs.BeforeEach = _ =>
        {
            Vm.UpdateSelection([Item("notes.md")]);
            duringOperation ??= Vm.Status;
        };

        await Vm.DownloadAsync([Item("readme.txt")], new FakeDownloadTarget());

        Assert.Equal("Downloading readme.txt…", duringOperation);
        Assert.Equal("Downloaded readme.txt", Vm.Status);
    }

    [Fact]
    public async Task A_result_stays_until_something_is_selected()
    {
        await OpenAsync("alpha");
        await Vm.DownloadAsync([Item("readme.txt")], new FakeDownloadTarget());

        Vm.UpdateSelection([]);
        Assert.Equal("Downloaded readme.txt", Vm.Status);

        Vm.UpdateSelection([Item("notes.md")]);
        Assert.Equal("1 item selected  ·  924 B", Vm.Status);

        Vm.UpdateSelection([]);
        Assert.Equal("3 items  ·  1 KB", Vm.Status);
    }

    [Fact]
    public async Task A_result_is_cleared_by_typing_in_the_filter_or_going_elsewhere()
    {
        await OpenAsync("alpha");
        await Vm.DownloadAsync([Item("readme.txt")], new FakeDownloadTarget());

        Vm.Filter = "notes";
        Assert.Equal("1 item (of 3)  ·  924 B", Vm.Status);

        await Vm.DownloadAsync([Item("notes.md")], new FakeDownloadTarget());
        await Vm.NavigateAsync(new Location("beta", ""));
        Assert.Equal("1 item  ·  5 B", Vm.Status);
    }

    [Fact]
    public async Task The_filter_survives_the_refresh_after_an_operation()
    {
        await OpenAsync("alpha");
        Vm.Filter = "notes";

        var delete = Vm.DeleteAsync([Item("notes.md")]);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await delete;

        Assert.Equal("notes", Vm.Filter);
        Assert.Equal(["notes.md"], ItemNames);
        Assert.Equal("Deleted 1 object", Vm.Status);
    }

    [Fact]
    public async Task A_refresh_that_fails_after_an_operation_is_reported_with_the_result()
    {
        await OpenAsync("alpha");
        Gcs.BeforeEach = _ => Gcs.ListError = new InvalidOperationException("Access denied.");

        var delete = Vm.DeleteAsync([Item("readme.txt")]);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await delete;

        Assert.Equal("Deleted 1 object. Could not load this folder: Access denied.", Vm.Status);
    }

    [Fact]
    public async Task A_message_can_be_shown_from_outside()
    {
        await OpenAsync("alpha");

        Vm.Notify("Something went wrong: disk on fire");

        Assert.Equal("Something went wrong: disk on fire", Vm.Status);
    }

    [Fact]
    public async Task Loading_is_shown_until_the_listing_arrives()
    {
        var slow = new TaskCompletionSource<List<BrowserItem>>();
        Gcs.PendingListings["alpha/"] = slow;
        await ConnectAsync();

        var loading = Vm.NavigateAsync(new Location("alpha", ""));
        Assert.Equal("Loading…", Vm.Status);
        Assert.True(Vm.IsBusy);
        Assert.False(Vm.IsEmpty);

        slow.SetResult([File("alpha", "only.txt", 1)]);
        await loading;

        Assert.Equal("1 item  ·  1 B", Vm.Status);
        Assert.False(Vm.IsBusy);
    }

    // Selection commands

    [Fact]
    public async Task Commands_that_need_a_selection_are_off_without_one()
    {
        await OpenAsync("alpha");

        Assert.Empty(Vm.Selection);
        Assert.False(Vm.CopySelectedCommand.CanExecute(null));
        Assert.False(Vm.CutSelectedCommand.CanExecute(null));
        Assert.False(Vm.DeleteSelectedCommand.CanExecute(null));
        Assert.False(Vm.RenameSelectedCommand.CanExecute(null));
        Assert.False(Vm.MeasureSelectedCommand.CanExecute(null));
        Assert.False(Vm.OpenContainingFolderOfSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task One_file_can_be_copied_renamed_and_deleted_but_not_measured()
    {
        await OpenAsync("alpha");

        Vm.UpdateSelection([Item("readme.txt")]);

        Assert.Equal([Item("readme.txt")], Vm.Selection);
        Assert.True(Vm.CopySelectedCommand.CanExecute(null));
        Assert.True(Vm.DeleteSelectedCommand.CanExecute(null));
        Assert.True(Vm.RenameSelectedCommand.CanExecute(null));
        Assert.True(Vm.OpenContainingFolderOfSelectedCommand.CanExecute(null));
        Assert.False(Vm.MeasureSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Several_items_can_be_copied_and_deleted_but_not_renamed()
    {
        await OpenAsync("alpha");

        Vm.UpdateSelection([Item("readme.txt"), Item("docs")]);

        Assert.True(Vm.CutSelectedCommand.CanExecute(null));
        Assert.True(Vm.DeleteSelectedCommand.CanExecute(null));
        Assert.False(Vm.RenameSelectedCommand.CanExecute(null));
        Assert.False(Vm.MeasureSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_bucket_can_only_be_measured()
    {
        await ConnectAsync();

        Vm.UpdateSelection([Item("alpha")]);

        Assert.True(Vm.MeasureSelectedCommand.CanExecute(null));
        Assert.False(Vm.CopySelectedCommand.CanExecute(null));
        Assert.False(Vm.DeleteSelectedCommand.CanExecute(null));
        Assert.False(Vm.RenameSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_selection_commands_act_on_what_is_selected()
    {
        await OpenAsync("alpha");
        Vm.UpdateSelection([Item("readme.txt")]);

        Vm.CutSelectedCommand.Execute(null);
        await Vm.NavigateAsync(new Location("beta", ""));
        await Vm.PasteCommand.ExecuteAsync(null);
        Assert.Equal(["move alpha/readme.txt -> beta/readme.txt"], Gcs.Transfers);

        Vm.UpdateSelection([Item("data.csv")]);
        var delete = Vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Equal("Delete \"data.csv\"?", Vm.Dialogs.ConfirmTitle);
        Vm.Dialogs.AcceptConfirmCommand.Execute(null);
        await delete;
        Assert.Equal(["beta/data.csv"], Gcs.DeletedObjects);

        Vm.UpdateSelection([Item("data.csv")]);
        var rename = Vm.RenameSelectedCommand.ExecuteAsync(null);
        Assert.Equal("Rename \"data.csv\"", Vm.Dialogs.PromptTitle);
        Vm.Dialogs.RejectPromptCommand.Execute(null);
        await rename;
    }

    [Fact]
    public async Task Measuring_and_opening_the_containing_folder_work_from_the_selection()
    {
        await OpenAsync("alpha");
        Vm.UpdateSelection([Item("docs")]);

        await Vm.MeasureSelectedCommand.ExecuteAsync(null);
        Assert.Equal("docs: 2 objects, 2 KB", Vm.Status);

        Vm.Filter = "v1";
        await Vm.SearchCommand.ExecuteAsync(null);
        Vm.UpdateSelection([Item("v1.pdf")]);
        await Vm.OpenContainingFolderOfSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["proj", "alpha", "docs", "old"], CrumbLabels);
    }

    // Local names for downloads

    [Fact]
    public async Task Objects_that_would_land_on_the_same_local_file_get_distinct_names()
    {
        Gcs.Listings["beta/"] =
        [
            File("beta", "Report.txt", 1), File("beta", "report.txt", 1), File("beta", "REPORT.TXT", 1),
            File("beta", "a:b", 1), File("beta", "a_b", 1),
        ];
        foreach (var item in Gcs.Listings["beta/"])
        {
            Gcs.Contents[$"beta/{item.Path}"] = Encoding.UTF8.GetBytes(item.Path[..1]);
        }

        await OpenAsync("beta");
        var target = new FakeDownloadTarget();

        await Vm.DownloadAsync([.. Vm.Items], target);

        Assert.Equal(["REPORT (3).TXT", "Report.txt", "a_b", "a_b (2)", "report (2).txt"], target.Paths.Order(StringComparer.Ordinal));
        Assert.Equal("Downloaded 5 files", Vm.Status);
    }

    [Fact]
    public async Task Clashing_names_inside_a_downloaded_folder_are_kept_apart_too()
    {
        Gcs.Listings["beta/"] = [Folder("beta", "logs/")];
        Gcs.Listings["beta/logs/"] = [File("beta", "logs/App.log", 1), File("beta", "logs/app.log", 1)];
        Gcs.Contents["beta/logs/App.log"] = [1];
        Gcs.Contents["beta/logs/app.log"] = [2];
        await OpenAsync("beta");
        var target = new FakeDownloadTarget();

        await Vm.DownloadAsync([Item("logs")], target);

        Assert.Equal(["logs/App.log", "logs/app (2).log"], target.Paths.Order(StringComparer.Ordinal));
    }
}
