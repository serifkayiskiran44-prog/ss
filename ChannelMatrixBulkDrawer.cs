using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The matrix's bulk-action drawer (#845): the products picked in the matrix, one target store among those the shell
/// offers, a target category, and a preview of the local channel plans that would be created -- affected, already
/// identical, blocked, and the field change per product -- before an explicit approval writes them. The write goes
/// through the same BulkProductOperations the bulk screen uses and stays local (ChannelPlans); every guard runs again
/// at the moment of applying: the store must still be offered, the matrix must not have changed since the preview,
/// and a preview applies once. Cancel stops a running preview or the validation phase of an apply and writes nothing.
/// </summary>
public static class ChannelMatrixBulkDrawer
{
    /// <param name="Products">The products selected in the matrix.</param>
    /// <param name="Stores">The store columns the matrix offers; the only targets the drawer lists.</param>
    /// <param name="DefaultStore">The store every selected cell belongs to, when there is exactly one.</param>
    /// <param name="RevisionAtPreview">The matrix fingerprint as shown when the drawer opened.</param>
    /// <param name="RevisionNow">Recomputes the fingerprint from the stores at apply time.</param>
    /// <param name="AllowedStoreKeys">The stores the shell offers at apply time (null offers every store).</param>
    /// <param name="AppliedPreviews">The owner's ledger of previews already applied.</param>
    public sealed record Context(IReadOnlyList<ChannelMatrixRow> Products, IReadOnlyList<ChannelMatrixColumn> Stores, ChannelMatrixColumn? DefaultStore, string RevisionAtPreview, Func<string> RevisionNow, Func<IReadOnlyCollection<string>?> AllowedStoreKeys, CatalogStore Catalog, ChannelProductsStore Plans, ISet<Guid> AppliedPreviews);

    public const string ApplyLabel = "Onayla ve uygula";
    public const string CancelLabel = "Vazgeç";

