using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;
namespace TrMarketplaceHubDesktop;
public partial class MainWindow {
 FrameworkElement BuildStockPolicies(){
  var panel=new StackPanel{Margin=new Thickness(DesignTokens.SpacePage),MaxWidth=750,HorizontalAlignment=HorizontalAlignment.Left};
  panel.Children.Add(Heading("Mağaza stok ayarları"));panel.Children.Add(Hint("Yerel katalog stoğundan mağaza güvenlik payını çıkarır ve üst sınırı uygular. Ürün stoğu değişmez, pazaryerine gönderim yapılmaz. Depo/rezervasyon motoru henüz bağlı değildir."));
  var channel=new TextBox{Text="etsy"};var shop=new TextBox();var safety=new TextBox{Text="0"};var maximum=new TextBox();var result=Hint("");StockPolicy? loaded=null;
  Label(panel,"Pazaryeri anahtarı (etsy, ebay, ozon…)",channel);Label(panel,"Mağaza anahtarı",shop);Label(panel,"Ek güvenlik stoğu",safety);Label(panel,"Maksimum gösterilecek stok (boş: sınırsız)",maximum);
  panel.Children.Add(Button("Ayarları yükle",()=>{loaded=store.GetStockPolicy(channel.Text,shop.Text)??new(){Channel=channel.Text.Trim().ToLowerInvariant(),Shop=shop.Text.Trim()};safety.Text=loaded.SafetyStock.ToString();maximum.Text=loaded.MaximumStock?.ToString()??"";result.Text=loaded.Version==0?"Yeni ayar; kaydedilmedi.":"Kayıtlı ayar yüklendi.";}));
  panel.Children.Add(Button("Ayarları kaydet",()=>{if(loaded==null||loaded.Channel!=channel.Text.Trim().ToLowerInvariant()||loaded.Shop!=shop.Text.Trim())throw new InvalidOperationException("Önce bu mağazanın ayarlarını yükleyin.");if(!int.TryParse(safety.Text,out var n)||n<0)throw new InvalidOperationException("Güvenlik stoğu negatif olmayan tam sayı olmalı.");int? cap=null;if(!string.IsNullOrWhiteSpace(maximum.Text)){if(!int.TryParse(maximum.Text,out var c)||c<0)throw new InvalidOperationException("Üst sınır negatif olmayan tam sayı olmalı.");cap=c;}loaded=store.SaveStockPolicy(new(){Channel=loaded.Channel,Shop=loaded.Shop,Version=loaded.Version,SafetyStock=n,MaximumStock=cap});result.Text="Mağaza stok politikası kaydedildi. Dış gönderim yapılmadı.";}));
  var product=new ComboBox{DisplayMemberPath="Name",MinWidth=350};Label(panel,"Önizlenecek ürün",product);panel.Children.Add(Button("Ürün listesini yenile",()=>product.ItemsSource=store.Products()));
  panel.Children.Add(Button("Kayıtlı ayarla stok önizle",()=>{if(product.SelectedItem is not CatalogProduct p)throw new InvalidOperationException("Ürün listesini yenileyip ürün seçin.");result.Text=$"{p.Sku} → gösterilebilir stok: {store.PreviewStock(channel.Text,shop.Text,p.Id)}. Kaydedilmemiş form değişiklikleri kullanılmaz.";}));panel.Children.Add(result);return Scroll(panel);
 }
}
