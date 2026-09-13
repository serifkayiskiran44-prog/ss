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
        var template = new ComboBox { ItemsSource = AutomationTemplateCatalog.All, DisplayMemberPath = "Name", Width = 180, SelectedIndex = 0 };
        var kind = new TextBox { IsReadOnly = true, Width = 90 };
        var channel = new TextBox { Text = "etsy", Width = 110 };
        var shop = new TextBox { Text = "default", Width = 140 };
        var interval = new TextBox { Text = "30", Width = 80 };
        var schedule = new ComboBox { ItemsSource = new[] { "Interval", "Daily", "Weekly" }, SelectedIndex = 0, Width = 90 };
        var runAt = new TextBox { Text = "09:00", Width = 70 };
        var days = new TextBox { Text = "Monday", Width = 110, ToolTip = "Weekly: Monday,Wednesday" };
        var windowStart = new TextBox { Width = 70, ToolTip = "İsteğe bağlı HH:mm" };
        var windowEnd = new TextBox { Width = 70, ToolTip = "İsteğe bağlı HH:mm" };
        var retryLimit = new TextBox { Text = "3", Width = 55 };
        var retryBackoff = new TextBox { Text = "5", Width = 55 };
        var enabled = new CheckBox { Content = "Etkin", IsChecked = true, Margin = new Thickness(8, 4, 8, 4) };
        var status = Hint("Zamanlayıcı yalnız uygulama açıkken çalışır. XML, stok, fiyat, normal sync ve sağlık şablonları yerel kuyruğa alınır.");
        template.SelectionChanged += (_, _) => { if (template.SelectedItem is AutomationTemplate selected) { kind.Text = selected.Kind.ToString(); interval.Text = selected.IntervalMinutes.ToString(CultureInfo.InvariantCulture); } };
        void Refresh() => jobs.ItemsSource = store.List();
        var save = Button("Kaydet", () =>
        {
            if (!int.TryParse(interval.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes < 1) throw new InvalidOperationException("Çalışma aralığı en az 1 dakika olmalı.");
            if (!int.TryParse(retryLimit.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || !int.TryParse(retryBackoff.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var backoff)) throw new InvalidOperationException("Retry alanları sayı olmalı.");
            var selected = template.SelectedItem as AutomationTemplate ?? throw new InvalidOperationException("Şablon seçin.");
            var job = new AutomationJob { Kind = selected.Kind, TemplateKey = selected.Key, Channel = channel.Text.Trim(), Shop = shop.Text.Trim(), IntervalMinutes = minutes, ScheduleMode = schedule.SelectedItem?.ToString() ?? "Interval", RunAtLocal = runAt.Text.Trim(), DaysOfWeek = days.Text.Trim(), WindowStartLocal = windowStart.Text.Trim(), WindowEndLocal = windowEnd.Text.Trim(), RetryLimit = limit, RetryBackoffMinutes = backoff, Enabled = enabled.IsChecked == true };
            job.NextRunUtc = AutomationSchedule.NextRunUtc(job, DateTime.UtcNow); store.Save(job); new AuditStore(dataDirectory).Append(new AuditEvent { Module = "automation", Action = "save", Marketplace = job.Channel, ShopId = job.Shop, Outcome = "Succeeded", Detail = $"{job.TemplateKey} / {job.ScheduleMode}" }); status.Text = $"Otomasyon kaydedildi: {job.Channel}/{job.Shop} · sonraki çalışma {job.NextRunUtc.ToLocalTime():g}"; Refresh();
        });
        var toggle = Button("Seçileni etkin/pasif yap", () => { if (jobs.SelectedItem is not AutomationJob job) throw new InvalidOperationException("Önce otomasyon seçin."); job.Enabled = !job.Enabled; store.Save(job); new AuditStore(dataDirectory).Append(new AuditEvent { Module = "automation", Action = "toggle", Marketplace = job.Channel, ShopId = job.Shop, Outcome = "Succeeded", Detail = job.Enabled ? "Etkin" : "Pasif" }); Refresh(); });
        var run = Button("Seçileni şimdi çalıştır", () => { if (jobs.SelectedItem is not AutomationJob job) throw new InvalidOperationException("Önce otomasyon seçin."); job.NextRunUtc = DateTime.UtcNow; job.FailureCount = 0; store.Save(job); status.Text = "İş çalıştırılmak üzere kuyruğa alındı; lease duplicate çalışmayı engeller."; Refresh(); });
        var form = new WrapPanel();
        foreach (var pair in new[] { ("Şablon", (Control)template), ("Tür", (Control)kind), ("Kanal", channel), ("Mağaza", shop), ("Dakika", interval), ("Takvim", schedule), ("Saat", runAt), ("Gün", days), ("Pencere baş", windowStart), ("Pencere son", windowEnd), ("Retry", retryLimit), ("Backoff", retryBackoff) }) { form.Children.Add(new TextBlock { Text = pair.Item1, Margin = new Thickness(4, 7, 2, 0) }); form.Children.Add(pair.Item2); }
        form.Children.Add(enabled); form.Children.Add(save); form.Children.Add(toggle); form.Children.Add(run);
        var panel = new StackPanel { Margin = new Thickness(DesignTokens.SpacePage), MaxWidth = 1200 };
        panel.Children.Add(Heading("Otomasyon takvimi ve şablonları")); panel.Children.Add(Hint("Günlük/haftalık saat ve isteğe bağlı çalışma penceresi kullanın. Uygulama kapalıyken arka plan servisi varmış gibi davranılmaz; açılışta due işler lease ile tek kez kuyruğa alınır. Retry backoff ve son hata kayıtlıdır.")); panel.Children.Add(form); panel.Children.Add(jobs); panel.Children.Add(status); Refresh(); return Scroll(panel);
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
