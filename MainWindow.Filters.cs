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
  foreach(var (title,control) in new (string,Control)[]{("Durum",status),("Marka (; ile ayır)",brand),("Kategori (; ile ayır)",category),("SKU (; ile ayır)",sku),("Açıklama",description),("Görsel kaydı",image)}){
   var group=new StackPanel{Margin=new Thickness(4)};group.Children.Add(new TextBlock{Text=title});group.Children.Add(control);panel.Children.Add(group);
  }
  static string[] Values(string text)=>text.Split(new[]{';','\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct().ToArray();
  static bool? State(ComboBox box)=>box.SelectedIndex==0?null:box.SelectedIndex==1;
  CatalogFilter Current()=>new(){Active=State(status),Brands=Values(brand.Text),Categories=Values(category.Text),Skus=Values(sku.Text),DescriptionPresent=State(description),ImagePresent=State(image)};
  void Apply(CatalogFilter filter){status.SelectedIndex=filter.Active is null?0:filter.Active.Value?1:2;description.SelectedIndex=filter.DescriptionPresent is null?0:filter.DescriptionPresent.Value?1:2;image.SelectedIndex=filter.ImagePresent is null?0:filter.ImagePresent.Value?1:2;brand.Text=string.Join(';',filter.Brands);category.Text=string.Join(';',filter.Categories);sku.Text=string.Join(';',filter.Skus);}
  var filterStore=new CatalogFilterStore(dataDirectory);var saved=new ComboBox{Width=170,DisplayMemberPath="Name"};var filterName=new TextBox{Width=140,ToolTip="Kaydedilecek filtre adı"};
  void ReloadSaved(){saved.ItemsSource=filterStore.List();}
  panel.Children.Add(Button("Filtreleri uygula",()=>{productFilter=Current();productOffset=0;RefreshProducts();}));
  panel.Children.Add(Button("Filtreleri temizle",()=>{status.SelectedIndex=description.SelectedIndex=image.SelectedIndex=0;brand.Clear();category.Clear();sku.Clear();productFilter=new();productOffset=0;RefreshProducts();}));
  panel.Children.Add(new TextBlock{Text="Kayıtlı filtre",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,2,0)});panel.Children.Add(saved);panel.Children.Add(filterName);
  panel.Children.Add(Button("Filtreyi kaydet",()=>{filterStore.Save(filterName.Text,Current());ReloadSaved();filterName.Clear();}));
  panel.Children.Add(Button("Filtreyi yükle",()=>{if(saved.SelectedItem is not SavedCatalogFilter selected)throw new InvalidOperationException("Kayıtlı filtre seçin.");Apply(selected.Filter);productFilter=selected.Filter;productOffset=0;RefreshProducts();}));
  panel.Children.Add(Button("Filtreyi sil",()=>{if(saved.SelectedItem is not SavedCatalogFilter selected)throw new InvalidOperationException("Kayıtlı filtre seçin.");filterStore.Delete(selected.Name);ReloadSaved();}));
  ReloadSaved();
  host.Children.Add(new Expander{Header="Detaylı ürün arama",Content=panel,HorizontalAlignment=HorizontalAlignment.Stretch});
 }
}
