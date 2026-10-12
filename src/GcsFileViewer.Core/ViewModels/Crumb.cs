namespace GcsFileViewer.ViewModels;

/// <summary>One step of the breadcrumb bar.</summary>
public sealed record Crumb(string Label, Location Location);
