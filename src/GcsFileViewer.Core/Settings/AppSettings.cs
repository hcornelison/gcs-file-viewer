namespace GcsFileViewer.Settings;

/// <summary>What the app remembers between runs.</summary>
/// <param name="DarkMode">True for the dark theme. The app starts in the light theme until the user chooses otherwise.</param>
public sealed record AppSettings(bool DarkMode = false);
