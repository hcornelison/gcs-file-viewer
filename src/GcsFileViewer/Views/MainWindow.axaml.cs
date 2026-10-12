using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GcsFileViewer.Settings;
using GcsFileViewer.Storage;
using GcsFileViewer.ViewModels;
using Location = GcsFileViewer.ViewModels.Location;

namespace GcsFileViewer.Views;

public partial class MainWindow : Window
{
    private const int FolderColumnIndex = 1;

    // How far the pointer must move with the button down before it counts as a drag.
    private const double DragThreshold = 6;

    // The press on a row that a drag could start from, and the rows that drag would carry. A drag is started
    // from the press that began it, once the pointer has moved far enough.
    private PointerPressedEventArgs? _press;
    private Point _pressPoint;
    private List<BrowserItem> _pressItems = [];

    // The rows being dragged, while a drag that started in this window is in progress.
    private List<BrowserItem>? _dragged;

    // The row, sidebar bucket or breadcrumb currently styled as the place a drop would land.
    private Control? _dropHighlight;

    // The decoded image in the preview, which holds unmanaged memory until it is disposed.
    private Bitmap? _previewBitmap;

    public MainWindow()
    {
        InitializeComponent();

        // Fetch the next page of a long folder as its last rows scroll into view.
        FileGrid.LoadingRow += (_, e) =>
        {
            if (Vm.HasMore && e.Row.Index >= Vm.Items.Count - 20)
            {
                Vm.LoadMoreCommand.Execute(null);
            }
        };

        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => HighlightDropTarget(null));
        AddHandler(DragDrop.DropEvent, Window_Drop);

