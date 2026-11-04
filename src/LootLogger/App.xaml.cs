using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace LootLogger;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = AppSettings.Load();

        // Язык: из настроек, а при первом запуске — по языку Windows.
        var english = settings.Language switch
        {
            "en" => true,
            "ru" => false,
            _ => !string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "uk", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "be", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "kk", StringComparison.OrdinalIgnoreCase)
        };
        settings.Language = english ? "en" : "ru";
        Loc.Instance.SetLanguage(english);

        _singleInstance = new Mutex(true, "AlbionLootLogger.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(Loc.T("AlreadyRunning"), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };

        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        MessageBox.Show(Loc.F("UnhandledError", e.Exception.Message, AppPaths.ErrorLog),
            Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    internal static void LogError(Exception? exception)
    {
        if (exception == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(AppPaths.ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\n\n");
        }
        catch
        {
            // ignored
        }
    }
}
