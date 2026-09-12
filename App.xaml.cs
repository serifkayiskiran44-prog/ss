using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // #784: capture a safe diagnostic for any exception that reaches the top of the stack, before the app
    // terminates. This deliberately does not set e.Handled = true on DispatcherUnhandledException -- a
    // genuinely unhandled exception means the app's state is no longer trustworthy, and staying alive on a
    // corrupted state is worse than a clean crash with a diagnostic already on disk.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => LogCrash(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception ex) LogCrash(ex); };
        TaskScheduler.UnobservedTaskException += (_, args) => { LogCrash(args.Exception); args.SetObserved(); };
    }

    internal static void LogCrash(Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
            Directory.CreateDirectory(directory);
            var report = $"{DateTime.UtcNow:O} UNHANDLED{Environment.NewLine}{SafeExceptionSerializer.Serialize(exception)}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "crash.log"), report);
        }
        catch { /* logging a crash must never itself throw and mask the original failure */ }
    }
}
