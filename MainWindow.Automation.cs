using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
    FrameworkElement BuildAutomation()
    {
        var store = new AutomationStore(dataDirectory);
        var jobs = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 240 };
        var kind = new ComboBox { ItemsSource = Enum.GetValues<AutomationKind>(), SelectedIndex = 0, Width = 110 };
        var channel = new TextBox { Text = "etsy", Width = 110 };
        var shop = new TextBox { Text = "default", Width = 140 };
        var interval = new TextBox { Text = "30", Width = 80 };
        var enabled = new CheckBox { Content = "Etkin", IsChecked = true, Margin = new Thickness(8, 4, 8, 4) };
        var status = Hint("Zamanlayıcı yalnız uygulama açıkken çalışır.");
        void Refresh() => jobs.ItemsSource = store.List();
        var save = Button("Kaydet", () =>
        {
            if (!int.TryParse(interval.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes < 1) throw new InvalidOperationException("Çalışma aralığı en az 1 dakika olmalı.");
            var job = new AutomationJob { Kind = (AutomationKind)kind.SelectedItem!, Channel = channel.Text.Trim(), Shop = shop.Text.Trim(), IntervalMinutes = minutes, Enabled = enabled.IsChecked == true, NextRunUtc = DateTime.UtcNow };
            store.Save(job); status.Text = $"Otomasyon kaydedildi: {job.Channel}/{job.Shop}"; Refresh();
        });
        var toggle = Button("Seçileni etkin/pasif yap", () => { if (jobs.SelectedItem is not AutomationJob job) throw new InvalidOperationException("Önce otomasyon seçin."); job.Enabled = !job.Enabled; store.Save(job); Refresh(); });
        var run = Button("Seçileni şimdi çalıştır", () => { if (jobs.SelectedItem is not AutomationJob job) throw new InvalidOperationException("Önce otomasyon seçin."); job.NextRunUtc = DateTime.UtcNow; store.Save(job); status.Text = "İş çalıştırılmak üzere kuyruğa alındı."; });
        var form = new WrapPanel();
        foreach (var pair in new[] { ("Tür", (Control)kind), ("Kanal", channel), ("Mağaza", shop), ("Dakika", interval) }) { form.Children.Add(new TextBlock { Text = pair.Item1, Margin = new Thickness(4, 7, 2, 0) }); form.Children.Add(pair.Item2); }
        form.Children.Add(enabled); form.Children.Add(save); form.Children.Add(toggle); form.Children.Add(run);
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 1200 };
        panel.Children.Add(Heading("Otomasyon zamanlayıcıları")); panel.Children.Add(Hint("Stok ve fiyat işleri kanal/mağaza bağlamıyla kaydedilir. Kilit, sonraki çalışma ve son hata bilgisi burada görünür.")); panel.Children.Add(form); panel.Children.Add(jobs); panel.Children.Add(status); Refresh(); return Scroll(panel);
    }

    (EtsyListingUpdatePreview Preview, CatalogProduct Product, string SyncJobId)? pendingEtsyDispatch;

    void CreateEtsyDispatchPreview(TextBlock status)
    {
        var product = SelectedProduct();
        var preview = new EtsyListingSyncService(new EtsyShopClient(http)).CreatePreview(product);
        var sync = new SyncStore(dataDirectory);
        var syncJob = sync.Enqueue(new SyncRequest("etsy", "stock-price", preview.ListingId.ToString(CultureInfo.InvariantCulture), $"{preview.ProductId}:{preview.ProductUpdatedUtc.Ticks}:{preview.Quantity}:{preview.Price:0.00}:{preview.Currency}"));
        pendingEtsyDispatch = (preview, product, syncJob.Id);
        status.Text = $"ÖNİZLEME — {product.Name} / Etsy #{preview.ListingId} / stok {preview.Quantity} / fiyat {preview.Price:0.00} {preview.Currency} / sürüm {preview.ProductUpdatedUtc:O}";
    }

    async Task ApproveEtsyDispatchAsync(TextBlock status)
    {
        if (pendingEtsyDispatch is not { } pending) throw new InvalidOperationException("Önce güncel bir önizleme oluşturun.");
        var current = store.Products().SingleOrDefault(p => p.Id == pending.Product.Id) ?? throw new InvalidOperationException("Ürün artık bulunamadı; yeni önizleme alın.");
        if (MessageBox.Show(this, status.Text + "\n\nEtsy'ye PATCH gönderilsin mi?", "Açık Etsy onayı", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var credentials = await AuthorizedAsync();
        await new EtsyListingSyncService(new EtsyShopClient(http)).DispatchAsync(credentials, current, pending.Preview, true, new SyncStore(dataDirectory), pending.SyncJobId, lifetime.Token);
        status.Text = "Etsy dispatch başarılı; ilan stok/fiyatı güncellendi."; pendingEtsyDispatch = null; Log(status.Text);
    }
}