    /// <summary>Builds the drawer; the caller shows it. The window's Tag carries the current preview once one exists.</summary>
    public static Window Build(Window? owner, Context context, Action<BulkProductApplyResult>? applied = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var operations = new BulkProductOperations(context.Catalog, context.Plans);
        var targets = context.Stores.Select(s => new ChannelMatrixBulkTarget(s.Key, s.Channel, s.ShopId, $"{s.ChannelName} · {s.ShopId}")).ToList();
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = $"{context.Products.Count:N0} seçili ürün için yerel kanal planı oluşturulur: hedef mağaza ve hedef kategori seçin, önizlemeyi okuyun, sonra onaylayın. Bu işlem yalnızca yerel plan yazar; pazaryerine hiçbir şey gönderilmez.", TextWrapping = TextWrapping.Wrap, Margin = Spacing.BelowControl });
        var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var target = new ComboBox { Tag = "channel-matrix-bulk-target", Width = 220, ItemsSource = targets, DisplayMemberPath = "Label", Margin = new Thickness(0, 0, 8, 4) };
        System.Windows.Automation.AutomationProperties.SetName(target, "Hedef mağaza");
        target.SelectedItem = context.DefaultStore is null ? targets.FirstOrDefault() : targets.FirstOrDefault(t => t.Key == context.DefaultStore.Key) ?? targets.FirstOrDefault();
        var category = new TextBox { Tag = "channel-matrix-bulk-category", Width = 200, Margin = new Thickness(0, 0, 8, 4), ToolTip = "Hedef kategori (planın kategori alanı)" };
        System.Windows.Automation.AutomationProperties.SetName(category, "Hedef kategori");
        var previewButton = new Button { Tag = "channel-matrix-bulk-preview", Content = "Önizle", Padding = new Thickness(10, 2, 10, 2), Margin = Spacing.BelowInline };
        form.Children.Add(Label("Hedef mağaza")); form.Children.Add(target); form.Children.Add(Label("Hedef kategori")); form.Children.Add(category); form.Children.Add(previewButton);
        body.Children.Add(form);
        var summary = new TextBlock { Tag = "channel-matrix-bulk-summary", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 2), Text = "Önizleme alınmadı." };
        var reason = new TextBlock { Tag = "channel-matrix-bulk-reason", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4), Visibility = Visibility.Collapsed };
        var lines = new ItemsControl { Tag = "channel-matrix-bulk-lines", Margin = new Thickness(0, 4, 0, 2) };
        var more = new TextBlock { Tag = "channel-matrix-bulk-more", Opacity = 0.85, Margin = Spacing.BelowInline, Visibility = Visibility.Collapsed };
        var approve = new CheckBox { Tag = "channel-matrix-bulk-approve", Content = "Yukarıdaki yerel planların oluşturulmasını onaylıyorum (canlı yazım yok).", Margin = new Thickness(0, 8, 0, 4) }; CommandState.Apply(approve, DisabledReason.StoreState("Önce önizleme alın."));
        var status = new TextBlock { Tag = "channel-matrix-bulk-status", TextWrapping = TextWrapping.Wrap, Margin = Spacing.AboveInline };
        body.Children.Add(summary); body.Children.Add(reason); body.Children.Add(lines); body.Children.Add(more); body.Children.Add(approve); body.Children.Add(status);

        var cts = new CancellationTokenSource();
        BulkProductPreview? preview = null; ChannelMatrixBulkDrawerModel? model = null; var busy = false; var applying = false;
        Window window = null!; Button? applyButton = null;

        ChannelMatrixBulkTarget? Target() => target.SelectedItem as ChannelMatrixBulkTarget;
        void Render(ChannelMatrixBulkTarget t)
        {
            model = ChannelMatrixBulk.Compose(preview, t, context.AllowedStoreKeys(), context.RevisionAtPreview, context.Products.Count);
            summary.Text = $"Hedef: {model.TargetLabel} · {model.Summary}";
            reason.Text = model.Reason; reason.Visibility = model.Reason.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            lines.ItemsSource = model.Lines.Select(l => l.Text).ToList();
            more.Text = model.LinesTruncated > 0 ? $"… ve {model.LinesTruncated:N0} daha (toplam {model.Lines.Count + model.LinesTruncated:N0} satır)" : ""; more.Visibility = model.LinesTruncated > 0 ? Visibility.Visible : Visibility.Collapsed;
            CommandState.Apply(approve, model.CanApply ? null : DisabledReason.StoreState(model.Reason.Length > 0 ? model.Reason : "Uygulanacak değişiklik yok.")); if (!model.CanApply) approve.IsChecked = false;
            window.Tag = preview;
        }
        async Task PreviewAsync()
        {
            if (busy) return;
            var t = Target();
            if (t is null) { preview = null; summary.Text = "Bu oturumda sunulan mağaza yok; toplu plan oluşturulamaz."; reason.Visibility = Visibility.Collapsed; CommandState.Apply(approve, DisabledReason.StoreState("Bu oturumda sunulan mağaza yok.")); return; }
            var categoryText = category.Text.Trim();
            if (categoryText.Length == 0) { preview = null; Render(t); reason.Text = "Hedef kategori girin, sonra Önizle."; reason.Visibility = Visibility.Visible; summary.Text = "Önizleme alınmadı."; return; }
            busy = true; CommandState.Apply(previewButton, DisabledReason.Busy("Önizleme hazırlanıyor.")); CommandState.Apply(approve, DisabledReason.Busy("Önizleme hazırlanıyor.")); status.Text = ""; summary.Text = "Önizleme hazırlanıyor…";
            var ids = context.Products.Select(p => (p.ProductId, p.Sku, p.ProductName)).ToList();
            var request = new BulkProductOperationRequest(BulkProductOperationKind.SetChannelMapping, Channel: t.Channel, ShopId: t.ShopId, TargetCategory: categoryText);
            var token = cts.Token;
            try
            {
                preview = await Task.Run(() =>
                {
                    var found = new List<CatalogProduct>(); var missing = new List<BulkProductPreviewLine>();
                    foreach (var (id, sku, name) in ids) { token.ThrowIfCancellationRequested(); var product = context.Catalog.FindProduct(id); if (product is null) missing.Add(new() { ProductId = id, Sku = sku, Name = name, Before = "?", After = "?", Status = "ERROR", Error = "Ürün artık katalogda yok; matrisi yenileyin." }); else found.Add(product); }
                    var built = operations.Preview(found, request);
                    return missing.Count == 0 ? built : built with { Lines = built.Lines.Concat(missing).ToList() };
                }, token);
                Render(t);
            }
            catch (OperationCanceledException) { preview = null; summary.Text = "Önizleme iptal edildi; hiçbir şey yazılmadı."; }
            catch (Exception error) { preview = null; Render(t); reason.Text = AuditStore.Sanitize(error.Message); reason.Visibility = Visibility.Visible; }
            finally { busy = false; CommandState.Apply(previewButton, null); }
        }
        async Task ApplyAsync(BulkProductPreview current)
        {
            applying = true; if (applyButton is not null) CommandState.Apply(applyButton, DisabledReason.Busy("Uygulama sürüyor.")); CommandState.Apply(previewButton, DisabledReason.Busy("Uygulama sürüyor.")); status.Text = "Uygulanıyor… yalnızca yerel plan yazılır.";
            try
            {
                var progress = new Progress<int>(p => status.Text = $"Uygulanıyor… %{p}");
                var result = await Task.Run(() => operations.Apply(current, approved: true, cts.Token, progress), cts.Token);
                context.AppliedPreviews.Add(current.Id);
                status.Text = $"Tamamlandı: {result.Applied} plan yazıldı · {result.Skipped} atlandı · {result.Errors} engelli.";
                applied?.Invoke(result);
                applying = false; // the Closing guard below only holds the window open while a write is in flight
                if (window.IsVisible) { try { window.DialogResult = true; } catch (InvalidOperationException) { } window.Close(); }
            }
            catch (OperationCanceledException) { status.Text = "Uygulama iptal edildi; hiçbir plan yazılmadı."; }
            catch (Exception error) { status.Text = "Uygulanamadı: " + AuditStore.Sanitize(error.Message); }
            finally { applying = false; if (applyButton is not null) CommandState.Apply(applyButton, null); CommandState.Apply(previewButton, null); }
        }
        bool OnApply()
        {
            if (applying || busy) { status.Text = busy ? "Önizleme sürüyor; bitmesini bekleyin." : "Uygulama sürüyor."; return false; }
            if (preview is null || model is null || !model.CanApply) { status.Text = model is null || model.Reason.Length == 0 ? "Önce önizleme alın." : model.Reason; return false; }
            if (approve.IsChecked != true) { status.Text = "Uygulamak için onay kutusunu işaretleyin."; return false; }
            var key = DashboardStoreFilter.KeyFor(preview.Request.Channel, preview.Request.ShopId);
            var check = ChannelMatrixBulk.CheckBeforeApply(context.RevisionAtPreview, context.RevisionNow(), context.AllowedStoreKeys(), key, context.AppliedPreviews, preview.Id);
            if (!check.Ok) { status.Text = check.Reason; approve.IsChecked = false; return false; }
            _ = ApplyAsync(preview); return false;
        }
        bool OnCancel()
        {
            cts.Cancel();
            if (applying) { status.Text = "İptal istendi: doğrulama aşamasındaysa durur; yazım aşaması başladıysa tamamlanır."; return false; }
            return true;
        }
        window = DialogShell.Create(owner, "Toplu yerel plan önizlemesi", body, new DialogShell.Action[] { new(CancelLabel, IsCancel: true, OnClick: OnCancel), new(ApplyLabel, IsPrimary: true, OnClick: OnApply) }, 760, 640, destructive: true);
        applyButton = Buttons(window).FirstOrDefault(b => (string?)b.Content == ApplyLabel);
        window.Closing += (_, e) => { if (applying) { cts.Cancel(); e.Cancel = true; } };
        previewButton.Click += async (_, _) => await PreviewAsync();
        target.SelectionChanged += (_, _) => { if (preview is not null && !busy) _ = PreviewAsync(); };
        window.Loaded += async (_, _) => await PreviewAsync();
        return window;
    }

    static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 3, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    static IEnumerable<Button> Buttons(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node)) { if (child is Button button) yield return button; if (child is DependencyObject d) foreach (var nested in Buttons(d)) yield return nested; }
    }
}
