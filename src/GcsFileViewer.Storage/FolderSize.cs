namespace GcsFileViewer.Storage;

/// <summary>How many objects are under a folder and how much they hold.</summary>
public readonly record struct FolderSize(long Objects, long Bytes);
