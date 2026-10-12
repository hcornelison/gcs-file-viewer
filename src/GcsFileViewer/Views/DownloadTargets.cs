using Avalonia.Platform.Storage;
using GcsFileViewer.ViewModels;

namespace GcsFileViewer.Views;

/// <summary>Writes a single download to the file chosen in a save dialog, whatever its name in the bucket.</summary>
internal sealed class SingleFileTarget(IStorageFile file) : IDownloadTarget
{
    public Task<Stream> OpenFileAsync(string relativePath) => file.OpenWriteAsync();

    public Task CreateFolderAsync(string relativePath) => Task.CompletedTask;
}

/// <summary>Writes downloads under a chosen folder, creating subfolders as they are needed.</summary>
internal sealed class FolderTarget(IStorageFolder root) : IDownloadTarget
{
    private readonly Dictionary<string, IStorageFolder> _folders = new() { [""] = root };

    public async Task<Stream> OpenFileAsync(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        var folder = await FolderAsync(slash < 0 ? "" : relativePath[..slash]);
        var file = await folder.CreateFileAsync(relativePath[(slash + 1)..])
            ?? throw new IOException($"Could not create {relativePath}.");
        return await file.OpenWriteAsync();
    }

    public async Task CreateFolderAsync(string relativePath) => await FolderAsync(relativePath);

    private async Task<IStorageFolder> FolderAsync(string path)
    {
        if (_folders.TryGetValue(path, out var known))
        {
            return known;
        }

        var slash = path.LastIndexOf('/');
        var parent = await FolderAsync(slash < 0 ? "" : path[..slash]);
        var folder = await parent.CreateFolderAsync(path[(slash + 1)..])
            ?? throw new IOException($"Could not create the folder {path}.");
        return _folders[path] = folder;
    }
}
