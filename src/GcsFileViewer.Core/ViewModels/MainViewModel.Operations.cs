using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GcsFileViewer.Pricing;
using GcsFileViewer.Storage;

namespace GcsFileViewer.ViewModels;

// Long-running work on files and folders: transfers, deletes, renames and sizing, with progress and cancel.
public partial class MainViewModel
{
    /// <summary>
    /// Up to this many names are looked up one by one in a folder that isn't fully loaded. Checking more than
    /// that is cheaper done by listing the folder.
    /// </summary>
    internal const int MaxNameLookups = 25;

    private List<BrowserItem> _clipboard = [];
    private bool _clipboardIsCut;
    private CancellationTokenSource? _operationCts;

    /// <summary>How far the running operation has got, from 0 to 1, or null when that isn't known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [NotifyPropertyChangedFor(nameof(ProgressValue))]
    private double? _progress;

    /// <summary>True while an operation is running that the Cancel button can stop.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isCancellable;

    public bool IsProgressIndeterminate => Progress is null;

    public double ProgressValue => Progress ?? 0;

    /// <summary>
    /// Turns an object path into a relative path that is safe to create on this computer: no empty, "." or ".."
    /// segments, and no characters a file name can't have.
    /// </summary>
    internal static string LocalPath(string objectPath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var segments = objectPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment is not ("." or ".."))
            .Select(segment => string.Concat(segment.Select(c => invalid.Contains(c) || c is '\\' or ':' ? '_' : c)));
        var path = string.Join('/', segments);
        return path.Length == 0 ? "unnamed" : path;
    }

    /// <summary>
    /// Downloads files, and folders with everything inside them, to <paramref name="target"/>. Folders keep their
    /// structure.
    /// </summary>
    public Task DownloadAsync(IReadOnlyList<BrowserItem> items, IDownloadTarget target)
    {
        var selected = items.Where(i => i.Kind != ItemKind.Bucket).ToList();
        if (selected.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync("Could not download", null, async operation =>
        {
            // Find every file first, so progress can be shown against the total size.
            var paths = new LocalPaths();
            var files = new List<(BrowserItem Item, string LocalPath)>();
            var folders = new List<string>();
            foreach (var item in selected)
            {
                if (item.Kind == ItemKind.File)
                {
                    files.Add((item, paths.Claim(item.Name)));
                    continue;
                }

                ShowOperation($"Listing {item.Name}…");
                folders.Add(LocalPath(item.Name));
                var listed = operation.Track<int>(count => ShowOperation($"Listing {item.Name}… {count:N0} items"));
                foreach (var child in await _gcs.ListRecursiveAsync(item.Bucket, item.Path, listed, operation.Token))
                {
                    var path = item.Name + "/" + child.Name;
                    if (child.Kind == ItemKind.Folder)
                    {
                        folders.Add(LocalPath(path));
                    }
                    else
                    {
                        files.Add((child, paths.Claim(path)));
                    }
                }
            }

            foreach (var folder in folders)
            {
                await target.CreateFolderAsync(folder);
            }

            var total = files.Sum(f => f.Item.Size ?? 0);
            long done = 0;
            for (var i = 0; i < files.Count; i++)
            {
                operation.Token.ThrowIfCancellationRequested();
                var (item, local) = files[i];
                var name = local[(local.LastIndexOf('/') + 1)..];
                ShowOperation(files.Count == 1 ? $"Downloading {name}…" : $"Downloading {name} ({i + 1:N0} of {files.Count:N0})…");

                var before = done;
                var written = operation.Track<long>(bytes => Progress = Fraction(before + bytes, total));
                await using (var stream = await target.OpenFileAsync(local))
                {
                    await _gcs.DownloadAsync(item.Bucket, item.Path, stream, written, operation.Token);
                }

                done += item.Size ?? 0;
                Progress = Fraction(done, total);
            }

            return files.Count == 1
                ? $"Downloaded {files[0].LocalPath[(files[0].LocalPath.LastIndexOf('/') + 1)..]}"
                : $"Downloaded {files.Count:N0} files";
        });
    }

    /// <summary>
    /// Uploads files into the current folder, asking first if any would replace or merge with what is there.
    /// </summary>
    public async Task UploadAsync(IReadOnlyList<UploadSource> files)
    {
        if (!IsInFolder || Current.Bucket is not { } bucket || files.Count == 0)
        {
            return;
        }

        var location = Current;
        var prefix = location.Prefix;

        // A plain file clashes with a file of the same name; a file inside a folder clashes with that folder.
        var nested = files.Any(f => f.Name.Contains('/'));
        var arriving = files.Select(f => (Top: f.Name.Split('/')[0], InFolder: f.Name.Contains('/'))).Distinct().ToList();
        List<string> replaced;
        try
        {
            var existing = await FindNamedAsync(location, [.. arriving.Select(a => a.Top).Distinct()]);
            replaced = [.. arriving.Where(a => existing.Any(i => i.Name == a.Top && i.IsContainer == a.InFolder)).Select(a => a.Top)];
        }
        catch (Exception ex)
        {
            Notify("Could not upload: " + ex.Message);
            return;
        }

        if (replaced.Count > 0)
        {
            var title = replaced.Count == 1
                ? $"Replace \"{replaced[0]}\"?"
                : $"Replace {replaced.Count:N0} {(nested ? "items" : "files")}?";
            var message = nested
                ? "The same names already exist here. Files are replaced and folders are merged."
                : replaced.Count == 1
                    ? "A file with this name already exists here. Uploading will replace it."
                    : "Files with these names already exist here. Uploading will replace them.";
            if (!await Dialogs.ConfirmAsync(title, message, "Replace"))
            {
                return;
            }
        }

        await RunAsync("Could not upload", location, async operation =>
        {
            // Progress goes by bytes when every size is known, otherwise by files finished.
            var total = files.All(f => f.Size is not null) ? files.Sum(f => f.Size!.Value) : 0;
            long done = 0;
            for (var i = 0; i < files.Count; i++)
            {
                operation.Token.ThrowIfCancellationRequested();
                ShowOperation($"Uploading {files[i].Name} ({i + 1:N0} of {files.Count:N0})…");

                var before = done;
                var sent = operation.Track<long>(bytes =>
                {
                    if (total > 0)
                    {
                        Progress = Fraction(before + bytes, total);
                    }
                });
                await using (var stream = await files[i].Open())
                {
                    await _gcs.UploadAsync(bucket, prefix + files[i].Name, stream, sent, operation.Token);
                }

                done += files[i].Size ?? 0;
                Progress = total > 0 ? Fraction(done, total) : Fraction(i + 1, files.Count);
            }

            return files.Count == 1 ? $"Uploaded {files[0].Name}" : $"Uploaded {files.Count:N0} files";
        });
    }

    /// <summary>Deletes files and folders (with everything inside them) after asking for confirmation.</summary>
    public async Task DeleteAsync(IReadOnlyList<BrowserItem> items)
    {
        var targets = items.Where(i => i.Kind != ItemKind.Bucket).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var location = Current;
        var title = targets.Count == 1 ? $"Delete \"{targets[0].Name}\"?" : $"Delete {targets.Count:N0} items?";
        var message = targets.Any(i => i.Kind == ItemKind.Folder)
            ? "Folders are deleted along with everything inside them. This can't be undone from this app."
            : "This can't be undone from this app.";
        if (ColdStorageNote(targets) is { } note)
        {
            message += " " + note;
        }

        if (!await Dialogs.ConfirmAsync(title, message, "Delete"))
        {
            return;
        }

        await RunAsync("Could not delete", location, async operation =>
        {
            var deleted = 0;
            foreach (var target in targets)
            {
                operation.Token.ThrowIfCancellationRequested();
                ShowOperation($"Deleting {target.Name}…");
                if (target.Kind == ItemKind.Folder)
                {
                    var steps = operation.Track<OperationProgress>(p => ShowSteps($"Deleting {target.Name}", p));
                    deleted += await _gcs.DeleteFolderAsync(target.Bucket, target.Path, steps, operation.Token);
                }
                else
                {
                    await _gcs.DeleteObjectAsync(target.Bucket, target.Path, operation.Token);
                    deleted++;
                }
            }

            return deleted == 1 ? "Deleted 1 object" : $"Deleted {deleted:N0} objects";
        });
    }

    /// <summary>Asks for a new name for a file or folder and renames it; folders move everything inside them.</summary>
    public async Task RenameAsync(BrowserItem item)
    {
        if (item.Kind == ItemKind.Bucket)
        {
            return;
        }

        // The new name is checked against the folder the item is in, which for a search result isn't the list on screen.
        var location = Current;
        var folder = new Location(item.Bucket, item.ParentPath);
        var name = await Dialogs.PromptAsync(
            $"Rename \"{item.Name}\"",
            item.Name,
            "Rename",
            text => text == item.Name ? Task.FromResult<string?>(null) : ValidateNewNameAsync(folder, text));
        if (name is null || name == item.Name)
        {
            return;
        }

        if (ColdStorageNote([item]) is { } note && !await Dialogs.ConfirmAsync("Rename in cold storage?", note, "Rename"))
        {
            return;
        }

        await RunAsync("Could not rename", location, async operation =>
        {
            ShowOperation($"Renaming {item.Name}…");
            var target = item.ParentPath + name;
            if (item.Kind == ItemKind.Folder)
            {
                var steps = operation.Track<OperationProgress>(p => ShowSteps($"Renaming {item.Name}", p));
                var moved = await _gcs.MoveFolderAsync(item.Bucket, item.Path, item.Bucket, target + "/", overwrite: false, steps, operation.Token);
                return $"Renamed {item.Name} to {name} ({(moved == 1 ? "1 object" : $"{moved:N0} objects")})";
            }

            await _gcs.MoveObjectAsync(item.Bucket, item.Path, item.Bucket, target, overwrite: false, operation.Token);
            return $"Renamed {item.Name} to {name}";
        });
    }

    /// <summary>Counts the objects in a folder or bucket and adds up their sizes, then shows the total in the list.</summary>
    public Task MeasureAsync(BrowserItem item)
    {
        if (item.Kind == ItemKind.File)
        {
            return Task.CompletedTask;
        }

        return RunAsync("Could not calculate the size", null, async operation =>
        {
            ShowOperation($"Measuring {item.Name}…");
            var running = operation.Track<FolderSize>(s =>
                ShowOperation($"Measuring {item.Name}… {s.Objects:N0} objects, {BrowserItem.FormatSize(s.Bytes)}"));
            var size = await _gcs.MeasureAsync(item.Bucket, item.Path, running, operation.Token);

            ReplaceItem(item, item with { Size = size.Bytes });
            var objects = size.Objects == 1 ? "1 object" : $"{size.Objects:N0} objects";
            return $"{item.Name}: {objects}, {BrowserItem.FormatSize(size.Bytes)}";
        });
    }

    /// <summary>Remembers files and folders to copy into whichever folder is pasted into next.</summary>
    public void CopyToClipboard(IReadOnlyList<BrowserItem> items) => SetClipboard(items, cut: false);

    /// <summary>Remembers files and folders to move into whichever folder is pasted into next.</summary>
    public void CutToClipboard(IReadOnlyList<BrowserItem> items) => SetClipboard(items, cut: true);

    /// <summary>Copies or moves files and folders into another folder, as when they are dragged onto it.</summary>
    public async Task DropAsync(IReadOnlyList<BrowserItem> items, Location destination, bool move)
    {
        var targets = items.Where(i => i.Kind != ItemKind.Bucket).ToList();
        if (targets.Count > 0 && destination.Bucket is not null)
        {
            await TransferAsync(targets, destination, move);
        }
    }

    [RelayCommand(CanExecute = nameof(IsCancellable))]
    private void Cancel() => _operationCts?.Cancel();

    /// <summary>Asks for a name and creates an empty folder with it in the current folder.</summary>
    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (!IsInFolder || Current.Bucket is not { } bucket)
        {
            return;
        }

        var location = Current;
        var name = await Dialogs.PromptAsync("New folder", "", "Create", text => ValidateNewNameAsync(location, text));
        if (name is null)
        {
            return;
        }

        await RunAsync("Could not create the folder", location, async operation =>
        {
            await _gcs.CreateFolderAsync(bucket, location.Prefix + name + "/", operation.Token);
            return $"Created folder {name}";
        });
    }

    /// <summary>Copies or moves the remembered items into the current folder, which may be in another bucket.</summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteAsync()
    {
        if (!CanPaste())
        {
            return;
        }

        var move = _clipboardIsCut;
        if (await TransferAsync(_clipboard, Current, move) && move)
        {
            // The originals are gone, so there is nothing left to paste again.
            _clipboard = [];
            PasteCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanPaste() => IsInFolder && _clipboard.Count > 0;

    partial void OnIsInFolderChanged(bool value) => PasteCommand.NotifyCanExecuteChanged();

    // The commands below act on the rows selected in the list, for menus and keyboard shortcuts.

    [RelayCommand(CanExecute = nameof(HasFilesOrFolders))]
    private void CopySelected() => CopyToClipboard(Selection);

    [RelayCommand(CanExecute = nameof(HasFilesOrFolders))]
    private void CutSelected() => CutToClipboard(Selection);

    [RelayCommand(CanExecute = nameof(HasFilesOrFolders))]
    private Task DeleteSelectedAsync() => DeleteAsync(Selection);

    [RelayCommand(CanExecute = nameof(HasOneFileOrFolder))]
    private Task RenameSelectedAsync() => RenameAsync(Selection[0]);

    [RelayCommand(CanExecute = nameof(HasOneFileOrFolder))]
    private Task OpenContainingFolderOfSelectedAsync() => OpenContainingFolderAsync(Selection[0]);

    [RelayCommand(CanExecute = nameof(HasOneContainer))]
    private Task MeasureSelectedAsync() => MeasureAsync(Selection[0]);

    private bool HasFilesOrFolders() => Selection.Any(i => i.Kind != ItemKind.Bucket);

    private bool HasOneFileOrFolder() => Selection is [{ Kind: not ItemKind.Bucket }];

    private bool HasOneContainer() => Selection is [{ IsContainer: true }];

    private void NotifySelectionCommands()
    {
        CopySelectedCommand.NotifyCanExecuteChanged();
        CutSelectedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RenameSelectedCommand.NotifyCanExecuteChanged();
        OpenContainingFolderOfSelectedCommand.NotifyCanExecuteChanged();
        MeasureSelectedCommand.NotifyCanExecuteChanged();
    }

    private void SetClipboard(IReadOnlyList<BrowserItem> items, bool cut)
    {
        var targets = items.Where(i => i.Kind != ItemKind.Bucket).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        _clipboard = targets;
        _clipboardIsCut = cut;
        PasteCommand.NotifyCanExecuteChanged();
        var what = targets.Count == 1 ? $"\"{targets[0].Name}\"" : $"{targets.Count:N0} items";
        Notify($"{what} ready to {(cut ? "move" : "copy")}. Open the destination folder and paste.");
    }

    // Returns true only if every item was copied or moved.
    private async Task<bool> TransferAsync(IReadOnlyList<BrowserItem> items, Location destination, bool move)
    {
        var location = Current;
        var bucket = destination.Bucket!;
        var prefix = destination.Prefix;
        var failure = move ? "Could not move" : "Could not copy";

        if (items.Any(i => i.Kind == ItemKind.Folder && i.Bucket == bucket && prefix.StartsWith(i.Path, StringComparison.Ordinal)))
        {
            Notify($"Can't {(move ? "move" : "copy")} a folder into itself.");
            return false;
        }

        if (move && items.All(i => i.Bucket == bucket && i.ParentPath == prefix))
        {
            Notify("Those items are already in this folder.");
            return false;
        }

        if (ColdStorageNote(items) is { } note
            && !await Dialogs.ConfirmAsync($"{(move ? "Move" : "Copy")} from cold storage?", note, move ? "Move" : "Copy"))
        {
            return false;
        }

        // Work out each item's name there, and which names are already taken, before anything is written.
        // Copies made beside the original get a new name; moves to the same place are skipped.
        var plan = new List<(BrowserItem Item, string Name, bool Replaces)>();
        try
        {
            var arriving = items.Where(i => !(i.Bucket == bucket && i.ParentPath == prefix)).ToList();
            var taken = (await FindNamedAsync(destination, [.. arriving.Select(i => i.Name).Distinct()]))
                .Select(i => i.Name)
                .ToHashSet();
            foreach (var item in items)
            {
                var alreadyThere = !arriving.Contains(item);
                if (alreadyThere && move)
                {
                    continue;
                }

                var name = alreadyThere ? await UniqueCopyNameAsync(item, destination, taken) : item.Name;
                plan.Add((item, name, !alreadyThere && taken.Contains(name)));
                taken.Add(name);
            }
        }
        catch (Exception ex)
        {
            Notify($"{failure}: {ex.Message}");
            Cost.Refresh();
            return false;
        }

        var replaced = plan.Where(p => p.Replaces).ToList();
        if (replaced.Count > 0)
        {
            var title = replaced.Count == 1 ? $"Replace \"{replaced[0].Name}\"?" : $"Replace {replaced.Count:N0} items?";
            if (!await Dialogs.ConfirmAsync(title, "The same name already exists there. Files are replaced and folders are merged.", "Replace"))
            {
                return false;
            }
        }

        return await RunAsync(failure, location, async operation =>
        {
            var count = 0;
            foreach (var (item, name, replaces) in plan)
            {
                operation.Token.ThrowIfCancellationRequested();
                var doing = $"{(move ? "Moving" : "Copying")} {item.Name}";
                ShowOperation(doing + "…");
                var target = prefix + name;
                var steps = operation.Track<OperationProgress>(p => ShowSteps(doing, p));
                switch (item.Kind, move)
                {
                    case (ItemKind.Folder, true):
                        count += await _gcs.MoveFolderAsync(item.Bucket, item.Path, bucket, target + "/", replaces, steps, operation.Token);
                        break;
                    case (ItemKind.Folder, false):
                        count += await _gcs.CopyFolderAsync(item.Bucket, item.Path, bucket, target + "/", replaces, steps, operation.Token);
                        break;
                    case (_, true):
                        await _gcs.MoveObjectAsync(item.Bucket, item.Path, bucket, target, replaces, operation.Token);
                        count++;
                        break;
                    default:
                        await _gcs.CopyObjectAsync(item.Bucket, item.Path, bucket, target, replaces, operation.Token);
                        count++;
                        break;
                }
            }

            return $"{(move ? "Moved" : "Copied")} {(count == 1 ? "1 object" : $"{count:N0} objects")}";
        });
    }

    /// <summary>
    /// The files and folders in <paramref name="folder"/> that have one of the given names. Uses what is on
    /// screen when that is the whole folder; otherwise asks about a few names directly, or lists the folder when
    /// there are too many names for that to be worthwhile.
    /// </summary>
    private async Task<List<BrowserItem>> FindNamedAsync(Location folder, IReadOnlyCollection<string> names)
    {
        if (names.Count == 0)
        {
            return [];
        }

        var onScreen = folder == Current;
        if (onScreen && !HasMore)
        {
            return [.. _all.Where(i => names.Contains(i.Name))];
        }

        if (names.Count <= MaxNameLookups)
        {
            return await _gcs.FindAsync(folder.Bucket!, folder.Prefix, names);
        }

        if (onScreen)
        {
            await EnsureAllLoadedAsync();
            return [.. _all.Where(i => names.Contains(i.Name))];
        }

        var found = new List<BrowserItem>();
        string? token = null;
        do
        {
            var page = await _gcs.ListPageAsync(folder.Bucket!, folder.Prefix, token);
            found.AddRange(page.Items.Where(i => names.Contains(i.Name)));
            token = page.NextPageToken;
        }
        while (token is not null);

        return found;
    }

    // A message if a new file or folder can't be given this name in a folder, or null if it can.
    private async Task<string?> ValidateNewNameAsync(Location folder, string name)
    {
        if (name.Length == 0)
        {
            return "Enter a name.";
        }

        if (name.Contains('/'))
        {
            return "A name can't contain a slash.";
        }

        if (name is "." or "..")
        {
            return "That name isn't allowed.";
        }

        try
        {
            return (await FindNamedAsync(folder, [name])).Count > 0 ? $"\"{name}\" already exists here." : null;
        }
        catch (Exception ex)
        {
            return "Could not check that name: " + ex.Message;
        }
    }

    // "report.pdf" becomes "report (copy).pdf", then "report (copy 2).pdf", until the name is free.
    private async Task<string> UniqueCopyNameAsync(BrowserItem item, Location folder, HashSet<string> planned)
    {
        var extension = item.Kind == ItemKind.File ? Path.GetExtension(item.Name) : "";
        var stem = item.Name[..^extension.Length];
        if (stem.Length == 0)
        {
            (stem, extension) = (item.Name, "");
        }

        for (var n = 1; ; n++)
        {
            var name = n == 1 ? $"{stem} (copy){extension}" : $"{stem} (copy {n}){extension}";
            if (!planned.Contains(name) && (await FindNamedAsync(folder, [name])).Count == 0)
            {
                return name;
            }
        }
    }

    // A warning for items in Nearline, Coldline or Archive storage, where reading, moving and deleting cost extra.
    // Folders are judged by their bucket's default class, since their contents aren't known without listing them.
    private string? ColdStorageNote(IEnumerable<BrowserItem> items)
    {
        var tiers = items
            .Select(i => i.Kind == ItemKind.File ? i.Tier : Buckets.FirstOrDefault(b => b.Name == i.Bucket)?.Tier)
            .Where(tier => tier?.IsCold() == true)
            .Select(tier => tier.ToString()!)
            .Distinct()
            .Order()
            .ToList();
        return tiers.Count == 0
            ? null
            : $"This involves {string.Join(" and ", tiers)} storage, where reading or copying data has a retrieval fee and " +
              "deleting, moving or renaming it early has an early deletion fee.";
    }

    private void ReplaceItem(BrowserItem old, BrowserItem replacement)
    {
        var index = _all.IndexOf(old);
        if (index >= 0)
        {
            _all[index] = replacement;
        }

        index = Items.IndexOf(old);
        if (index >= 0)
        {
            Items[index] = replacement;
        }
    }

    // Shows "Deleting docs… 1,200 of 5,000" and moves the progress bar to match.
    private void ShowSteps(string doing, OperationProgress progress)
    {
        ShowOperation($"{doing}… {progress.Done:N0} of {progress.Total:N0}");
        Progress = Fraction(progress.Done, progress.Total);
    }

    private static double? Fraction(long done, long total) => total > 0 ? Math.Clamp(done / (double)total, 0, 1) : null;

    /// <summary>
    /// Runs one cancellable operation at a time: shows it as busy, turns failure and cancellation into a status
    /// message, and refreshes <paramref name="reload"/> afterwards if it is still the folder on screen.
    /// </summary>
    /// <param name="failure">What to say went wrong, such as "Could not delete"; the reason is added after it.</param>
    /// <returns>True if the work finished; false if it failed, was cancelled, or another operation was running.</returns>
    private async Task<bool> RunAsync(string failure, Location? reload, Func<Operation, Task<string>> work)
    {
        if (_operationCts is not null)
        {
            Notify("Another operation is still running. Wait for it to finish, or cancel it.");
            return false;
        }

        var cts = _operationCts = new CancellationTokenSource();
        _notice = null;
        IsBusy = true;
        IsCancellable = true;
        Progress = null;

        string result;
        var completed = false;
        try
        {
            result = await work(new Operation(this, cts));
            completed = true;
        }
        catch (OperationCanceledException)
        {
            result = "Cancelled";
        }
        catch (Exception ex)
        {
            result = $"{failure}: {ex.Message}";
        }

        _operationCts = null;
        _operationText = null;
        IsCancellable = false;
        Progress = null;

        // Even a failed or cancelled operation may have changed the folder part-way.
        if (reload is not null && Current == reload)
        {
            if (await LoadAsync(reload, reload: true, keepState: true) is { } reloadError)
            {
                result += ". " + reloadError;
            }
        }
        else
        {
            IsBusy = _loadingText is not null;
            Cost.Refresh();
        }

        Notify(result);
        return completed;
    }

    /// <summary>The running operation: its cancellation token, and progress callbacks that stop once it has ended.</summary>
    private sealed class Operation(MainViewModel owner, CancellationTokenSource cts)
    {
        public CancellationToken Token => cts.Token;

        public IProgress<T> Track<T>(Action<T> apply) => new CallbackProgress<T>(value => owner._postToUi(() =>
        {
            if (owner._operationCts == cts && !cts.IsCancellationRequested)
            {
                apply(value);
            }
        }));
    }

    /// <summary>Progress that calls straight through, leaving it to the callback to get onto the right thread.</summary>
    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>
    /// Hands out local paths for downloads, making sure no two objects get the same one. Names that differ only
    /// by case, or only in characters a file name can't have, would otherwise overwrite each other.
    /// </summary>
    private sealed class LocalPaths
    {
        private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);

        public string Claim(string objectPath)
        {
            var path = LocalPath(objectPath);
            if (_taken.Add(path))
            {
                return path;
            }

            var folder = path[..(path.LastIndexOf('/') + 1)];
            var extension = Path.GetExtension(path);
            var stem = path[folder.Length..^extension.Length];
            for (var n = 2; ; n++)
            {
                var candidate = $"{folder}{stem} ({n}){extension}";
                if (_taken.Add(candidate))
                {
                    return candidate;
                }
            }
        }
    }
}
