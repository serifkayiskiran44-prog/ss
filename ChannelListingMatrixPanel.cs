using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TrMarketplaceHubDesktop;

public static class ChannelListingMatrixPanel
{
    /// <param name="allowedStoreKeys">#842: the stores the shell offers (dashboard store keys); a store outside it gets no column and no cells. Null offers every store the service built.</param>
    public static FrameworkElement Create(string? directory, Action<string>? navigate = null, Func<IReadOnlyCollection<string>>? allowedStoreKeys = null)
    {
        var panel = new StackPanel { Margin = new Thickness(DesignTokens.SpacePage), MaxWidth = 1450 };
        panel.Children.Add(Heading("Kanal yayın durumu ve ürün matrisi"));
        panel.Children.Add(Hint("Ürün × kanal × mağaza görünümünde yerel plan, mapping, sync ve bağlantı durumunu karşılaştırın. Bu ekran yalnızca yerel plan/önizleme okur; canlı ürün, stok veya fiyat yazmaz."));
        // #816: one error surface for this workspace; failures land here with retry / go-to-source / diagnostics
        // instead of a modal MessageBox.
        var errorHost = new StackPanel { Visibility = Visibility.Collapsed };
        var errors = new ErrorSurface(errorHost, navigate);
        panel.Children.Add(errorHost);
        var query = new TextBox { Width = 240, ToolTip = "SKU, ürün, kanal, ilan veya hata ara" };
        var channel = new TextBox { Width = 130, ToolTip = "Kanal filtresi (etsy, ebay...)" };
        var shop = new TextBox { Width = 140, ToolTip = "Mağaza filtresi" };
        var statusFilter = new ComboBox { Width = 145, ItemsSource = new[] { "Tümü", "MISSING", "ERROR", "STALE", "AUTH_ERROR", "PENDING", "SYNCED", "DRAFT" }, SelectedIndex = 0 };
        var status = Hint("");
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 560, EnableRowVirtualization = true, EnableColumnVirtualization = false, SelectionMode = DataGridSelectionMode.Single };
        // #842: the product × store pivot with sticky axes -- the two product columns frozen, the store headers in the
        // header row -- virtualized in both directions, cell-selectable from the keyboard. The flat list stays as a view.
        var matrix = new DataGrid { Tag = "channel-matrix", AutoGenerateColumns = false, IsReadOnly = true, Height = 560, EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.Cell, FrozenColumnCount = 2, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserReorderColumns = false };
        VirtualizingPanel.SetIsVirtualizing(matrix, true); VirtualizingPanel.SetVirtualizationMode(matrix, VirtualizationMode.Recycling);
        var view = new ComboBox { Width = 110, ItemsSource = new[] { "Matris", "Liste" }, SelectedIndex = 0, ToolTip = "Görünüm: ürün × mağaza matrisi veya düz liste" }; System.Windows.Automation.AutomationProperties.SetName(view, "Görünüm");
        ChannelMatrix current = new(Array.Empty<ChannelMatrixColumn>(), Array.Empty<ChannelMatrixRow>());
        // #844: the legend's states double as "any of" row filters; counts stay those of the whole matrix; nothing left says so.
        IReadOnlySet<string> activeStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase); ChannelMatrixReadinessResult readiness = ChannelMatrixReadinessFilter.Apply(current, null);
        var emptyText = new TextBlock { Tag = "channel-matrix-empty", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 6), Visibility = Visibility.Collapsed };
        // #843: the legend explains only the states on screen, with their counts; each chip is a focusable tab stop whose tooltip opens from the keyboard.
        var legend = new WrapPanel { Tag = "channel-matrix-legend", Margin = new Thickness(4, 2, 4, 4) };
        void RenderLegend()
        {
            legend.Children.Clear(); var hc = SeverityStyle.IsHighContrast;
            foreach (var (entry, count) in readiness.Counts)
            {
                var on = activeStates.Contains(entry.Key);
                var chip = new System.Windows.Controls.Primitives.ToggleButton { Tag = entry.Key, IsChecked = on, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 6, 2), BorderBrush = SeverityStyle.AccentBrush(entry.Level, hc), BorderThickness = new Thickness(SeverityStyle.For(entry.Level, hc).BorderWeight), ToolTip = entry.Description + (on ? " · Filtre açık: bu durumdaki ürünler gösteriliyor." : " · Etkinleştirince yalnız bu durumdaki ürünler kalır."), Content = new TextBlock { Text = $"{entry.Badge} ({count:N0})", Foreground = SeverityStyle.AccentBrush(entry.Level, hc) } };
                ToolTipService.SetShowsToolTipOnKeyboardFocus(chip, true);
                System.Windows.Automation.AutomationProperties.SetName(chip, $"{entry.Word}: {count:N0} hücre, filtre {(on ? "açık" : "kapalı")}"); System.Windows.Automation.AutomationProperties.SetHelpText(chip, entry.Description);
                chip.Click += (_, _) => { activeStates = ChannelMatrixReadinessFilter.Toggle(activeStates, entry.Key); RefreshGrid(); };
                legend.Children.Add(chip);
            }
            if (activeStates.Count > 0) { var reset = new Button { Tag = "channel-matrix-filter-reset", Content = "Filtreyi kaldır", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 2) }; reset.Click += (_, _) => { activeStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase); RefreshGrid(); }; legend.Children.Add(reset); }
            if (legend.Children.Count == 0) legend.Children.Add(new TextBlock { Text = "Gösterilecek hücre yok.", Opacity = 0.8 });
        }
        IReadOnlyList<ChannelListingMatrixRow> all = [];
        // #845: the last bulk result stays readable across refreshes; applied previews are remembered so one never applies twice.
        var lastBulkNote = ""; var appliedBulkPreviews = new HashSet<Guid>();
        void RefreshGrid()
        {
            var filtered = ChannelListingMatrixService.Filter(all, query.Text, statusFilter.SelectedItem?.ToString() ?? "Tümü", channel.Text, shop.Text); grid.ItemsSource = filtered;
            current = ChannelMatrixPivot.Build(filtered, allowedStoreKeys?.Invoke()); readiness = ChannelMatrixReadinessFilter.Apply(current, activeStates); RenderMatrix(matrix, readiness.Matrix); RenderLegend(); emptyText.Text = readiness.EmptyText; emptyText.Visibility = readiness.IsEmpty && view.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            var isMatrix = view.SelectedIndex == 0; matrix.Visibility = isMatrix ? Visibility.Visible : Visibility.Collapsed; grid.Visibility = isMatrix ? Visibility.Collapsed : Visibility.Visible;
            status.Text = (lastBulkNote.Length > 0 ? lastBulkNote + " · " : "") + $"{filtered.Count:N0} satır · {current.Rows.Count:N0} ürün × {current.Columns.Count:N0} mağaza" + (readiness.HiddenRows > 0 ? $" · {readiness.HiddenRows:N0} ürün durum filtresiyle gizli" : "") + $" · {filtered.Count(x => x.MappingStatus == "MISSING")} mapping eksik · {filtered.Count(x => x.MappingStatus == "ERROR")} sync hatası · {filtered.Count(x => x.AuthStatus == "AUTH_ERROR")} bağlantı uyarısı";
        }
        async Task RefreshAsync() { status.Text = "Matris hazırlanıyor…"; all = await Task.Run(() => new ChannelListingMatrixService(directory).Build()); RefreshGrid(); }
        var refresh = AsyncButton(errors, "Matrisi yenile", RefreshAsync);
        var product = Button(errors, "Ürün havuzuna git", () => navigate?.Invoke("products"));
        var channelOpen = Button(errors, "Seçili kanala git", () =>
        {
            if (view.SelectedIndex == 0) { var cell = matrix.CurrentCell; if (!cell.IsValid || cell.Column is null || cell.Column.DisplayIndex < 2 || cell.Column.DisplayIndex - 2 >= current.Columns.Count) throw new InvalidOperationException("Önce matristen bir mağaza hücresi seçin."); navigate?.Invoke(current.Columns[cell.Column.DisplayIndex - 2].Channel); return; }
            if (grid.SelectedItem is not ChannelListingMatrixRow row) throw new InvalidOperationException("Önce matristen satır seçin."); navigate?.Invoke(row.Channel);
        });
        // #845: the channel bulk action -- selected matrix cells become the drawer's products; the store they share is the
        // default target; the drawer previews local plans and applies them only with approval, a fresh matrix and an offered store.
        var bulk = Button(errors, "Toplu plan…", () =>
        {
            if (view.SelectedIndex != 0) throw new InvalidOperationException("Toplu plan matris görünümünde başlatılır: ürün hücrelerini seçin (Shift/Ctrl ile çoklu).");
            var cells = matrix.SelectedCells.Where(c => c.IsValid).ToList();
            var selected = cells.Select(c => c.Item).OfType<ChannelMatrixRow>().Distinct().ToList();
            if (selected.Count == 0) throw new InvalidOperationException("Seçim boş: önce matristen ürün hücreleri seçin (Shift/Ctrl ile çoklu).");
            var stores = cells.Where(c => c.Column is not null && c.Column.DisplayIndex >= 2 && c.Column.DisplayIndex - 2 < current.Columns.Count).Select(c => current.Columns[c.Column.DisplayIndex - 2]).Distinct().ToList();
            var context = new ChannelMatrixBulkDrawer.Context(selected, current.Columns, stores.Count == 1 ? stores[0] : null, ChannelMatrixBulk.Revision(all), () => ChannelMatrixBulk.Revision(new ChannelListingMatrixService(directory).Build()), () => allowedStoreKeys?.Invoke(), new Catalog.CatalogStore(directory), new ChannelProductsStore(directory), appliedBulkPreviews);
            var drawer = ChannelMatrixBulkDrawer.Build(Window.GetWindow(panel), context, result => { lastBulkNote = $"Son toplu plan: {result.Applied:N0} yazıldı · {result.Skipped:N0} atlandı · {result.Errors:N0} engelli"; _ = RefreshAsync(); });
            drawer.ShowDialog();
        });
        bulk.Tag = "channel-matrix-bulk"; bulk.ToolTip = "Seçili ürünler için tek mağazada yerel kanal planı önizle ve onayla (canlı yazım yok)";
        var bar = new WrapPanel(); bar.Children.Add(new TextBlock { Text = "Ara", Margin = Spacing.Inline, VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(query); bar.Children.Add(new TextBlock { Text = "Kanal", Margin = Spacing.Inline, VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(channel); bar.Children.Add(new TextBlock { Text = "Mağaza", Margin = Spacing.Inline, VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(shop); bar.Children.Add(statusFilter); bar.Children.Add(view); bar.Children.Add(refresh); bar.Children.Add(product); bar.Children.Add(channelOpen); bar.Children.Add(bulk); panel.Children.Add(bar); panel.Children.Add(legend); panel.Children.Add(emptyText); panel.Children.Add(matrix); panel.Children.Add(grid); panel.Children.Add(status);
        query.TextChanged += (_, _) => RefreshGrid(); channel.TextChanged += (_, _) => RefreshGrid(); shop.TextChanged += (_, _) => RefreshGrid(); statusFilter.SelectionChanged += (_, _) => RefreshGrid(); view.SelectionChanged += (_, _) => RefreshGrid(); _ = RefreshAsync();
        return Scroll(panel);
    }
    /// <summary>Lays the pivot out on a grid: SKU and product frozen (they stay put while the store columns scroll), one 120-DIP column per store bound by index, the header carrying channel and shop, rows named for automation.</summary>
    public static void RenderMatrix(DataGrid target, ChannelMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(matrix);
        target.ItemsSource = null; target.Columns.Clear();
        target.Columns.Add(new DataGridTextColumn { Header = "SKU", Binding = new Binding("Sku"), Width = new DataGridLength(110), MinWidth = 80 });
        target.Columns.Add(new DataGridTextColumn { Header = "Ürün", Binding = new Binding("ProductName"), Width = new DataGridLength(220), MinWidth = 120 });
        for (var i = 0; i < matrix.Columns.Count; i++)
        {
            var column = matrix.Columns[i];
            var cellStyle = RowSelection.AddToCellStyle(FocusStyles.AddTo(new Style(typeof(DataGridCell)))); cellStyle.Setters.Add(new Setter(ToolTipService.ShowsToolTipOnKeyboardFocusProperty, true)); cellStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding($"Descriptions[{i}]")));
            target.Columns.Add(new DataGridTextColumn { Header = column.Header, Binding = new Binding($"Labels[{i}]"), Width = new DataGridLength(120), MinWidth = 90, CellStyle = cellStyle });
        }
        target.FrozenColumnCount = Math.Min(2, target.Columns.Count);
        target.LoadingRow -= MatrixRowLoaded; target.LoadingRow += MatrixRowLoaded;
        target.ItemsSource = matrix.Rows;
    }
    static void MatrixRowLoaded(object? sender, DataGridRowEventArgs e) { if (e.Row.Item is ChannelMatrixRow row) System.Windows.Automation.AutomationProperties.SetName(e.Row, $"{row.ProductName} ({row.Sku}): {row.Problems} sorunlu mağaza"); }
    static TextBlock Heading(string text) => TextStyles.Apply(new TextBlock { Text = text, Margin = Spacing.TitleBlock }, TextRole.SectionTitle);
    static TextBlock Hint(string text) => TextStyles.Apply(new TextBlock { Text = text, Margin = Spacing.HintBlock }, TextRole.Hint);
    static Button Button(ErrorSurface errors, string text, Action action) { var button = new Button { Content = text, Margin = Spacing.Control }; button.Click += (_, _) => { try { errors.Clear(); action(); } catch (Exception error) { errors.Show(error, null, "connections", "Bağlantılara git"); } }; return button; }
    static Button AsyncButton(ErrorSurface errors, string text, Func<Task> action) { var button = new Button { Content = text, Margin = Spacing.Control }; button.Click += async (_, _) => { try { button.IsEnabled = false; errors.Clear(); await action(); } catch (Exception error) { errors.Show(error, action, "connections", "Bağlantılara git"); } finally { button.IsEnabled = true; } }; return button; }
    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
}
