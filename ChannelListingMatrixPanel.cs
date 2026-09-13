using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TrMarketplaceHubDesktop;

public static class ChannelListingMatrixPanel
{
    /// <param name="allowedStoreKeys">#842: the stores the shell offers (dashboard store keys); a store outside it gets no column and no cells. Null offers every store the service built.</param>
    public static FrameworkElement Create(string? directory, Action<string>? navigate = null, Func<IReadOnlyCollection<string>>? allowedStoreKeys = null)
    {
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 1450 };
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
        var matrix = new DataGrid { Tag = "channel-matrix", AutoGenerateColumns = false, IsReadOnly = true, Height = 560, EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.Cell, FrozenColumnCount = 2, HeadersVisibility = DataGridHeadersVisibility.Column, CanUserReorderColumns = false };
        VirtualizingPanel.SetIsVirtualizing(matrix, true); VirtualizingPanel.SetVirtualizationMode(matrix, VirtualizationMode.Recycling);
        var view = new ComboBox { Width = 110, ItemsSource = new[] { "Matris", "Liste" }, SelectedIndex = 0, ToolTip = "Görünüm: ürün × mağaza matrisi veya düz liste" }; System.Windows.Automation.AutomationProperties.SetName(view, "Görünüm");
        ChannelMatrix current = new(Array.Empty<ChannelMatrixColumn>(), Array.Empty<ChannelMatrixRow>());
        // #843: the legend explains only the states on screen, with their counts; each chip is a focusable tab stop whose tooltip opens from the keyboard.
        var legend = new WrapPanel { Tag = "channel-matrix-legend", Margin = new Thickness(4, 2, 4, 4) };
        void RenderLegend()
        {
            legend.Children.Clear(); var hc = SeverityStyle.IsHighContrast;
            foreach (var (entry, count) in ChannelMatrixLegend.Counts(current))
            {
                var chip = new Border { Tag = entry.Key, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 6, 2), BorderBrush = SeverityStyle.AccentBrush(entry.Level, hc), BorderThickness = new Thickness(SeverityStyle.For(entry.Level, hc).BorderWeight), Focusable = true, ToolTip = entry.Description, Child = new TextBlock { Text = $"{entry.Badge} ({count:N0})", Foreground = SeverityStyle.AccentBrush(entry.Level, hc) } };
                System.Windows.Input.KeyboardNavigation.SetIsTabStop(chip, true); ToolTipService.SetShowsToolTipOnKeyboardFocus(chip, true);
                System.Windows.Automation.AutomationProperties.SetName(chip, $"{entry.Word}: {count:N0} hücre"); System.Windows.Automation.AutomationProperties.SetHelpText(chip, entry.Description);
                legend.Children.Add(chip);
            }
            if (legend.Children.Count == 0) legend.Children.Add(new TextBlock { Text = "Gösterilecek hücre yok.", Opacity = 0.8 });
        }
        void Column(string header, string path, double width) => grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        Column("Ürün / SKU", "ProductName", 2); Column("Kanal", "ChannelName", 1); Column("Mağaza", "ShopId", 1); Column("Mapping", "MappingStatus", .8); Column("İlan ID", "ListingId", 1); Column("Sync", "SyncStatus", .8); Column("Bağlantı", "AuthStatus", 1); Column("Yetenekler", "Capabilities", 2); Column("Son hata", "LastError", 2);
        IReadOnlyList<ChannelListingMatrixRow> all = [];
        void RefreshGrid()
        {
            var filtered = ChannelListingMatrixService.Filter(all, query.Text, statusFilter.SelectedItem?.ToString() ?? "Tümü", channel.Text, shop.Text); grid.ItemsSource = filtered;
            current = ChannelMatrixPivot.Build(filtered, allowedStoreKeys?.Invoke()); RenderMatrix(matrix, current); RenderLegend();
            var isMatrix = view.SelectedIndex == 0; matrix.Visibility = isMatrix ? Visibility.Visible : Visibility.Collapsed; grid.Visibility = isMatrix ? Visibility.Collapsed : Visibility.Visible;
            status.Text = $"{filtered.Count:N0} satır · {current.Rows.Count:N0} ürün × {current.Columns.Count:N0} mağaza · {filtered.Count(x => x.MappingStatus == "MISSING")} mapping eksik · {filtered.Count(x => x.MappingStatus == "ERROR")} sync hatası · {filtered.Count(x => x.AuthStatus == "AUTH_ERROR")} bağlantı uyarısı";
        }
        async Task RefreshAsync() { status.Text = "Matris hazırlanıyor…"; all = await Task.Run(() => new ChannelListingMatrixService(directory).Build()); RefreshGrid(); }
        var refresh = AsyncButton(errors, "Matrisi yenile", RefreshAsync);
        var product = Button(errors, "Ürün havuzuna git", () => navigate?.Invoke("products"));
        var channelOpen = Button(errors, "Seçili kanala git", () =>
        {
            if (view.SelectedIndex == 0) { var cell = matrix.CurrentCell; if (!cell.IsValid || cell.Column is null || cell.Column.DisplayIndex < 2 || cell.Column.DisplayIndex - 2 >= current.Columns.Count) throw new InvalidOperationException("Önce matristen bir mağaza hücresi seçin."); navigate?.Invoke(current.Columns[cell.Column.DisplayIndex - 2].Channel); return; }
            if (grid.SelectedItem is not ChannelListingMatrixRow row) throw new InvalidOperationException("Önce matristen satır seçin."); navigate?.Invoke(row.Channel);
        });
        var bar = new WrapPanel(); bar.Children.Add(new TextBlock { Text = "Ara", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(query); bar.Children.Add(new TextBlock { Text = "Kanal", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(channel); bar.Children.Add(new TextBlock { Text = "Mağaza", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(shop); bar.Children.Add(statusFilter); bar.Children.Add(view); bar.Children.Add(refresh); bar.Children.Add(product); bar.Children.Add(channelOpen); panel.Children.Add(bar); panel.Children.Add(legend); panel.Children.Add(matrix); panel.Children.Add(grid); panel.Children.Add(status);
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
            var cellStyle = new Style(typeof(DataGridCell)); cellStyle.Setters.Add(new Setter(ToolTipService.ShowsToolTipOnKeyboardFocusProperty, true)); cellStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding($"Descriptions[{i}]")));
            target.Columns.Add(new DataGridTextColumn { Header = column.Header, Binding = new Binding($"Labels[{i}]"), Width = new DataGridLength(120), MinWidth = 90, CellStyle = cellStyle });
        }
        target.FrozenColumnCount = Math.Min(2, target.Columns.Count);
        target.LoadingRow -= MatrixRowLoaded; target.LoadingRow += MatrixRowLoaded;
        target.ItemsSource = matrix.Rows;
    }
    static void MatrixRowLoaded(object? sender, DataGridRowEventArgs e) { if (e.Row.Item is ChannelMatrixRow row) System.Windows.Automation.AutomationProperties.SetName(e.Row, $"{row.ProductName} ({row.Sku}): {row.Problems} sorunlu mağaza"); }
    static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 12) };
    static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(87, 112, 125)), Margin = new Thickness(4, 8, 4, 8) };
    static Button Button(ErrorSurface errors, string text, Action action) { var button = new Button { Content = text, Margin = new Thickness(3) }; button.Click += (_, _) => { try { errors.Clear(); action(); } catch (Exception error) { errors.Show(error, null, "connections", "Bağlantılara git"); } }; return button; }
    static Button AsyncButton(ErrorSurface errors, string text, Func<Task> action) { var button = new Button { Content = text, Margin = new Thickness(3) }; button.Click += async (_, _) => { try { button.IsEnabled = false; errors.Clear(); await action(); } catch (Exception error) { errors.Show(error, action, "connections", "Bağlantılara git"); } finally { button.IsEnabled = true; } }; return button; }
    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
}
