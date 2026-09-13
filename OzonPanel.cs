using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class OzonPanel
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    /// <param name="directory">The data directory the encrypted store lives in (null: the profile default); the shell passes its own.</param>
    /// <param name="editState">#854: the app's settings edit state; the key is tracked by presence only.</param>
    public static FrameworkElement Create(string? directory = null, SettingsEditState? editState = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("Ozon Seller API bağlantısı — yalnızca ürün sayısı ve FBS/rFBS depo listesi okuma kontrolü. Ürün yayınlama, stok/fiyat ve sipariş aktarımı henüz uygulanmadı."));
        panel.Children.Add(Text("Seller hesabı → Ayarlar → Seller API bölümünden Client ID ve API key alın. Ürün kontrolü için Product read-only, depo kontrolü için Warehouse erişimi gerekir. Anahtarı aşağıdaki gizli alana yapıştırın."));
        var inputs = new StackPanel();
        inputs.Children.Add(Text("Client ID"));
        var clientId = new TextBox { Margin = new Thickness(0,0,0,8) }; inputs.Children.Add(clientId);
        // #855: the secret standard -- masked, paste-cleaned, never copied, presence only for a saved value, kept when left empty.
        var key = SecretField.Build("API key", "Seller API anahtarı; şifreli saklanır, ekranda ve kayıtlarda gösterilmez."); inputs.Children.Add(key.Field.Root); panel.Children.Add(inputs);
        var actions = new WrapPanel();
        var save = Button(actions, "Ayarları güvenli kaydet");
        var products = Button(actions, "Ürün okuma erişimini doğrula");
        var warehouses = Button(actions, "Depo okuma erişimini doğrula");
        var delete = Button(actions, "Yerel bağlantıyı sil"); panel.Children.Add(actions);
        var status = Text("Bağlantı doğrulanmadı. Client ID ve API key girin."); status.Tag = "ozon-status"; panel.Children.Add(status);
        var productStatus = Text("Ürün erişimi: bu oturumda doğrulanmadı."); panel.Children.Add(productStatus);
        var warehouseStatus = Text("Depo erişimi: bu oturumda doğrulanmadı."); panel.Children.Add(warehouseStatus);
        var store = new OzonSettingsStore(directory is null ? null : System.IO.Path.Combine(directory, "ozon.bin")); var api = new OzonConnection(Http); bool busy = false;
        DateTimeOffset lastWarehouseAttempt = DateTimeOffset.MinValue; OzonSettings? saved = null;
        try {
            saved = store.Load();
            if (saved is not null) { clientId.Text = saved.ClientId; key.SetSaved(true); status.Text = "Ayarlar şifreli kayıttan yüklendi; API erişimi bu oturumda doğrulanmadı."; }
        } catch { status.Text = "Kayıtlı Ozon bilgileri okunamadı. Client ID ve API key yeniden girilmeli."; }
        var tracker = editState?.Form("ozon-connection").Track("Client ID", () => clientId.Text).Track("API key", () => key.Box.Password, secret: true); tracker?.Snapshot();
        void Changed() {
            status.Text = "Ayarlar değişti; henüz kaydedilmedi veya doğrulanmadı.";
            productStatus.Text = "Ürün erişimi: doğrulanmadı."; warehouseStatus.Text = "Depo erişimi: doğrulanmadı."; tracker?.Recompute();
        }
        clientId.TextChanged += (_,_) => Changed(); key.Box.PasswordChanged += (_,_) => Changed();
        OzonSettings Read() => new(clientId.Text.Trim(), key.Resolve(saved?.ApiKey).Trim());
        async Task Run(Func<Task> action)
        {
            if (busy) return; busy = true; inputs.IsEnabled = actions.IsEnabled = false;
            status.Text = "İşlem sürüyor…";
            try { await action(); }
            catch (ArgumentException) { status.Text = "Sayısal Client ID ve geçerli API key girin."; }
            catch (InvalidOperationException error) { status.Text = error.Message; }
            catch (OperationCanceledException) { status.Text = "Ozon isteği zaman aşımına uğradı; erişim doğrulanamadı."; }
            catch { status.Text = "İşlem tamamlanamadı. İnternet bağlantısını ve Windows güvenli kayıt erişimini kontrol edin."; }
            finally { busy = false; inputs.IsEnabled = actions.IsEnabled = true; }
        }
        save.Click += async (_,_) => await Run(() => {
            var settings = Read(); store.Save(settings); saved = settings; key.MarkSaved(); tracker?.Snapshot();
            status.Text = "Ayarlar Windows kullanıcı profilinde şifreli kaydedildi. Kaydetme API erişimini doğrulamaz."; return Task.CompletedTask;
        });
        products.Click += async (_,_) => await Run(async () => {
            productStatus.Text = "Ürün erişimi: doğrulanmadı; kontrol sürüyor.";
            var count = await api.ReadProductCountAsync(Read());
            productStatus.Text = $"Ürün okuma erişimi {DateTime.Now:HH:mm:ss} itibarıyla doğrulandı. Ozon toplam ürün sayısı: {count}.";
            status.Text = "Bu kontrol yalnızca ürün okuma yetkisini doğrular. Satışa uygunluk veya ürün yayınlama onayı değildir.";
        });
        warehouses.Click += async (_,_) => await Run(async () => {
            if (DateTimeOffset.UtcNow - lastWarehouseAttempt < TimeSpan.FromMinutes(1))
                throw new InvalidOperationException("Depo sorguları arasında en az bir dakika bekleyin.");
            var settings = Read(); OzonConnection.Validate(settings);
            warehouseStatus.Text = "Depo erişimi: doğrulanmadı; kontrol sürüyor."; lastWarehouseAttempt = DateTimeOffset.UtcNow;
            var count = await api.ReadWarehouseCountAsync(settings);
            warehouseStatus.Text = $"Depo okuma erişimi {DateTime.Now:HH:mm:ss} itibarıyla doğrulandı. Dönen FBS/rFBS depo sayısı: {count}.";
            status.Text = count == 0 ? "API erişimi var; listede FBS/rFBS deposu yok. Seller panelindeki depo ve teslimat kurulumunu tamamlayın." : "Depo listesi okundu. Depoların sevkiyata hazır olması bu kontrolle doğrulanmaz.";
        });
        delete.Click += async (_,_) => await Run(() => {
            store.Delete(); saved = null; key.MarkCleared(); tracker?.Snapshot(); status.Text = "Yerel Ozon anahtarı silindi. Ozon tarafındaki anahtarı kaldırmak için Seller API ayarlarını kullanın."; return Task.CompletedTask;
        });
        return panel;
    }
    static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,8), Foreground = Brushes.DarkSlateGray };
    static Button Button(Panel panel, string label) { var button = new Button { Content = label, Margin = new Thickness(0,0,8,8) }; panel.Children.Add(button); return button; }
}
