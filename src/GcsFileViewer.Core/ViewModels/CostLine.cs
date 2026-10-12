namespace GcsFileViewer.ViewModels;

/// <summary>One row of the session cost breakdown.</summary>
public sealed record CostLine(string Label, string Quantity, string Cost);
