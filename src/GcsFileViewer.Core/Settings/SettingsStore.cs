using System.Text.Json;

namespace GcsFileViewer.Settings;

/// <summary>
/// Keeps <see cref="AppSettings"/> in a JSON file. Settings are a convenience, so a file that is missing,
/// unreadable or damaged gives the defaults, and a failure to save is ignored.
/// </summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The settings file in the user's application data folder.</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GcsFileViewer", "settings.json");

    public string Path { get; } = path;

    public AppSettings Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <returns>True if the settings were written.</returns>
    public bool Save(AppSettings settings)
    {
        try
        {
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(Path, JsonSerializer.Serialize(settings, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
