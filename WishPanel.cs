using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class WishPanel
{
    public static FrameworkElement Create(string? directory = null)
    {
        var store = new WishSettingsStore(directory is null ? null : System.IO.Path.Combine(directory, "wish.bin")); var root = new StackPanel { Margin = new Thickness(18), MaxWidth = 820 };
        root.Children.Add(new TextBlock { Text = "Wish bağlantısı", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) }); root.Children.Add(new TextBlock { Text = "Merchant ID ve API key DPAPI ile şifrelenir. Güncel Wish API sözleşmesi doğrulanmadan canlı veri değişmez.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 0, 0, 12) });
        var merchant = Field(root, "Merchant ID"); var key = Password(root, "API key"); var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 8, 0, 8) }; root.Children.Add(state);
        WishSettings Read() => new(merchant.Text.Trim(), key.Password);
        root.Children.Add(Button("Şifreli kaydet", () => { var settings = Read(); WishConnection.Validate(settings); store.Save(settings); state.Text = "Wish ayarları DPAPI ile kaydedildi. Canlı API durumu: LIVE_API_BLOCKED."; })); root.Children.Add(AsyncButton("Salt okunur bağlantı testi", async () => await new WishConnection().TestReadOnlyAsync(Read()))); root.Children.Add(Button("Yerel ayarı sil", () => { store.Delete(); key.Clear(); state.Text = "Wish ayarı silindi."; }));
        try { var saved = store.Load(); if (saved is not null) { merchant.Text = saved.MerchantId; state.Text = WishConnection.Describe(saved); } else state.Text = "NOT_CONFIGURED: Wish ayarı yok."; } catch (Exception e) { state.Text = MarketplaceConnectionStore.Redact(e.Message); }
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    static TextBox Field(Panel p, string label) { var b = new TextBox { Width = 420 }; Add(p, label, b); return b; } static PasswordBox Password(Panel p, string label) { var b = new PasswordBox { Width = 420 }; Add(p, label, b); return b; } static void Add(Panel p, string label, UIElement b) { p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) }); p.Children.Add(b); }
    static Button Button(string text, Action action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = DesignTokens.CompactButtonPadding }; b.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Wish", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return b; }
    static Button AsyncButton(string text, Func<Task> action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = DesignTokens.CompactButtonPadding }; b.Click += async (_, _) => { try { b.IsEnabled = false; await action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Wish", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { b.IsEnabled = true; } }; return b; }
}
