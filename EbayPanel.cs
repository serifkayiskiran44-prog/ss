using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class EbayPanel
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    /// <param name="directory">The data directory the encrypted store lives in (null: the profile default); the shell passes its own.</param>
    /// <param name="editState">#854: the app's settings edit state; the secret is tracked by presence only.</param>
    public static FrameworkElement Create(string? directory = null, SettingsEditState? editState = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("Kendi eBay geliştirici uygulamanızla OAuth bağlantısı. Ürün, stok ve sipariş aktarımı etkin değildir."));
        panel.Children.Add(Text("App ID / Cert ID ve OAuth RuName, eBay Developers hesabından alınır. Kabul adresi RuName altında kayıtlı, size ait HTTPS sayfasıyla birebir eşleşmelidir; sorgu veya # içermemelidir. Onaydan sonra tarayıcıdaki tam dönüş adresini aşağıya yapıştırın."));
        var inputs = new StackPanel();
        var clientId = Field(inputs, "App ID (Client ID)");
        // #855: the secret standard -- masked, paste-cleaned, never copied, presence only for a saved value, kept when left empty.
        var secret = SecretField.Build("Cert ID (Client Secret)", "Şifreli saklanır; ekranda, kayıtlarda ve dışa aktarımlarda gösterilmez."); inputs.Children.Add(secret.Field.Root);
        var ruName = Field(inputs, "OAuth RuName (URL değil)");
        var callback = Field(inputs, "RuName altında kayıtlı kabul URL'si (HTTPS)");
        var sandbox = new CheckBox { Content = "Sandbox test ortamı (gerçek satıcı hesabından ayrı)", Margin = new Thickness(0, 4, 0, 12) };
        inputs.Children.Add(sandbox);
        panel.Children.Add(inputs);
        var actions = new WrapPanel();
        var save = Button(actions, "Ayarları güvenli kaydet");
        var authorize = Button(actions, "eBay OAuth onayını aç");
        var verify = Button(actions, "Bağlantı / satıcı durumunu doğrula");
        var disconnect = Button(actions, "Yerel bağlantıyı sil");
        panel.Children.Add(actions);
        panel.Children.Add(Text("Tam dönüş URL'si (kod ve state içerir; paylaşmayın; işlemden sonra temizlenir)"));
        var returned = new PasswordBox { Margin = Spacing.BelowControl };
        panel.Children.Add(returned);
        var complete = new Button { Content = "Dönüşü doğrula ve token al", HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(complete);
        var status = Text("Bağlı değil — geliştirici anahtarları ve OAuth onayı gerekiyor.");
        status.Foreground = new SolidColorBrush(DesignTokens.WarningTextColor); status.Tag = "ebay-status";
        panel.Children.Add(status);
        var store = new EbaySettingsStore(directory is null ? null : System.IO.Path.Combine(directory, "ebay.bin"));
        var api = new EbayConnection(Http);
        EbaySavedConnection? saved = null;
        EbayAuthorization? attempt = null;
        bool busy = false;
        try {
            saved = store.Load();
            if (saved is not null) {
                clientId.Text = saved.Settings.ClientId; secret.SetSaved(!string.IsNullOrEmpty(saved.Settings.ClientSecret)); ruName.Text = saved.Settings.RuName;
                callback.Text = saved.Settings.CallbackUrl; sandbox.IsChecked = saved.Settings.Sandbox;
                status.Text = saved.Tokens is null ? "Ayarlar kayıtlı; OAuth onayı yok." : "OAuth bilgileri kayıtlı; bu oturumda bağlantı henüz doğrulanmadı.";
            }
        } catch { status.Text = "Kayıtlı eBay bilgileri okunamadı. Bilgileri yeniden girip kaydedin."; }
        var tracker = editState?.Form("ebay-connection").Track("App ID", () => clientId.Text).Track("Cert ID", () => secret.Box.Password, secret: true).Track("RuName", () => ruName.Text).Track("Kabul URL'si", () => callback.Text).Track("Sandbox", () => sandbox.IsChecked == true ? "1" : "0"); tracker?.Snapshot();
        EbaySettings Read() => new(clientId.Text.Trim(), secret.Resolve(saved?.Settings.ClientSecret), ruName.Text.Trim(), callback.Text.Trim(), sandbox.IsChecked == true);
        void Changed() { attempt = null; returned.Clear(); status.Text = "Ayarlar değişti; bağlantı doğrulanmadı. Kaydedin ve gerekirse yeniden OAuth onayı alın."; tracker?.Recompute(); }
        clientId.TextChanged += (_, _) => Changed(); secret.Box.PasswordChanged += (_, _) => Changed(); ruName.TextChanged += (_, _) => Changed(); callback.TextChanged += (_, _) => Changed();
        sandbox.Checked += (_, _) => Changed(); sandbox.Unchecked += (_, _) => Changed();
        async Task Run(Func<Task> action)
        {
            if (busy) return;
            busy = true; inputs.IsEnabled = actions.IsEnabled = complete.IsEnabled = returned.IsEnabled = false;
            status.Text = "İşlem sürüyor; bağlantı henüz doğrulanmadı.";
            try { await action(); }
            catch (ArgumentException) { status.Text = "App ID, Cert ID, RuName ve sorgusuz HTTPS kabul adresini kontrol edin."; }
            catch (InvalidOperationException ex) { status.Text = ex.Message; }
            catch { status.Text = "eBay işlemi tamamlanamadı. İnternet bağlantısını ve Windows güvenli kayıt erişimini kontrol edin. Gerekirse OAuth işlemini yeniden başlatın."; }
            finally { busy = false; inputs.IsEnabled = actions.IsEnabled = complete.IsEnabled = returned.IsEnabled = true; }
        }
        EbaySavedConnection SaveCurrent()
        {
            var current = Read(); EbayConnection.Validate(current);
            var next = new EbaySavedConnection(current, saved?.Settings == current ? saved.Tokens : null);
            store.Save(next); saved = next; secret.MarkSaved(); tracker?.Snapshot(); editState?.NotifySaved("ebay-connection"); return next;
        }
        save.Click += async (_, _) => await Run(() => { SaveCurrent(); status.Text = "Ayarlar Windows kullanıcı profilinde şifreli kaydedildi; bağlantı doğrulanmadı."; return Task.CompletedTask; });
        authorize.Click += async (_, _) => await Run(() => {
            var current = SaveCurrent(); attempt = EbayConnection.Begin(current.Settings);
            Process.Start(new ProcessStartInfo(attempt.AuthorizeUrl) { UseShellExecute = true });
            status.Text = "eBay onayından sonra tam dönüş URL'sini yapıştırın. İstek 10 dakika ve tek kullanım için geçerlidir.";
            return Task.CompletedTask;
        });
        complete.Click += async (_, _) => await Run(async () => {
            var currentAttempt = attempt ?? throw new InvalidOperationException("Önce eBay OAuth onayını bu panelden başlatın.");
            var value = returned.Password; returned.Clear();
            // Once an exchange is attempted, restart authorization after any failure.
            attempt = null;
            var tokens = await api.CompleteAsync(currentAttempt, value);
            var next = new EbaySavedConnection(currentAttempt.Settings, tokens);
            store.Save(next); saved = next;
            status.Text = "OAuth token alındı ve şifreli kaydedildi. Bağlantı / satıcı durumu doğrulamasını çalıştırın.";
        });
        verify.Click += async (_, _) => await Run(async () => {
            var current = Read();
            if (saved?.Settings != current || saved.Tokens is null) throw new InvalidOperationException("Bu ayarlar için OAuth token yok. Kaydedip eBay onayını tamamlayın.");
            var tokens = saved.Tokens;
            if (tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1)) {
                tokens = await api.RefreshAsync(current, tokens);
                var next = new EbaySavedConnection(current, tokens); store.Save(next); saved = next;
            }
            var registered = await api.VerifyAsync(current, tokens);
            status.Text = $"{(current.Sandbox ? "SANDBOX" : "PRODUCTION")} API bağlantısı {DateTime.Now:HH:mm:ss} itibarıyla doğrulandı. "
                + (registered ? "eBay satıcı kaydı tamamlanmış bildiriliyor." : "eBay satıcı kaydı tamamlanmamış bildiriliyor; satıcı panelindeki adımları tamamlayın.")
                + " Bu kontrol ödeme/Payoneer kurulumunu veya ürün yayınlama uygunluğunu doğrulamaz.";
        });
        disconnect.Click += async (_, _) => await Run(() => {
            store.Delete(); saved = null; attempt = null; returned.Clear(); secret.MarkCleared(); tracker?.Snapshot(); editState?.NotifySaved("ebay-connection");
            status.Text = "Yerel eBay bilgileri silindi. eBay tarafındaki uygulama iznini kaldırmak için hesap ayarlarınızı kullanın.";
            return Task.CompletedTask;
        });
        return panel;
    }
    static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Margin = Spacing.BelowControl, Foreground = Brushes.DarkSlateGray };
    static TextBox Field(Panel panel, string label) { panel.Children.Add(Text(label)); var box = new TextBox { Margin = Spacing.BelowControl }; panel.Children.Add(box); return box; }
    static Button Button(Panel panel, string label) { var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) }; panel.Children.Add(button); return button; }
}
