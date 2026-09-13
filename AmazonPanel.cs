using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class AmazonPanel
{
    public static FrameworkElement Create(string? directory = null)
    {
        var store = new AmazonSettingsStore(directory is null ? null : System.IO.Path.Combine(directory, "amazon.bin"));
        var root = new StackPanel { Margin = new Thickness(18), MaxWidth = 820 };
        root.Children.Add(new TextBlock { Text = "Amazon SP-API bağlantısı", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(new TextBlock { Text = "Kimlik bilgileri Windows kullanıcı profiline DPAPI ile şifrelenir. Resmi bölge sözleşmesi doğrulanmadan canlı HTTP isteği yapılmaz.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 0, 0, 12) });
        var seller = Field(root, "Seller ID"); var client = Field(root, "LWA Client ID"); var secret = Password(root, "LWA Client Secret"); var refresh = Password(root, "Refresh Token");
        var region = new ComboBox { ItemsSource = new[] { "NA", "EU", "FE" }, SelectedIndex = 1, Width = 150 }; AddLabel(root, "SP-API bölgesi", region);
        var marketplace = Field(root, "Marketplace ID"); var sandbox = new CheckBox { Content = "Sandbox", Margin = new Thickness(0, 6, 0, 6) }; root.Children.Add(sandbox);
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 8, 0, 8) }; root.Children.Add(state);
        AmazonSettings Read() => new(seller.Text.Trim(), client.Text.Trim(), secret.Password, refresh.Password, region.SelectedItem?.ToString() ?? "EU", marketplace.Text.Trim(), sandbox.IsChecked == true);
        root.Children.Add(Button("Şifreli kaydet", () => { var settings = Read(); AmazonConnection.Validate(settings); store.Save(settings); state.Text = "Amazon ayarları DPAPI ile şifreli kaydedildi. Canlı API durumu: LIVE_API_BLOCKED."; }));
        root.Children.Add(AsyncButton("Salt okunur bağlantı testi", async () => { var settings = Read(); await new AmazonConnection().TestReadOnlyAsync(settings); }));
        try { var saved = store.Load(); if (saved is not null) { seller.Text = saved.SellerId; client.Text = saved.ClientId; region.SelectedItem = saved.Region; marketplace.Text = saved.MarketplaceId; sandbox.IsChecked = saved.Sandbox; state.Text = AmazonConnection.Describe(saved); } else state.Text = "NOT_CONFIGURED: Amazon ayarı yok."; } catch (Exception e) { state.Text = MarketplaceConnectionStore.Redact(e.Message); }
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    static TextBox Field(Panel panel, string label) { var box = new TextBox { Width = 420 }; AddLabel(panel, label, box); return box; }
    static PasswordBox Password(Panel panel, string label) { var box = new PasswordBox { Width = 420 }; AddLabel(panel, label, box); return box; }
    static void AddLabel(Panel panel, string label, UIElement control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) }); panel.Children.Add(control); }
    static Button Button(string text, Action action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = DesignTokens.CompactButtonPadding }; b.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Amazon", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return b; }
    static Button AsyncButton(string text, Func<Task> action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = DesignTokens.CompactButtonPadding }; b.Click += async (_, _) => { try { b.IsEnabled = false; await action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Amazon", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { b.IsEnabled = true; } }; return b; }
}
