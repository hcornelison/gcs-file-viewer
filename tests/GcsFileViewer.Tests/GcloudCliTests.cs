using GcsFileViewer.GcloudAuth;

namespace GcsFileViewer.Tests;

/// <summary>GcloudCli against stand-in gcloud launchers and configuration files written to a temporary folder.</summary>
public sealed class GcloudCliTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gcloud-cli-tests-").FullName;

    // Long enough that a slow machine starting a shell never trips it.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        // Restore access to anything a test locked, so the folder can be removed.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private string NewDirectory(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    // Writes a launcher named like gcloud that prints the given output and error lines and exits with the given code.
    private string FakeGcloud(string directoryName, string stdout = "", string stderr = "", int exitCode = 0)
    {
        var path = Path.Combine(NewDirectory(directoryName), GcloudCli.ExecutableName);
        if (OperatingSystem.IsWindows())
        {
            var lines = new List<string> { "@echo off" };
            lines.AddRange(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => $"echo {l}"));
            lines.AddRange(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => $"echo {l} 1>&2"));
            lines.Add($"exit /b {exitCode}");
            File.WriteAllLines(path, lines);
        }
        else
        {
            var lines = new List<string> { "#!/bin/sh" };
            lines.AddRange(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => $"echo \"{l}\""));
            lines.AddRange(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => $"echo \"{l}\" 1>&2"));
            lines.Add($"exit {exitCode}");
            File.WriteAllText(path, string.Join('\n', lines) + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private string ConfigDirectory(params string[] lines)
    {
        var directory = NewDirectory("config");
        Directory.CreateDirectory(Path.Combine(directory, "configurations"));
        File.WriteAllLines(Path.Combine(directory, "configurations", "config_default"), lines);
        return directory;
    }

    // Finding gcloud

    [Fact]
    public void Find_looks_through_the_path_in_order()
    {
        var empty = NewDirectory("empty");
        var first = FakeGcloud("first");
        var second = FakeGcloud("second");
        var path = string.Join(Path.PathSeparator, empty, Path.GetDirectoryName(first), Path.GetDirectoryName(second));

        Assert.Equal(first, GcloudCli.Find(path, []));
    }

    [Fact]
    public void Find_falls_back_to_the_usual_install_locations()
    {
        var installed = FakeGcloud("sdk");

        Assert.Equal(installed, GcloudCli.Find(NewDirectory("empty"), [NewDirectory("also-empty"), Path.GetDirectoryName(installed)!]));
    }

    [Fact]
    public void Find_prefers_the_path_over_the_fallback_locations()
    {
        var onPath = FakeGcloud("on-path");
        var fallback = FakeGcloud("fallback");

        Assert.Equal(onPath, GcloudCli.Find(Path.GetDirectoryName(onPath), [Path.GetDirectoryName(fallback)!]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Find_copes_with_a_missing_path(string? path)
    {
        var installed = FakeGcloud("sdk");

        Assert.Equal(installed, GcloudCli.Find(path, [Path.GetDirectoryName(installed)!]));
    }

    [Fact]
    public void Find_returns_null_when_gcloud_is_nowhere() =>
        Assert.Null(GcloudCli.Find(NewDirectory("empty"), [NewDirectory("also-empty"), Path.Combine(_root, "does-not-exist")]));

    [Fact]
    public void Find_ignores_a_directory_that_merely_shares_the_name()
    {
        var directory = NewDirectory("has-folder");
        Directory.CreateDirectory(Path.Combine(directory, GcloudCli.ExecutableName));

        Assert.Null(GcloudCli.Find(directory, []));
    }

    // Getting a token

    [Fact]
    public async Task GetAccessToken_returns_what_gcloud_prints_without_the_line_ending()
    {
        var gcloud = FakeGcloud("ok", stdout: "ya29.token-value");

        Assert.Equal("ya29.token-value", await GcloudCli.GetAccessTokenAsync(gcloud, Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessToken_asks_gcloud_to_print_the_access_token()
    {
        // This launcher echoes its own arguments back as the "token".
        var gcloud = Path.Combine(NewDirectory("echo"), GcloudCli.ExecutableName);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllLines(gcloud, ["@echo off", "echo %1 %2"]);
        }
        else
        {
            File.WriteAllText(gcloud, "#!/bin/sh\necho \"$1 $2\"\n");
            File.SetUnixFileMode(gcloud, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal("auth print-access-token", await GcloudCli.GetAccessTokenAsync(gcloud, Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessToken_ignores_warnings_gcloud_writes_alongside_a_token()
    {
        var gcloud = FakeGcloud("warns", stdout: "the-token", stderr: "WARNING: update available");

        Assert.Equal("the-token", await GcloudCli.GetAccessTokenAsync(gcloud, Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAccessToken_reports_that_gcloud_is_not_installed()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GcloudCli.GetAccessTokenAsync(null, Timeout, TestContext.Current.CancellationToken));

        Assert.StartsWith("No Google credentials found and the gcloud CLI is not installed.", error.Message);
        Assert.Contains("gcloud auth login", error.Message);
    }

    [Fact]
    public async Task GetAccessToken_reports_the_first_line_of_gclouds_error_when_it_fails()
    {
        var gcloud = FakeGcloud(
            "logged-out",
            stderr: "ERROR: You do not currently have an active account selected.\nPlease run: gcloud auth login",
            exitCode: 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GcloudCli.GetAccessTokenAsync(gcloud, Timeout, TestContext.Current.CancellationToken));

        Assert.StartsWith(
            "The gcloud CLI has no usable login (ERROR: You do not currently have an active account selected.).",
            error.Message);
        Assert.DoesNotContain("Please run", error.Message);
    }

    [Fact]
    public async Task GetAccessToken_treats_a_failed_run_as_no_login_even_if_it_printed_something()
    {
        var gcloud = FakeGcloud("partial", stdout: "not-a-token", stderr: "ERROR: token expired", exitCode: 2);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GcloudCli.GetAccessTokenAsync(gcloud, Timeout, TestContext.Current.CancellationToken));

        Assert.Contains("ERROR: token expired", error.Message);
    }

    [Fact]
    public async Task GetAccessToken_treats_empty_output_as_no_login()
    {
        var gcloud = FakeGcloud("silent");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GcloudCli.GetAccessTokenAsync(gcloud, Timeout, TestContext.Current.CancellationToken));

        Assert.StartsWith("The gcloud CLI has no usable login", error.Message);
    }

    [Fact]
    public async Task GetAccessToken_gives_up_on_a_gcloud_that_never_answers()
    {
        var gcloud = Path.Combine(NewDirectory("stuck"), GcloudCli.ExecutableName);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllLines(gcloud, ["@echo off", "ping -n 60 127.0.0.1 > nul"]);
        }
        else
        {
            File.WriteAllText(gcloud, "#!/bin/sh\nsleep 60\n");
            File.SetUnixFileMode(gcloud, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var started = DateTime.UtcNow;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GcloudCli.GetAccessTokenAsync(gcloud, TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));

        Assert.StartsWith("The gcloud CLI did not answer within", error.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "The stuck gcloud should have been stopped promptly.");
    }

    [Fact]
    public async Task GetAccessToken_stops_when_cancelled()
    {
        var gcloud = Path.Combine(NewDirectory("slow"), GcloudCli.ExecutableName);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllLines(gcloud, ["@echo off", "ping -n 60 127.0.0.1 > nul"]);
        }
        else
        {
            File.WriteAllText(gcloud, "#!/bin/sh\nsleep 60\n");
            File.SetUnixFileMode(gcloud, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GcloudCli.GetAccessTokenAsync(gcloud, Timeout, cts.Token));
    }

    [Fact]
    public async Task GetAccessToken_does_not_wait_on_a_gcloud_that_asks_a_question()
    {
        // This launcher waits for a line of input before answering, as gcloud does when it wants confirmation.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var gcloud = Path.Combine(NewDirectory("asks"), GcloudCli.ExecutableName);
        File.WriteAllText(gcloud, "#!/bin/sh\nread answer\necho \"token-after-$answer\"\n");
        File.SetUnixFileMode(gcloud, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.Equal("token-after-", await GcloudCli.GetAccessTokenAsync(gcloud, Timeout, TestContext.Current.CancellationToken));
    }

    // Reading the default configuration

    [Fact]
    public void ReadDefaultConfig_returns_the_files_lines()
    {
        var directory = ConfigDirectory("[core]", "account = me@example.com", "project = my-project");

        var lines = GcloudCli.ReadDefaultConfig(directory);

        Assert.Equal(["[core]", "account = me@example.com", "project = my-project"], lines);
        Assert.Equal("my-project", GcloudCli.ParseConfiguredProject(lines!));
    }

    [Fact]
    public void ReadDefaultConfig_is_null_when_gcloud_has_never_been_configured()
    {
        Assert.Null(GcloudCli.ReadDefaultConfig(NewDirectory("fresh")));
        Assert.Null(GcloudCli.ReadDefaultConfig(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void ReadDefaultConfig_is_null_when_the_file_cannot_be_read()
    {
        // Permission bits don't stop an administrator, and Windows has no equivalent of them.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        var directory = ConfigDirectory("project = secret");
        File.SetUnixFileMode(Path.Combine(directory, "configurations", "config_default"), UnixFileMode.None);

        Assert.Null(GcloudCli.ReadDefaultConfig(directory));
    }

    [Fact]
    public void ReadDefaultConfig_is_null_when_the_file_is_in_use()
    {
        var directory = ConfigDirectory("project = busy");
        var file = Path.Combine(directory, "configurations", "config_default");

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(GcloudCli.ReadDefaultConfig(directory));
        }
    }

    [Fact]
    public void ParseConfiguredProject_reads_the_project_setting()
    {
        string[] lines = ["[core]", "account = me@example.com", "project = my-project-123", "", "[compute]", "region = us-east1"];

        Assert.Equal("my-project-123", GcloudCli.ParseConfiguredProject(lines));
    }

    [Fact]
    public void ParseConfiguredProject_tolerates_missing_spaces() =>
        Assert.Equal("p1", GcloudCli.ParseConfiguredProject(["project=p1"]));

    [Theory]
    [InlineData("[core]", "account = me@example.com")]
    [InlineData("project =", "")]
    [InlineData("project_number = 42", "")]
    public void ParseConfiguredProject_returns_null_when_no_project_is_set(params string[] lines) =>
        Assert.Null(GcloudCli.ParseConfiguredProject(lines));

    // The real machine: only checks that the default lookup runs without throwing, whatever is installed.

    [Fact]
    public void The_default_config_lookup_never_throws()
    {
        var lines = GcloudCli.ReadDefaultConfig();

        Assert.True(lines is null || lines.All(line => line is not null));
    }
}
