using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;
namespace TrMarketplaceHubDesktop;
public partial class MainWindow {
 CatalogFilter productFilter=new();
 void AddProductFilters(Panel host){
  var panel=new WrapPanel();
  var status=new ComboBox{ItemsSource=new[]{"Tümü","Aktif","Pasif"},SelectedIndex=0,Width=100};
  var brand=new TextBox{Width=130,MaxLength=6000};var category=new TextBox{Width=130,MaxLength=6000};var sku=new TextBox{Width=160,MaxLength=6000};
  var description=new ComboBox{ItemsSource=new[]{"Tümü","Dolu","Boş"},SelectedIndex=0,Width=90};
  var image=new ComboBox{ItemsSource=new[]{"Tümü","Var","Yok"},SelectedIndex=0,Width=90};
  var minPrice=new TextBox{Width=85};var maxPrice=new TextBox{Width=85};var sort=new ComboBox{ItemsSource=new[]{"Name","Sku","Barcode","Price","Cost","Stock","Updated"},SelectedIndex=0,Width=95};var descSort=new CheckBox{Content="Azalan"};
  // The sort controls are part of the persisted product list layout (#792); exposed to the layout code as delegates.
  applyProductSort=(by,descending)=>{sort.SelectedItem=by;descSort.IsChecked=descending;};captureProductSort=()=>(sort.SelectedItem?.ToString()??"Name",descSort.IsChecked==true);
  foreach(var (title,control) in new (string,Control)[]{("Durum",status),("Marka (; ile ayır)",brand),("Kategori (; ile ayır)",category),("SKU (; ile ayır)",sku),("Açıklama",description),("Görsel kaydı",image),("Min fiyat",minPrice),("Max fiyat",maxPrice),("Sıralama",sort),("",descSort)}){
   var group=new StackPanel{Margin=new Thickness(4)};group.Children.Add(new TextBlock{Text=title});group.Children.Add(control);panel.Children.Add(group);
  }
  static string[] Values(string text)=>text.Split(new[]{';','\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct().ToArray();
  static bool? State(ComboBox box)=>box.SelectedIndex==0?null:box.SelectedIndex==1;
  decimal? Number(string text)=>decimal.TryParse(text,System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.CurrentCulture,out var value)?value:null;
  CatalogFilter Current()=>new(){Active=State(status),Brands=Values(brand.Text),Categories=Values(category.Text),Skus=Values(sku.Text),DescriptionPresent=State(description),ImagePresent=State(image),MinPrice=Number(minPrice.Text),MaxPrice=Number(maxPrice.Text),SortBy=sort.SelectedItem?.ToString()??"Name",SortDescending=descSort.IsChecked==true};
  void Apply(CatalogFilter filter){status.SelectedIndex=filter.Active is null?0:filter.Active.Value?1:2;description.SelectedIndex=filter.DescriptionPresent is null?0:filter.DescriptionPresent.Value?1:2;image.SelectedIndex=filter.ImagePresent is null?0:filter.ImagePresent.Value?1:2;brand.Text=string.Join(';',filter.Brands);category.Text=string.Join(';',filter.Categories);sku.Text=string.Join(';',filter.Skus);minPrice.Text=filter.MinPrice?.ToString(System.Globalization.CultureInfo.CurrentCulture)??"";maxPrice.Text=filter.MaxPrice?.ToString(System.Globalization.CultureInfo.CurrentCulture)??"";sort.SelectedItem=filter.SortBy;descSort.IsChecked=filter.SortDescending;}
  var filterStore=new CatalogFilterStore(dataDirectory);var saved=new ComboBox{Width=170,DisplayMemberPath="Name"};var filterName=new TextBox{Width=140,ToolTip="Kaydedilecek filtre adı"};
  void ReloadSaved(){saved.ItemsSource=filterStore.List();}
  panel.Children.Add(Button("Filtreleri uygula",()=>{productFilter=Current();productOffset=0;RefreshProducts();}));
  panel.Children.Add(Button("Filtreleri temizle",()=>{status.SelectedIndex=description.SelectedIndex=image.SelectedIndex=0;brand.Clear();category.Clear();sku.Clear();productFilter=new();productOffset=0;RefreshProducts();}));
  panel.Children.Add(new TextBlock{Text="Kayıtlı filtre",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,2,0)});panel.Children.Add(saved);panel.Children.Add(filterName);
  panel.Children.Add(Button("Filtreyi kaydet",()=>{filterStore.Save(filterName.Text,Current());ReloadSaved();filterName.Clear();}));
  panel.Children.Add(Button("Filtreyi yükle",()=>{if(saved.SelectedItem is not SavedCatalogFilter selected)throw new InvalidOperationException("Kayıtlı filtre seçin.");Apply(selected.Filter);productFilter=selected.Filter;productOffset=0;RefreshProducts();}));
  panel.Children.Add(Button("Filtreyi sil",()=>{if(saved.SelectedItem is not SavedCatalogFilter selected)throw new InvalidOperationException("Kayıtlı filtre seçin.");filterStore.Delete(selected.Name);ReloadSaved();}));
  panel.Children.Add(Button("Kolon görünürlüğü",OpenProductColumnChooser));
  ReloadSaved();
  host.Children.Add(new Expander{Header="Detaylı ürün arama",Content=panel,HorizontalAlignment=HorizontalAlignment.Stretch});
 }
 void ApplyProductColumnPreferences()
 {
  var hidden = (uiPreferences.Get("columns:products") ?? "").Split('\u001f', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
  foreach (var column in products.Columns)
  {
   var header = column.Header?.ToString() ?? "";
   column.Visibility = hidden.Contains(header) ? Visibility.Collapsed : Visibility.Visible;
  }
 }
 // Product list column layout persistence (#792). The previous version persisted only hidden headers, and applied
 // them from AddProductFilters -- which BuildProducts calls before a single column exists, so nothing was ever
 // restored after a restart. The layout (order, width, visibility, sort) now lives under one key per view in the
 // store's own ui-preferences.db, keyed by binding path rather than header text (so a renamed or translated
 // header keeps its layout), applied once the columns exist, and saved on every reorder, resize, visibility or
 // sort change and on closing. ApplyProductColumnPreferences above stays as the fallback for a database that
 // only has the old hidden-header list.
 const string ProductLayoutKey = "layout:products";
 Action<string, bool>? applyProductSort; Func<(string SortBy, bool Descending)>? captureProductSort; bool applyingProductLayout;
 static string ColumnKey(DataGridColumn column) => column is DataGridBoundColumn { Binding: System.Windows.Data.Binding binding } ? binding.Path.Path : column.Header?.ToString() ?? "";
 void InitializeProductLayout()
 {
  ApplyProductLayout(DataGridLayoutCodec.Deserialize(uiPreferences.Get(ProductLayoutKey)));
  products.ColumnDisplayIndexChanged += (_, _) => SaveProductLayout(); products.ColumnReordered += (_, _) => SaveProductLayout();
  var width = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
  var visibility = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(DataGridColumn.VisibilityProperty, typeof(DataGridColumn));
  foreach (var column in products.Columns) { width.AddValueChanged(column, (_, _) => SaveProductLayout()); visibility.AddValueChanged(column, (_, _) => SaveProductLayout()); }
  Closing += (_, _) => SaveProductLayout();
 }
 void ApplyProductLayout(DataGridLayoutState? state)
 {
  applyingProductLayout = true;
  try
  {
   if (state is null) { ApplyProductColumnPreferences(); return; }
   var byKey = products.Columns.GroupBy(ColumnKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
   var order = DataGridLayoutCodec.ResolveOrder(products.Columns.Select(ColumnKey).ToList(), state.Columns);
   for (var i = 0; i < order.Count; i++) if (byKey.TryGetValue(order[i], out var column) && column.DisplayIndex != i) column.DisplayIndex = i;
   foreach (var saved in state.Columns) { if (!byKey.TryGetValue(saved.Key, out var column)) continue; if (saved.Width > 0) column.Width = new DataGridLength(saved.Width); column.Visibility = saved.Visible ? Visibility.Visible : Visibility.Collapsed; }
   if (!string.IsNullOrWhiteSpace(state.SortBy) && applyProductSort is not null) { applyProductSort(state.SortBy, state.SortDescending); productFilter = productFilter with { SortBy = state.SortBy, SortDescending = state.SortDescending }; }
  }
  finally { applyingProductLayout = false; }
 }
 DataGridLayoutState CaptureProductLayout()
 {
  var columns = products.Columns.Select(c => new DataGridColumnLayout(ColumnKey(c), c.DisplayIndex, c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth, c.Visibility == Visibility.Visible)).ToList();
  var sort = captureProductSort?.Invoke() ?? (productFilter.SortBy, productFilter.SortDescending);
  return new(DataGridLayoutCodec.CurrentVersion, columns, sort.SortBy, sort.Descending);
 }
 void SaveProductLayout()
 {
  if (applyingProductLayout || products.Columns.Count == 0) return;
  try { uiPreferences.Set(ProductLayoutKey, DataGridLayoutCodec.Serialize(CaptureProductLayout())); } catch (Exception e) { Log(Safe(e)); }
 }
 // Product list density (#793). One selector on the same DataGrid the list already owns -- no parallel styles --
 // driving the shared metrics table so font, row height, thumbnail and hit target scale together. The choice is
 // a view setting like the column layout and is restored on the next launch; switching never rebinds the
 // ItemsSource, so the selection and the virtualized panel are untouched.
 const string ProductDensityKey = "density:products";
 ComboBox? productDensityBox;
 internal string CurrentProductDensity() => ProductListDensity.Normalize(productDensityBox?.SelectedItem as string ?? uiPreferences.Get(ProductDensityKey));
 ComboBox BuildProductDensitySelector()
 {
  var modes = new[] { ProductListDensity.Comfortable, ProductListDensity.Compact };
  var box = new ComboBox { Name = "ProductDensityBox", Width = 95, ItemsSource = modes.Select(ProductListDensity.Label).ToArray(), ToolTip = "Satır yoğunluğu" };
  productDensityBox = box;
  var stored = ProductListDensity.Normalize(uiPreferences.Get(ProductDensityKey));
  box.SelectedIndex = Array.IndexOf(modes, stored);
  ApplyProductDensity(stored);
  box.SelectionChanged += (_, _) =>
  {
   var mode = ProductListDensity.Normalize(box.SelectedItem as string);
   ApplyProductDensity(mode);
   try { uiPreferences.Set(ProductDensityKey, mode); } catch (Exception e) { Log(Safe(e)); }
  };
  return box;
 }
 void ApplyProductDensity(string mode)
 {
  var metrics = ProductListDensity.Metrics(mode);
  products.RowHeight = metrics.RowHeight; products.FontSize = metrics.FontSize; products.MinRowHeight = metrics.RowHeight;
  var cell = new Style(typeof(DataGridCell));
  cell.Setters.Add(new Setter(PaddingProperty, metrics.CellPadding));
  cell.Setters.Add(new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Center));
  products.CellStyle = cell;
 }
 // Product card pricing summary (#798). One ranked block -- price, then approximate margin, then when it was
 // last calculated -- above the editable price fields, so the card leads with the number that matters instead
 // of four equally weighted text boxes. Read-only and derived: no pricing behaviour is added here, and the
 // authoritative check remains the dispatch-time money preflight.
 readonly StackPanel productPriceSummaryPanel = new() { Margin = new Thickness(3, 2, 3, 8) };
 void ShowProductPriceSummary(CatalogProduct? product)
 {
  productPriceSummaryPanel.Children.Clear();
  if (product is null) return;
  var summary = ProductPriceSummary.Build(product, DateTime.UtcNow);
  var headline = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Text = summary.SalePrice, VerticalAlignment = VerticalAlignment.Center };
  var currency = new TextBlock { Text = " " + summary.Currency, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 0, 3), Foreground = new SolidColorBrush(Color.FromRgb(87, 112, 125)) };
  var priceLine = new StackPanel { Orientation = Orientation.Horizontal, Children = { headline, currency } };
  productPriceSummaryPanel.Children.Add(priceLine);
  var marginBrush = summary.MarginLevel switch
  {
   ProductPriceSummary.Negative => new SolidColorBrush(Color.FromRgb(190, 52, 52)),
   ProductPriceSummary.Thin => new SolidColorBrush(Color.FromRgb(196, 132, 22)),
   ProductPriceSummary.Healthy => new SolidColorBrush(Color.FromRgb(46, 125, 80)),
   _ => new SolidColorBrush(Color.FromRgb(87, 112, 125)),
  };
  productPriceSummaryPanel.Children.Add(new TextBlock { Text = $"Kâr (yaklaşık): {summary.Margin}", Foreground = marginBrush, FontWeight = FontWeights.SemiBold });
  productPriceSummaryPanel.Children.Add(new TextBlock { Text = $"Son hesaplama: {summary.Calculated}", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(87, 112, 125)) });
  foreach (var warning in summary.Warnings)
   productPriceSummaryPanel.Children.Add(new TextBlock { Text = "⚠ " + warning, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(160, 82, 22)), Margin = new Thickness(0, 3, 0, 0) });
  productPriceSummaryPanel.Children.Add(new TextBlock { Text = summary.MarginCaveat, FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(126, 146, 158)), Margin = new Thickness(0, 4, 0, 0) });
  System.Windows.Automation.AutomationProperties.SetName(productPriceSummaryPanel, $"{summary.SalePrice} {summary.Currency}, kâr {summary.Margin}");
 }
 // Product card stock composition (#799). The available figure comes from CatalogStore.PreviewStock -- the owner
 // of that projection -- and is only presented here; the card never re-derives it, so it cannot disagree with
 // what the dispatcher would send. Without a saved policy for the channel there is no projection, and the card
 // says so instead of showing on-hand stock as if it were sellable.
 readonly StackPanel productStockSummaryPanel = new() { Margin = new Thickness(3, 2, 3, 8) };
 void ShowProductStockSummary(CatalogProduct? product)
 {
  productStockSummaryPanel.Children.Clear();
  if (product is null) return;
  StockPolicy? policy = null; int? projected = null;
  try
  {
   policy = store.GetStockPolicy("local", "default");
   if (policy is { Enabled: true }) projected = store.PreviewStock("local", "default", product.Id);
  }
  catch (InvalidOperationException) { projected = null; }
  var summary = ProductStockSummary.Build(product, policy, projected, DateTime.UtcNow);
  productStockSummaryPanel.Children.Add(new TextBlock { Text = $"{summary.Glyph} {summary.Label}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
  productStockSummaryPanel.Children.Add(new TextBlock { Text = $"Elde: {summary.OnHand} · Kanala açık: {summary.Available} · Tutulan: {summary.Withheld}", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(87, 112, 125)) });
  productStockSummaryPanel.Children.Add(new TextBlock { Text = $"Güvenlik payı: {summary.SafetyBuffer} · Üst sınır: {summary.MaximumCap} · Güncelleme: {summary.Updated}", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(126, 146, 158)) });
  foreach (var warning in summary.Warnings)
   productStockSummaryPanel.Children.Add(new TextBlock { Text = "⚠ " + warning, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(160, 82, 22)), Margin = new Thickness(0, 3, 0, 0) });
  System.Windows.Automation.AutomationProperties.SetName(productStockSummaryPanel, $"{summary.Label}, elde {summary.OnHand}, kanala açık {summary.Available}");
 }
 // Content before/after preview (#805). Opened deliberately, read-only, and closed again: it renders the values
 // a save is about to change to customer-facing text, sanitized and capped by ProductContentDiff. It writes
 // nothing -- locally or to a marketplace -- and offers no control that could.
 internal ProductContentDiffView CurrentContentDiff()
 {
  if (edit is null || productEditBaseline is null) return ProductContentDiff.Build(new CatalogProduct(), new CatalogProduct());
  var baseline = System.Text.Json.JsonSerializer.Deserialize<CatalogProduct>(productEditBaseline) ?? new CatalogProduct();
  return ProductContentDiff.Build(baseline, edit, store.FindProduct(edit.Id));
 }
 void OpenContentPreview()
 {
  if (edit is null) throw new InvalidOperationException("Önce ürün seçin.");
  var diff = CurrentContentDiff();
  var body = new StackPanel { Margin = new Thickness(14) };
  body.Children.Add(new TextBlock { Text = diff.Headline, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
  if (diff.Warning.Length > 0)
   body.Children.Add(new TextBlock { Text = "⚠ " + diff.Warning, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(160, 82, 22)), Margin = new Thickness(0, 0, 0, 8) });
  foreach (var row in diff.Rows)
  {
   body.Children.Add(new TextBlock { Text = row.Field, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
   body.Children.Add(new TextBlock { Text = "Önce: " + row.Before, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(126, 146, 158)) });
   body.Children.Add(new TextBlock { Text = "Sonra: " + row.After, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(46, 90, 46)) });
  }
  var close = new Button { Content = "Kapat", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 3, 12, 3) };
  body.Children.Add(close);
  var dialog = new Window { Owner = this, Title = "İçerik değişiklik önizlemesi", Width = 620, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
  close.Click += (_, _) => dialog.DialogResult = true;
  dialog.ShowDialog();
 }
 // Validation summary panel (#803). Blocking / warning / info from the same evaluator the store refuses saves
 // with, grouped by section, with a jump to the first blocker. Field names only -- never the offending value.
 readonly StackPanel productValidationPanel = new() { Margin = new Thickness(3, 0, 3, 8) };
 void ShowProductValidation(CatalogProduct? product)
 {
  productValidationPanel.Children.Clear();
  if (product is null) { productValidationPanel.Visibility = Visibility.Collapsed; return; }
  var result = ProductValidation.Evaluate(product);
  var actionable = result.Findings.Where(f => f.Severity != ProductValidation.Info).ToList();
  if (actionable.Count == 0) { productValidationPanel.Visibility = Visibility.Collapsed; return; }
  productValidationPanel.Visibility = Visibility.Visible;
  var header = new DockPanel { LastChildFill = true };
  if (result.FirstBlocking is { } first)
  {
   var jump = new Button { Content = "İlk soruna git", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0), Tag = first.Section };
   jump.Click += (_, _) => SelectProductSection((string)jump.Tag);
   DockPanel.SetDock(jump, System.Windows.Controls.Dock.Right); header.Children.Add(jump);
  }
  header.Children.Add(new TextBlock { Text = result.Summary, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(result.HasBlocking ? Color.FromRgb(190, 52, 52) : Color.FromRgb(160, 82, 22)) });
  productValidationPanel.Children.Add(header);
  foreach (var section in ProductWorkspaceSections.All)
  {
   var rows = actionable.Where(f => f.Section == section.Key).ToList();
   if (rows.Count == 0) continue;
   productValidationPanel.Children.Add(new TextBlock { Text = section.Label, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(87, 112, 125)) });
   foreach (var finding in rows)
    productValidationPanel.Children.Add(new TextBlock { Text = (finding.Severity == ProductValidation.Blocking ? "✖ " : "⚠ ") + finding.Message, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(finding.Severity == ProductValidation.Blocking ? Color.FromRgb(190, 52, 52) : Color.FromRgb(160, 82, 22)) });
  }
  System.Windows.Automation.AutomationProperties.SetName(productValidationPanel, result.Summary);
 }
 // Unsaved-change indicator (#802). The guard that refuses to lose an edit already existed; this makes the
 // pending work visible -- a dot on each dirty section's tab, a summary line, and a per-section undo -- and
 // names fields only, never the values that changed.
 readonly StackPanel productDirtyBar = new() { Margin = new Thickness(3, 0, 3, 8) };
 internal ProductDirtyState CurrentProductDirtyState()
 {
  if (edit is null || productEditBaseline is null) return new([], "");
  var baseline = System.Text.Json.JsonSerializer.Deserialize<CatalogProduct>(productEditBaseline);
  return baseline is null ? new([], "") : ProductDirtySections.Compare(baseline, edit);
 }
 internal void RefreshProductDirtyIndicator()
 {
  ShowProductValidation(edit);
  var state = CurrentProductDirtyState();
  if (productWorkspaceTabs is not null)
   foreach (var tab in productWorkspaceTabs.Items.OfType<TabItem>())
   {
    var key = tab.Tag as string ?? "";
    var label = ProductWorkspaceSections.All.FirstOrDefault(s => s.Key == key)?.Label ?? tab.Header?.ToString() ?? "";
    tab.Header = state.Contains(key) ? label + " •" : label;
   }
  productDirtyBar.Children.Clear();
  if (!state.IsDirty) { productDirtyBar.Visibility = Visibility.Collapsed; return; }
  productDirtyBar.Visibility = Visibility.Visible;
  productDirtyBar.Children.Add(new TextBlock { Text = state.Summary, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(160, 82, 22)) });
  foreach (var section in state.Sections)
  {
   var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 3, 0, 0) };
   var undo = new Button { Content = "Geri al", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0), Tag = section.Key };
   undo.Click += (_, _) => ResetProductSection((string)undo.Tag);
   DockPanel.SetDock(undo, System.Windows.Controls.Dock.Right); row.Children.Add(undo);
   row.Children.Add(new TextBlock { Text = $"{section.Label}: {string.Join(", ", section.Fields)}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(87, 112, 125)) });
   productDirtyBar.Children.Add(row);
  }
  System.Windows.Automation.AutomationProperties.SetName(productDirtyBar, state.Summary);
 }
 internal void ResetProductSection(string sectionKey)
 {
  if (edit is null || productEditBaseline is null) return;
  var baseline = System.Text.Json.JsonSerializer.Deserialize<CatalogProduct>(productEditBaseline);
  if (baseline is null) return;
  var restored = ProductDirtySections.ResetSection(baseline, edit, sectionKey);
  edit = restored;
  productEditor.DataContext = null; productEditor.DataContext = edit;
  ShowProductPriceSummary(edit); ShowProductStockSummary(edit); ShowProductProvenance(edit);
  RefreshProductDirtyIndicator();
 }
 // Product card source provenance (#800). Collapsed by default -- the issue's "kartı kalabalıklaştırma" -- and
 // opened by the operator; the header alone carries the one-line answer. The source is named, never located:
 // XmlSource.Location is the feed URL and carries keys.
 readonly StackPanel productProvenanceBody = new();
 readonly Expander productProvenanceExpander = new() { Header = "Köken", Margin = new Thickness(3, 2, 3, 8), IsExpanded = false };
 void ShowProductProvenance(CatalogProduct? product)
 {
  productProvenanceBody.Children.Clear();
  productProvenanceExpander.Visibility = product is null ? Visibility.Collapsed : Visibility.Visible;
  if (product is null) return;
  var source = string.IsNullOrWhiteSpace(product.SourceId) ? null : store.Sources().FirstOrDefault(s => s.Id == product.SourceId);
  var view = ProductProvenance.Build(product, source, DateTime.UtcNow);
  productProvenanceExpander.Header = $"Köken · {view.Headline}";
  productProvenanceBody.Children.Add(new TextBlock { Text = view.SourceSummary, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
  foreach (var row in view.Rows)
   productProvenanceBody.Children.Add(new TextBlock { Text = $"{row.Field}: {row.Origin} — {row.Detail}", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(87, 112, 125)) });
  foreach (var warning in view.Warnings)
   productProvenanceBody.Children.Add(new TextBlock { Text = "⚠ " + warning, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(160, 82, 22)), Margin = new Thickness(0, 4, 0, 0) });
  System.Windows.Automation.AutomationProperties.SetName(productProvenanceExpander, "Alan kökenleri: " + view.Headline);
 }
 // Product quick-inspect drawer (#796). A read-only panel beside the list, opened with Ctrl+I on the selected
 // row and closed with Esc, so the operator can check identity/price/stock/source/readiness/last error without
 // leaving the row or opening the editor. It is built from ProductQuickInspect's label/value rows, which carry
 // no commands and are sanitized, so the drawer cannot mutate anything and cannot echo a secret.
 readonly StackPanel productInspectBody = new();
 readonly TextBlock productInspectTitle = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
 Border? productInspectDrawer;
 UIElement BuildProductInspectHost(UIElement list)
 {
  var close = new Button { Content = "Kapat (Esc)", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(10, 3, 10, 3) };
  close.Click += (_, _) => CloseProductInspect();
  var stack = new StackPanel { Margin = new Thickness(12) };
  stack.Children.Add(productInspectTitle); stack.Children.Add(productInspectBody); stack.Children.Add(close);
  productInspectDrawer = new Border
  {
   Width = 320, Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromRgb(250, 252, 254)),
   BorderBrush = new SolidColorBrush(Color.FromRgb(214, 226, 235)), BorderThickness = new Thickness(1, 0, 0, 0),
   Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
  };
  productInspectDrawer.Name = "ProductInspectDrawer";
  System.Windows.Automation.AutomationProperties.SetName(productInspectDrawer, "Ürün hızlı inceleme");
  var host = new Grid();
  host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
  host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
  Grid.SetColumn(list, 0); host.Children.Add(list);
  Grid.SetColumn(productInspectDrawer, 1); host.Children.Add(productInspectDrawer);
  products.InputBindings.Add(new KeyBinding(new SimpleCommand(OpenProductInspect), Key.I, ModifierKeys.Control));
  products.InputBindings.Add(new KeyBinding(new SimpleCommand(CloseProductInspect), Key.Escape, ModifierKeys.None));
  productInspectDrawer.InputBindings.Add(new KeyBinding(new SimpleCommand(CloseProductInspect), Key.Escape, ModifierKeys.None));
  return host;
 }
 internal void OpenProductInspect()
 {
  if (productInspectDrawer is null) return;
  if (products.SelectedItem is not CatalogProduct product) { CloseProductInspect(); return; }
  var jobs = new SyncStore(dataDirectory).List().Where(j => j.EntityId == product.Id).ToList();
  var view = ProductQuickInspect.Build(product, jobs, DateTime.UtcNow);
  productInspectTitle.Text = view.Rows.First(r => r.Label == "Ürün adı").Value;
  productInspectBody.Children.Clear();
  foreach (var section in view.Sections)
  {
   productInspectBody.Children.Add(new TextBlock { Text = section, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 3) });
   foreach (var row in view.InSection(section))
    productInspectBody.Children.Add(new TextBlock { Text = row.Label + ": " + row.Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
  }
  productInspectDrawer.Visibility = Visibility.Visible;
  productInspectDrawer.Focus();
 }
 internal void CloseProductInspect()
 {
  if (productInspectDrawer is null || productInspectDrawer.Visibility != Visibility.Visible) return;
  productInspectDrawer.Visibility = Visibility.Collapsed;
  products.Focus();
 }
 sealed class SimpleCommand(Action run) : System.Windows.Input.ICommand
 {
  public event EventHandler? CanExecuteChanged { add { } remove { } }
  public bool CanExecute(object? parameter) => true;
  public void Execute(object? parameter) => run();
 }
 // Product list selection summary bar (#795). Sticky between the toolbar and the grid, hidden until something is
 // selected. It reports only counts -- never product text -- and it reconciles the selection against the rows
 // currently in the list, so a filter change that leaves rows behind is stated rather than silently acted on.
 readonly TextBlock productSelectionHeadline = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
 readonly TextBlock productSelectionDetail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(76, 102, 118)) };
 Border? productSelectionBar;
 Border BuildProductSelectionBar()
 {
  var clear = new Button { Content = "Seçimi temizle", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
  clear.Click += (_, _) => { products.UnselectAll(); UpdateProductSelectionSummary(); };
  var layout = new DockPanel { LastChildFill = true };
  DockPanel.SetDock(clear, System.Windows.Controls.Dock.Right); layout.Children.Add(clear);
  DockPanel.SetDock(productSelectionHeadline, System.Windows.Controls.Dock.Left); layout.Children.Add(productSelectionHeadline);
  layout.Children.Add(productSelectionDetail);
  productSelectionBar = new Border { Background = new SolidColorBrush(Color.FromRgb(240, 245, 249)), BorderBrush = new SolidColorBrush(Color.FromRgb(214, 226, 235)), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 6, 12, 6), Visibility = Visibility.Collapsed, Child = layout };
  System.Windows.Automation.AutomationProperties.SetName(productSelectionBar, "Seçim özeti");
  return productSelectionBar;
 }
 void UpdateProductSelectionSummary()
 {
  if (productSelectionBar is null) return;
  var visible = products.ItemsSource?.OfType<CatalogProduct>().ToList() ?? [];
  var summary = ProductSelectionSummary.Describe(products.SelectedItems.OfType<CatalogProduct>().ToList(), visible, productTotal);
  productSelectionBar.Visibility = summary.IsVisible ? Visibility.Visible : Visibility.Collapsed;
  productSelectionHeadline.Text = summary.Headline;
  productSelectionDetail.Text = string.Join(" ", new[] { summary.ScopeText, summary.RiskText }.Where(x => x.Length > 0));
  System.Windows.Automation.AutomationProperties.SetHelpText(productSelectionBar, productSelectionDetail.Text);
 }
 // Product row state hierarchy (#794). LoadingRow is the one hook that also fires when a recycled row is reused,
 // so a virtualized list cannot end up showing a previous product's state; the badge column, this border and the
 // tooltip all read the same ProductRowState classification.
 void InitializeProductRowStates()
 {
  products.LoadingRow += (_, e) =>
  {
   if (e.Row.Item is not CatalogProduct product) return;
   var state = ProductRowState.Classify(product, DateTime.UtcNow);
   e.Row.BorderThickness = ProductRowState.BorderThickness(state.Key);
   e.Row.BorderBrush = ProductRowState.AccentBrush(state.Key, ProductRowState.IsHighContrast);
   e.Row.ToolTip = state.Key == ProductRowState.Normal ? null : ProductRowState.Tooltip(state, product);
   System.Windows.Automation.AutomationProperties.SetItemStatus(e.Row, state.Badge);
  };
 }
 void OpenProductColumnChooser()
 {
  var checks = products.Columns.Select(column => new CheckBox { Content = column.Header?.ToString() ?? "", IsChecked = column.Visibility == Visibility.Visible, Margin = new Thickness(5) }).ToList();
  var panel = new StackPanel { Margin = new Thickness(14) };
  panel.Children.Add(new TextBlock { Text = "Ürün tablosunda gösterilecek kolonları seçin.", Margin = new Thickness(3, 3, 3, 9) });
  var list = new ScrollViewer { Content = new StackPanel { Children = { } }, Height = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
  var fields = (StackPanel)list.Content;
  foreach (var check in checks) fields.Children.Add(check);
  panel.Children.Add(list);
  var save = new Button { Content = "Kaydet", HorizontalAlignment = HorizontalAlignment.Right };
  panel.Children.Add(save);
  var dialog = new Window { Owner = this, Title = "Ürün kolonları", Width = 360, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
  save.Click += (_, _) => { var hidden = products.Columns.Zip(checks).Where(x => x.Second.IsChecked != true).Select(x => x.First.Header?.ToString() ?? ""); uiPreferences.Set("columns:products", string.Join('\u001f', hidden)); ApplyProductColumnPreferences(); SaveProductLayout(); dialog.DialogResult = true; };
  dialog.ShowDialog();
 }
}
