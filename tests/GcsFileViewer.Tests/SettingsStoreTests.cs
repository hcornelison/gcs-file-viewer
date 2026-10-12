using GcsFileViewer.Settings;

namespace GcsFileViewer.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("settings-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SettingsStore Store(string name = "settings.json") => new(Path.Combine(_root, name));

    [Fact]
    public void The_app_starts_in_light_mode() => Assert.False(new AppSettings().DarkMode);

    [Fact]
    public void With_no_file_the_defaults_are_used() => Assert.Equal(new AppSettings(), Store().Load());

    [Fact]
    public void Saved_settings_are_read_back()
    {
        var store = Store();

        Assert.True(store.Save(new AppSettings(DarkMode: true)));

        Assert.True(store.Load().DarkMode);
        Assert.True(Store().Load().DarkMode);
    }

    [Fact]
    public void Saving_again_replaces_what_was_saved()
    {
        var store = Store();
        store.Save(new AppSettings(DarkMode: true));

        store.Save(new AppSettings(DarkMode: false));

        Assert.False(store.Load().DarkMode);
    }

    [Fact]
    public void Saving_creates_the_folder_if_it_is_not_there()
    {
        var store = Store(Path.Combine("deep", "er", "settings.json"));

        Assert.True(store.Save(new AppSettings(DarkMode: true)));

        Assert.True(File.Exists(store.Path));
        Assert.True(store.Load().DarkMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"DarkMode\": \"maybe\" }")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    public void A_damaged_file_gives_the_defaults(string contents)
    {
        var store = Store();
        File.WriteAllText(store.Path, contents);

        Assert.Equal(new AppSettings(), store.Load());
    }

    [Fact]
    public void Settings_this_version_does_not_know_are_ignored()
    {
        var store = Store();
        File.WriteAllText(store.Path, "{ \"DarkMode\": true, \"SomethingFromTheFuture\": 42 }");

        Assert.True(store.Load().DarkMode);
    }

    [Fact]
    public void A_file_that_cannot_be_written_is_reported_and_not_thrown()
    {
        // A folder where the file should be makes the write fail on every platform.
        var store = Store();
        Directory.CreateDirectory(store.Path);

        Assert.False(store.Save(new AppSettings(DarkMode: true)));
        Assert.Equal(new AppSettings(), store.Load());
    }

    [Fact]
    public void The_default_location_is_in_the_users_application_data()
    {
        Assert.EndsWith(Path.Combine("GcsFileViewer", "settings.json"), SettingsStore.DefaultPath);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), SettingsStore.DefaultPath);
    }
}
