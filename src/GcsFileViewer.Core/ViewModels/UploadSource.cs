namespace GcsFileViewer.ViewModels;

/// <summary>
/// A local file to upload: its name, a way to open it for reading, and its size if known. A name with slashes,
/// such as "photos/2026/a.jpg", uploads into those folders under the current one.
/// </summary>
public sealed record UploadSource(string Name, Func<Task<Stream>> Open, long? Size = null);
