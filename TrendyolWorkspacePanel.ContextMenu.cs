using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;

public sealed partial class TrendyolWorkspacePanel
{
    void ConfigureProductContextMenu()
    {
        var menu = new ContextMenu();
        Add("Kategori eşleştir", OpenCategoryPicker);
        Add("Marka seç", OpenBrandPicker);
        Add("Barkod / GTIN ile kategori ve marka bul", OpenCatalogMatchPreview);
        menu.Items.Add(new Separator());
        Add("Barkodla eşleştir / oluştur", MatchProducts);
        Add("Ürün kartını aç", OpenProductEditor);
        menu.Items.Add(new Separator());
        Add("Fiyat ve stok önizle", () => PreviewOperation(TrendyolOperation.PriceAndStock));
        Add("Yalnız fiyat önizle", () => PreviewOperation(TrendyolOperation.Price));
        Add("Yalnız stok önizle", () => PreviewOperation(TrendyolOperation.Stock));
        products.ContextMenu = menu;
        products.PreviewMouseRightButtonDown += (_, e) =>
        {
            var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row is not null && !row.IsSelected) { products.UnselectAll(); row.IsSelected = true; }
        };
        void Add(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => { try { action(); } catch (Exception ex) { status.Text = Safe(ex); } };
            menu.Items.Add(item);
        }
    }

    static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null && child is not T) child = VisualTreeHelper.GetParent(child);
        return child as T;
    }

    void OpenCategoryPicker()
    {
        var values = state.Categories.Where(c => c.IsLeaf).OrderBy(c => c.Path).ToArray();
        if (values.Length == 0) throw new InvalidOperationException("Önce Mağaza ayarları bölümünden kategori ve marka listesini yenileyin.");
        var selected = Pick("Trendyol kategori eşleştirme", values, c => c.Path, c => c.Id.ToString());
        if (selected is null) return;
        ApplyTaxonomySelection(selected.Id, null, $"Kategori: {selected.Path}");
    }

    void OpenBrandPicker()
    {
        var values = state.Brands.OrderBy(b => b.Name).ToArray();
        if (values.Length == 0) throw new InvalidOperationException("Önce Mağaza ayarları bölümünden kategori ve marka listesini yenileyin.");
        var selected = Pick("Trendyol marka seçimi", values, b => b.Name, b => b.Id.ToString());
        if (selected is null) return;
        ApplyTaxonomySelection(null, selected.Id, $"Marka: {selected.Name}");
    }

    T? Pick<T>(string title, IReadOnlyList<T> values, Func<T, string> label, Func<T, string> id) where T : class
    {
        var search = Box(); search.Name = "TrendyolTaxonomySearch";
        var list = new ListBox { MinHeight = 330, Margin = new(4) };
        var result = new TextBlock { Margin = new(5), Foreground = Brushes.SlateGray };
        T? chosen = null;
        void Refresh()
        {
            var query = search.Text.Trim();
            var filtered = values.Where(v => query.Length == 0 || label(v).Contains(query, StringComparison.CurrentCultureIgnoreCase) || id(v).Contains(query, StringComparison.OrdinalIgnoreCase)).Take(1000).ToArray();
            list.ItemsSource = filtered;
            result.Text = $"{filtered.Length:N0} sonuç · {values.Count:N0} kayıt";
        }
        var panel = new DockPanel { Margin = new Thickness(10) };
        var top = new StackPanel(); top.Children.Add(new TextBlock { Text = $"{products.SelectedItems.Count} seçili ürüne uygulanacak", Margin = new Thickness(5), Foreground = Brushes.SlateGray }); top.Children.Add(search); top.Children.Add(result); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "Seçili ürünlere uygula", Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
        var cancel = new Button { Content = "Vazgeç", Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
        actions.Children.Add(cancel); actions.Children.Add(apply); DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions); panel.Children.Add(list);
        var window = new Window { Title = title, Content = panel, Width = 760, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this) };
        list.ItemTemplate = BuildPickerTemplate(label, id);
        search.TextChanged += (_, _) => Refresh(); search.KeyDown += (_, e) => { if (e.Key == Key.Enter) Refresh(); };
        apply.Click += (_, _) => { if (list.SelectedItem is T value) { chosen = value; window.DialogResult = true; } };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is T value) { chosen = value; window.DialogResult = true; } };
        cancel.Click += (_, _) => window.DialogResult = false;
        Refresh(); window.ShowDialog(); return chosen;
    }

    static DataTemplate BuildPickerTemplate<T>(Func<T, string> label, Func<T, string> id)
    {
        var template = new DataTemplate();
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new PickerTextConverter<T>(label, id) });
        factory.SetValue(FrameworkElement.MarginProperty, new Thickness(5));
        template.VisualTree = factory; return template;
    }

    sealed class PickerTextConverter<T>(Func<T, string> label, Func<T, string> id) : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is T item ? $"{label(item)}   ·   ID {id(item)}" : "";
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }

    void ApplyTaxonomySelection(long? categoryId, long? brandId, string description)
    {
        var ids = SelectedProductIds(); var next = store.Load(Account().SupplierId);
        foreach (var id in ids)
        {
            var profile = next.Profiles.SingleOrDefault(p => p.ProductId == id);
            if (profile is null) { profile = new() { ProductId = id }; next.Profiles.Add(profile); }
            if (categoryId.HasValue) profile.CategoryId = categoryId;
            if (brandId.HasValue) profile.BrandId = brandId;
        }
        store.Save(next); Reload(); status.Text = $"{description} · {ids.Length} seçili ürüne kaydedildi. Trendyol'a henüz gönderilmedi.";
    }

    void OpenCatalogMatchPreview()
    {
        var selectedRows = products.SelectedItems.Cast<TrendyolProductRow>().ToArray();
        if (selectedRows.Length == 0) throw new InvalidOperationException("Ürünler listesinden en az bir ürün seçin.");
        var next = store.Load(Account().SupplierId);
        var inputs = selectedRows.Select(r => new TrendyolCatalogMatchInput(r.Id, r.Sku, r.Barcode, r.Gtin)).ToArray();
        var rows = TrendyolCatalogMatch.Preview(next, inputs).ToArray();
        var grid = Grid("TrendyolCatalogMatchPreview", ("SKU", "Sku", 120), ("Yerel barkod / GTIN", "LocalCode", 190), ("Trendyol barkodu", "RemoteBarcode", 150), ("Trendyol ürünü", "RemoteTitle", 260), ("Kategori ID", "CategoryId", 90), ("Marka ID", "BrandId", 90), ("Sonuç", "Detail", 320));
        grid.ItemsSource = rows; grid.SelectionMode = DataGridSelectionMode.Extended;
        var summary = T($"{rows.Count(r => r.CanApply)} / {rows.Length} ürün uygulanabilir. Eşleşme; yerel barkod ve GTIN'in mağazadaki Trendyol barkoduyla birebir karşılığına göre yapılır.", true);
        var panel = new DockPanel { Margin = new Thickness(10) }; DockPanel.SetDock(summary, Dock.Top); panel.Children.Add(summary);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Vazgeç", Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
        var apply = new Button { Content = "Barkoda göre eşleştir", IsEnabled = rows.Any(r => r.CanApply), Margin = new Thickness(4), Padding = new Thickness(12, 6, 12, 6) };
        actions.Children.Add(cancel); actions.Children.Add(apply); DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions); panel.Children.Add(grid);
        var window = new Window { Title = "Barkod / GTIN ile Trendyol eşleştirme", Content = panel, Width = 1250, Height = 590, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this) };
        cancel.Click += (_, _) => window.DialogResult = false;
        apply.Click += (_, _) => { TrendyolCatalogMatch.Apply(next, inputs); store.Save(next); window.DialogResult = true; };
        if (window.ShowDialog() == true) { Reload(); status.Text = $"{rows.Count(r => r.CanApply)} ürünün Trendyol barkodu, kategori ID ve marka ID eşleştirildi. Trendyol'a henüz gönderilmedi."; }
    }
}
