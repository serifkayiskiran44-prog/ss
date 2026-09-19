using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    StartupCrashGuard? crashGuard;
    string? dataDirectory;
    SingleInstanceGuard? singleInstance;
    BackgroundAppController? backgroundController;
    MonoBridgeTrayIcon? trayIcon;
    bool exitStarted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        dataDirectory = ResolveDataDirectory();

        singleInstance = SingleInstanceGuard.TryAcquire(dataDirectory, ActivateMainWindow);
        if (singleInstance is null)
        {
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { LogFatal(dataDirectory, args.Exception); args.SetObserved(); };

        try
        {
            crashGuard = new StartupCrashGuard(dataDirectory);
        }
        catch { crashGuard = null; }

        if (crashGuard?.RecoveryModeRequired == true)
        {
            MessageBox.Show(
                "Uygulama üst üste birkaç kez açılırken kapandı. Bu pencereyi kapattıktan sonra program yine de açılmayı deneyecek; sorun sürerse Tanılama ekranından destek paketi dışa aktarabilir veya veri klasörünü yedekleyip inceleyebilirsiniz.",
                "MarketplaceHub — kurtarma modu",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        StartupHealthReport health;
        // An unexpected exception here must never be reported as an empty (and
        // therefore, before #2657, silently "Ready") check list - it becomes an
        // explicit Incomplete check so the gate below gets to see it.
        try { health = StartupPreflight.Run(dataDirectory); }
        catch (Exception ex) { LogFatal(dataDirectory, ex); health = new([new StartupHealthCheck("preflight", StartupHealthStatus.Incomplete, "Başlangıç sağlık kontrolü çalıştırılamadı: " + AuditStore.Sanitize(ex.Message))]); }

        if (health.Overall is StartupHealthStatus.Blocked or StartupHealthStatus.RecoveryRequired or StartupHealthStatus.Incomplete)
        {
            var problems = string.Join(Environment.NewLine, health.Checks.Where(c => c.Status is StartupHealthStatus.Blocked or StartupHealthStatus.RecoveryRequired or StartupHealthStatus.Incomplete).Select(c => "• " + c.Detail));
            MessageBox.Show(
                "Uygulama şu anda güvenli şekilde açılamıyor:" + Environment.NewLine + Environment.NewLine + problems,
                "MarketplaceHub — başlatma engellendi",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        if (health.Overall == StartupHealthStatus.Degraded)
        {
            var warnings = string.Join(Environment.NewLine, health.Checks.Where(c => c.Status == StartupHealthStatus.Degraded).Select(c => "• " + c.Detail));
            MessageBox.Show(
                "Uygulama açılıyor ancak bazı özellikler sınırlı olabilir:" + Environment.NewLine + Environment.NewLine + warnings,
                "MarketplaceHub — sınırlı başlangıç",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        try
        {
            backgroundController = new BackgroundAppController(dataDirectory, BackgroundJobFactory.Create(dataDirectory));
            var window = new MainWindow(dataDirectory, backgroundController);
            window.ContentRendered += (_, _) => crashGuard?.MarkReachedUi();
            window.FirstHiddenToTray += (_, _) => trayIcon?.ShowBackgroundNotice();
            window.ExitRequested += async (_, _) => await ExitAsync();
            // The 1440x900 default is larger than common laptop work areas (e.g. 1366x720):
            // clamp the restore size and start maximized so nothing opens off-screen.
            var work = SystemParameters.WorkArea;
            if (window.Width > work.Width || window.Height > work.Height)
            {
                window.Width = Math.Min(window.Width, work.Width);
                window.Height = Math.Min(window.Height, work.Height);
                window.WindowState = WindowState.Maximized;
            }
            MainWindow = window;
            window.Show();
            trayIcon = new MonoBridgeTrayIcon(backgroundController, Dispatcher, ActivateMainWindow, () => ExitAsync());
            backgroundController.Start();
        }
        catch (Exception ex)
        {
            LogFatal(dataDirectory, ex);
            MessageBox.Show(
                "Uygulama başlatılamadı. Yerel veri klasörü bozulmuş veya erişilemez olabilir. Ayrıntılar için operations.log dosyasına bakın.",
                "MarketplaceHub — başlatma hatası",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _ = ExitAsync(-1);
        }
    }

    void ActivateMainWindow()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is not Window window) return;
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        });
    }

    async Task ExitAsync(int exitCode = 0)
    {
        if (exitStarted) return;
        exitStarted = true;
        try
        {
            if (backgroundController is not null)
            {
                backgroundController.RequestExplicitExit();
                await backgroundController.ShutdownAsync();
            }
        }
        finally
        {
            trayIcon?.Dispose();
            if (MainWindow is Window window) window.Close();
            Shutdown(exitCode);
        }
    }

    /// Test/smoke hook only: MARKETPLACEHUB_DATA_DIR lets Smoke-Test.ps1 point a real
    /// launched EXE at an isolated temp directory instead of the user's LocalAppData
    /// data. Unset (the normal case) preserves the existing default resolution.
    static string? ResolveDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("MARKETPLACEHUB_DATA_DIR");
        return string.IsNullOrWhiteSpace(overridePath) ? null : overridePath;
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatal(dataDirectory, e.Exception);
        MessageBox.Show(
            "Beklenmeyen bir hata oluştu; uygulama güvenli şekilde kapatılacak. Ayrıntılar için operations.log dosyasına bakın.",
            "MarketplaceHub — beklenmeyen hata",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        _ = ExitAsync(-1);
    }

    void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) LogFatal(dataDirectory, ex);
    }

    static void LogFatal(string? directory, Exception ex)
    {
        try
        {
            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
            Directory.CreateDirectory(directory);
            var line = $"{DateTime.UtcNow:O} FATAL {AuditStore.Sanitize(ex.ToString())}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "operations.log"), line);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        trayIcon?.Dispose();
        if (backgroundController is not null)
        {
            backgroundController.RequestExplicitExit();
            backgroundController.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
