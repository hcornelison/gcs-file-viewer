using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using GcsFileViewer.Settings;
using GcsFileViewer.Storage;
using GcsFileViewer.ViewModels;
using GcsFileViewer.Views;

namespace GcsFileViewer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel(new GcsService(), action => Dispatcher.UIThread.Post(action));

            // Light unless the user chose dark last time.
            var settings = new SettingsStore(SettingsStore.DefaultPath);
            RequestedThemeVariant = settings.Load().DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;

            // The last line of defence: report an error nothing else caught, and keep the app running.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                viewModel.Notify("Something went wrong: " + e.Exception.Message);
                e.Handled = true;
            };

            desktop.MainWindow = new MainWindow { DataContext = viewModel, Settings = settings };
            ShowDockIcon();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowDockIcon()
    {
        using var icon = AssetLoader.Open(new Uri("avares://GcsFileViewer/Assets/icon.png"));
        using var bytes = new MemoryStream();
        icon.CopyTo(bytes);
        MacDockIcon.Set(bytes.ToArray());
    }
}
