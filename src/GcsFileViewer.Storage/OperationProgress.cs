namespace GcsFileViewer.Storage;

/// <summary>How far a multi-object operation has got.</summary>
public readonly record struct OperationProgress(int Done, int Total);
