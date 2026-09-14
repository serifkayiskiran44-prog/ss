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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { LogFatal(args.Exception); args.SetObserved(); };

        try
        {
            crashGuard = new StartupCrashGuard();
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
        try { health = StartupPreflight.Run(); }
        catch (Exception ex) { LogFatal(ex); health = new([]); }

        if (health.Overall is StartupHealthStatus.Blocked or StartupHealthStatus.RecoveryRequired)
        {
            var problems = string.Join(Environment.NewLine, health.Checks.Where(c => c.Status is StartupHealthStatus.Blocked or StartupHealthStatus.RecoveryRequired).Select(c => "• " + c.Detail));
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
            var window = new MainWindow();
            window.ContentRendered += (_, _) => crashGuard?.MarkReachedUi();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            LogFatal(ex);
            MessageBox.Show(
                "Uygulama başlatılamadı. Yerel veri klasörü bozulmuş veya erişilemez olabilir. Ayrıntılar için operations.log dosyasına bakın.",
                "MarketplaceHub — başlatma hatası",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatal(e.Exception);
        MessageBox.Show(
            "Beklenmeyen bir hata oluştu; uygulama güvenli şekilde kapatılacak. Ayrıntılar için operations.log dosyasına bakın.",
            "MarketplaceHub — beklenmeyen hata",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(-1);
    }

    void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) LogFatal(ex);
    }

    static void LogFatal(Exception ex)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
            Directory.CreateDirectory(directory);
            var line = $"{DateTime.UtcNow:O} FATAL {AuditStore.Sanitize(ex.GetType().Name + ": " + ex.Message)}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(directory, "operations.log"), line);
        }
        catch { }
    }
}
