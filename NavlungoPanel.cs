using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>International Express setup only. No booking, payment or order mutation.</summary>
public static class NavlungoPanel
{
    public static FrameworkElement Create(string? directory = null)
    {
        var panel = new StackPanel { Margin = new Thickness(24), MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Left };
        void Text(string text, double size = 14) => panel.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        Text("Navlungo Express • Yurt dışı kargo", 25);
        Text("Yurt dışı gönderiler için bağlantı hazırlığı. Navlungo hesabına ek olarak API başvurusu ve sağlayıcının verdiği uygulama bilgileri gerekir.");
        var store = new NavlungoSettingsStore(directory is null ? null : System.IO.Path.Combine(directory, "navlungo.bin"));
        NavlungoSettings? saved = null;
        string? loadError = null;
        try { saved = store.Load(); } catch { loadError = "Kayıtlı Navlungo ayarları okunamadı. Yeniden girip kaydedin."; }
        var status = new TextBlock { Text = loadError ?? NavlungoConnection.Describe(saved), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20), Foreground = Brushes.DarkOrange };
        panel.Children.Add(status);
        Text("Navlungo uygulama kimliği (client_id)");
        var clientId = new TextBox { Text = saved?.ClientId ?? "", MinWidth = 450, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(clientId);
        Text("Başvuruda kayıtlı HTTPS dönüş adresi");
        var callback = new TextBox { Text = saved?.CallbackUri ?? "", Margin = new Thickness(0, 0, 0, 12) };
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
        Text("Yerel tracking görünümü", 20);
        Text("Navlungo read-only tracking sözleşmesi bu sürümde doğrulanmadı. Aşağıdaki kayıtlar yalnızca kullanıcı/kanal gözlemi olarak saklanır; kargo oluşturmaz ve Navlungo'ya istek göndermez.");
        var trackingStore = new NavlungoTrackingStore(directory); var trackingGrid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 220, EnableRowVirtualization = true };
        var trackingMarketplace = new TextBox { Width = 100, Text = "Etsy" }; var trackingShop = new TextBox { Width = 110, Text = "default" }; var trackingOrder = new TextBox { Width = 130 }; var trackingShipment = new TextBox { Width = 130 }; var trackingNumber = new TextBox { Width = 140 }; var trackingCarrier = new TextBox { Width = 120 }; var trackingState = new ComboBox { ItemsSource = new[] { "Preparing", "InTransit", "Delivered", "Exception" }, SelectedIndex = 0, Width = 120 }; var trackingStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 8, 0, 8) };
        var trackingRow = new WrapPanel(); foreach (var control in new Control[] { trackingMarketplace, trackingShop, trackingOrder, trackingShipment, trackingNumber, trackingCarrier, trackingState }) trackingRow.Children.Add(control);
        var saveTracking = new Button { Content = "Yerel tracking kaydet", Margin = new Thickness(3) }; trackingRow.Children.Add(saveTracking); panel.Children.Add(trackingRow); panel.Children.Add(trackingGrid); panel.Children.Add(trackingStatus);
        void RefreshTracking() { var rows = trackingStore.List(); trackingGrid.ItemsSource = rows; trackingStatus.Text = $"{rows.Count} yerel tracking kaydı · LIVE_API_BLOCKED dış sorgu yok."; }
        saveTracking.Click += (_, _) => { try { trackingStore.Save(new("", trackingMarketplace.Text, trackingShop.Text, trackingOrder.Text, trackingShipment.Text, trackingNumber.Text, trackingCarrier.Text, trackingState.SelectedItem?.ToString() ?? "Unknown", DateTime.UtcNow, "Yerel / manuel", "")); RefreshTracking(); } catch (Exception error) { trackingStatus.Text = MarketplaceConnectionStore.Redact(error.Message); } };
        RefreshTracking();
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
