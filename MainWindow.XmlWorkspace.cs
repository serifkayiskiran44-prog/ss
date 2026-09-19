using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
    readonly DataGrid xmlCategories = new() { IsReadOnly = false, SelectionMode = DataGridSelectionMode.Extended, RowHeight = 29 };
    readonly ObservableCollection<XmlCategoryRule> categoryRows = new();
    readonly Grid xmlMappingFields = new();
    readonly TextBlock xmlListStatus = Hint("Otomatik kaynaklar tek sırada çalışır: biri tamamlanınca sıradaki başlar. Süre, kaynağın yeniden sıraya girmeden önceki en kısa beklemesidir.");
    readonly ObservableCollection<string> itemPaths = new();
    Window? xmlDefinitionDialog;

    void BuildXmlWorkspace()
    {
        sources.RowHeight = 29;
        Column(sources, "Aktif", "Enabled", 55);
        Column(sources, "XML adı", "Name", 245);
        Column(sources, "Son çalışma", "LastRunUtc", 145);
        Column(sources, "Sonuç / hata mesajı", "LastStatus", 440);
        Column(sources, "Sıraya dahil", "AutoImport", 90);
        Column(sources, "Yeniden sıra (dk)", "IntervalMinutes", 115);
        sources.SelectionChanged += (_, _) => { if (sources.SelectedItem is XmlSource selected) SetSource(Clone(selected)); };
        sources.MouseDoubleClick += (_, e) => { if (e.OriginalSource is DependencyObject origin && FindParent<DataGridRow>(origin) != null) OpenXmlDefinition(); };
        var toolbar = new WrapPanel();
        toolbar.Children.Add(Button("+ Yeni XML", () => { SetSource(new() { Name = "Yeni XML", Currency = "TRY", PriceMode = "Formula", Formula = "x", MaximumStock = 99999 }); OpenXmlDefinition(); }));
        toolbar.Children.Add(Button("Tanımı aç", OpenXmlDefinition));
        toolbar.Children.Add(Button("Çoğalt", () => { CloneCurrentSource(); OpenXmlDefinition(); }));
        toolbar.Children.Add(Button("Yenile", () => RefreshSources(false)));
        toolbar.Children.Add(AsyncButton("▶ Seçili XML'i başlat", RunSelectedXmlAsync));
        toolbar.Children.Add(AsyncButton("Ürün önizlemesi", async () => { await InspectAsync(); await PreviewAsync(); _ = Dispatcher.BeginInvoke(new Action(OpenXmlPreview)); }));
        toolbar.Children.Add(Button("Şablon aç", () => { LoadSourceTemplate(); OpenXmlDefinition(); }));
        toolbar.Children.Add(Button("Şablon kaydet", ExportSourceTemplate));

        xmlCategories.ItemsSource = categoryRows;
        xmlCategories.FrozenColumnCount = 2;
        Column(xmlCategories, "XML kategorisi", "XmlCategory", 240); xmlCategories.Columns[0].IsReadOnly = true;
        var categoryEditor = new FrameworkElementFactory(typeof(ComboBox));
        categoryEditor.SetValue(ComboBox.IsEditableProperty, true);
        categoryEditor.SetValue(ComboBox.ItemsSourceProperty, store.Products().Select(p => p.Category).Where(c => c.Length > 0).Distinct().Order().ToList());
        categoryEditor.SetBinding(ComboBox.TextProperty, new Binding("TargetCategory") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        xmlCategories.Columns.Add(new DataGridTemplateColumn { Header = "Bizim kategorimiz", Width = 220, CellTemplate = new DataTemplate { VisualTree = categoryEditor } });
        xmlCategories.Columns.Add(new DataGridCheckBoxColumn { Header = "Aktif", Binding = new Binding("Enabled") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 50 });
        foreach (var channel in XmlCategoryRules.Channels)
        {
            EditableColumn(xmlCategories, channel + " satış", $"Prices[{channel}].SaleFormula", 160);
            EditableColumn(xmlCategories, channel + " üstü çizili", $"Prices[{channel}].ListFormula", 170);
        }
        var categoryToolbar = new WrapPanel();
        categoryToolbar.Children.Add(Button("Kategori / formülleri kaydet", SaveSource));
        categoryToolbar.Children.Add(Button("Tümünü seç", () => xmlCategories.SelectAll()));
        categoryToolbar.Children.Add(Button("Seçilileri aktif yap", () => SetCategoryEnabled(true)));
        categoryToolbar.Children.Add(Button("Seçilileri pasif yap", () => SetCategoryEnabled(false)));
        categoryToolbar.Children.Add(Button("İlk seçili satırın formüllerini kopyala", CopyCategoryFormulas));
        var categoryHeader = new StackPanel();
        categoryHeader.Children.Add(categoryToolbar);
        categoryHeader.Children.Add(Hint("x = XML alış fiyatı. Örnek: x*1.40+25. Boş satış formülü genel fiyatı kullanır; boş / 0 üstü çizili fiyatı kaldırır. Diğer pazaryerleri için tabloyu sağa kaydırın."));
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = new GridLength(0.38, GridUnitType.Star), MinHeight = 140 });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(0.62, GridUnitType.Star), MinHeight = 180 });
        layout.Children.Add(new GroupBox { Header = "XML kaynakları", Content = sources });
        Grid.SetRow(xmlListStatus, 1); layout.Children.Add(xmlListStatus);
        var categoryBox = new GroupBox { Header = "Kategori eşleştirme ve pazaryeri fiyat formülleri", Content = Dock(categoryHeader, xmlCategories) };
        Grid.SetRow(categoryBox, 2); layout.Children.Add(categoryBox);
        Tab("XML otomasyonu", Dock(toolbar, layout));
        BuildCompactXmlDefinition();
        foreach (var c in new[] { ("SKU", "Sku", 135d), ("Ürün", "Name", 260d), ("Alış", "Cost", 80d), ("Satış", "Price", 80d), ("Döviz", "Currency", 65d), ("Stok", "Stock", 60d), ("XML kategorisi", "XmlCategory", 220d), ("Bizim kategorimiz", "Category", 220d), ("Aktif", "Active", 55d) }) Column(preview, c.Item1, c.Item2, c.Item3);
    }

    static T? FindParent<T>(DependencyObject origin) where T : DependencyObject
    {
        for (var node = origin; node != null; node = VisualTreeHelper.GetParent(node)) if (node is T match) return match;
        return null;
    }

    static void EditableColumn(DataGrid grid, string header, string path, int width) => grid.Columns.Add(new DataGridTextColumn { Header = header, Width = width, Binding = new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });

    static void CompactField(Panel panel, string label, UIElement control, int labelWidth = 105)
    {
        var row = new Grid { Margin = new Thickness(2, 2, 6, 2), MinHeight = 27 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(labelWidth) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2) });
        Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row);
    }

    static TextBox CompactBoundField(Panel panel, string label, string property)
    {
        var box = new TextBox { MinHeight = 25, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(1) };
        box.SetBinding(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
        CompactField(panel, label, box); return box;
    }

    void BuildCompactXmlDefinition()
    {
        var top = new UniformGrid { Columns = 2 };
        CompactBoundField(top, "XML adı", "Name");
        CompactBoundField(top, "XML URL / dosya", "Location");
        itemPath.ItemsSource = itemPaths;
        CompactField(top, "Ürünler (kök)", itemPath);
        var fileAndActive = new WrapPanel();
        Flag(fileAndActive, "Aktif", "Enabled");
        fileAndActive.Children.Add(Button("XML dosyası seç", () => { var picker = new OpenFileDialog { Filter = "XML (*.xml)|*.xml" }; if (source != null && picker.ShowDialog(xmlDefinitionDialog) == true) { source.Location = picker.FileName; BindSource(); } }));
        top.Children.Add(fileAndActive); sourceGeneral.Children.Add(top);
        sourceGeneral.Children.Add(xmlMappingFields);
        var flags = new WrapPanel(); Flag(flags, "Adı güncelle", "UpdateName"); Flag(flags, "Açıklamayı güncelle", "UpdateDescription"); Flag(flags, "Görselleri güncelle", "UpdateImages"); Flag(flags, "Kategori / marka / ek alanları güncelle", "UpdateDetails"); sourceGeneral.Children.Add(flags);
        var auth = new StackPanel(); CompactField(auth, "Kullanıcı adı", xmlUser); CompactField(auth, "Şifre", xmlPassword);
        sourceGeneral.Children.Add(new Expander { Header = "XML adresi şifre istiyorsa", Content = auth });

        BuildXmlDefaults();
        sourceRules.Children.Add(Heading("XML sayı biçimi"));
        CompactField(sourceRules, "Fiyat ayracı", decimalSeparator);
        Flag(sourceRules, "XML alış fiyatı KDV dahil", "PriceIncludesVat");
        sourceRules.Children.Add(Heading("Stok ayarları"));
        CompactField(sourceRules, "Stok sayı ayracı", stockDecimalSeparator);
        var stock = new StackPanel();
        CompactBoundField(stock, "Yedekte tutulan adet", "SafetyStock");
        CompactBoundField(stock, "Bu adedin altında 0", "MinimumStock");
        CompactBoundField(stock, "En fazla gösterilen", "MaximumStock");
        var stockExample = Hint("");
        void UpdateStockExample() { if(source!=null) stockExample.Text=source.UseFixedStock?$"Her ürün için sabit {source.FixedStock} adet kullanılır.":$"Örnek: XML'de 30 adet varsa {source.SafetyStock} adet yedekte tutulur. Sonuç: {(30<=source.SafetyStock||30<source.MinimumStock?0:Math.Min(source.MaximumStock,30-source.SafetyStock))} adet. 0 yedek = stoktan düşme yapılmaz."; }
        stock.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_,_)=>Dispatcher.BeginInvoke(new Action(UpdateStockExample))));
        sourceRules.DataContextChanged+=(_,_)=>UpdateStockExample();
        sourceRules.Children.Add(stock); sourceRules.Children.Add(stockExample);
        var advancedStock = new StackPanel(); Flag(advancedStock, "XML yerine sabit adet kullan", "UseFixedStock"); CompactBoundField(advancedStock, "Sabit adet", "FixedStock");
        Flag(advancedStock, "Stok sayı yerine metin içeriyor", "StockIsText"); CompactBoundField(advancedStock, "Stok var sayılan metin", "AvailableStockText"); CompactBoundField(advancedStock, "Bu metin varsa adet", "AvailableStockQuantity");
        advancedStock.Children.Add(Hint("Sabit adet kapalıyken XML'deki ürün adedi kullanılır. Metin seçeneği yalnızca stok alanında 'var' gibi yazılar olan dosyalar içindir."));
        advancedStock.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,new TextChangedEventHandler((_,_)=>Dispatcher.BeginInvoke(new Action(UpdateStockExample))));
        advancedStock.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,new RoutedEventHandler((_,_)=>Dispatcher.BeginInvoke(new Action(UpdateStockExample))));
        advancedStock.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,new RoutedEventHandler((_,_)=>Dispatcher.BeginInvoke(new Action(UpdateStockExample))));
        sourceRules.Children.Add(new Expander{Header="Gelişmiş stok ayarları",Content=advancedStock});
        Flag(sourceRules, "Otomatik XML sırasına dahil et", "AutoImport"); CompactBoundField(sourceRules, "Yeniden sıraya giriş (dk)", "IntervalMinutes");
        sourceRules.Children.Add(Hint("Bu kaynak için ayrı zamanlayıcı açılmaz. MonoBridge tek kuyruğu çalıştırır; bir XML bitmeden diğeri başlamaz."));
        sourceRules.Children.Add(Hint("Kategori ve pazaryeri formüllerini ana XML listesinin alt tablosunda düzenleyin."));
        var extra = new StackPanel();
        CompactBoundField(extra, "Marka filtresi (;)", "BrandFilter"); CompactBoundField(extra, "Kategori filtresi (;)", "CategoryFilter");
        extra.Children.Add(AsyncButton("Kaynağı kontrol et", CheckXmlSourceAsync)); extra.Children.Add(xmlSourceHealth);
        sourceRules.Children.Add(new Expander { Header = "Kaynak filtreleri ve bağlantı kontrolü", Content = extra });
    }

    void RebuildCompactMappings()
    {
        xmlMappingFields.Children.Clear(); xmlMappingFields.RowDefinitions.Clear(); xmlMappingFields.ColumnDefinitions.Clear(); for(var i=0;i<3;i++)xmlMappingFields.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)}); sampleViews.Clear();
        foreach (var entry in mappings) AddXmlMapping(entry);
        itemPaths.Clear(); if (source != null && source.ItemPath.Length > 0) { itemPaths.Add(source.ItemPath); itemPath.Text = source.ItemPath; }
        RenderXmlSamples();
    }

    void OpenXmlDefinition()
    {
        if (source == null) throw new InvalidOperationException("Önce XML kaynağı seçin.");
        if (xmlDefinitionDialog != null) { xmlDefinitionDialog.Activate(); return; }
        var panel = new Grid { Margin = new Thickness(8) };
        panel.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new() { Width = new GridLength(1.15, GridUnitType.Star), MinWidth = 285 });
        var left = Scroll(sourceGeneral); var right = Scroll(sourceRules); Grid.SetColumn(right, 1); panel.Children.Add(left); panel.Children.Add(right);
        var actions = new WrapPanel();
        actions.Children.Add(Button("Kaydet", SaveSource));
        actions.Children.Add(AsyncButton("XML'i indir / alanları bul", InspectAsync));
        actions.Children.Add(AsyncButton("Önizle", async () => { if (xml.Length == 0 || loadedLocation != source.Location) await InspectAsync(); await PreviewAsync(); _ = Dispatcher.BeginInvoke(new Action(OpenXmlPreview)); }));
        actions.Children.Add(Button("Kapat", () => xmlDefinitionDialog?.Close()));
        var status = new TextBlock { Margin = new Thickness(10, 4, 10, 4), TextWrapping = TextWrapping.Wrap };
        status.SetBinding(TextBlock.TextProperty, new Binding("Text") { Source = StatusText });
        var root = new DockPanel(); DockPanel.SetDock(actions, System.Windows.Controls.Dock.Bottom); root.Children.Add(actions); DockPanel.SetDock(status, System.Windows.Controls.Dock.Bottom); root.Children.Add(status); root.Children.Add(panel);
        xmlDefinitionDialog = CreateXmlWindow("XML tanımı — " + source.Name, root, 1500, 950);
        try { xmlDefinitionDialog.ShowDialog(); }
        finally { StopThumbnailRequests(); left.Content = null; right.Content = null; xmlDefinitionDialog = null; }
    }

    Window CreateXmlWindow(string title, UIElement content, double width, double height) => new() { Title = title, Owner = xmlDefinitionDialog ?? this, Content = content, Width = Math.Min(width, SystemParameters.WorkArea.Width - 30), Height = Math.Min(height, SystemParameters.WorkArea.Height - 30), MinWidth = 850, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, Resources = Resources, FontFamily = FontFamily, FontSize = 12 };

    void RefreshCategoryRows(IEnumerable<CatalogProduct>? rows = null)
    {
        if (source == null) return;
        var categories = (rows ?? store.Products().Where(p => p.SourceId == source.Id)).Select(p => string.IsNullOrEmpty(p.XmlCategory) ? p.Category : p.XmlCategory).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        foreach (var category in categories)
            if (!source.CategoryRules.Any(r => string.Equals(r.XmlCategory, category, StringComparison.OrdinalIgnoreCase))) source.CategoryRules.Add(new() { XmlCategory = category, TargetCategory = category });
        categoryRows.Clear();
        foreach (var row in source.CategoryRules)
        {
            foreach (var channel in XmlCategoryRules.Channels) row.Prices.TryAdd(channel, new());
            categoryRows.Add(row);
        }
        xmlListStatus.Text = $"{source.Name} • {categoryRows.Count:N0} kategori • Kaynak tanımı: çift tık. Formüller: hücreye çift tık. Kaydet → XML'i başlat.";
    }

    void CommitCategoryEdits() { xmlCategories.CommitEdit(DataGridEditingUnit.Cell, true); xmlCategories.CommitEdit(DataGridEditingUnit.Row, true); ValidBindings(xmlCategories); }
    void SetCategoryEnabled(bool enabled) { CommitCategoryEdits(); foreach (var row in xmlCategories.SelectedItems.OfType<XmlCategoryRule>()) row.Enabled = enabled; xmlCategories.Items.Refresh(); previewRevision = ""; }
    void CopyCategoryFormulas()
    {
        CommitCategoryEdits(); var selected = xmlCategories.SelectedItems.OfType<XmlCategoryRule>().ToList();
        if (selected.Count < 2) throw new InvalidOperationException("Önce kopyalanacak satırı, ardından Ctrl ile hedef kategorileri seçin.");
        foreach (var target in selected.Skip(1)) target.Prices = Clone(selected[0].Prices);
        xmlCategories.Items.Refresh(); previewRevision = "";
    }

    async Task RunSelectedXmlAsync()
    {
        SaveSource(); await InspectAsync(); await PreviewAsync(); preview.SelectAll(); await ImportAsync();
    }

    void OpenXmlPreview()
    {
        var buttons = new WrapPanel(); buttons.Children.Add(Button("Tüm ürünleri seç", () => preview.SelectAll()));
        buttons.Children.Add(AsyncButton("Seçilileri ürünlere aktar", ImportAsync)); buttons.Children.Add(previewStatus);
        var root = Dock(buttons, preview); var dialog = CreateXmlWindow("XML ürün önizlemesi", root, 1200, 700);
        try { dialog.ShowDialog(); } finally { root.Children.Remove(preview); root.Children.Remove(buttons); buttons.Children.Remove(previewStatus); }
    }

    void OpenSelectedProductCard() => OpenProductCard(false);
}
