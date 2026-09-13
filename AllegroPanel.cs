using System.Windows;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class AllegroPanel
{
    public static FrameworkElement Create(string? directory = null)
    {
        var store = new AllegroSettingsStore(directory is null ? null : System.IO.Path.Combine(directory, "allegro.bin")); var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var root = new StackPanel { Margin = new Thickness(18), MaxWidth = 820 }; root.Children.Add(new TextBlock { Text = "Allegro OAuth bağlantısı", FontSize = DesignTokens.TextSectionTitleSize, FontWeight = DesignTokens.FontWeightTitle, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(new TextBlock { Text = "Allegro public API ürün ve sipariş okumaları bu panelden salt okunur doğrulanır. OAuth bilgileri DPAPI ile şifrelenir; canlı yazma/fulfillment yoktur.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 0, 0, 12) });
        var client = Field(root, "Client ID"); var secret = Password(root, "Client secret"); var redirect = Field(root, "Redirect URI (HTTPS)"); var token = Password(root, "Access token"); var refresh = Password(root, "Refresh token"); var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 8, 0, 8) }; root.Children.Add(state);
        AllegroSettings Read() => new(client.Text.Trim(), secret.Password, redirect.Text.Trim(), token.Password, refresh.Password, false);
        root.Children.Add(Button("Şifreli kaydet", () => { var settings = Read(); AllegroConnection.Validate(settings); store.Save(settings); state.Text = "Allegro ayarları DPAPI ile kaydedildi."; }));
        var test = AsyncButton("Salt okunur teklif testi", async () => state.Text = await new AllegroConnection(http).TestReadOnlyAsync(Read())); root.Children.Add(test);
        root.Children.Add(Button("Yerel ayarı sil", () => { store.Delete(); secret.Clear(); token.Clear(); refresh.Clear(); state.Text = "Allegro ayarı silindi."; }));
        try { var saved = store.Load(); if (saved is not null) { client.Text = saved.ClientId; redirect.Text = saved.RedirectUri; state.Text = "Ayarlar şifreli kayıttan yüklendi; read-only testi bekliyor."; } else state.Text = "NOT_CONFIGURED: Allegro ayarı yok."; } catch (Exception e) { state.Text = MarketplaceConnectionStore.Redact(e.Message); }
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    static TextBox Field(Panel p, string label) { var b = new TextBox { Width = 420 }; Add(p, label, b); return b; } static PasswordBox Password(Panel p, string label) { var b = new PasswordBox { Width = 420 }; Add(p, label, b); return b; } static void Add(Panel p, string label, UIElement b) { p.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) }); p.Children.Add(b); }
    static Button Button(string text, Action action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = DesignTokens.CompactButtonPadding }; b.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Allegro", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return b; }
    static Button AsyncButton(string text, Func<Task> action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = DesignTokens.CompactButtonPadding }; b.Click += async (_, _) => { try { b.IsEnabled = false; await action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Allegro", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { b.IsEnabled = true; } }; return b; }
}
