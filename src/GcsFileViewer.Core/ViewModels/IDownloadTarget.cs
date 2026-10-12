namespace GcsFileViewer.ViewModels;

/// <summary>Where downloads are written. Paths are relative, use forward slashes, and are safe to create locally.</summary>
public interface IDownloadTarget
{
    Task<Stream> OpenFileAsync(string relativePath);

    Task CreateFolderAsync(string relativePath);
}
