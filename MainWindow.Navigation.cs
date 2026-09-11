using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
 readonly Dictionary<string, UIElement> builtPages = new();
 readonly Dictionary<string, TabItem> routes = new();
 TabControl? etsyTabs;

 void BuildNavigation()
 {
  void Group(string title) => NavigationList.Items.Add(new ListBoxItem { Content=title, IsEnabled=false, Focusable=false, FontSize=10, FontWeight=FontWeights.Bold, Foreground=new SolidColorBrush(Color.FromRgb(130,161,177)), Padding=new Thickness(14,12,4,2) });
  void Page(string key,string title,string description,UIElement content,string? label=null)
  {
   var page=new TabItem{Tag=key,Header=title,Content=content};routes.Add(key,page);ModuleTabs.Items.Add(page);
   var item=new ListBoxItem{Tag=key,Content=label??title,ToolTip=description};NavigationList.Items.Add(item);
   item.Selected+=(_,_)=>{ModuleTabs.SelectedItem=page;PageTitle.Text=title;PageDescription.Text=description;};
  }
  Group("KATALOG VE TEDARİK");
  Page("products","Ürün yönetimi","Ortak ürün havuzu • Ürün seçerek kartını, fiyatını ve stok kilitlerini düzenleyin.",builtPages["Ürün havuzu"]);
  Page("xml","XML yönetimi","Kaynak bağlantısı → Alan eşleştirme → Fiyat ve stok → Önizleme ve havuza aktarım",builtPages["XML yönetimi"]);
  Page("excel","Excel ürün işlemleri","Excel dışa aktarma ve içe aktarma önizlemesi",BuildExcel());
  Page("taxonomy","Kategori / marka / özellik","Yerel sözlük kayıtları ve harici anahtar eşlemeleri",BuildTaxonomy());
  Page("sync","Sync merkezi","Yerel sync kuyruğu, idempotency ve tekrar deneme durumları",BuildSync());
  Group("PAZARYERLERİ");
  etsyTabs=new TabControl();
  etsyTabs.Items.Add(new TabItem{Header="Ürünler",Content=builtPages["Etsy ilanları"]});
  etsyTabs.Items.Add(new TabItem{Header="Şablon ve eşleştirme",Content=builtPages["Global Etsy şablonu"]});
  etsyTabs.Items.Add(new TabItem{Header="Bağlantı",Content=builtPages["Etsy bağlantısı"]});
  Page("etsy","Etsy","Mağaza ilanları, ortak havuzdan taslak ve hesap yetkilendirmesi. Bağlantı durumu Bağlantı sekmesinde doğrulanır.",etsyTabs);
  foreach(var id in new[]{"ebay","ozon","joom"})
  {
   var channel=MarketplaceRegistry.All.Single(c=>c.Id==id);
   var tabs=new TabControl();
   tabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create(id,dataDirectory)});
   tabs.Items.Add(new TabItem{Header="Bağlantı",Content=MarketplaceSetupPanel.CreateChannel(id)});
   Page(id,channel.Name,id=="joom"?"Satıcı kaydı / kabulü bekleniyor • Canlı ürün aktarımı etkin değil.":"Yerel kanal ürün planları • API bağlantı kontrolü ayrı; canlı ürün aktarımı etkin değil.",tabs);
  }
  Page("channels","Diğer pazaryerleri","Wish, Allegro ve Fruugo: hesap başvuruları ve entegrasyon gereksinimleri.",MarketplaceSetupPanel.CreateOther(dataDirectory));
  Group("OPERASYON");
  Page("price-policies","Mağaza fiyat kuralları","CASE formülü, kur ve güvenli fiyat önizlemesi",BuildPricePolicies());
  Page("stock-policies","Mağaza stok ayarları","Güvenlik stoğu, üst sınır ve yerel önizleme",BuildStockPolicies());
  Page("orders","Sipariş ve kargo","Sipariş kayıtları, paket ve kargo takibi",OrdersPanel.Create(dataDirectory,AuthorizedAsync,RefreshProducts));
  Page("shipping","Navlungo","Kargo bağlantısı ve mevcut hizmet işlemleri",NavlungoPanel.Create(),"Kargo bağlantısı");
  Group("YÖNETİM");
  var settings=new StackPanel{Margin=new Thickness(20)};
  settings.Children.Add(Heading("Hesaplar ve uygulama ayarları"));
  settings.Children.Add(Hint("Pazaryeri erişim bilgileri ilgili kanalın Bağlantı sekmesindedir. Bağlantı doğrulaması ürün aktarımının etkin olduğu anlamına gelmez."));
  foreach(var id in new[]{"etsy","ebay","ozon","joom"})
  {var key=id;settings.Children.Add(Button(id=="ebay"?"eBay bağlantı ayarları":char.ToUpper(id[0])+id[1..]+" bağlantı ayarları",()=>{Navigate(key);if(routes[key].Content is TabControl tabs)tabs.SelectedIndex=tabs.Items.Count-1;}));}
  settings.Children.Add(Heading("Görseller"));settings.Children.Add(MarketplaceImagePanel.Create());
  settings.Children.Add(Heading("Yerel veri ve otomasyon"));settings.Children.Add(Hint("XML kaynakları ve ürün kilitleri XML yönetimi / Ürün yönetimi ekranlarından düzenlenir. Zamanlı XML yenilemesi yalnız uygulama açıkken çalışır. İşlem geçmişi pencerenin altındadır."));
  Page("settings","Ayarlar","Hesap bağlantıları, pazaryeri görselleri ve yerel çalışma bilgileri",Scroll(settings));
  Navigate("products");
 }
 void Navigate(string key)
 {
  NavigationList.SelectedItem=NavigationList.Items.OfType<ListBoxItem>().Single(i=>i.Tag?.ToString()==key);
 }
}



