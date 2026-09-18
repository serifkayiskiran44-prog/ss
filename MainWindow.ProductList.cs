using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
    int productPageSize = 100;
    sealed record PageLimit(string Label, int Count);
    static readonly PageLimit[] PageLimits = [new("50", 50), new("100", 100), new("200", 200), new("500", 500), new("Tümü", 0)];
    readonly TextBlock productSelection = Hint("");
    readonly TextBlock productPageLabel = Hint("");
    readonly ComboBox productBulkChoice = new() { Width = 225, DisplayMemberPath = "Label", SelectedValuePath = "Kind" };
    readonly ComboBox productBulkValue = new() { Width = 230, IsEditable = true };
    readonly ComboBox productBulkScope = new() { Width = 225, ItemsSource = new[] { "Yalnız seçtiğim ürünler", "Filtreye uyan tüm ürünler" }, SelectedIndex = 0 };
    Expander? productBulkSection;
    record ProductAction(BulkProductOperationKind Kind, string Label);
    static readonly ProductAction[] ProductActions =
    [
        new(BulkProductOperationKind.Activate,"Aktif yap"), new(BulkProductOperationKind.Deactivate,"Pasif yap"),
        new(BulkProductOperationKind.SetCategory,"Kategori seç"), new(BulkProductOperationKind.SetBrand,"Marka seç"),
        new(BulkProductOperationKind.SetPrice,"Satış fiyatını değiştir"), new(BulkProductOperationKind.AdjustPricePercent,"Fiyatı yüzde artır / azalt"),
        new(BulkProductOperationKind.SetStock,"Stok adedini değiştir"), new(BulkProductOperationKind.AdjustStockDelta,"Stok adedi ekle / çıkar"),
        new(BulkProductOperationKind.SetName,"Ürün adını değiştir"), new(BulkProductOperationKind.SetDescription,"Açıklamayı değiştir"),
        new(BulkProductOperationKind.SetInvoiceName,"Fatura adını değiştir"),new(BulkProductOperationKind.SetSubtitle,"Alt başlığı değiştir"),new(BulkProductOperationKind.SetShelf,"Raf numarasını değiştir"),new(BulkProductOperationKind.SetGtin,"GTIN değiştir"),new(BulkProductOperationKind.SetMpn,"MPN değiştir"),
        new(BulkProductOperationKind.SetPriceLock,"Fiyat güncelleme kilidi"),new(BulkProductOperationKind.SetStockLock,"Stok güncelleme kilidi"),new(BulkProductOperationKind.SetNameLock,"Ürün adı güncelleme kilidi"),new(BulkProductOperationKind.SetDescriptionLock,"Açıklama güncelleme kilidi"),new(BulkProductOperationKind.SetImageLock,"Görsel güncelleme kilidi")
    ];

    StackPanel BuildProductListHeader()
    {
        var host = new StackPanel(); var bar = new WrapPanel(); host.Children.Add(bar);
        search.Width = 340; search.ToolTip = "Ürün adı, stok kodu, barkod, marka veya kategori"; bar.Children.Add(search);
        search.TextChanged += (_, _) => { productOffset = 0; searchTimer.Stop(); searchTimer.Start(); };
        bar.Children.Add(Button("Ara", () => { productOffset = 0; RefreshProducts(); }));
        bar.Children.Add(Button("Temizle", () => search.Clear()));
        bar.Children.Add(Button("+ Yeni ürün", () => OpenProductCard(true)));
        bar.Children.Add(Button("Ürün kartı", () => OpenProductCard(false)));
        bar.Children.Add(Button("Kolonlar", OpenProductColumnChooser));
        bar.Children.Add(Button("Excel'e aktar", ExportProductList));
        bar.Children.Add(Button("Excel ile güncelle", () => Navigate("excel")));
        bar.Children.Add(Button("BizimHesap", () => Navigate("bizimhesap")));
        AddProductFilters(host);
        var bulk = new WrapPanel { Margin = new Thickness(4) };
        productBulkChoice.ItemsSource = ProductActions; productBulkChoice.SelectedIndex = 0;
        productBulkChoice.SelectionChanged += (_, _) =>
        {
            var kind = (productBulkChoice.SelectedItem as ProductAction)?.Kind;
            productBulkValue.IsEnabled = kind is not (BulkProductOperationKind.Activate or BulkProductOperationKind.Deactivate);
            productBulkValue.ItemsSource = kind == BulkProductOperationKind.SetCategory ? store.Products().Select(p => p.Category).Distinct().Order().ToList() : kind == BulkProductOperationKind.SetBrand ? store.Products().Select(p => p.Brand).Distinct().Order().ToList() : IsLockAction(kind) ? new List<string>{"Kilitli","Serbest"} : null;
            productBulkValue.Text="";
        };
        bulk.Children.Add(productBulkChoice); bulk.Children.Add(productBulkValue); bulk.Children.Add(productBulkScope);
        bulk.Children.Add(Button("İşlemi önizle", PreviewProductBulk));
        bulk.Children.Add(Button("Sayfadakileri seç", () => products.SelectAll()));
        bulk.Children.Add(Button("Seçimi temizle", () => products.UnselectAll())); bulk.Children.Add(productSelection);
        productBulkSection = new Expander { Header = "Toplu işlemler", Content = bulk }; host.Children.Add(productBulkSection);
        host.Children.Add(Hint("Ürün kartı: çift tık • Seçim: Ctrl / Shift • Toplu değişiklikler: sağ tık • Görseller ve kanal durumu: +"));
        return host;
    }

    FrameworkElement BuildProductTable()
    {
        var panel = new DockPanel(); var footer = new WrapPanel(); DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom); panel.Children.Add(footer);
        footer.Children.Add(Button("‹ Önceki", () => { if (productPageSize == 0) return; productOffset = Math.Max(0, productOffset - productPageSize); RefreshProducts(); }));
        footer.Children.Add(productPageLabel);
        footer.Children.Add(Button("Sonraki ›", () => { if (productPageSize == 0) return; if (productOffset + productPageSize < productTotal) productOffset += productPageSize; RefreshProducts(); }));
        footer.Children.Add(Hint("Sayfa limiti"));
        var size = new ComboBox { ItemsSource = PageLimits, DisplayMemberPath = "Label", SelectedValuePath = "Count", SelectedValue = productPageSize, Width = 85 };
        size.SelectionChanged += (_, _) => { if (size.SelectedItem is PageLimit limit) { productPageSize = limit.Count; productOffset = 0; RefreshProducts(); } }; footer.Children.Add(size);
        panel.Children.Add(products); return panel;
    }

    void ConfigureProductGrid()
    {
        products.RowHeight = 27; products.GridLinesVisibility = DataGridGridLinesVisibility.All;
        products.HorizontalGridLinesBrush = Brushes.LightGray; products.VerticalGridLinesBrush = Brushes.LightGray;
        products.FrozenColumnCount = 3; products.CanUserReorderColumns = true; products.CanUserResizeColumns = true;
        products.RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.Collapsed;
        products.CellEditEnding += (_, args) =>
        {
            if (args.EditAction != DataGridEditAction.Commit || args.Row.Item is not CatalogProduct changed) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    store.SaveProduct(changed);
                    Log($"Hücre güncellendi: {changed.ProductIdLabel} • {changed.Name}");
                    RefreshProducts();
                }
                catch (Exception error)
                {
                    Log("Hücre kaydedilemedi: " + Safe(error));
                    MessageBox.Show(Safe(error), "Ürün hücresi", MessageBoxButton.OK, MessageBoxImage.Warning);
                    RefreshProducts();
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        };
        products.SelectionChanged += (_, _) => productSelection.Text = $"Seçilen: {products.SelectedItems.Count:N0}";
        var detail = new FrameworkElementFactory(typeof(Button)); detail.SetValue(ContentControl.ContentProperty, "+"); detail.SetValue(Control.PaddingProperty, new Thickness(0));
        detail.AddHandler(System.Windows.Controls.Button.ClickEvent, new RoutedEventHandler((sender, args) =>
        {
            if (sender is not Button button || FindParent<DataGridRow>(button) is not { } row) return;
            row.DetailsVisibility = row.DetailsVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            button.Content = row.DetailsVisibility == Visibility.Visible ? "−" : "+"; args.Handled = true;
        }));
        products.Columns.Insert(0, new DataGridTemplateColumn { Header = "", Width = 30, CellTemplate = new DataTemplate { VisualTree = detail } });
        var detailHost = new FrameworkElementFactory(typeof(ContentControl));
        products.RowDetailsTemplate = new DataTemplate { VisualTree = detailHost };
        products.LoadingRowDetails += (_, args) =>
        {
            if (args.Row.Item is not CatalogProduct product || args.DetailsElement is not ContentControl target) return;
            var line = new WrapPanel { Margin = new Thickness(10), MaxWidth = 1200 };
            foreach (var url in ProductImages(product).Take(5)) { var image = new Image { Width = 78, Height = 78, Margin = new Thickness(4), Tag = url }; line.Children.Add(image); _ = ShowXmlThumbnailAsync(image, url, lifetime.Token, product.Id); }
            var text = new StackPanel { Width = 650, Margin = new Thickness(12, 0, 0, 0) };
            text.Children.Add(Hint($"ID: {product.ProductIdLabel} • Marka ID: {product.BrandIdLabel} • Kategori ID: {product.CategoryIdLabel}"));
            text.Children.Add(Hint(product.Category));
            text.Children.Add(Hint(string.Join("\n", MarketplaceProductPanelModel.Build(product.Id, dataDirectory).Select(p => $"{p.Channel} / {p.ShopId}: {p.Status}"))));
            text.Children.Add(Hint($"Fiyat: {(product.LockPrice ? "kilitli" : "güncellenebilir")} • Stok: {(product.LockStock ? "kilitli" : "güncellenebilir")}"));
            line.Children.Add(text); target.Content = line;
        };
        var menu = new ContextMenu();
        void Item(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => { try { action(); } catch (Exception error) { Log(Safe(error)); } }; menu.Items.Add(item); }
        Item("Ürün kartını aç", () => OpenProductCard(false)); Item("Seçili ürünleri Excel'e aktar", ExportProductList); menu.Items.Add(new Separator());
        foreach (var action in ProductActions) Item(action.Label, () => { productBulkChoice.SelectedItem = action; productBulkScope.SelectedIndex = 0; productBulkSection!.IsExpanded = true; productBulkValue.Focus(); if (action.Kind is BulkProductOperationKind.Activate or BulkProductOperationKind.Deactivate) PreviewProductBulk(); });
        menu.Items.Add(new Separator()); Item("Ürün kimliğini kopyala", () => Clipboard.SetText(SelectedProduct().ProductIdLabel)); Item("SKU'yu kopyala", () => Clipboard.SetText(SelectedProduct().Sku));
        Item("Ürünü sil",DeleteSelectedProduct); products.ContextMenu = menu;
        products.PreviewMouseRightButtonDown += (_, e) => { if (e.OriginalSource is DependencyObject origin && FindParent<DataGridRow>(origin) is { } row && !row.IsSelected) { products.UnselectAll(); row.IsSelected = true; } };
        ApplyProductColumnPreferences();
        var editable = new HashSet<string> { "Name", "Brand", "Category", "Description", "Price", "Currency", "VatRate", "Stock", "Gtin", "Shelf", "Mpn", "InvoiceName", "Subtitle" };
        foreach (var column in products.Columns.OfType<DataGridBoundColumn>())
            column.IsReadOnly = column.Binding is Binding binding && !editable.Contains(binding.Path.Path);
    }

    List<CatalogProduct> FilteredProducts()
    {
        var result = new List<CatalogProduct>(); int offset = 0;
        while (true) { var page = store.Search(search.Text.Trim(), offset, 1000, productFilter); result.AddRange(page.Items); offset += page.Items.Count; if (offset >= page.Total || page.Items.Count == 0) break; }
        return result;
    }

    CatalogPage LoadProductPage(string query, int offset, CatalogFilter filter)
    {
        if (productPageSize > 0) return store.Search(query, offset, productPageSize, filter);
        var first = store.Search(query, 0, 1000, filter);
        if (first.Total <= first.Items.Count) return first;
        var rows = first.Items.ToList();
        for (var next = rows.Count; next < first.Total; next += 1000)
            rows.AddRange(store.Search(query, next, 1000, filter).Items);
        return new CatalogPage(rows, first.Total, first.InStock, first.Linked);
    }

    void ExportProductList()
    {
        var rows = products.SelectedItems.OfType<CatalogProduct>().ToList(); if (rows.Count == 0) rows = FilteredProducts();
        if (rows.Count == 0) throw new InvalidOperationException("Aktarılacak ürün yok.");
        var picker = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "Urunler.xlsx" };
        if (picker.ShowDialog(this) != true) return;
        CatalogExcel.Export(picker.FileName, rows); Log($"{rows.Count:N0} ürün Excel'e aktarıldı.");
    }

    static bool IsLockAction(BulkProductOperationKind? kind)=>kind is BulkProductOperationKind.SetPriceLock or BulkProductOperationKind.SetStockLock or BulkProductOperationKind.SetNameLock or BulkProductOperationKind.SetDescriptionLock or BulkProductOperationKind.SetImageLock;

    void PreviewProductBulk()
    {
        if (productBulkChoice.SelectedItem is not ProductAction action) return;
        var scope = productBulkScope.SelectedIndex == 0 ? BulkSelectionScope.Selected : BulkSelectionScope.FilteredAll;
        var rows = scope == BulkSelectionScope.Selected ? products.SelectedItems.OfType<CatalogProduct>().ToList() : FilteredProducts();
        if (rows.Count == 0) throw new InvalidOperationException("İşlem için ürün seçin veya 'Filtreye uyan tüm ürünler' kapsamını seçin.");
        var value = productBulkValue.Text;
        if(IsLockAction(action.Kind)) value=value=="Kilitli"?"true":value=="Serbest"?"false":throw new InvalidOperationException("Kilitli veya Serbest seçin.");
        if (action.Kind is BulkProductOperationKind.SetPrice or BulkProductOperationKind.AdjustPricePercent)
        { if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.GetCultureInfo("tr-TR"), out var number)) throw new InvalidOperationException("Geçerli bir fiyat / yüzde girin."); value = number.ToString(CultureInfo.InvariantCulture); }
        var operations = new BulkProductOperations(store, new ChannelProductsStore(dataDirectory));
        var snapshot = operations.Preview(rows, new(action.Kind, value), scope);
        var grid = new DataGrid { ItemsSource = snapshot.Lines, IsReadOnly = true, AutoGenerateColumns = false };
        foreach (var column in new[] { ("SKU", "Sku", 130d), ("Ürün", "Name", 230d), ("Önce", "Before", 180d), ("Sonra", "After", 180d), ("Durum", "Status", 80d), ("Açıklama", "Error", 260d) }) Column(grid, column.Item1, column.Item2, column.Item3);
        var header = new StackPanel(); header.Children.Add(Heading(action.Label));
        var ready = snapshot.Lines.Count(p => p.Status == "READY"); header.Children.Add(Hint($"{(scope == BulkSelectionScope.Selected ? "Seçili" : "Filtrelenmiş")} {rows.Count:N0} ürün • {ready:N0} değişiklik • Yerel ürün kataloğuna uygulanacak."));
        Window? dialog = null;
        var apply = Button($"{ready:N0} değişikliği uygula", () => { var result = operations.Apply(snapshot, true); RefreshProducts(); Log($"{result.Applied} ürün güncellendi; {result.Skipped} ürün atlandı."); dialog!.Close(); }); apply.IsEnabled = ready > 0 && snapshot.Lines.All(p => p.Status != "ERROR");
        header.Children.Add(apply); dialog = CreateXmlWindow("Toplu işlem önizlemesi", Dock(header, grid), 1150, 680); dialog.ShowDialog();
    }

    static IEnumerable<string> ProductImages(CatalogProduct product) => product.ImageUrls.Split(new[] { '|', '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct();
}
