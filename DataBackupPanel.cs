using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TrMarketplaceHubDesktop;

public static class DataBackupPanel
{
    public static FrameworkElement Create(string? directory)
    {
        var service = new DataBackupService(directory);
        var panel = new StackPanel { Margin = Spacing.Inline, MaxWidth = 850 };
        panel.Children.Add(new TextBlock { Text = AppVersion.Display, FontSize = DesignTokens.TextSubsectionTitleSize, FontWeight = DesignTokens.FontWeightTitle, Margin = new Thickness(0, 4, 0, 6) });
        panel.Children.Add(new TextBlock { Text = $"Yerel veri: {AuditStore.Redact(service.DataDirectory)}\nŞifreli credential dosyaları çözülmeden byte olarak korunur. Yedek/geri yükleme yalnızca yerel dosyalarla çalışır.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var status = new TextBlock { Text = "Güncelleme kaynağı yapılandırılmadı; doğrulanmamış uzak endpoint çağrılmıyor.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        var backup = new Button { Content = "Verileri güvenli yedekle" };
        backup.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { Filter = "MonoBridge veri yedeği (*.zip)|*.zip", FileName = ExportFileNames.Build("monobridge-yedek", "zip"), AddExtension = true };
            if (dialog.ShowDialog() != true) return;
            try { status.Text = $"Yedek hazırlandı: {service.Backup(dialog.FileName, overwrite: true)}"; } catch (Exception error) { status.Text = MarketplaceConnectionStore.Redact(error.Message); }
        };
        var restore = new Button { Content = "Yedekten geri yükle" };
        restore.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "MonoBridge veri yedeği (*.zip)|*.zip", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var manifest = service.Validate(dialog.FileName);
                if (MessageBox.Show($"{manifest.Files.Count:N0} dosya doğrulandı. Mevcut veri klasörü önce güvenlik yedeğine alınacak ve uygulama yeniden başlatılmalıdır. Devam edilsin mi?", "Geri yükleme", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                service.Restore(dialog.FileName); status.Text = "Geri yükleme tamamlandı. Değişiklikleri görmek için uygulamayı yeniden başlatın.";
            }
            catch (Exception error) { status.Text = MarketplaceConnectionStore.Redact(error.Message); }
        };
        var open = new Button { Content = "Uygulama veri klasörünü aç" };
        open.Click += (_, _) => { try { Directory.CreateDirectory(service.DataDirectory); Process.Start(new ProcessStartInfo(service.DataDirectory) { UseShellExecute = true }); } catch { status.Text = "Veri klasörü açılamadı."; } };
        var row = new WrapPanel(); row.Children.Add(backup); row.Children.Add(restore); row.Children.Add(open); panel.Children.Add(row); panel.Children.Add(status);
        return panel;
    }
}
