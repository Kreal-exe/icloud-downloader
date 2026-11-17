using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace iCloudDownloader;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // A bug in the window must not close it in the middle of a download: write it down and carry on.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            WriteCrash(e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => window.Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }

    static void WriteCrash(Exception? error)
    {
        try
        {
            File.AppendAllText(Path.Combine(Platform.LogDir, "crash.log"), $"{DateTime.Now:s} {error}\n\n");
        }
        catch
        {
        }
    }
}