        // Tunnel on press to see the selection before the grid collapses it to the pressed row.
        FileGrid.AddHandler(PointerPressedEvent, FileGrid_PointerPressed, RoutingStrategies.Tunnel);
        FileGrid.AddHandler(PointerMovedEvent, FileGrid_PointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        FileGrid.AddHandler(PointerReleasedEvent, (_, _) => _press = null, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Where the choice of theme is remembered. Without one, the choice lasts until the app closes.</summary>
    public SettingsStore? Settings { get; init; }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ProjectBox.Focus();
        DarkModeToggle.IsChecked = Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;

        Vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsSearch))
            {
                // Search results come from many folders, so show which one each is in.
                FileGrid.Columns[FolderColumnIndex].IsVisible = Vm.IsSearch;
            }
        };
        Vm.Dialogs.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DialogsViewModel.IsPromptVisible) && Vm.Dialogs.IsPromptVisible)
            {
                PromptBox.Focus();
                PromptBox.SelectAll();
            }
        };
        Vm.Details.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DetailsViewModel.PreviewImage))
            {
                ShowPreviewImage(Vm.Details.PreviewImage);
            }
        };

        Run(Vm.Connect.InitializeAsync);
    }

    /// <summary>
    /// Runs work started by a click or key press. Anything it throws is shown in the status bar; an exception
    /// escaping an event handler would otherwise end the app.
    /// </summary>
    private async void Run(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Vm.Notify("Something went wrong: " + ex.Message);
        }
    }

    private void ShowPreviewImage(byte[]? bytes)
    {
        PreviewImage.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        if (bytes is null)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            PreviewImage.Source = _previewBitmap = new Bitmap(stream);
        }
        catch (Exception)
        {
            // Not an image this platform can decode; the pane just shows no picture.
        }
    }

    private void DarkModeToggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var dark = DarkModeToggle.IsChecked == true;
        var theme = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (app.RequestedThemeVariant != theme)
        {
            app.RequestedThemeVariant = theme;
            Settings?.Save(new AppSettings(DarkMode: dark));
        }
    }

    private void Crumb_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Crumb crumb })
        {
            Run(() => Vm.NavigateAsync(crumb.Location));
        }
    }

    private void FileGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        Vm.UpdateSelection(FileGrid.SelectedItems.OfType<BrowserItem>().ToList());

    private void FileGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // Ignore double-clicks on the column headers.
        if (RowUnder(e.Source as Visual) is not null)
        {
            OpenSelected();
        }
    }

    private void FileGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        // Cmd on macOS, Ctrl elsewhere.
        var shortcut = e.KeyModifiers is KeyModifiers.Meta or KeyModifiers.Control;
        var command = (e.Key, shortcut) switch
        {
            (Key.C, true) => Vm.CopySelectedCommand,
            (Key.X, true) => Vm.CutSelectedCommand,
            (Key.V, true) => Vm.PasteCommand,
            (Key.Delete, _) => Vm.DeleteSelectedCommand,
            (Key.Back, _) when e.KeyModifiers.HasFlag(KeyModifiers.Meta) => Vm.DeleteSelectedCommand,
            (Key.Back, _) => Vm.UpCommand,
            (Key.F2, _) => Vm.RenameSelectedCommand,
            _ => (System.Windows.Input.ICommand?)null,
        };

        if (command is not null)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OpenSelected();
            e.Handled = true;
        }
    }

    private void Open_Click(object? sender, RoutedEventArgs e) => OpenSelected();

    // Folders and buckets open in place; a file is downloaded.
    private void OpenSelected()
    {
        if (FileGrid.SelectedItem is BrowserItem { IsContainer: true } container)
        {
            Vm.OpenCommand.Execute(container);
        }
        else if (FileGrid.SelectedItem is BrowserItem)
        {
            Run(DownloadSelectedAsync);
        }
    }

    private void Download_Click(object? sender, RoutedEventArgs e) => Run(DownloadSelectedAsync);

    private async Task DownloadSelectedAsync()
    {
        var items = Vm.Selection.Where(i => i.Kind != ItemKind.Bucket).ToList();
        if (items.Count == 0)
        {
            return;
        }

        // One file gets a save dialog; anything else is written under a chosen folder, keeping its structure.
        if (items is [{ Kind: ItemKind.File } file])
        {
            var saveAs = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Download",
                SuggestedFileName = file.Name,
            });
            if (saveAs is not null)
            {
                await Vm.DownloadAsync(items, new SingleFileTarget(saveAs));
            }

            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Download to…" });
        if (folders.Count > 0)
        {
            await Vm.DownloadAsync(items, new FolderTarget(folders[0]));
        }
    }

    private void Upload_Click(object? sender, RoutedEventArgs e) => Run(async () =>
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Upload files",
            AllowMultiple = true,
        });
        await UploadAsync(picked);
    });

    private void UploadFolder_Click(object? sender, RoutedEventArgs e) => Run(async () =>
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Upload folders",
            AllowMultiple = true,
        });
        await UploadAsync(picked);
    });

    // Uploads files, and folders with everything inside them under their own names.
    private async Task UploadAsync(IEnumerable<IStorageItem> items)
    {
        var files = new List<UploadSource>();
        foreach (var item in items)
        {
            await CollectAsync(item, "", files);
        }

        await Vm.UploadAsync(files);
    }

    private static async Task CollectAsync(IStorageItem item, string folder, List<UploadSource> files)
    {
        if (item is IStorageFile file)
        {
            var size = (await file.GetBasicPropertiesAsync()).Size;
            files.Add(new UploadSource(folder + file.Name, file.OpenReadAsync, (long?)size));
        }
        else if (item is IStorageFolder directory)
        {
            await foreach (var child in directory.GetItemsAsync())
            {
                await CollectAsync(child, folder + directory.Name + "/", files);
            }
        }
    }

    private void CopyUri_Click(object? sender, RoutedEventArgs e) => Run(async () =>
    {
        var uris = string.Join(Environment.NewLine, Vm.Selection.Select(i => i.Uri));
        if (uris.Length > 0 && Clipboard is not null)
        {
            await Clipboard.SetTextAsync(uris);
        }
    });

    private void UseKeyFile_Click(object? sender, RoutedEventArgs e) => Run(async () =>
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Service account key file",
            FileTypeFilter = [new FilePickerFileType("JSON key") { Patterns = ["*.json"] }],
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            await Vm.Connect.UseKeyFileAsync(path);
        }
    });

    private void FileGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        if (e.GetCurrentPoint(FileGrid).Properties.IsLeftButtonPressed
            && RowUnder(e.Source as Visual) is { Kind: not ItemKind.Bucket } item)
        {
            _press = e;
            _pressPoint = e.GetPosition(FileGrid);
            _pressItems = Vm.Selection.Contains(item) ? Vm.Selection.Where(i => i.Kind != ItemKind.Bucket).ToList() : [item];
        }
    }

    private void FileGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _dragged is not null)
        {
            return;
        }

        var moved = e.GetPosition(FileGrid) - _pressPoint;
        if (!e.GetCurrentPoint(FileGrid).Properties.IsLeftButtonPressed
            || (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold))
        {
            return;
        }

        _press = null;
        var dragged = _dragged = _pressItems;
        Run(async () =>
        {
            // Dropped outside the app, the items arrive as their gs:// URIs.
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(string.Join(Environment.NewLine, dragged.Select(i => i.Uri))));
            try
            {
                await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move | DragDropEffects.Copy);
            }
            finally
            {
                _dragged = null;
                HighlightDropTarget(null);
            }
        });
    }

    private void HighlightDropTarget(Control? target)
    {
        if (ReferenceEquals(_dropHighlight, target))
        {
            return;
        }

        _dropHighlight?.Classes.Remove("dropTarget");
        _dropHighlight = target;
        target?.Classes.Add("dropTarget");
    }

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        if (_dragged is { } items)
        {
            var target = DropTarget(e, items);
            HighlightDropTarget(target?.Visual);
            e.DragEffects = target is null ? DragDropEffects.None
                : CopyRequested(e) ? DragDropEffects.Copy
                : DragDropEffects.Move;
        }
        else
        {
            // Files from outside the app upload into the folder being viewed.
            e.DragEffects = Vm.IsInFolder && IsOverFileGrid(e) && e.DataTransfer.Contains(DataFormat.File)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }
    }

    private void Window_Drop(object? sender, DragEventArgs e)
    {
        HighlightDropTarget(null);
        if (_dragged is { } items)
        {
            if (DropTarget(e, items) is { } target)
            {
                var move = !CopyRequested(e);
                Run(() => Vm.DropAsync(items, target.Destination, move));
            }
        }
        else if (Vm.IsInFolder && IsOverFileGrid(e) && e.DataTransfer.TryGetFiles() is { } dropped)
        {
            Run(() => UploadAsync(dropped));
        }
    }

    // Holding Ctrl (or Option on macOS) while dropping copies instead of moving.
    private static bool CopyRequested(DragEventArgs e) =>
        e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt);

    private Visual? VisualUnder(DragEventArgs e) => this.InputHitTest(e.GetPosition(this)) as Visual;

    private bool IsOverFileGrid(DragEventArgs e) =>
        VisualUnder(e) is { } visual && (visual == FileGrid || FileGrid.IsVisualAncestorOf(visual));

    private static BrowserItem? RowUnder(Visual? visual) =>
        visual?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext as BrowserItem;

    // Where a drop would land and the control showing it: a folder or bucket row, a sidebar bucket, or a breadcrumb.
    private (Location Destination, Control Visual)? DropTarget(DragEventArgs e, List<BrowserItem> items)
    {
        var visual = VisualUnder(e);
        if (visual?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: BrowserItem { IsContainer: true } folder } row)
        {
            return items.Contains(folder) ? null : (new Location(folder.Bucket, folder.Path), row);
        }

        if (visual?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: BrowserItem bucket } sidebarItem)
        {
            return (new Location(bucket.Name, ""), sidebarItem);
        }

        return visual?.FindAncestorOfType<Button>(includeSelf: true) is { DataContext: Crumb { Location.Bucket: not null } crumb } button
            ? (crumb.Location, button)
            : null;
    }
}
