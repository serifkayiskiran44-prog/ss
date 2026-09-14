using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;
namespace TrMarketplaceHubDesktop;
public partial class MainWindow {
 CatalogFilter productFilter=new();
 void AddProductFilters(Panel host){
  var panel=new WrapPanel();
  var status=new ComboBox{ItemsSource=new[]{"Tümü","Aktif","Pasif"},SelectedIndex=0,Width=100};
  var brand=new TextBox{Width=130,MaxLength=6000};var category=new TextBox{Width=130,MaxLength=6000};var sku=new TextBox{Width=160,MaxLength=6000};var source=new TextBox{Width=160,MaxLength=6000,ToolTip="XML kaynağı Id'leri (; ile ayır)"};
  var description=new ComboBox{ItemsSource=new[]{"Tümü","Dolu","Boş"},SelectedIndex=0,Width=90};
  var image=new ComboBox{ItemsSource=new[]{"Tümü","Var","Yok"},SelectedIndex=0,Width=90};
  var manualOnly=new CheckBox{Content="Yalnız manuel ürünler",Margin=new Thickness(4)};
  var duplicateOnly=new CheckBox{Content="Yalnız mükerrer SKU/barkod",Margin=new Thickness(4)};
  foreach(var (title,control) in new (string,Control)[]{("Durum",status),("Marka (; ile ayır)",brand),("Kategori (; ile ayır)",category),("SKU (; ile ayır)",sku),("Kaynak (; ile ayır)",source),("Açıklama",description),("Görsel kaydı",image)}){
   var group=new StackPanel{Margin=new Thickness(4)};group.Children.Add(new TextBlock{Text=title});group.Children.Add(control);panel.Children.Add(group);
  }
  panel.Children.Add(manualOnly);panel.Children.Add(duplicateOnly);
  static string[] Values(string text)=>text.Split(new[]{';','\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct().ToArray();
  static bool? State(ComboBox box)=>box.SelectedIndex==0?null:box.SelectedIndex==1;
  CatalogFilter Current(){var sourceIds=Values(source.Text).ToList();if(manualOnly.IsChecked==true)sourceIds.Add(CatalogFilter.ManualSource);return new(){Active=State(status),Brands=Values(brand.Text),Categories=Values(category.Text),Skus=Values(sku.Text),SourceIds=sourceIds.ToArray(),DescriptionPresent=State(description),ImagePresent=State(image),DuplicateIdentityOnly=duplicateOnly.IsChecked==true};}
  void Apply(CatalogFilter filter){status.SelectedIndex=filter.Active is null?0:filter.Active.Value?1:2;description.SelectedIndex=filter.DescriptionPresent is null?0:filter.DescriptionPresent.Value?1:2;image.SelectedIndex=filter.ImagePresent is null?0:filter.ImagePresent.Value?1:2;brand.Text=string.Join(';',filter.Brands);category.Text=string.Join(';',filter.Categories);sku.Text=string.Join(';',filter.Skus);manualOnly.IsChecked=filter.SourceIds.Contains(CatalogFilter.ManualSource);source.Text=string.Join(';',filter.SourceIds.Where(x=>x!=CatalogFilter.ManualSource));duplicateOnly.IsChecked=filter.DuplicateIdentityOnly;}
  var filterStore=new CatalogFilterStore(dataDirectory);var saved=new ComboBox{Width=170,DisplayMemberPath="Name"};var filterName=new TextBox{Width=140,ToolTip="Kaydedilecek filtre adı"};
  void ReloadSaved(){saved.ItemsSource=filterStore.List();}
  panel.Children.Add(Button("Filtreleri uygula",()=>{productFilter=Current();productOffset=0;RefreshProducts();}));
  panel.Children.Add(Button("Filtreleri temizle",()=>{status.SelectedIndex=description.SelectedIndex=image.SelectedIndex=0;brand.Clear();category.Clear();sku.Clear();source.Clear();manualOnly.IsChecked=false;duplicateOnly.IsChecked=false;productFilter=new();productOffset=0;RefreshProducts();}));
  panel.Children.Add(new TextBlock{Text="Kayıtlı filtre",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,2,0)});panel.Children.Add(saved);panel.Children.Add(filterName);
  panel.Children.Add(Button("Filtreyi kaydet",()=>{filterStore.Save(filterName.Text,Current());ReloadSaved();filterName.Clear();}));
  panel.Children.Add(Button("Filtreyi yükle",()=>{if(saved.SelectedItem is not SavedCatalogFilter selected)throw new InvalidOperationException("Kayıtlı filtre seçin.");Apply(selected.Filter);productFilter=selected.Filter;productOffset=0;RefreshProducts();}));
  panel.Children.Add(Button("Filtreyi sil",()=>{if(saved.SelectedItem is not SavedCatalogFilter selected)throw new InvalidOperationException("Kayıtlı filtre seçin.");filterStore.Delete(selected.Name);ReloadSaved();}));
  panel.Children.Add(Button("Kolon görünürlüğü",OpenProductColumnChooser));
  ApplyProductColumnPreferences();
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
  save.Click += (_, _) => { var hidden = products.Columns.Zip(checks).Where(x => x.Second.IsChecked != true).Select(x => x.First.Header?.ToString() ?? ""); uiPreferences.Set("columns:products", string.Join('\u001f', hidden)); ApplyProductColumnPreferences(); dialog.DialogResult = true; };
  dialog.ShowDialog();
 }
}
