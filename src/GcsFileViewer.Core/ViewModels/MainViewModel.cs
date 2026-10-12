using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GcsFileViewer.Storage;

namespace GcsFileViewer.ViewModels;

/// <summary>
/// The browser window: where it is, what is listed there, what is selected, and the status line. The connect
/// prompt, dialogs, details pane and cost estimate are separate view models it owns.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IGcsService _gcs;
    private readonly Action<Action> _postToUi;
    private readonly NavigationHistory _history = new();

    // Buckets opened by name, which stay listed alongside whichever project is open.
    private readonly List<BrowserItem> _namedBuckets = [];
    private List<BrowserItem> _projectBuckets = [];

    // Everything loaded for the current location, before the filter is applied.
    private List<BrowserItem> _all = [];
    private CancellationTokenSource? _loadCts;

    // The token for the next page of the current listing while there is more of it, and the fetch of that page
    // if one is under way. Everything that wants the next page shares that one fetch.
    private string? _nextPageToken;
    private Task? _pageFetch;

    // The status line shows the first of these that is set, then falls back to a summary of the listing.
    private string? _operationText;
    private string? _loadingText;
    private string? _notice;
    private string? _selectionText;

    [ObservableProperty]
    private string? _projectId;

    /// <summary>True when the list shows a folder inside a bucket, where things can be added.</summary>
    [ObservableProperty]
    private bool _isInFolder;

    /// <summary>True when the list shows search results instead of a folder.</summary>
    [ObservableProperty]
    private bool _isSearch;

    /// <summary>True while the current folder or search has more pages that haven't been loaded.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _hasMore;

    [ObservableProperty]
    private BrowserItem? _selectedBucket;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _filter = "";

    /// <param name="postToUi">
    /// Runs an action on the UI thread. Progress from background work goes through it. When omitted, as in tests,
    /// actions run immediately on whichever thread reports them.
    /// </param>
    public MainViewModel(IGcsService gcs, Action<Action>? postToUi = null)
    {
        _gcs = gcs;
        _postToUi = postToUi ?? (action => action());
        Cost = new CostViewModel(gcs.Usage);
        Details = new DetailsViewModel(gcs, Cost.Refresh, Notify);
        Connect = new ConnectViewModel(gcs) { ProjectOpened = OpenProjectAsync, BucketOpened = OpenNamedBucketAsync };
    }

    public ConnectViewModel Connect { get; }

    public DialogsViewModel Dialogs { get; } = new();

    public DetailsViewModel Details { get; }

    public CostViewModel Cost { get; }

    public ObservableCollection<BrowserItem> Buckets { get; } = [];

    public ObservableCollection<BrowserItem> Items { get; } = [];

    public ObservableCollection<Crumb> Crumbs { get; } = [];

    /// <summary>The rows selected in the list, as last reported by <see cref="UpdateSelection"/>.</summary>
    public IReadOnlyList<BrowserItem> Selection { get; private set; } = [];

    private Location Current => _history.Current;

    public Task NavigateAsync(Location location) =>
        _history.Navigate(location) ? LoadAsync(location) : Task.CompletedTask;

    /// <summary>Goes to the folder that holds an item, which is how to get from a search result to its surroundings.</summary>
    public Task OpenContainingFolderAsync(BrowserItem item) =>
        item.Kind == ItemKind.Bucket ? Task.CompletedTask : NavigateAsync(new Location(item.Bucket, item.ParentPath));

    /// <summary>Tells the view model which rows are selected. Selection-based commands and the details pane follow it.</summary>
    public void UpdateSelection(IReadOnlyList<BrowserItem> selected)
    {
        Selection = [.. selected];
        if (selected.Count == 0)
        {
            _selectionText = null;
        }
        else
        {
            // Picking something is a sign the last message has been read.
            _notice = null;
            var size = selected.Sum(i => i.Size ?? 0);
            var text = selected.Count == 1 ? "1 item selected" : $"{selected.Count:N0} items selected";
            _selectionText = selected.Any(i => i.Size is not null) ? $"{text}  ·  {BrowserItem.FormatSize(size)}" : text;
        }

        RefreshStatus();
        NotifySelectionCommands();
        Details.Show(Selection);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task BackAsync() => LoadAsync(_history.Back());

    private bool CanGoBack() => _history.CanGoBack;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task ForwardAsync() => LoadAsync(_history.Forward());

    private bool CanGoForward() => _history.CanGoForward;

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task UpAsync()
    {
        if (Current.Search is not null)
        {
            return NavigateAsync(Current with { Search = null });
        }

        if (Current.Prefix.Length == 0)
        {
            return NavigateAsync(Location.Root);
        }

        var trimmed = Current.Prefix.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return NavigateAsync(Current with { Prefix = slash < 0 ? "" : trimmed[..(slash + 1)] });
    }

    private bool CanGoUp() => Current.Bucket is not null;

    [RelayCommand]
    private Task RefreshAsync() => Connect.IsConnected ? LoadAsync(Current, reload: true) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenAsync(BrowserItem? item) => item switch
    {
        { Kind: ItemKind.Bucket } => NavigateAsync(new Location(item.Name, "")),
        { Kind: ItemKind.Folder } => NavigateAsync(new Location(item.Bucket, item.Path)),
        _ => Task.CompletedTask,
    };

    /// <summary>Searches every folder under the current one for the text in the filter box.</summary>
    [RelayCommand]
    private Task SearchAsync()
    {
        var term = Filter.Trim();
        return term.Length == 0 || Current.Bucket is null
            ? Task.CompletedTask
            : NavigateAsync(Current with { Search = term });
    }

    /// <summary>Loads the next page of a long folder or search.</summary>
    [RelayCommand(CanExecute = nameof(HasMore))]
    private async Task LoadMoreAsync()
    {
        var cts = _loadCts;
        try
        {
            await NextPageAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cts == _loadCts)
            {
                Notify("Could not load more: " + ex.Message);
            }
        }
    }

    partial void OnSelectedBucketChanged(BrowserItem? value)
    {
        if (value is not null && value.Name != Current.Bucket)
        {
            _ = NavigateAsync(new Location(value.Name, ""));
        }
    }

    partial void OnFilterChanged(string value)
    {
        _notice = null;
        ApplyFilter();
    }

    // The connect prompt has listed a project's buckets: show them and start from the bucket list.
    private async Task OpenProjectAsync(string projectId, List<BrowserItem> buckets)
    {
        ProjectId = projectId;
        _projectBuckets = buckets;
        _history.Reset(Location.Root);
        ShowBuckets();
        await LoadAsync(Location.Root);
    }

    // The connect prompt has opened a bucket by name: add it to the sidebar and go to it.
    private async Task OpenNamedBucketAsync(BrowserItem bucket, Location location)
    {
        _namedBuckets.RemoveAll(b => b.Name == bucket.Name);
        _namedBuckets.Add(bucket);
        ShowBuckets();
        if (!_history.Navigate(location))
        {
            await LoadAsync(location, reload: true);
            return;
        }

        await LoadAsync(location);
    }

    /// <summary>Shows a location, replacing whatever was listed.</summary>
    /// <param name="reload">Fetches the bucket list again when showing it, instead of reusing what is known.</param>
    /// <param name="keepState">
    /// Keeps the filter and the last message, for refreshing a folder after something in it changed.
    /// </param>
    /// <returns>Why the location could not be shown, or null if it was.</returns>
    private async Task<string?> LoadAsync(Location location, bool reload = false, bool keepState = false)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        IsInFolder = location.Bucket is not null && location.Search is null;
        IsSearch = location.Search is not null;
        _all = [];
        _nextPageToken = null;
        _pageFetch = null;
        HasMore = false;
        if (!keepState)
        {
            Filter = "";
            _notice = null;
        }

        _loadingText = location.Search is null ? "Loading…" : $"Searching for \"{location.Search}\"…";
        IsBusy = true;
        ApplyFilter();
        IsEmpty = false;
        RebuildCrumbs();
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        UpCommand.NotifyCanExecuteChanged();
        SelectedBucket = Buckets.FirstOrDefault(b => b.Name == location.Bucket);

        string? error = null;
        try
        {
            ListPage page;
            if (location.Bucket is null)
            {
                if (reload && ProjectId is not null)
                {
                    _projectBuckets = await _gcs.ListBucketsAsync(ProjectId, cts.Token);
                    ShowBuckets();
                }

                page = new ListPage([.. Buckets], null);
            }
            else
            {
                page = await FetchPageAsync(location, null, cts.Token);
            }

            if (cts == _loadCts)
            {
                _all = page.Items;
                _nextPageToken = page.NextPageToken;
                HasMore = _nextPageToken is not null;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            error = (location.Search is null ? "Could not load this folder: " : "Could not search: ") + ex.Message;
        }

        // A newer load has taken over the list; leave everything to it.
        if (cts != _loadCts)
        {
            return null;
        }

        _loadingText = null;
        IsBusy = _operationCts is not null;
        if (error is not null)
        {
            _notice = error;
        }

        ApplyFilter();
        Cost.Refresh();
        return error;
    }

    private Task<ListPage> FetchPageAsync(Location location, string? token, CancellationToken ct) =>
        location.Search is null
            ? _gcs.ListPageAsync(location.Bucket!, location.Prefix, token, ct)
            : _gcs.SearchPageAsync(location.Bucket!, location.Prefix, location.Search, token, ct);

    // Fetches and shows the next page. Callers that ask while a fetch is under way wait for that one, so a page
    // is never fetched or added twice.
    private Task NextPageAsync()
    {
        if (_pageFetch is { IsCompleted: false } running)
        {
            return running;
        }

        var fetch = FetchNextPageAsync();
        _pageFetch = fetch.IsCompleted ? null : fetch;
        return fetch;
    }

    private async Task FetchNextPageAsync()
    {
        var cts = _loadCts;
        var location = Current;
        try
        {
            if (_nextPageToken is { } token && cts is not null)
            {
                var page = await FetchPageAsync(location, token, cts.Token);
                if (cts == _loadCts)
                {
                    AppendPage(page);
                }
            }
        }
        finally
        {
            if (cts == _loadCts)
            {
                _pageFetch = null;
            }

            Cost.Refresh();
        }
    }

    // Loads whatever is left of the current folder, for work that needs every name in it.
    private async Task EnsureAllLoadedAsync()
    {
        var location = Current;
        while (location == Current && _nextPageToken is not null)
        {
            await NextPageAsync();
        }
    }

    // Adds a page to the list without disturbing what is already shown: new folders join the other folders
    // at the top, and files go on the end.
    private void AppendPage(ListPage page)
    {
        var filter = Filter.Trim();
        var folders = _all.TakeWhile(i => i.IsContainer).Count();
        var shownFolders = Items.TakeWhile(i => i.IsContainer).Count();
        foreach (var item in page.Items)
        {
            var shown = Matches(item, filter);
            if (item.IsContainer)
            {
                _all.Insert(folders++, item);
                if (shown)
                {
                    Items.Insert(shownFolders++, item);
                }
            }
            else
            {
                _all.Add(item);
                if (shown)
                {
                    Items.Add(item);
                }
            }
        }

        _nextPageToken = page.NextPageToken;
        HasMore = _nextPageToken is not null;
        IsEmpty = Items.Count == 0 && !IsBusy;
        RefreshStatus();
    }

    private void ShowBuckets()
    {
        var selected = Current.Bucket;
        Buckets.Clear();
        foreach (var bucket in _projectBuckets)
        {
            Buckets.Add(bucket);
        }

        foreach (var bucket in _namedBuckets.Where(b => _projectBuckets.All(p => p.Name != b.Name)))
        {
            Buckets.Add(bucket);
        }

        SelectedBucket = Buckets.FirstOrDefault(b => b.Name == selected);
    }

    private void ApplyFilter()
    {
        var filter = Filter.Trim();
        Items.Clear();
        foreach (var item in _all.Where(item => Matches(item, filter)))
        {
            Items.Add(item);
        }

        IsEmpty = Items.Count == 0 && !IsBusy;
        RefreshStatus();
    }

    private static bool Matches(BrowserItem item, string filter) =>
        filter.Length == 0 || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Shows a message in the status line until the user moves on.</summary>
    public void Notify(string message)
    {
        _notice = message;
        RefreshStatus();
    }

    // What an operation is doing right now, which takes over the status line while it runs.
    private void ShowOperation(string? text)
    {
        _operationText = text;
        RefreshStatus();
    }

    private void RefreshStatus() =>
        Status = _operationText ?? _loadingText ?? _notice ?? _selectionText ?? DescribeListing();

    private string DescribeListing()
    {
        // A "+" marks counts that will grow as more of a long folder or search is loaded.
        var more = HasMore ? "+" : "";
        var count = Items.Count == 1 && !HasMore ? "1 item" : $"{Items.Count:N0}{more} items";
        if (Items.Count != _all.Count)
        {
            count += $" (of {_all.Count:N0}{more})";
        }

        var size = Items.Sum(i => i.Size ?? 0);
        var text = size > 0 ? $"{count}  ·  {BrowserItem.FormatSize(size)}" : count;
        return HasMore ? text + "  ·  more to load" : text;
    }

    private void RebuildCrumbs()
    {
        Crumbs.Clear();
        Crumbs.Add(new Crumb(ProjectId ?? "Buckets", Location.Root));
        if (Current.Bucket is not { } bucket)
        {
            return;
        }

        Crumbs.Add(new Crumb(bucket, new Location(bucket, "")));
        var prefix = "";
        foreach (var segment in Current.Prefix.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            prefix += segment + "/";
            Crumbs.Add(new Crumb(segment, new Location(bucket, prefix)));
        }

        if (Current.Search is not null)
        {
            Crumbs.Add(new Crumb($"Search: {Current.Search}", Current));
        }
    }
}
