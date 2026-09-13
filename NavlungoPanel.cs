using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>International Express setup only. No booking, payment or order mutation.</summary>
public static class NavlungoPanel
{
    public static FrameworkElement Create()
    {
        var panel = new StackPanel { Margin = new Thickness(24), MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Left };
        void Text(string text, double size = 14) => panel.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = Spacing.BelowSection });
        Text("Navlungo Express • Yurt dışı kargo", 25);
        Text("Yurt dışı gönderiler için bağlantı hazırlığı. Navlungo hesabına ek olarak API başvurusu ve sağlayıcının verdiği uygulama bilgileri gerekir.");
        var store = new NavlungoSettingsStore();
        NavlungoSettings? saved = null;
        string? loadError = null;
        try { saved = store.Load(); } catch { loadError = "Kayıtlı Navlungo ayarları okunamadı. Yeniden girip kaydedin."; }
        var status = new TextBlock { Text = loadError ?? NavlungoConnection.Describe(saved), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20), Foreground = new SolidColorBrush(DesignTokens.WarningTextColor) };
        panel.Children.Add(status);
        Text("Navlungo uygulama kimliği (client_id)");
        var clientId = new TextBox { Text = saved?.ClientId ?? "", MinWidth = 450, Margin = Spacing.BelowSection };
        panel.Children.Add(clientId);
        Text("Başvuruda kayıtlı HTTPS dönüş adresi");
        var callback = new TextBox { Text = saved?.CallbackUri ?? "", Margin = Spacing.BelowSection };
        panel.Children.Add(callback);
        var sandbox = new CheckBox { Content = "QA test ortamı (ayrı test hesabı ve uygulama bilgileri)", IsChecked = saved?.Sandbox ?? false, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(sandbox);
        var save = new Button { Content = "Ayarları güvenli kaydet", Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        save.Click += (_, _) =>
        {
            try { saved = new(clientId.Text.Trim(), callback.Text.Trim(), sandbox.IsChecked == true); store.Save(saved); status.Text = NavlungoConnection.Describe(saved); }
            catch (ArgumentException error) { status.Text = error.Message; }
            catch { status.Text = "Ayarlar Windows kullanıcı profilinde güvenli kaydedilemedi."; }
        };
        panel.Children.Add(save);
        Text("API başvurusu onaylandıktan sonra yetkilendirme tamamlanmalı. Bu sürümde token alışverişi ve canlı kargo sorgusu henüz etkin değil. Ayar kaydetmek hesabı bağlamaz.");
        Text("Belgelenen teklif işlemi Navlungo üzerinde sipariş de oluşturduğu için bu hazırlık ekranından teklif, sipariş, sevkiyat veya ödeme gönderilmez.");
        var web = new Button { Content = "Navlungo hesabını aç", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 8, 14, 8) };
        web.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("https://navlungo.com") { UseShellExecute = true }); } catch { status.Text = "Tarayıcı açılamadı. navlungo.com adresini açın."; } };
        panel.Children.Add(web);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
