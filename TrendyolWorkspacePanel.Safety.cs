using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;

public sealed record TrendyolBrandSafetyReviewRow(string BrandName, int ProductCount);

public sealed partial class TrendyolWorkspacePanel
{
    readonly ListBox safetyBrands = new()
    {
        Name = "TrendyolSafetyBrands", SelectionMode = SelectionMode.Multiple,
        Margin = new(3), MinHeight = 130
    };
    readonly TextBox safetyBrandSearch = Box();
    readonly ComboBox safetyClearField = Combo();
    readonly TextBlock safetySelectionSummary = T("0 marka · 0 ürün");
    readonly TextBlock safetyFormHeading = T("Önce bir veya daha fazla marka seçin.");
    readonly DataGrid safetyReview = Grid("TrendyolSafetyReview", ("Seçili marka", "BrandName", 175), ("Ürün", "ProductCount", 65));
    readonly Dictionary<long, TextBox> safetyFields = new();
    readonly Dictionary<string, int> safetyBrandProductCounts = new(StringComparer.Ordinal);
    List<string> safetyBrandNames = [];
    Button saveBrandSafety = null!;
    Button clearBrandSafetyField = null!;
    string safetySellerId = "";
    long safetyRevision;
    bool refreshingSafety;

    FrameworkElement BuildBrandSafety()
    {
        safetyBrandSearch.Name = "TrendyolSafetyBrandSearch";
        safetySelectionSummary.Name = "TrendyolSafetySelectionSummary";
        safetySelectionSummary.FontWeight = FontWeights.SemiBold;
        safetyFormHeading.FontWeight = FontWeights.SemiBold;
        safetyReview.MinHeight = 60;
        safetyReview.Height = 70;
        safetyReview.MaxHeight = 110;
        safetyReview.SelectionMode = DataGridSelectionMode.Single;
        foreach (var field in TrendyolProductSafety.Fields)
        {
            var input = Box();
            input.Name = "TrendyolSafetyField" + field.Id;
            input.MaxLength = 4000;
            if (field.Id is 1296 or 1304 or 1299 or 1302 or 1116)
            {
                input.AcceptsReturn = true;
                input.TextWrapping = TextWrapping.Wrap;
                input.MinHeight = 54;
                input.MaxHeight = 110;
                input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            safetyFields.Add(field.Id, input);
        }

        var root = new DockPanel { Margin = new(4) };
        var introduction = T("Yeni ve onaysız ürünlerde marka bilgileri kullanılır; ürüne girilen değerler önceliklidir. Kayıt yereldir, satıştaki ürünler değişmez. Gönderim ayrıca önizlenip onaylanır.", true);
        DockPanel.SetDock(introduction, Dock.Top);
        root.Children.Add(introduction);

        var brandList = new DockPanel();
        var brandTools = new StackPanel();
        var searchRow = new DockPanel();
        var searchLabel = T("Ara");
        DockPanel.SetDock(searchLabel, Dock.Left);
        searchRow.Children.Add(searchLabel);
        var refreshBrands = B("Yenile", Reload);
        refreshBrands.ToolTip = "Marka listesini yenile";
        DockPanel.SetDock(refreshBrands, Dock.Right);
        searchRow.Children.Add(refreshBrands);
        safetyBrandSearch.MinWidth = 120;
        safetyBrandSearch.ToolTip = "Marka ara";
        searchRow.Children.Add(safetyBrandSearch);
        brandTools.Children.Add(searchRow);
        var selectionActions = new WrapPanel();
        var selectAll = B("Tüm markaları seç", SelectAllSafetyBrands);
        selectAll.Name = "TrendyolSafetySelectAll";
        selectAll.ToolTip = "Aramayı temizler ve bütün markaları seçer";
        var clear = B("Seçimi kaldır", () => safetyBrands.UnselectAll());
        clear.Name = "TrendyolSafetyClearSelection";
        selectionActions.Children.Add(selectAll);
        selectionActions.Children.Add(clear);
        brandTools.Children.Add(selectionActions);
        safetyBrands.ToolTip = "Markalara tıklayarak çoklu seçim yapın";
        DockPanel.SetDock(brandTools, Dock.Top);
        brandList.Children.Add(brandTools);
        var review = new StackPanel();
        review.Children.Add(safetySelectionSummary);
        review.Children.Add(new Expander
        {
            Name = "TrendyolSafetyReviewExpander", Header = "Seçili markaları incele", Margin = new(3),
            Content = safetyReview
        });
        DockPanel.SetDock(review, Dock.Bottom);
        brandList.Children.Add(review);
        brandList.Children.Add(safetyBrands);

        var editor = new DockPanel();
        var saveArea = new StackPanel();
        saveArea.Children.Add(T("Dolu alanlar kaydedilir; boş alanlar mevcut değerleri korur.", true));
        saveBrandSafety = Primary(B("Seçili markaların dolu alanlarını kaydet", SaveBrandSafety));
        saveBrandSafety.Name = "TrendyolSaveBrandSafety";
        saveBrandSafety.IsEnabled = false;
        saveArea.Children.Add(saveBrandSafety);
        var removalTools = new StackPanel();
        removalTools.Children.Add(T("Boş bırakmak silmez. Yanlış kaydedilen alanı buradan kaldırabilirsiniz.", true));
        safetyClearField.Name = "TrendyolSafetyClearField";
        safetyClearField.ItemsSource = TrendyolProductSafety.Fields;
        safetyClearField.SelectedIndex = -1;
        safetyClearField.Width = 270;
        safetyClearField.ToolTip = "Kaldırılacak kayıtlı alanı seçin";
        clearBrandSafetyField = B("Seçili alanı markalardan kaldır", ClearBrandSafetyField);
        clearBrandSafetyField.Name = "TrendyolClearBrandSafetyField";
        clearBrandSafetyField.IsEnabled = false;
        var removeActions = new WrapPanel();
        removeActions.Children.Add(safetyClearField);
        removeActions.Children.Add(clearBrandSafetyField);
        removalTools.Children.Add(removeActions);
        saveArea.Children.Add(new Expander
        {
            Name = "TrendyolSafetyRemovalExpander", Header = "Kayıtlı alanı kaldır", Margin = new(3),
            Content = removalTools
        });
        DockPanel.SetDock(saveArea, Dock.Bottom);
        editor.Children.Add(saveArea);
        var form = new StackPanel();
        form.Children.Add(safetyFormHeading);
        form.Children.Add(SafetyFieldSection("Üretici", 1198, 1294, 1296));
        form.Children.Add(SafetyFieldSection("1. ithalatçı / yetkili temsilci / ifa hizmet sağlayıcı", 1216, 1305, 1304));
        form.Children.Add(new Expander
        {
            Header = "2. ithalatçı / yetkili temsilci / ifa hizmet sağlayıcı", Margin = new(5),
            Content = SafetyFieldSection("İkinci kuruluş", 1297, 1298, 1299)
        });
        form.Children.Add(new Expander
        {
            Header = "3. ithalatçı / yetkili temsilci / ifa hizmet sağlayıcı", Margin = new(5),
            Content = SafetyFieldSection("Üçüncü kuruluş", 1300, 1301, 1302)
        });
        form.Children.Add(SafetyFieldSection("Ürün güvenliği", 1116));
        editor.Children.Add(Scroll(form));

        var columns = new System.Windows.Controls.Grid();
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(330) });
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var brandsFrame = Section("Markalar ve kayıt kapsamı", brandList);
        var editorFrame = Section("Kaydedilecek bilgiler", editor);
        columns.Children.Add(brandsFrame);
        System.Windows.Controls.Grid.SetColumn(editorFrame, 1);
        columns.Children.Add(editorFrame);
        root.Children.Add(columns);

