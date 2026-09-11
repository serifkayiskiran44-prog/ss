using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
 readonly Dictionary<string, UIElement> builtPages = new();
 readonly Dictionary<string, TabItem> routes = new();
 readonly Dictionary<string, string> routeTitles = new();
 readonly Stack<string> routeHistory = new();
 UiPreferenceStore uiPreferences = null!;
 string? currentRoute;
 bool selectingRoute;
 TabControl? etsyTabs;

 void BuildNavigation()
 {
  void Group(string title) => NavigationList.Items.Add(new ListBoxItem { Content=title, IsEnabled=false, Focusable=false, FontSize=10, FontWeight=FontWeights.Bold, Foreground=new SolidColorBrush(Color.FromRgb(130,161,177)), Padding=new Thickness(14,12,4,2) });
  void Page(string key,string title,string description,UIElement content,string? label=null)
  {
   var page=new TabItem{Tag=key,Header=title,Content=content};routes.Add(key,page);routeTitles[key]=title;ModuleTabs.Items.Add(page);
   var item=new ListBoxItem{Tag=key,Content=label??title,ToolTip=description};NavigationList.Items.Add(item);
   item.Selected+=(_,_)=>SelectRoute(key, !selectingRoute, title, description);
  }
  Group("KATALOG VE TEDARİK");
  Page("dashboard","Genel bakış","Ürün, sipariş, XML, bağlantı ve sync durumunu tek ekranda izleyin.",DashboardPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("products","Ürün yönetimi","Ortak ürün havuzu • Ürün seçerek kartını, fiyatını ve stok kilitlerini düzenleyin.",builtPages["Ürün havuzu"]);
  Page("bulk-products","Toplu ürün işlemleri","Seçili veya filtrelenmiş ürünleri preview, sürüm kontrolü ve açık onay ile güncelleyin.",BulkProductsPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("media","Görsel / medya","Ürün görsellerini kaynak, doğrulama durumu, sıra ve ana görsel olarak yönetin.",MediaPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("listing-matrix","Kanal yayın matrisi","Ürün, kanal ve mağaza bazında mapping, ilan, sync ve bağlantı durumlarını karşılaştırın.",ChannelListingMatrixPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("xml","XML yönetimi","Kaynak bağlantısı → Alan eşleştirme → Fiyat ve stok → Önizleme ve havuza aktarım",builtPages["XML yönetimi"]);
  Page("excel","Excel ürün işlemleri","Excel dışa aktarma ve içe aktarma önizlemesi",BuildExcel());
  Page("taxonomy","Kategori / marka / özellik","Yerel sözlük kayıtları ve harici anahtar eşlemeleri",BuildTaxonomy());
  Page("sync","Sync merkezi","Yerel sync kuyruğu, idempotency ve tekrar deneme durumları",BuildSync());
  Page("automation","Otomasyon","Kanal ve mağaza bazlı stok/fiyat zamanlayıcıları",BuildAutomation());
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
   tabs.Items.Add(new TabItem{Header="Bağlantı",Content=id=="joom"?JoomPanel.Create(dataDirectory):MarketplaceSetupPanel.CreateChannel(id)});
   Page(id,channel.Name,id=="joom"?"Satıcı kaydı / kabulü bekleniyor • Canlı ürün aktarımı etkin değil.":"Yerel kanal ürün planları • API bağlantı kontrolü ayrı; canlı ürün aktarımı etkin değil.",tabs);
  }
  var amazonTabs=new TabControl();amazonTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("amazon",dataDirectory)});amazonTabs.Items.Add(new TabItem{Header="Bağlantı",Content=AmazonPanel.Create(dataDirectory)});Page("amazon","Amazon","SP-API ayarları ve yerel ürün planları; resmi sözleşme doğrulanana kadar canlı operasyon kapalı.",amazonTabs);
  var trendyolTabs=new TabControl();trendyolTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("trendyol",dataDirectory)});trendyolTabs.Items.Add(new TabItem{Header="Bağlantı",Content=TrendyolPanel.Create(dataDirectory)});Page("trendyol","Trendyol","Satıcı API ayarları ve yerel ürün planları; resmi sözleşme doğrulanana kadar canlı operasyon kapalı.",trendyolTabs);
  var hepsiTabs=new TabControl();hepsiTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("hepsiburada",dataDirectory)});hepsiTabs.Items.Add(new TabItem{Header="Bağlantı",Content=HepsiburadaPanel.Create(dataDirectory)});Page("hepsiburada","Hepsiburada","Merchant API ayarları ve yerel ürün planları; resmi sözleşme doğrulanana kadar canlı operasyon kapalı.",hepsiTabs);
  var fruugoTabs=new TabControl();fruugoTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("fruugo",dataDirectory)});fruugoTabs.Items.Add(new TabItem{Header="Bağlantı",Content=FruugoPanel.Create(dataDirectory)});Page("fruugo","Fruugo","Retailer ayarları ve yerel ürün planları; resmi sözleşme doğrulanana kadar canlı operasyon kapalı.",fruugoTabs);
  var allegroTabs=new TabControl();allegroTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("allegro",dataDirectory)});allegroTabs.Items.Add(new TabItem{Header="Bağlantı",Content=AllegroPanel.Create(dataDirectory)});Page("allegro","Allegro","Public API read-only ürün/sipariş okuma ve yerel ürün planları.",allegroTabs);
  var wishTabs=new TabControl();wishTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("wish",dataDirectory)});wishTabs.Items.Add(new TabItem{Header="Bağlantı",Content=WishPanel.Create(dataDirectory)});Page("wish","Wish","Merchant ayarları ve yerel ürün planları; resmi sözleşme doğrulanana kadar canlı operasyon kapalı.",wishTabs);
  Page("channels","Diğer pazaryerleri","Wish, Allegro ve Fruugo: hesap başvuruları ve entegrasyon gereksinimleri.",MarketplaceSetupPanel.CreateOther(dataDirectory));
  Page("connections","Mağaza bağlantıları","Tüm kanal ve mağaza kayıtları, yetenekler ve salt okunur bağlantı testleri.",MarketplaceConnectionsPanel.Create(dataDirectory,key=>Navigate(key)));
  Group("OPERASYON");
  Page("price-policies","Mağaza fiyat kuralları","CASE formülü, kur ve güvenli fiyat önizlemesi",BuildPricePolicies());
  Page("stock-policies","Mağaza stok ayarları","Güvenlik stoğu, üst sınır ve yerel önizleme",BuildStockPolicies());
  Page("policy-center","Stok / fiyat politika merkezi","Kanal + mağaza politikaları, kopyalama ve ürün preview'i",PolicyCenterPanel.Create(dataDirectory));
  Page("orders","Sipariş ve kargo","Sipariş kayıtları, paket ve kargo takibi",OrdersPanel.Create(dataDirectory,AuthorizedAsync,RefreshProducts));
  Page("order-exceptions","Sipariş istisnaları","Eksik SKU, iptal/iade ve stok kararlarını önizleme/onay ile yönetin.",OrderExceptionsPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("messages","Mesaj merkezi","Müşteri mesajları, sistem bildirimleri ve yerel yanıt şablonları",MessagePanel.Create(dataDirectory,key=>Navigate(key)));
  Page("shipping","Navlungo","Kargo bağlantısı ve mevcut hizmet işlemleri",NavlungoPanel.Create(),"Kargo bağlantısı");
  Group("YÖNETİM");
  Page("diagnostics","Tanılama / audit","Güvenli sistem sağlık özeti, audit trail ve destek paketi",DiagnosticsPanel.Create(dataDirectory,key=>Navigate(key)));
  var settings=new StackPanel{Margin=new Thickness(20)};
  settings.Children.Add(Heading("Hesaplar ve uygulama ayarları"));
  settings.Children.Add(Hint("Pazaryeri erişim bilgileri ilgili kanalın Bağlantı sekmesindedir. Bağlantı doğrulaması ürün aktarımının etkin olduğu anlamına gelmez."));
  foreach(var id in new[]{"etsy","ebay","ozon","joom","amazon","trendyol","hepsiburada","fruugo","allegro","wish"})
  {var key=id;settings.Children.Add(Button(id=="ebay"?"eBay bağlantı ayarları":char.ToUpper(id[0])+id[1..]+" bağlantı ayarları",()=>{Navigate(key);if(routes[key].Content is TabControl tabs)tabs.SelectedIndex=tabs.Items.Count-1;}));}
  settings.Children.Add(Heading("Görseller"));settings.Children.Add(MarketplaceImagePanel.Create());
  settings.Children.Add(Heading("Sürüm, yedek ve taşıma"));settings.Children.Add(DataBackupPanel.Create(dataDirectory));
  settings.Children.Add(Heading("Yerel veri ve otomasyon"));settings.Children.Add(Hint("XML kaynakları ve ürün kilitleri XML yönetimi / Ürün yönetimi ekranlarından düzenlenir. Zamanlı XML yenilemesi yalnız uygulama açıkken çalışır. İşlem geçmişi pencerenin altındadır."));
  Page("settings","Ayarlar","Hesap bağlantıları, pazaryeri görselleri ve yerel çalışma bilgileri",Scroll(settings));
  NavigationSearchBox.TextChanged += (_, _) => FilterNavigationItems();
  var initial = uiPreferences.Get("last-route");
  Navigate(routes.ContainsKey(initial ?? "") ? initial! : "dashboard", false);
 }
 void SelectRoute(string key, bool push, string? title = null, string? description = null)
 {
  if (!routes.TryGetValue(key, out var page)) return;
  if (push && !selectingRoute && currentRoute is not null && currentRoute != key) routeHistory.Push(currentRoute);
  currentRoute = key;
  uiPreferences.Set("last-route", key);
  var item = NavigationList.Items.OfType<ListBoxItem>().Single(i => i.Tag?.ToString() == key);
  selectingRoute = true;
  try { NavigationList.SelectedItem = item; ModuleTabs.SelectedItem = page; } finally { selectingRoute = false; }
  PageTitle.Text = title ?? routeTitles[key];
  PageDescription.Text = description ?? item.ToolTip?.ToString() ?? "";
  BreadcrumbText.Text = routeHistory.Count == 0 ? "Ana sayfa" : $"Ana sayfa  /  {string.Join("  /  ", routeHistory.Reverse().Take(2).Select(x => routeTitles.TryGetValue(x, out var t) ? t : x))}";
  BackButton.IsEnabled = routeHistory.Count > 0;
 }
 void Navigate(string key, bool push = true) => SelectRoute(key, push);
 void FilterNavigationItems()
 {
  var query = NavigationSearchBox.Text.Trim();
  foreach (var item in NavigationList.Items.OfType<ListBoxItem>())
  {
   if (item.Tag is null) { item.Visibility = Visibility.Visible; continue; }
   item.Visibility = query.Length == 0 || item.Content?.ToString()?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true ? Visibility.Visible : Visibility.Collapsed;
  }
 }
 void Back_Click(object sender, RoutedEventArgs e)
 {
  if (routeHistory.Count == 0) return;
  var previous = routeHistory.Pop();
  SelectRoute(previous, false);
 }
}



