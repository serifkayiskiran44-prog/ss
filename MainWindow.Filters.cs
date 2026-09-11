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
  panel.Children.Add(Button("Filtreleri uygula",()=>{productFilter=new(){Active=State(status),Brands=Values(brand.Text),Categories=Values(category.Text),Skus=Values(sku.Text),DescriptionPresent=State(description),ImagePresent=State(image)};productOffset=0;RefreshProducts();}));
  panel.Children.Add(Button("Filtreleri temizle",()=>{status.SelectedIndex=description.SelectedIndex=image.SelectedIndex=0;brand.Clear();category.Clear();sku.Clear();productFilter=new();productOffset=0;RefreshProducts();}));
  host.Children.Add(new Expander{Header="Detaylı ürün arama",Content=panel,HorizontalAlignment=HorizontalAlignment.Stretch});
 }
}
