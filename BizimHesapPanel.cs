using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public static class BizimHesapPanel
{
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public static FrameworkElement Create(string? directory = null)
    {
        var root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14) };
        var panel = new StackPanel(); root.Content = panel;
        panel.Children.Add(Text("BizimHesap bağlantısı", 20));
        panel.Children.Add(Text("XML ile oluşan merkez ürün havuzunu BizimHesap ürünleri ve depolarıyla karşılaştırmak için bağlantıyı ayarlayın. Token Windows kullanıcı profilinde şifreli saklanır."));
        var form = new Grid { Margin = new Thickness(0, 8, 0, 8) }; form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); form.ColumnDefinitions.Add(new ColumnDefinition());
        var firm = new TextBox { MinWidth = 260 }; var token = new PasswordBox { MinWidth = 260 };
        Add(form, "FirmID", firm, 0); Add(form, "API Token", token, 1); panel.Children.Add(form);
        var actions = new WrapPanel(); var save = Button(actions, "Ayarları güvenli kaydet"); var products = Button(actions, "Ürünleri oku"); var warehouses = Button(actions, "Depoları oku"); var clear = Button(actions, "Yerel bağlantıyı sil"); panel.Children.Add(actions);
        var status = Text("Bağlantı ayarları kaydedilmedi."); panel.Children.Add(status);
        var productGrid = Table(); var warehouseGrid = Table();
        panel.Children.Add(Group("BizimHesap ürünleri", productGrid)); panel.Children.Add(Group("Depolar", warehouseGrid));
        var mappingActions = new WrapPanel(); var preview = Button(mappingActions, "Ürün eşleştirme önizlemesi oluştur"); panel.Children.Add(mappingActions);
        var mappingGrid = Table(); panel.Children.Add(Group("Ürün eşleştirme önizlemesi", mappingGrid));
        panel.Children.Add(Group("Stok / fiyat", Text("Stok / fiyat güncellemesi API doğrulaması bekliyor. Bu ekran hiçbir stok veya fiyat yazma isteği göndermez.")));

        var store = new BizimHesapSettingsStore(directory is null ? null : Path.Combine(directory, "bizimhesap.bin")); var api = new BizimHesapConnection(Http); var busy = false; var remoteProducts = Array.Empty<BizimHesapProduct>();
        try { var existing = store.Load(); if (existing is not null) { firm.Text = existing.FirmId; token.Password = existing.Token; status.Text = "Şifreli ayarlar yüklendi; erişim henüz doğrulanmadı."; } } catch { status.Text = "Kayıtlı BizimHesap ayarları okunamadı; bilgileri yeniden girin."; }
        BizimHesapSettings Settings() => new(firm.Text.Trim(), token.Password.Trim());
        async Task Run(Func<Task> action)
        {
            if (busy) return; busy = true; form.IsEnabled = actions.IsEnabled = false;
            try { await action(); } catch (ArgumentException e) { status.Text = e.Message; } catch (InvalidOperationException e) { status.Text = MarketplaceConnectionStore.Redact(e.Message); } catch (OperationCanceledException) { status.Text = "İstek zaman aşımına uğradı; erişim doğrulanamadı."; } catch { status.Text = "İşlem tamamlanamadı. İnternet bağlantısını ve ayarları kontrol edin."; } finally { busy = false; form.IsEnabled = actions.IsEnabled = true; }
        }
        save.Click += async (_, _) => await Run(() => { store.Save(Settings()); status.Text = "Ayarlar Windows kullanıcı profilinde şifreli kaydedildi. Kaydetmek erişimi doğrulamaz."; return Task.CompletedTask; });
        products.Click += async (_, _) => await Run(async () => { remoteProducts = (await api.ReadProductsAsync(Settings())).ToArray(); productGrid.ItemsSource = remoteProducts; status.Text = $"{remoteProducts.Length:N0} BizimHesap ürünü okundu. SKU/barkod eşleştirme önizlemesi hazır."; });
        warehouses.Click += async (_, _) => await Run(async () => { var rows = await api.ReadWarehousesAsync(Settings()); warehouseGrid.ItemsSource = rows; status.Text = $"{rows.Count:N0} depo okundu. Depo seçimi stok yazma yetkisi vermez."; });
        clear.Click += async (_, _) => await Run(() => { store.Delete(); token.Clear(); status.Text = "Yerel şifreli BizimHesap bilgileri silindi."; return Task.CompletedTask; });
        preview.Click += (_, _) =>
        {
            if (remoteProducts.Length == 0) { status.Text = "Önce BizimHesap ürünlerini okuyun."; return; }
            var local = new CatalogStore(directory).Products();
            mappingGrid.ItemsSource = local.Select(product =>
            {
                var barcode = remoteProducts.Where(remote => product.Barcode.Length > 0 && remote.Barcode == product.Barcode).ToArray();
                var sku = remoteProducts.Where(remote => product.Sku.Length > 0 && remote.Code == product.Sku).ToArray();
                var match = barcode.Length == 1 ? barcode[0] : barcode.Length == 0 && sku.Length == 1 ? sku[0] : null;
                return new { product.Sku, product.Barcode, product.Name, Durum = match is null ? (barcode.Length + sku.Length > 1 ? "ÇAKIŞMA" : "YENİ ÜRÜN ÖNİZLEMESİ") : "EŞLEŞTİ", BizimHesapUrunu = match?.Title ?? "—" };
            }).ToArray();
            status.Text = $"{local.Count:N0} yerel ürün için salt-okunur eşleştirme önizlemesi oluşturuldu. Yeni ürün gönderimi henüz açık değil.";
        };
        return root;
    }

    static TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 0, 0, 8) };
    static void Add(Grid grid, string label, UIElement input, int row) { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var text = new TextBlock { Text = label, Margin = new Thickness(0, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(text, row); grid.Children.Add(text); Grid.SetColumn(input, 1); Grid.SetRow(input, row); grid.Children.Add(input); }
    static Button Button(Panel panel, string label) { var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 5, 10, 5) }; panel.Children.Add(button); return button; }
    static DataGrid Table() { var grid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 180, EnableRowVirtualization = true }; return grid; }
    static GroupBox Group(string title, object content) => new() { Header = title, Content = content, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) };
}
