using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;
namespace TrMarketplaceHubDesktop;
public partial class MainWindow {
 sealed record ProductSourceFilterChoice(string Label,string SourceId);
 CatalogFilter productFilter=new();
 void AddProductFilters(Panel host){
  var panel=new WrapPanel();
  var status=new ComboBox{ItemsSource=new[]{"Tümü","Aktif","Pasif"},SelectedIndex=0,Width=100};
  var brand=new ComboBox{Width=150,IsEditable=true,ItemsSource=store.Products().Select(p=>p.Brand).Where(v=>v.Length>0).Distinct().Order().ToList()};var category=new ComboBox{Width=180,IsEditable=true,ItemsSource=store.Products().Select(p=>p.Category).Where(v=>v.Length>0).Distinct().Order().ToList()};var sku=new TextBox{Width=160,MaxLength=6000};
  var description=new ComboBox{ItemsSource=new[]{"Tümü","Dolu","Boş"},SelectedIndex=0,Width=90};
  var image=new ComboBox{ItemsSource=new[]{"Tümü","Var","Yok"},SelectedIndex=0,Width=90};
  var minStock=new TextBox{Width=75};var maxStock=new TextBox{Width=75};var minPrice=new TextBox{Width=85};var maxPrice=new TextBox{Width=85};var minId=new TextBox{Width=75};var maxId=new TextBox{Width=75};var barcodes=new TextBox{Width=160,MaxLength=6000};
  var categoryRoot=new ComboBox{Width=190,IsEditable=true,ItemsSource=store.Products().SelectMany(p=>{var parts=p.Category.Split('>',StringSplitOptions.TrimEntries);return Enumerable.Range(1,parts.Length).Select(n=>string.Join(">",parts.Take(n)));}).Where(v=>v.Length>0).Distinct().Order().ToList()};
  var currency=new ComboBox{Width=90,ItemsSource=new[]{"","TRY","USD","EUR","GBP"},SelectedIndex=0};
  var priceLock=new ComboBox{Width=105,ItemsSource=new[]{"Tümü","Kilitli","Serbest"},SelectedIndex=0};var stockLock=new ComboBox{Width=105,ItemsSource=new[]{"Tümü","Kilitli","Serbest"},SelectedIndex=0};
  var sourceChoices=new List<ProductSourceFilterChoice>{new("Tüm kaynaklar",""),new("Manuel ürünler",CatalogFilter.ManualSource)};
  sourceChoices.AddRange(store.Sources().OrderBy(item=>item.Name,StringComparer.CurrentCultureIgnoreCase).Select(item=>new ProductSourceFilterChoice($"XML · {item.Name}",item.Id)));
  var sourceChoice=new ComboBox{Name="ProductSourceFilter",Width=220,ItemsSource=sourceChoices,DisplayMemberPath="Label",SelectedIndex=0};
  var duplicateOnly=new CheckBox{Content="Yalnız mükerrer SKU/barkod",Margin=new Thickness(4)};
  foreach(var (title,control) in new (string,Control)[]{("Durum",status),("Ürün kaynağı",sourceChoice),("Marka (; ile ayır)",brand),("Kategori (; ile ayır)",category),("SKU (; ile ayır)",sku),("Açıklama",description),("Görsel kaydı",image),("Kategori ağacı (altları dahil)",categoryRoot),("Barkod (; veya alt alta)",barcodes),("Stok en az",minStock),("Stok en çok",maxStock),("Fiyat en az",minPrice),("Fiyat en çok",maxPrice),("ID en az",minId),("ID en çok",maxId),("Para birimi",currency),("Fiyat kilidi",priceLock),("Stok kilidi",stockLock)}){
   var group=new StackPanel{Margin=new Thickness(4)};group.Children.Add(new TextBlock{Text=title});group.Children.Add(control);panel.Children.Add(group);
  }
  panel.Children.Add(duplicateOnly);
  static string[] Values(string text)=>text.Split(new[]{';','\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct().ToArray();
  static bool? State(ComboBox box)=>box.SelectedIndex==0?null:box.SelectedIndex==1;
  static decimal? Amount(TextBox box){if(string.IsNullOrWhiteSpace(box.Text))return null;if(!decimal.TryParse(box.Text,NumberStyles.Number,CultureInfo.GetCultureInfo("tr-TR"),out var result))throw new InvalidOperationException("Filtrede geçerli bir sayı yazın.");return result;}
  static long? Whole(TextBox box){if(string.IsNullOrWhiteSpace(box.Text))return null;if(!long.TryParse(box.Text,out var result))throw new InvalidOperationException("Stok ve ID tam sayı olmalı.");return result;}
  CatalogFilter Current(){var selectedSource=(sourceChoice.SelectedItem as ProductSourceFilterChoice)?.SourceId??"";var sourceIds=selectedSource.Length==0?Array.Empty<string>():new[]{selectedSource};return new(){MinimumStock=checked((int?)Whole(minStock)),MaximumStock=checked((int?)Whole(maxStock)),MinimumPrice=Amount(minPrice),MaximumPrice=Amount(maxPrice),MinimumId=Whole(minId),MaximumId=Whole(maxId),Barcodes=Values(barcodes.Text),CategoryPrefix=categoryRoot.Text,Currency=currency.SelectedItem?.ToString()??"",PriceLocked=State(priceLock),StockLocked=State(stockLock),Active=State(status),Brands=Values(brand.Text),Categories=Values(category.Text),Skus=Values(sku.Text),SourceIds=sourceIds,DescriptionPresent=State(description),ImagePresent=State(image),DuplicateIdentityOnly=duplicateOnly.IsChecked==true};}
  void Apply(CatalogFilter filter){minStock.Text=filter.MinimumStock?.ToString()??"";maxStock.Text=filter.MaximumStock?.ToString()??"";minPrice.Text=filter.MinimumPrice?.ToString(CultureInfo.GetCultureInfo("tr-TR"))??"";maxPrice.Text=filter.MaximumPrice?.ToString(CultureInfo.GetCultureInfo("tr-TR"))??"";minId.Text=filter.MinimumId?.ToString()??"";maxId.Text=filter.MaximumId?.ToString()??"";barcodes.Text=string.Join(";",filter.Barcodes);categoryRoot.Text=filter.CategoryPrefix;currency.SelectedItem=filter.Currency;priceLock.SelectedIndex=filter.PriceLocked is null?0:filter.PriceLocked.Value?1:2;stockLock.SelectedIndex=filter.StockLocked is null?0:filter.StockLocked.Value?1:2;status.SelectedIndex=filter.Active is null?0:filter.Active.Value?1:2;description.SelectedIndex=filter.DescriptionPresent is null?0:filter.DescriptionPresent.Value?1:2;image.SelectedIndex=filter.ImagePresent is null?0:filter.ImagePresent.Value?1:2;brand.Text=string.Join(';',filter.Brands);category.Text=string.Join(';',filter.Categories);sku.Text=string.Join(';',filter.Skus);var wanted=filter.SourceIds.Length==1?filter.SourceIds[0]:"";sourceChoice.SelectedItem=sourceChoices.FirstOrDefault(choice=>choice.SourceId==wanted)??sourceChoices[0];duplicateOnly.IsChecked=filter.DuplicateIdentityOnly;}
  var filterStore=new CatalogFilterStore(dataDirectory);var saved=new ComboBox{Width=170,DisplayMemberPath="Name"};var filterName=new TextBox{Width=140,ToolTip="Kaydedilecek filtre adı"};
  var corruptWarning=new TextBlock{Foreground=System.Windows.Media.Brushes.DarkRed,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,2,0)};
  var corruptFilters=new ComboBox{Width=170,DisplayMemberPath="Name",Visibility=Visibility.Collapsed};
  Button deleteCorrupt=null!;
  deleteCorrupt=Button("Bozuk kaydı sil",()=>{if(corruptFilters.SelectedItem is not CorruptCatalogFilterRow selected)throw new InvalidOperationException("Bozuk kayıt seçin.");filterStore.DeleteCorruptFilter(selected.Name);ReloadSaved();});
  deleteCorrupt.Visibility=Visibility.Collapsed;
  void ReloadSaved(){saved.ItemsSource=filterStore.List();var corrupt=filterStore.CorruptFilters();corruptFilters.ItemsSource=corrupt;var show=corrupt.Count>0?Visibility.Visible:Visibility.Collapsed;corruptFilters.Visibility=show;deleteCorrupt.Visibility=show;corruptWarning.Text=corrupt.Count>0?$"⚠ {corrupt.Count} bozuk kayıtlı filtre":"";}
  panel.Children.Add(Button("Filtreleri uygula",()=>{productFilter=Current();productOffset=0;RefreshProducts();}));
  panel.Children.Add(Button("Filtreleri temizle",()=>{status.SelectedIndex=description.SelectedIndex=image.SelectedIndex=0;Apply(new());sku.Clear();duplicateOnly.IsChecked=false;productFilter=new();productOffset=0;RefreshProducts();}));
  panel.Children.Add(new TextBlock{Text="Kayıtlı filtre",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,2,0)});panel.Children.Add(saved);panel.Children.Add(filterName);
  panel.Children.Add(Button("Filtreyi kaydet",()=>{filterStore.Save(filterName.Text,Current());ReloadSaved();filterName.Clear();}));
  panel.Children.Add(Button("Filtreyi yükle",()=>{if(saved.SelectedItem is not SavedCatalogFilter selected)throw new InvalidOperationException("Kayıtlı filtre seçin.");Apply(selected.Filter);productFilter=selected.Filter;productOffset=0;RefreshProducts();}));
  panel.Children.Add(Button("Filtreyi sil",()=>{if(saved.SelectedItem is not SavedCatalogFilter selected)throw new InvalidOperationException("Kayıtlı filtre seçin.");var result=filterStore.Delete(selected.Name,selected.Revision);if(result==CatalogFilterStore.DeleteResult.Stale)throw new InvalidOperationException("Bu filtre başka bir işlemde değişti; listeyi yenileyip tekrar deneyin.");ReloadSaved();}));
  panel.Children.Add(corruptWarning);panel.Children.Add(corruptFilters);panel.Children.Add(deleteCorrupt);
  panel.Children.Add(Button("Kolon görünürlüğü",OpenProductColumnChooser));
  ApplyProductColumnPreferences();
  ReloadSaved();
  host.Children.Add(new Expander{Header="Detaylı arama",Content=new ScrollViewer{Content=panel,MaxHeight=310,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},HorizontalAlignment=HorizontalAlignment.Stretch});
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
