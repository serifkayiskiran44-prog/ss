using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class TrendyolPanel
{
    public static FrameworkElement Create(string? directory = null)
    {
        var store = new TrendyolSettingsStore(directory is null ? null : System.IO.Path.Combine(directory, "trendyol.bin"));
        var root = new StackPanel { Margin = new Thickness(18), MaxWidth = 820 };
        root.Children.Add(new TextBlock { Text = "Trendyol bağlantısı", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(new TextBlock { Text = "API bilgileri DPAPI ile şifrelenir. Resmi sözleşme doğrulanmadan ürün, stok, fiyat veya sipariş isteği oluşturulmaz.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 0, 0, 12) });
        var supplier = Field(root, "Supplier ID"); var key = Field(root, "API key"); var secret = Password(root, "API secret"); var agent = Field(root, "User-Agent");
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 8, 0, 8) }; root.Children.Add(state);
        TrendyolSettings Read() => new(supplier.Text.Trim(), key.Text.Trim(), secret.Password, agent.Text.Trim());
        root.Children.Add(Button("Şifreli kaydet", () => { var settings = Read(); TrendyolConnection.Validate(settings); store.Save(settings); state.Text = "Trendyol ayarları DPAPI ile kaydedildi. Canlı API durumu: LIVE_API_BLOCKED."; }));
        root.Children.Add(AsyncButton("Salt okunur bağlantı testi", async () => await new TrendyolConnection().TestReadOnlyAsync(Read())));
        try { var saved = store.Load(); if (saved is not null) { supplier.Text = saved.SupplierId; key.Text = saved.ApiKey; agent.Text = saved.UserAgent; state.Text = TrendyolConnection.Describe(saved); } else state.Text = "NOT_CONFIGURED: Trendyol ayarı yok."; } catch (Exception e) { state.Text = MarketplaceConnectionStore.Redact(e.Message); }
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    static TextBox Field(Panel panel, string label) { var box = new TextBox { Width = 420 }; AddLabel(panel, label, box); return box; }
    static PasswordBox Password(Panel panel, string label) { var box = new PasswordBox { Width = 420 }; AddLabel(panel, label, box); return box; }
    static void AddLabel(Panel panel, string label, UIElement control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) }); panel.Children.Add(control); }
    static Button Button(string text, Action action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 5, 10, 5) }; b.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Trendyol", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return b; }
    static Button AsyncButton(string text, Func<Task> action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 5, 10, 5) }; b.Click += async (_, _) => { try { b.IsEnabled = false; await action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Trendyol", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { b.IsEnabled = true; } }; return b; }
}
