using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class TrendyolPanel
{
    /// <param name="editState">#854: the app's settings edit state; the secret is tracked by presence only.</param>
    public static FrameworkElement Create(string? directory = null, SettingsEditState? editState = null)
    {
        var store = new TrendyolSettingsStore(directory is null ? null : System.IO.Path.Combine(directory, "trendyol.bin"));
        var root = new StackPanel { Margin = new Thickness(18), MaxWidth = 820 };
        root.Children.Add(new TextBlock { Text = "Trendyol bağlantısı", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        root.Children.Add(new TextBlock { Text = "API bilgileri DPAPI ile şifrelenir. Resmi sözleşme doğrulanmadan ürün, stok, fiyat veya sipariş isteği oluşturulmaz.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 0, 0, 12) });
        var supplier = Field(root, "Supplier ID"); var key = Field(root, "API key");
        // #855: the secret standard -- masked, paste-cleaned, never copied, presence only for a saved value, kept when left empty.
        var secret = SecretField.Build("API secret", "Şifreli saklanır; ekranda, kayıtlarda ve dışa aktarımlarda gösterilmez."); root.Children.Add(secret.Field.Root);
        var agent = Field(root, "User-Agent");
        var state = new TextBlock { Tag = "trendyol-status", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 8, 0, 8) }; root.Children.Add(state);
        TrendyolSettings? saved = null;
        // #854: unsaved fields by label; the secret contributes presence, never its value.
        var tracker = editState?.Form("trendyol-connection").Track("Supplier ID", () => supplier.Text).Track("API key", () => key.Text).Track("API secret", () => secret.Box.Password, secret: true).Track("User-Agent", () => agent.Text);
        foreach (var box in new[] { supplier, key, agent }) box.TextChanged += (_, _) => tracker?.Recompute();
        secret.Box.PasswordChanged += (_, _) => tracker?.Recompute();
        TrendyolSettings Read() => new(supplier.Text.Trim(), key.Text.Trim(), secret.Resolve(saved?.ApiSecret), agent.Text.Trim());
        // Errors land in the status line (redacted), never in a modal; a validation failure keeps what was typed.
        Button Button(string text, Action action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = DesignTokens.CompactButtonPadding }; b.Click += (_, _) => { try { action(); } catch (Exception e) { state.Text = MarketplaceConnectionStore.Redact(e.Message); } }; return b; }
        Button AsyncButton(string text, Func<Task> action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = DesignTokens.CompactButtonPadding }; b.Click += async (_, _) => { try { b.IsEnabled = false; await action(); } catch (Exception e) { state.Text = MarketplaceConnectionStore.Redact(e.Message); } finally { b.IsEnabled = true; } }; return b; }
        root.Children.Add(Button("Şifreli kaydet", () =>
        {
            var settings = Read();
            // Worded without the literal "secret": the redaction pass would blank that word in the slot and the status line.
            if (!secret.HasTyped && !secret.HasSaved) { secret.Field.SetValidation("Gizli değer gerekli."); throw new InvalidOperationException("Gizli değer gerekli; kaydetmek için yeni değeri yazın."); }
            TrendyolConnection.Validate(settings); store.Save(settings); saved = settings; secret.MarkSaved(); secret.Field.SetValidation("");
            state.Text = "Trendyol ayarları DPAPI ile kaydedildi. Canlı API durumu: LIVE_API_BLOCKED."; tracker?.Snapshot(); editState?.NotifySaved("trendyol-connection");
        }));
        root.Children.Add(AsyncButton("Salt okunur bağlantı testi", async () => await new TrendyolConnection().TestReadOnlyAsync(Read())));
        try { saved = store.Load(); if (saved is not null) { supplier.Text = saved.SupplierId; key.Text = saved.ApiKey; agent.Text = saved.UserAgent; secret.SetSaved(true); state.Text = TrendyolConnection.Describe(saved); } else { secret.SetSaved(false); state.Text = "NOT_CONFIGURED: Trendyol ayarı yok."; } } catch (Exception e) { secret.SetSaved(false); state.Text = MarketplaceConnectionStore.Redact(e.Message); }
        tracker?.Snapshot();
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    static TextBox Field(Panel panel, string label) { var box = new TextBox { Width = 420 }; AddLabel(panel, label, box); return box; }
    static void AddLabel(Panel panel, string label, UIElement control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) }); panel.Children.Add(control); }
}
