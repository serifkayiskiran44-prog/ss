using System.Windows;
using System.Windows.Controls;
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
