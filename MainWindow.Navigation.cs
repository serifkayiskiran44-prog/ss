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
 MarketplaceAccountHomePanel marketplaceHome = null!;

 void BuildNavigation()
 {
  void Group(string title) => NavigationList.Items.Add(new ListBoxItem { Content=title, IsEnabled=false, Focusable=false, FontSize=10, FontWeight=FontWeights.Bold, Foreground=new SolidColorBrush(Color.FromRgb(130,161,177)), Padding=new Thickness(14,12,4,2) });
  void Page(string key,string title,string description,UIElement content,string? label=null)
  {
   var page=new TabItem{Tag=key,Header=title,Content=content};routes.Add(key,page);routeTitles[key]=title;ModuleTabs.Items.Add(page);
   var item=new ListBoxItem{Tag=key,Content=label??title,ToolTip=description};NavigationList.Items.Add(item);
   item.Selected+=(_,_)=>
   {
    if(!selectingRoute && key=="marketplaces") marketplaceHome.ShowAll();
    SelectRoute(key, !selectingRoute, title, description);
   };
  }
  Group("ÇALIŞMA ALANI");
  Page("products","Ürün yönetimi","Tüm ürün bilgilerini yan yana inceleyin, filtreleyin ve düzenleyin.",builtPages["Ürün havuzu"]);
  Page("categories","Kategoriler","Kategori ağacı, ürün sayıları, pazaryeri eşlemeleri ve içerik şablonları.",new TaxonomyWorkspacePanel(dataDirectory,Catalog.TaxonomyKind.Category,RefreshProducts));
  Page("brands","Markalar","Marka listesi, ürün sayıları ve pazaryeri karşılıkları.",new TaxonomyWorkspacePanel(dataDirectory,Catalog.TaxonomyKind.Brand,RefreshProducts));
  Page("xml","XML otomasyonu","XML kaynakları, alan eşleme, fiyat formülü, zamanlama ve manuel çalıştırma.",builtPages["XML otomasyonu"]);
  Page("excel","Excel işlemleri","SKU ve sütun harfleriyle ürün, fiyat, stok, kategori ve sipariş aktarımı; önizleme ve geri alma.",BuildExcel());
  Page("orders","Siparişler","Sipariş listesi, detay, kargo takibi, iade filtresi ve toplu işlem önizlemesi.",OrdersPanel.Create(dataDirectory,AuthorizedAsync,RefreshProducts));
  Group("ENTEGRASYON");
  marketplaceHome=new MarketplaceAccountHomePanel(dataDirectory);
  marketplaceHome.ProductCardRequested += productId => OpenProductCard(false, productId);
  Page("marketplaces","Pazaryeri hesapları","Etkin mağazaları ayrı kartlar halinde açın; ürün ve işlem verileri seçili hesaba bağlı kalır.",marketplaceHome);
  CompatibilityAccountLink("trendyol","Trendyol","Kayıtlı bir Trendyol hesabını açar.");
  CompatibilityAccountLink("etsy","Etsy","Kayıtlı bir Etsy hesabını açar.");
  Page("bizimhesap","BizimHesap","Ürün/depo okuma ve XML ürünleri için SKU-barkod eşleştirme önizlemesi.",BizimHesapPanel.Create(dataDirectory));
  Group("YÖNETİM");
  Page("connections","Mağaza bağlantıları","Hesap metadatası, etkinlik, bağlantı durumu ve desteklenen yetenekler.",MarketplaceConnectionsPanel.Create(dataDirectory,OpenMarketplaceRoute,marketplaceHome.Refresh));
  var settings=new StackPanel{Margin=new Thickness(20)};
  settings.Children.Add(Heading("Hesaplar ve uygulama ayarları"));
  settings.Children.Add(Hint("Pazaryeri erişim bilgileri ilgili kanalın Bağlantı sekmesindedir. Bağlantı doğrulaması ürün aktarımının etkin olduğu anlamına gelmez."));
  settings.Children.Add(Button("BizimHesap bağlantı ayarları",()=>Navigate("bizimhesap")));
  settings.Children.Add(Button("Mağaza bağlantılarını yönet",()=>Navigate("connections")));
  if(backgroundController is not null)
  {
   var closeToTray=new CheckBox{Content="Pencereyi kapatınca MonoBridge'i bildirim alanında çalıştır",IsChecked=backgroundController.CloseToTray,Margin=new Thickness(4,10,4,4)};
   closeToTray.Checked+=(_,_)=>backgroundController.CloseToTray=true;
   closeToTray.Unchecked+=(_,_)=>backgroundController.CloseToTray=false;
   settings.Children.Add(closeToTray);
  }
  settings.Children.Add(Heading("Sürüm, yedek ve taşıma"));settings.Children.Add(DataBackupPanel.Create(dataDirectory));
  settings.Children.Add(Heading("Yerel veri ve otomasyon"));settings.Children.Add(Hint("XML kaynakları ve ürün kilitleri XML otomasyonu / Ürün yönetimi ekranlarından düzenlenir. Zamanlı salt okunur işler MonoBridge bildirim alanında çalışırken devam eder. İşlem geçmişi pencerenin altındadır."));
  Page("settings","Ayarlar","Hesap bağlantıları, pazaryeri görselleri ve yerel çalışma bilgileri",Scroll(settings));
  NavigationSearchBox.TextChanged += (_, _) => FilterNavigationItems();
  var parity=ScreenParityAudit.Evaluate(routes.Keys); if(!parity.IsComplete) Log("Ekran paritesi BLOCKED: "+string.Join(", ",parity.MissingRoutes));
  var initial = uiPreferences.Get("last-route");
  Navigate(routes.ContainsKey(initial ?? "") ? initial! : initial is "trendyol" or "etsy" ? "marketplaces" : "products", false);

  void CompatibilityAccountLink(string channel,string title,string description)
  {
   var item=new ListBoxItem{Tag=channel,Content=title,ToolTip=description};NavigationList.Items.Add(item);
   item.Selected+=(_,_)=>{if(selectingRoute)return;try{marketplaceHome.ShowChannel(channel);SelectRoute("marketplaces",true,"Pazaryeri hesapları",description);}catch(Exception error){Log(Safe(error));SelectRoute("marketplaces",true);}};
  }
 }

 void OpenMarketplaceRoute(string key)
 {
  if(key is "trendyol" or "etsy")
  {
   marketplaceHome.ShowChannel(key);
   SelectRoute("marketplaces",true);
   return;
  }
  Navigate(key);
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
  // Ürün ekranının kendi arama ve işlem araçları vardır; ortak başlık ve özet şeridi tablo alanını daraltmasın.
  PageHeader.Visibility = key is "products" or "marketplaces" ? Visibility.Collapsed : Visibility.Visible;
  ProductSummaryBar.Visibility = Visibility.Collapsed;
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



