using System.Diagnostics;

namespace GcsFileViewer.GcloudAuth;

/// <summary>Reads what the gcloud CLI knows: the signed-in account's access token and the default configuration.</summary>
public static class GcloudCli
{
    /// <summary>How long gcloud gets to answer before it is stopped.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private const string Help = "Run \"gcloud auth login\" and try again, or use a service account key file.";

    /// <summary>The file name of the gcloud launcher on this operating system.</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "gcloud.cmd" : "gcloud";

    /// <summary>Asks the gcloud CLI for an access token for the account signed in with "gcloud auth login".</summary>
    public static Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
        GetAccessTokenAsync(Find(), DefaultTimeout, ct);

    /// <summary>
    /// Runs the gcloud launcher at <paramref name="gcloud"/> to get an access token; null means it isn't installed.
    /// gcloud is stopped if it takes longer than <paramref name="timeout"/>, as it would if it were waiting for
    /// someone to sign in again.
    /// </summary>
    public static async Task<string> GetAccessTokenAsync(string? gcloud, TimeSpan timeout, CancellationToken ct = default)
    {
        if (gcloud is null)
        {
            throw new InvalidOperationException("No Google credentials found and the gcloud CLI is not installed. " + Help);
        }

        // gcloud is a .cmd script on Windows, which has to be run through the shell.
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/c", gcloud, "auth", "print-access-token"])
            : new ProcessStartInfo(gcloud, ["auth", "print-access-token"]);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the gcloud CLI. " + Help);

        // Nothing will ever be typed, so a gcloud that asks a question gets end-of-input instead of waiting.
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                $"The gcloud CLI did not answer within {timeout.TotalSeconds:0} seconds. It may be waiting for you to sign in. " + Help);
        }

        var token = (await output).Trim();
        if (process.ExitCode != 0 || token.Length == 0)
        {
            var reason = (await error).Trim().Split('\n')[0].Trim();
            throw new InvalidOperationException($"The gcloud CLI has no usable login ({reason}). " + Help);
        }

        return token;
    }

    /// <summary>The lines of gcloud's default configuration file, or null if it is missing or unreadable.</summary>
    public static IEnumerable<string>? ReadDefaultConfig() => ReadDefaultConfig(
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gcloud")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "gcloud"));

    /// <summary>Reads the default configuration from gcloud's configuration directory.</summary>
    public static IEnumerable<string>? ReadDefaultConfig(string configDirectory)
    {
        try
        {
            var config = Path.Combine(configDirectory, "configurations", "config_default");
            return File.Exists(config) ? File.ReadAllLines(config) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Finds the "project = ..." setting in the lines of a gcloud configuration file.</summary>
    public static string? ParseConfiguredProject(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0] == "project" && parts[1].Length > 0)
            {
                return parts[1];
            }
        }

        return null;
    }

    /// <summary>Finds gcloud in the directories of a PATH value, then in <paramref name="fallbackDirectories"/>.</summary>
    public static string? Find(string? path, IEnumerable<string> fallbackDirectories) =>
        (path ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(fallbackDirectories)
            .Select(directory => Path.Combine(directory, ExecutableName))
            .FirstOrDefault(File.Exists);

    private static string? Find()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Apps launched outside a shell get a minimal PATH, so also look in the usual install locations.
        return Find(
            Environment.GetEnvironmentVariable("PATH"),
            ["/opt/homebrew/bin", "/usr/local/bin", "/snap/bin", Path.Combine(home, "google-cloud-sdk", "bin")]);
    }
}
