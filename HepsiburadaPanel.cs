using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class HepsiburadaPanel
{
    public static FrameworkElement Create(string? directory = null)
    {
        var store = new HepsiburadaSettingsStore(directory is null ? null : System.IO.Path.Combine(directory, "hepsiburada.bin"));
        var root = new StackPanel { Margin = new Thickness(18), MaxWidth = 820 };
        root.Children.Add(new TextBlock { Text = "Hepsiburada bağlantısı", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(new TextBlock { Text = "Merchant ve API bilgileri DPAPI ile şifrelenir. Resmi sözleşme doğrulanmadan ürün, stok, fiyat veya sipariş isteği oluşturulmaz.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 0, 0, 12) });
        var merchant = Field(root, "Merchant ID"); var username = Field(root, "API kullanıcı adı"); var password = Password(root, "API şifresi"); var agent = Field(root, "User-Agent");
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 8, 0, 8) }; root.Children.Add(state);
        HepsiburadaSettings Read() => new(merchant.Text.Trim(), username.Text.Trim(), password.Password, agent.Text.Trim());
        root.Children.Add(Button("Şifreli kaydet", () => { var settings = Read(); HepsiburadaConnection.Validate(settings); store.Save(settings); state.Text = "Hepsiburada ayarları DPAPI ile kaydedildi. Canlı API durumu: LIVE_API_BLOCKED."; }));
        root.Children.Add(AsyncButton("Salt okunur bağlantı testi", async () => await new HepsiburadaConnection().TestReadOnlyAsync(Read())));
        try { var saved = store.Load(); if (saved is not null) { merchant.Text = saved.MerchantId; username.Text = saved.Username; agent.Text = saved.UserAgent; state.Text = HepsiburadaConnection.Describe(saved); } else state.Text = "NOT_CONFIGURED: Hepsiburada ayarı yok."; } catch (Exception e) { state.Text = MarketplaceConnectionStore.Redact(e.Message); }
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    static TextBox Field(Panel panel, string label) { var box = new TextBox { Width = 420 }; AddLabel(panel, label, box); return box; }
    static PasswordBox Password(Panel panel, string label) { var box = new PasswordBox { Width = 420 }; AddLabel(panel, label, box); return box; }
    static void AddLabel(Panel panel, string label, UIElement control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) }); panel.Children.Add(control); }
    static Button Button(string text, Action action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 5, 10, 5) }; b.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Hepsiburada", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return b; }
    static Button AsyncButton(string text, Func<Task> action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 5, 10, 5) }; b.Click += async (_, _) => { try { b.IsEnabled = false; await action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Hepsiburada", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { b.IsEnabled = true; } }; return b; }
}