        safetyBrands.SelectionChanged += (_, _) => { if (!refreshingSafety) LoadSafetyBrandSelection(); };
        safetyBrandSearch.TextChanged += (_, _) => { if (!refreshingSafety) FilterSafetyBrands(); };
        safetyClearField.SelectionChanged += (_, _) => { if (!refreshingSafety) UpdateSafetySelectionSummary(); };
        foreach (var input in safetyFields.Values)
            input.TextChanged += (_, _) => { if (!refreshingSafety) UpdateSafetySelectionSummary(); };
        return root;
    }

    FrameworkElement SafetyFieldSection(string title, params long[] ids)
    {
        var fields = new StackPanel();
        foreach (var id in ids)
            Field(fields, TrendyolProductSafety.Fields.Single(f => f.Id == id).Name, safetyFields[id]);
        return Section(title, fields);
    }

    void RefreshBrandSafety()
    {
        var selected = safetySellerId == state.SellerId
            ? safetyBrands.SelectedItems.Cast<string>().Select(TrendyolMatching.Normalize).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        safetySellerId = state.SellerId;
        safetyRevision = state.Revision;
        safetyClearField.SelectedIndex = -1;
        var products = catalog.Products().Where(p => !string.IsNullOrWhiteSpace(p.Brand)).ToArray();
        safetyBrandProductCounts.Clear();
        foreach (var group in products.GroupBy(p => TrendyolMatching.Normalize(p.Brand)))
            safetyBrandProductCounts[group.Key] = group.Count();
        safetyBrandNames = products.Select(p => p.Brand.Trim())
            .Concat(state.BrandSafetyTemplates.Select(t => t.BrandName))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .DistinctBy(TrendyolMatching.Normalize)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        FilterSafetyBrands(selected);
    }

    void FilterSafetyBrands(HashSet<string>? selected = null)
    {
        selected ??= safetyBrands.SelectedItems.Cast<string>().Select(TrendyolMatching.Normalize).ToHashSet(StringComparer.Ordinal);
        var query = TrendyolMatching.Normalize(safetyBrandSearch.Text);
        refreshingSafety = true;
        try
        {
            safetyBrands.ItemsSource = safetyBrandNames.Where(n => query.Length == 0 || TrendyolMatching.Normalize(n).Contains(query, StringComparison.Ordinal)).ToList();
            safetyBrands.UnselectAll();
            foreach (var name in safetyBrands.Items.Cast<string>().Where(n => selected.Contains(TrendyolMatching.Normalize(n))))
                safetyBrands.SelectedItems.Add(name);
        }
        finally { refreshingSafety = false; }
        LoadSafetyBrandSelection();
    }

    void SelectAllSafetyBrands()
    {
        refreshingSafety = true;
        try
        {
            safetyBrandSearch.Clear();
            safetyBrands.ItemsSource = safetyBrandNames;
            safetyBrands.SelectAll();
        }
        finally { refreshingSafety = false; }
        LoadSafetyBrandSelection();
    }

    void LoadSafetyBrandSelection()
    {
        refreshingSafety = true;
        try
        {
            foreach (var input in safetyFields.Values) input.Clear();
            var selected = safetyBrands.SelectedItems.Cast<string>().ToArray();
            safetyFormHeading.Text = selected.Length switch
            {
                0 => "Önce bir veya daha fazla marka seçin.",
                1 => selected[0] + " · kayıtlı bilgiler",
                _ => "Yeni ortak değerler · " + selected.Length + " marka"
            };
            if (selected.Length == 1)
            {
                var template = state.BrandSafetyTemplates.SingleOrDefault(t => TrendyolMatching.Normalize(t.BrandName) == TrendyolMatching.Normalize(selected[0]));
                if (template != null)
                    foreach (var field in template.Values)
                        if (safetyFields.TryGetValue(field.Key, out var input)) input.Text = field.Value;
            }
        }
        finally { refreshingSafety = false; }
        UpdateSafetySelectionSummary();
    }

    void UpdateSafetySelectionSummary()
    {
        var selected = safetyBrands.SelectedItems.Cast<string>().ToArray();
        var review = selected.Select(name => new TrendyolBrandSafetyReviewRow(name, safetyBrandProductCounts.GetValueOrDefault(TrendyolMatching.Normalize(name)))).ToList();
        var fieldCount = safetyFields.Values.Count(input => !string.IsNullOrWhiteSpace(input.Text));
        safetyReview.ItemsSource = review;
        safetySelectionSummary.Text = $"{selected.Length} marka · {review.Sum(row => row.ProductCount)} ürün · {fieldCount} dolu alan";
        saveBrandSafety.IsEnabled = selected.Length > 0 && fieldCount > 0 && safetySellerId.Length > 0;
        clearBrandSafetyField.IsEnabled = selected.Length > 0 && safetyClearField.SelectedItem is TrendyolSafetyField && safetySellerId.Length > 0;
    }

    void SaveBrandSafety()
    {
        var selected = safetyBrands.SelectedItems.Cast<string>().ToArray();
        var values = safetyFields.Where(field => !string.IsNullOrWhiteSpace(field.Value.Text)).ToDictionary(field => field.Key, field => field.Value.Text.Trim());
        if (selected.Length == 0 || values.Count == 0) throw new InvalidOperationException("En az bir marka seçin ve kaydedilecek alanı doldurun.");
        var current = CurrentSafetyState();
        store.SaveBrandSafety(current.SellerId, safetyRevision, selected, values);
        state = store.Load(current.SellerId);
        InvalidatePreview();
        RefreshBrandSafety();
        status.Text = $"{selected.Length} markanın {values.Count} dolu alanı kaydedildi. Boş alanların mevcut değerleri korundu; Trendyol'a gönderim yapılmadı.";
    }

    TrendyolWorkspaceState CurrentSafetyState()
    {
        if (Account().SupplierId != safetySellerId || state.Revision != safetyRevision)
            throw new InvalidOperationException("Marka ayarları veya mağaza değişti; listeyi yenileyip tekrar deneyin.");
        return CurrentDeliveryState();
    }

    void ClearBrandSafetyField()
    {
        var selected = safetyBrands.SelectedItems.Cast<string>().ToArray();
        if (selected.Length == 0 || safetyClearField.SelectedItem is not TrendyolSafetyField field)
            throw new InvalidOperationException("En az bir marka ve kaldırılacak alanı seçin.");
        _ = CurrentSafetyState();
        if (MessageBox.Show(Window.GetWindow(this),
            $"'{field.Name}' alanının kayıtlı değeri {selected.Length} markadan kaldırılsın mı?\nDiğer alanlar korunur. Bu işlem Trendyol'a gönderim yapmaz.",
            "Marka bilgisini kaldır", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var current = CurrentSafetyState();
        store.ClearBrandSafetyFields(current.SellerId, safetyRevision, selected, new[] { field.Id });
        state = store.Load(current.SellerId);
        InvalidatePreview();
        RefreshBrandSafety();
        status.Text = $"'{field.Name}' alanı {selected.Length} markanın yerel ayarlarından kaldırıldı. Diğer alanlar korundu; Trendyol'a gönderim yapılmadı.";
    }
}
