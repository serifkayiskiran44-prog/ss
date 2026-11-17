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
    sealed class SyncRow
    {
        public CatalogProduct Product { get; init; } = null!;
        public string ProductId => Product.Id;
        public string Sku => Product.Sku;
        public string Barcode => Product.Barcode;
        public string Name => Product.Name;
        public string Status { get; init; } = "";
        public string BizimHesapId { get; init; } = "—";
        public string BizimHesapProduct { get; init; } = "—";
    }

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
        var mappingActions = new WrapPanel(); var preview = Button(mappingActions, "Ürün eşleştirme önizlemesi oluştur"); var selectNew = Button(mappingActions, "İlk yeni ürünü seç"); var send = Button(mappingActions, "Seçili ürünü oluştur / fiyat güncelle"); panel.Children.Add(mappingActions);
        var mappingGrid = Table(); mappingGrid.SelectionMode = DataGridSelectionMode.Extended; panel.Children.Add(Group("Ürün eşleştirme önizlemesi", mappingGrid));
        panel.Children.Add(Group("Stok / fiyat", Text("Canlı doğrulamada AddProduct aynı ürün kodunda mevcut GUID'yi koruyarak fiyatı güncelledi. Fiyat KDV hariç gönderilir ve BizimHesap'tan KDV dahil okunur. Quantity alanı mevcut ürünün depo stoğunu güncellemediği için depo stoğu yazımı kapalıdır.")));

        var store = new BizimHesapSettingsStore(directory is null ? null : Path.Combine(directory, "bizimhesap.bin")); var api = new BizimHesapConnection(Http); var receipts = new BizimHesapCreateReceiptStore(directory); var busy = false; var remoteProductsLoaded = false; var remoteProducts = Array.Empty<BizimHesapProduct>();
        try { var existing = store.Load(); if (existing is not null) { firm.Text = existing.FirmId; token.Password = existing.Token; status.Text = "Şifreli ayarlar yüklendi; erişim henüz doğrulanmadı."; } } catch { status.Text = "Kayıtlı BizimHesap ayarları okunamadı; bilgileri yeniden girin."; }
        BizimHesapSettings Settings() => new(firm.Text.Trim(), token.Password.Trim());
        async Task Run(Func<Task> action)
        {
            if (busy) return; busy = true; form.IsEnabled = actions.IsEnabled = false;
            try { await action(); } catch (ArgumentException e) { status.Text = e.Message; } catch (InvalidOperationException e) { status.Text = MarketplaceConnectionStore.Redact(e.Message); } catch (OperationCanceledException) { status.Text = "İstek zaman aşımına uğradı; erişim doğrulanamadı."; } catch { status.Text = "İşlem tamamlanamadı. İnternet bağlantısını ve ayarları kontrol edin."; } finally { busy = false; form.IsEnabled = actions.IsEnabled = true; }
        }
        save.Click += async (_, _) => await Run(() => { store.Save(Settings()); status.Text = "Ayarlar Windows kullanıcı profilinde şifreli kaydedildi. Kaydetmek erişimi doğrulamaz."; return Task.CompletedTask; });
        products.Click += async (_, _) => await Run(async () => { remoteProducts = (await api.ReadAllProductsAsync(Settings())).ToArray(); remoteProductsLoaded = true; productGrid.ItemsSource = remoteProducts; status.Text = $"{remoteProducts.Length:N0} BizimHesap ürünü okundu. SKU/barkod eşleştirme önizlemesi hazır."; });
        warehouses.Click += async (_, _) => await Run(async () => { var rows = await api.ReadWarehousesAsync(Settings()); warehouseGrid.ItemsSource = rows; status.Text = $"{rows.Count:N0} depo okundu. Depo seçimi stok yazma yetkisi vermez."; });
        clear.Click += async (_, _) => await Run(() => { store.Delete(); token.Clear(); status.Text = "Yerel şifreli BizimHesap bilgileri silindi."; return Task.CompletedTask; });
        SyncRow[] BuildPreviewRows()
        {
            var local = new CatalogStore(directory).Products();
            var accountKey = BizimHesapCreateSafety.AccountKey(Settings());
            return local.Select(product =>
            {
                var payload = CreatePayload(product);
                var storedRemoteId = receipts.RemoteProductId(accountKey, product.Id);
                var readback = storedRemoteId.Length == 0
                    ? BizimHesapCreateSafety.Match(payload, remoteProducts)
                    : BizimHesapCreateSafety.MatchRemoteId(storedRemoteId, remoteProducts);
                return new SyncRow
                {
                    Product = product,
                    Status = readback.Status switch
                    {
                        BizimHesapReadbackStatus.Matched when BizimHesapCreateSafety.IsPriceCurrent(payload, readback.Product!) => "GÜNCEL",
                        BizimHesapReadbackStatus.Matched => "FİYAT GÜNCELLENECEK",
                        BizimHesapReadbackStatus.Missing => "YENİ ÜRÜN",
                        BizimHesapReadbackStatus.Ambiguous => "ÇAKIŞMA",
                        _ => "KİMLİK ÇATIŞMASI"
                    },
                    BizimHesapId = readback.Product?.Id ?? (storedRemoteId.Length == 0 ? "—" : storedRemoteId),
                    BizimHesapProduct = readback.Product?.Title ?? "—"
                };
            }).ToArray();
        }
        preview.Click += (_, _) =>
        {
            if (!remoteProductsLoaded) { status.Text = "Önce BizimHesap ürünlerini okuyun."; return; }
            var rows = BuildPreviewRows();
            mappingGrid.ItemsSource = rows;
            var localCount = rows.Length;
            status.Text = $"{localCount:N0} yerel ürün için eşleştirme önizlemesi oluşturuldu. YENİ ÜRÜN ve FİYAT GÜNCELLENECEK satırları gönderilebilir.";
        };
        selectNew.Click += (_, _) =>
        {
            mappingGrid.UnselectAll();
            var row = mappingGrid.Items.OfType<SyncRow>().FirstOrDefault(x => x.Status is "YENİ ÜRÜN" or "FİYAT GÜNCELLENECEK");
            if (row is null) { status.Text = "Oluşturulacak veya fiyatı güncellenecek ürün bulunamadı."; return; }
            mappingGrid.SelectedItems.Add(row);
            mappingGrid.ScrollIntoView(row);
            status.Text = $"{row.Sku} deneme gönderimi için seçildi. Göndermeden önce ürün özeti gösterilecek.";
        };
        send.Click += async (_, _) => await Run(async () =>
        {
            var selected = mappingGrid.SelectedItems.OfType<SyncRow>().ToArray();
            if (selected.Length != 1) throw new InvalidOperationException("Canlı işlem için yalnız bir ürün seçin.");
            if (selected[0].Status is not ("YENİ ÜRÜN" or "FİYAT GÜNCELLENECEK")) throw new InvalidOperationException("Yalnız YENİ ÜRÜN veya FİYAT GÜNCELLENECEK satırı gönderilebilir.");

            var current = new CatalogStore(directory).Products().SingleOrDefault(product => product.Id == selected[0].ProductId)
                ?? throw new InvalidOperationException("Seçilen yerel ürün artık bulunamadı; önizlemeyi yenileyin.");
            var payload = CreatePayload(current);
            using (BizimHesapConnection.BuildAddProductRequest(Settings(), payload)) { }

            remoteProducts = (await api.ReadAllProductsAsync(Settings())).ToArray();
            remoteProductsLoaded = true;
            var preflight = BizimHesapCreateSafety.Match(payload, remoteProducts);
            if (preflight.Status == BizimHesapReadbackStatus.Matched && BizimHesapCreateSafety.IsPriceCurrent(payload, preflight.Product!))
            {
                productGrid.ItemsSource = remoteProducts;
                mappingGrid.ItemsSource = BuildPreviewRows();
                status.Text = $"{payload.Sku} BizimHesap'ta mevcut ve fiyatı güncel; gönderilmedi.";
                return;
            }
            if (preflight.Status is BizimHesapReadbackStatus.Ambiguous or BizimHesapReadbackStatus.Conflict)
                throw new InvalidOperationException("BizimHesap'ta SKU/barkod çakışması var; ürün gönderilmedi. Önce mevcut ürünleri düzeltin.");

            var settings = Settings();
            var accountKey = BizimHesapCreateSafety.AccountKey(settings);
            var payloadHash = BizimHesapCreateSafety.PayloadHash(payload);
            if (receipts.HasBlockingAttempt(accountKey, current.Id, payloadHash))
                throw new InvalidOperationException("Bu ürün ve içerik daha önce gönderildi veya sonucu doğrulama bekliyor; tekrar gönderilmedi.");

            var gross = Math.Round(payload.NetPrice * (1 + payload.VatRate / 100m), 2, MidpointRounding.AwayFromZero);
            var action = preflight.Status == BizimHesapReadbackStatus.Matched ? "fiyatı güncellenecek" : "oluşturulacak";
            var summary = $"BizimHesap hesabındaki ürün {action}.\n\nFirma: {settings.FirmId}\nSKU: {payload.Sku}\nBarkod: {payload.Barcode}\nÜrün: {payload.Title}\nNet fiyat: {payload.NetPrice:N2} {payload.Currency}\nKDV: %{payload.VatRate:N2}\nKDV dahil: {gross:N2} {payload.Currency}\n\nNot: Bu işlem depo stoğunu güncellemez.\n\nGönderilsin mi?";
            if (MessageBox.Show(summary, "BizimHesap canlı ürün işlemi", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                status.Text = "BizimHesap ürün gönderimi iptal edildi.";
                return;
            }

            receipts.Begin(accountKey, current.Id, payloadHash, DateTimeOffset.UtcNow);
            BizimHesapCreateResponse response;
            try
            {
                response = await api.AddProductAsync(settings, payload);
            }
            catch (BizimHesapProductRejectedException e)
            {
                receipts.Complete(accountKey, current.Id, payloadHash, BizimHesapCreateAttemptStatus.Rejected, "", e.Message, DateTimeOffset.UtcNow);
                throw;
            }
            catch (Exception e)
            {
                receipts.Complete(accountKey, current.Id, payloadHash, BizimHesapCreateAttemptStatus.NeedsReconciliation, "", "Gönderim sonucu belirsiz: " + e.GetType().Name, DateTimeOffset.UtcNow);
                throw new InvalidOperationException("BizimHesap gönderim sonucu kesinleşmedi. Aynı ürünü tekrar göndermeyin; Ürünleri oku ile kontrol edin.");
            }

            try
            {
                remoteProducts = (await api.ReadAllProductsAsync(settings)).ToArray();
                remoteProductsLoaded = true;
                var readback = response.RemoteId.Length == 0
                    ? BizimHesapCreateSafety.Match(payload, remoteProducts)
                    : BizimHesapCreateSafety.MatchRemoteId(response.RemoteId, remoteProducts);
                if (readback.Status != BizimHesapReadbackStatus.Matched || !BizimHesapCreateSafety.IsPriceCurrent(payload, readback.Product!))
                {
                    receipts.Complete(accountKey, current.Id, payloadHash, BizimHesapCreateAttemptStatus.NeedsReconciliation, response.RemoteId, "API kabul etti; ürün geri okumada kesin eşleşmedi: " + readback.Status, DateTimeOffset.UtcNow);
                    status.Text = "BizimHesap isteği kabul etti ancak ürün veya fiyat geri okumada doğrulanamadı. Aynı ürünü tekrar göndermeyin; destek yanıtını kontrol edin.";
                    return;
                }
                receipts.Complete(accountKey, current.Id, payloadHash, BizimHesapCreateAttemptStatus.Succeeded, readback.Product?.Id ?? response.RemoteId, "Ürün geri okumayla doğrulandı.", DateTimeOffset.UtcNow);
                productGrid.ItemsSource = remoteProducts;
                mappingGrid.ItemsSource = BuildPreviewRows();
                status.Text = $"{payload.Sku} BizimHesap'a gönderildi; ürün kimliği ve fiyat hesaptan geri okunarak doğrulandı.";
            }
            catch (Exception e) when (e is not InvalidOperationException || !e.Message.Contains("tekrar göndermeyin", StringComparison.OrdinalIgnoreCase))
            {
                receipts.Complete(accountKey, current.Id, payloadHash, BizimHesapCreateAttemptStatus.NeedsReconciliation, response.RemoteId, "Ürün oluşturuldu; geri okuma başarısız: " + e.GetType().Name, DateTimeOffset.UtcNow);
                throw new InvalidOperationException("Ürün gönderildi ancak BizimHesap'tan geri okuma tamamlanamadı. Aynı ürünü tekrar göndermeyin.");
            }
        });
        return root;
    }

    static TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 0, 0, 8) };
    static void Add(Grid grid, string label, UIElement input, int row) { grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); var text = new TextBlock { Text = label, Margin = new Thickness(0, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(text, row); grid.Children.Add(text); Grid.SetColumn(input, 1); Grid.SetRow(input, row); grid.Children.Add(input); }
    static Button Button(Panel panel, string label) { var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 5, 10, 5) }; panel.Children.Add(button); return button; }
    static DataGrid Table() { var grid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 180, EnableRowVirtualization = true }; return grid; }
    static GroupBox Group(string title, object content) => new() { Header = title, Content = content, Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0) };
    static BizimHesapCreateProduct CreatePayload(CatalogProduct product) => new(product.Sku, product.Barcode, product.Name, product.Price, product.VatRate, product.Stock, product.Currency);
}
