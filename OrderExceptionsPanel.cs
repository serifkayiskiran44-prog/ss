using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public static class OrderExceptionsPanel
{
    public static FrameworkElement Create(string? directory, Action<string>? navigate = null)
    {
        var exceptions = new OrderExceptionStore(directory); var orders = new OrdersStore(directory); var catalog = new CatalogStore(directory);
        var panel = new StackPanel { Margin = new Thickness(DesignTokens.SpacePage), MaxWidth = 1350 };
        panel.Children.Add(Heading("Sipariş istisna ve karar merkezi"));
        panel.Children.Add(Hint("Eksik SKU, belirsiz eşleme, iptal/iade ve stok uyuşmazlıklarını tek kuyruğa alın. İptal/iade stok geri koyma yalnız somut önizleme ve açık onayla çalışır; aynı sipariş için ikinci geri koyma idempotent olarak reddedilir."));
        var query = new TextBox { Width = 230, ToolTip = "Sipariş, SKU veya hata ara" };
        var state = new ComboBox { Width = 140, ItemsSource = new[] { "Tümü", "Pending", "PreviewReady", "Resolved", "Rejected" }, SelectedIndex = 0 };
        var age = new ComboBox { Width = 130, ItemsSource = new[] { "Tümü", "1 gün", "7 gün", "30 gün" }, SelectedIndex = 0 };
        var type = new ComboBox { Width = 140, ItemsSource = new[] { "Tümü", "MissingSku", "AmbiguousSku", "Cancel", "Return" }, SelectedIndex = 0 };
        var status = Hint(""); var detail = Hint("Bir istisna seçin."); OrderExceptionRecord? selected = null; OrderRestockPreview? preview = null; IReadOnlyList<OrderExceptionRecord> all = [];
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 470, EnableRowVirtualization = true, SelectionMode = DataGridSelectionMode.Single };
        foreach (var (header, path, width) in new[] { ("Öncelik", "Severity", 80d), ("Kanal", "Marketplace", 90d), ("Mağaza", "ShopId", 100d), ("Sipariş", "OrderId", 120d), ("Tür", "Type", 120d), ("Durum", "Status", 110d), ("Güncelleme", "UpdatedUtc", 150d), ("Açıklama", "Message", 350d) }) grid.Columns.Add(GridColumns.Text(header, path, width));
        void RefreshGrid()
        {
            var selectedState = state.SelectedItem?.ToString() ?? "Tümü"; var selectedType = type.SelectedItem?.ToString() ?? "Tümü"; var selectedAge = age.SelectedItem?.ToString() ?? "Tümü"; var min = selectedAge switch { "1 gün" => DateTime.UtcNow.AddDays(-1), "7 gün" => DateTime.UtcNow.AddDays(-7), "30 gün" => DateTime.UtcNow.AddDays(-30), _ => DateTime.MinValue }; var rows = all.Where(x => selectedState == "Tümü" || x.Status == selectedState).Where(x => selectedType == "Tümü" || x.Type == selectedType).Where(x => x.UpdatedUtc >= min).Where(x => string.IsNullOrWhiteSpace(query.Text) || $"{x.OrderId} {x.Message} {x.Type} {x.Marketplace} {x.ShopId}".Contains(query.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList(); grid.ItemsSource = rows; var critical = rows.Count(x => x.Severity is "Critical" or "Error"); var decisions = rows.Count(x => x.Status == "PreviewReady"); status.Text = $"{rows.Count:N0} istisna · {critical} kritik/hata · {decisions} karar bekliyor";
        }
        void Load() { all = exceptions.List(); RefreshGrid(); }
        var refresh = Button("Kuyruğu yenile", Load);
        var reconcile = Button("Siparişleri tara", () => { var count = exceptions.Reconcile(orders.ReadAll(), catalog); Load(); status.Text = $"{count} problem tarandı/güncellendi; duplicate olaylar tek kayıt altında tutulur."; });
        var openOrder = Button("Sipariş ekranına git", () => navigate?.Invoke("orders"));
        var openProduct = Button("Ürün eşleme ekranına git", () => navigate?.Invoke("products"));
        var previewButton = Button("İptal/iade stok önizlemesi", () => { if (selected is null) throw new InvalidOperationException("Önce istisna seçin."); if (selected.Type is not ("Cancel" or "Return")) throw new InvalidOperationException("Stok geri koyma yalnız iptal/iade kararlarında kullanılabilir."); preview = catalog.CreateOrderRestockPreview(selected.Marketplace, selected.ShopId, selected.OrderId, selected.Id); detail.Text = string.Join("\n", preview.Lines.Select(x => $"{x.Sku}: {x.CurrentStock} → {x.RestoredStock} (+{x.Quantity}) · ürün sürümü {x.ProductUpdatedUtc:O}")); status.Text = "Geri koyma önizlemesi hazır; onay verilmedi."; });
        var approve = Button("Önizlemeyi onayla ve stok koy", () => { if (selected is null || preview is null) throw new InvalidOperationException("Önce iptal/iade için güncel önizleme alın."); if (MessageBox.Show(detail.Text + "\n\nBu yerel stok hareketi uygulansın mı?", "İptal/iade stok kararı", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; var result = catalog.ApplyOrderRestock(preview, true); exceptions.SetStatus(selected.Id, "Resolved"); preview = null; detail.Text = result.AlreadyApplied ? "Bu iptal/iade olayı daha önce uygulandı; stok tekrar koyulmadı." : "Stok geri koyma uygulandı ve karar çözüldü."; Load(); });
        var reject = Button("Kararı reddet", () => { if (selected is null) throw new InvalidOperationException("Önce istisna seçin."); exceptions.SetStatus(selected.Id, "Rejected"); preview = null; Load(); });
        // #788: partial return / refund reconciliation of the selected cancel/return record against the order,
        // its stock receipt and the return ledger (quantity, refund amount, currency, duplicate event).
        var returnSku = new TextBox { Width = 90, ToolTip = "İade edilen SKU" }; var returnQuantity = new TextBox { Width = 50, Text = "1", ToolTip = "İade adedi" }; var refundAmount = new TextBox { Width = 80, Text = "0", ToolTip = "İade tutarı" }; var refundCurrency = new TextBox { Width = 50, Text = "TRY", ToolTip = "Para birimi" };
        (OrderSnapshot Order, OrderReturnEvent Event) ReturnInput()
        {
            if (selected is null) throw new InvalidOperationException("Önce istisna seçin.");
            if (selected.Type is not ("Cancel" or "Return")) throw new InvalidOperationException("İade mutabakatı yalnız iptal/iade kayıtlarında kullanılabilir.");
            var order = orders.Find(selected.Marketplace, selected.ShopId, selected.OrderId) ?? throw new InvalidOperationException("Sipariş kaydı bulunamadı; önce siparişleri yenileyin.");
            if (!int.TryParse(returnQuantity.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var quantity)) throw new InvalidOperationException("İade adedi tam sayı olmalı.");
            if (!decimal.TryParse(refundAmount.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var amount)) throw new InvalidOperationException("İade tutarı sayı olmalı.");
            var key = $"{selected.Id}:{returnSku.Text.Trim()}:{quantity}:{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{refundCurrency.Text.Trim().ToUpperInvariant()}";
            return (order, new OrderReturnEvent(order.Marketplace, order.ShopId, order.OrderId, key, returnSku.Text.Trim(), quantity, amount, refundCurrency.Text.Trim(), DateTimeOffset.UtcNow));
        }
        static string Describe(OrderReturnReconciliation r) => $"{r.Status}{(r.Reasons.Count > 0 ? " · " + string.Join(", ", r.Reasons) : "")}\nSipariş adedi {r.OrderedQuantity} · teslim {r.FulfilledQuantity} · iade edilen {r.ReturnedBefore} → {r.ReturnedAfter}\nİade tutarı {r.RefundedBefore:0.00} → {r.RefundedAfter:0.00} / {r.OrderTotal:0.00} {r.OrderCurrency}\nStoğa dönecek: {r.StockToRestore}";
        var returnPreview = Button("İade mutabakatı önizle", () => { var (order, evt) = ReturnInput(); detail.Text = Describe(catalog.PreviewOrderReturn(order, evt)); status.Text = "İade mutabakatı önizlendi; uygulanmadı."; });
        var returnApply = Button("İade mutabakatını uygula", () =>
        {
            var (order, evt) = ReturnInput(); var check = catalog.PreviewOrderReturn(order, evt);
            if (!check.Allowed && check.Status != OrderReturnReconciliation.Duplicate) throw new InvalidOperationException("İade mutabakatı engellendi: " + string.Join(", ", check.Reasons));
            if (MessageBox.Show(Describe(check) + "\n\nİade kaydedilsin ve stok geri konulsun mu?", "İade mutabakatı", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var result = catalog.ApplyOrderReturn(order, evt, true);
            if (result.Status == OrderReturnReconciliation.OkFull) exceptions.SetStatus(selected!.Id, "Resolved");
            detail.Text = Describe(result); status.Text = result.Status == OrderReturnReconciliation.Duplicate ? "Bu iade olayı daha önce kaydedilmişti; tekrar sayılmadı." : "İade mutabakatı uygulandı."; Load();
        });
        grid.SelectionChanged += (_, _) => { selected = grid.SelectedItem as OrderExceptionRecord; preview = null; detail.Text = selected is null ? "Bir istisna seçin." : $"{selected.Marketplace}/{selected.ShopId} · {selected.OrderId}\n{selected.Type} · {selected.Status}\n{selected.Message}"; };
        var bar = new WrapPanel(); bar.Children.Add(new TextBlock { Text = "Ara", Margin = Spacing.Inline, VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(query); bar.Children.Add(state); bar.Children.Add(type); bar.Children.Add(age); bar.Children.Add(refresh); bar.Children.Add(reconcile); bar.Children.Add(openOrder); bar.Children.Add(openProduct); bar.Children.Add(previewButton); bar.Children.Add(approve); bar.Children.Add(reject); bar.Children.Add(new TextBlock { Text = "İade", Margin = new Thickness(8, 4, 2, 4), VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(returnSku); bar.Children.Add(returnQuantity); bar.Children.Add(refundAmount); bar.Children.Add(refundCurrency); bar.Children.Add(returnPreview); bar.Children.Add(returnApply); panel.Children.Add(bar); panel.Children.Add(grid); panel.Children.Add(detail); panel.Children.Add(status);
        query.TextChanged += (_, _) => RefreshGrid(); state.SelectionChanged += (_, _) => RefreshGrid(); type.SelectionChanged += (_, _) => RefreshGrid(); age.SelectionChanged += (_, _) => RefreshGrid(); Load(); return Scroll(panel);
    }
    static TextBlock Heading(string text) => TextStyles.Apply(new TextBlock { Text = text, Margin = Spacing.TitleBlock }, TextRole.SectionTitle);
    static TextBlock Hint(string text) => TextStyles.Apply(new TextBlock { Text = text, Margin = Spacing.HintBlock }, TextRole.Hint);
    static Button Button(string text, Action action) { var button = new Button { Content = text, Margin = Spacing.Control }; button.Click += (_, _) => { try { action(); } catch (Exception error) { MessageBox.Show(MarketplaceConnectionStore.Redact(error.Message), "Sipariş istisnaları", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return button; }
    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
}
