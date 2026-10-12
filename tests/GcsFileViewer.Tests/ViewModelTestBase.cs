using System.Globalization;
using GcsFileViewer.Storage;
using GcsFileViewer.ViewModels;
using static GcsFileViewer.Tests.FakeGcsService;

namespace GcsFileViewer.Tests;

/// <summary>A view model over a small fake project: buckets "alpha" and "beta" with a few folders and files.</summary>
public abstract class ViewModelTestBase
{
    private protected readonly FakeGcsService Gcs = new();
    private protected readonly MainViewModel Vm;

    protected ViewModelTestBase()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        Gcs.Projects.AddRange(["proj", "other"]);
        Gcs.Buckets["proj"] = [Bucket("alpha"), Bucket("beta")];
        Gcs.Listings["alpha/"] = [Folder("alpha", "docs/"), File("alpha", "readme.txt", 100), File("alpha", "notes.md", 924)];
        Gcs.Listings["alpha/docs/"] = [Folder("alpha", "docs/old/"), File("alpha", "docs/guide.pdf", 2048)];
        Gcs.Listings["alpha/docs/old/"] = [File("alpha", "docs/old/v1.pdf", 10)];
        Gcs.Listings["beta/"] = [File("beta", "data.csv", 5)];

        // No waiting before the details pane fetches, so tests see its results straight away.
        Vm = new MainViewModel(Gcs) { Details = { Delay = TimeSpan.Zero } };
    }

    private protected IEnumerable<string> ItemNames => Vm.Items.Select(i => i.Name);

    private protected IEnumerable<string> CrumbLabels => Vm.Crumbs.Select(c => c.Label);

    private protected BrowserItem Item(string name) => Vm.Items.Single(i => i.Name == name);

    private protected async Task ConnectAsync(string project = "proj")
    {
        Vm.Connect.ProjectInput = project;
        await Vm.Connect.ConnectCommand.ExecuteAsync(null);
    }

    /// <summary>Connects and opens a folder, given as "bucket" or "bucket/path/".</summary>
    private protected async Task OpenAsync(string bucket, string prefix = "")
    {
        await ConnectAsync();
        await Vm.NavigateAsync(new Location(bucket, prefix));
    }
}
