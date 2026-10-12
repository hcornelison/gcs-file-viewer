using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GcsFileViewer.Storage;

namespace GcsFileViewer.ViewModels;

/// <summary>The prompt for what to browse: a project's buckets, or a single bucket by name.</summary>
public partial class ConnectViewModel(IGcsService gcs) : ObservableObject
{
    private const string NoCredentials = "No Google credentials found";

    private string? _openProject;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private string _projectInput = "";

    [ObservableProperty]
    private string _bucketInput = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _credentialSource = "Looking for Google credentials…";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenBucketCommand))]
    private bool _isConnecting;

    /// <summary>True once a project or a bucket has been opened, so the prompt can be cancelled back to it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isConnected;

    public ObservableCollection<string> Projects { get; } = [];

    /// <summary>Called with a project's ID and buckets once they have been listed. The prompt closes when it completes.</summary>
    public Func<string, List<BrowserItem>, Task>? ProjectOpened { get; set; }

    /// <summary>Called with a bucket that was opened by name, and where in it to go. The prompt closes when it completes.</summary>
    public Func<BrowserItem, Location, Task>? BucketOpened { get; set; }

    /// <summary>Reads a bucket name, "bucket/folder" path or gs:// URI as a location; null if there is no bucket in it.</summary>
    internal static Location? ParseBucketInput(string input)
    {
        var text = input.Trim();
        if (text.StartsWith("gs://", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..];
        }

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        return new Location(parts[0], parts.Length == 1 ? "" : string.Join('/', parts[1..]) + "/");
    }

    public async Task InitializeAsync()
    {
        ProjectInput = gcs.GuessDefaultProject() ?? "";
        await LoadProjectsAsync();
    }

    public async Task UseKeyFileAsync(string path)
    {
        gcs.UseKeyFile(path);
        Error = null;
        await LoadProjectsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectAsync()
    {
        var project = ProjectInput.Trim();
        if (project.Length == 0)
        {
            Error = "Enter a project ID.";
            return Task.CompletedTask;
        }

        return OpenAsync(async () =>
        {
            var buckets = await gcs.ListBucketsAsync(project);
            _openProject = project;
            if (ProjectOpened is { } opened)
            {
                await opened(project, buckets);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task OpenBucketAsync()
    {
        if (ParseBucketInput(BucketInput) is not { Bucket: { } name } location)
        {
            Error = "Enter a bucket name.";
            return Task.CompletedTask;
        }

        return OpenAsync(async () =>
        {
            var bucket = await gcs.OpenBucketAsync(name);
            BucketInput = "";
            if (BucketOpened is { } opened)
            {
                await opened(bucket, location);
            }
        });
    }

    private bool CanConnect() => !IsConnecting;

    [RelayCommand]
    private void Show()
    {
        Error = null;
        ProjectInput = _openProject ?? ProjectInput;
        IsVisible = true;
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void Cancel() => IsVisible = false;

    // Runs one of the two ways of opening something; on success the prompt closes, on failure it shows why.
    private async Task OpenAsync(Func<Task> open)
    {
        IsConnecting = true;
        Error = null;
        try
        {
            await open();
            IsConnected = true;
            IsVisible = false;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsConnecting = false;
            CredentialSource = gcs.CredentialDescription ?? NoCredentials;
        }
    }

    private async Task LoadProjectsAsync()
    {
        try
        {
            var projects = await gcs.ListProjectsAsync();
            Projects.Clear();
            foreach (var project in projects)
            {
                Projects.Add(project);
            }
        }
        catch (GcsException ex) when (ex.Error == GcsError.NoCredentials)
        {
            // Say so up front rather than after the user clicks Connect.
            Error = ex.Message;
        }
        catch (Exception)
        {
            // Listing projects is a convenience; the user can still type a project ID.
        }

        CredentialSource = gcs.CredentialDescription ?? NoCredentials;
    }
}
