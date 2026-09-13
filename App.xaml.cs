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
        // #857: a missing or mistyped design token is a startup failure that names the key -- never a control quietly
        // falling back to a default. The handlers above are already in place, so the crash log carries the reason.
        VerifyDesignTokens(Resources);
        // #879: the UI freeze watchdog — a heartbeat the dispatcher cannot answer within the threshold is a freeze,
        // recorded with its duration, the active command and a correlation id (never a stack or a payload).
        watchdog = StartWatchdog(Dispatcher);
    }

    UiFreezeWatchdog? watchdog;

    protected override void OnExit(ExitEventArgs e)
    {
        watchdog?.Dispose(); watchdog = null;
        base.OnExit(e);
    }

    /// <summary>Starts and installs the process-wide watchdog; the audit store on the data directory takes the freeze rows (in memory only when it cannot be opened).</summary>
    internal static UiFreezeWatchdog StartWatchdog(Dispatcher dispatcher, string? directory = null, int thresholdMs = UiFreezeWatchdog.DefaultThresholdMs, int intervalMs = UiFreezeWatchdog.DefaultIntervalMs)
    {
        AuditStore? audit = null;
        try { audit = new AuditStore(directory); } catch (Exception error) { System.Diagnostics.Debug.WriteLine(error.Message); }
        var started = UiFreezeWatchdog.Start(dispatcher, audit, thresholdMs, intervalMs);
        UiFreezeWatchdog.Install(started);
        return started;
    }

    internal static void VerifyDesignTokens(ResourceDictionary resources) => DesignTokens.Verify(resources);

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
