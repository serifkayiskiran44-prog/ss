using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
 readonly Dictionary<string, UIElement> builtPages = new();
 readonly Dictionary<string, TabItem> routes = new();
 readonly Dictionary<string, Func<UIElement>> routeFactories = new();
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
   RegisterPage(key,title,description,new TabItem{Tag=key,Header=title,Content=content},label);
  }
  void LazyPage(string key,string title,string description,Func<UIElement> factory,string? label=null)
  {
   ArgumentNullException.ThrowIfNull(factory);
   var page=new TabItem{Tag=key,Header=title};routeFactories.Add(key,factory);RegisterPage(key,title,description,page,label);
  }
  void RegisterPage(string key,string title,string description,TabItem page,string? label)
  {
   routes.Add(key,page);routeTitles[key]=title;ModuleTabs.Items.Add(page);
   var item=new ListBoxItem{Tag=key,Content=label??title,ToolTip=description};NavigationList.Items.Add(item);
   item.Selected+=(_,_)=>
   {
    if(!selectingRoute && key=="marketplaces") marketplaceHome.ShowAll();
    SelectRoute(key, !selectingRoute, title, description);
   };
  }
  Group("GENEL");
  Page("dashboard","Genel bakış","Ürün, sipariş, XML ve bağlantı sağlığını tek ekranda izleyin.",DashboardPanel.Create(dataDirectory,OpenMarketplaceRoute));
  Group("KATALOG");
  Page("products","Ürün yönetimi","Tüm ürün bilgilerini yan yana inceleyin, filtreleyin ve düzenleyin.",builtPages["Ürün havuzu"]);
  LazyPage("bulk-products","Toplu ürün işlemleri","Seçili veya filtrelenmiş ürünlerde güvenli yerel toplu işlemleri önizleyin.",()=>BulkProductsPanel.Create(dataDirectory,OpenMarketplaceRoute));
  Page("categories","Kategoriler","Kategori ağacı, ürün sayıları, pazaryeri eşlemeleri ve içerik şablonları.",new TaxonomyWorkspacePanel(dataDirectory,Catalog.TaxonomyKind.Category,RefreshProducts));
  Page("brands","Markalar","Marka listesi, ürün sayıları ve pazaryeri karşılıkları.",new TaxonomyWorkspacePanel(dataDirectory,Catalog.TaxonomyKind.Brand,RefreshProducts));
  Page("taxonomy","Kategori ve marka eşleme","Yerel sözlükleri pazaryeri kategori ve markalarıyla eşleyin.",BuildTaxonomy());
  Page("media","Medya yönetimi","Ürün görsellerini ve medya doğrulama uyarılarını yönetin.",MediaPanel.Create(dataDirectory,OpenMarketplaceRoute));
  Page("xml","XML otomasyonu","XML kaynakları, alan eşleme, fiyat formülü, zamanlama ve manuel çalıştırma.",builtPages["XML otomasyonu"]);
  Page("excel","Excel işlemleri","SKU ve sütun harfleriyle ürün, fiyat, stok, kategori ve sipariş aktarımı; önizleme ve geri alma.",BuildExcel());
  Group("OPERASYON");
  Page("orders","Siparişler","Sipariş listesi, detay, kargo takibi, iade filtresi ve toplu işlem önizlemesi.",OrdersPanel.Create(dataDirectory,AuthorizedAsync,RefreshProducts));
  LazyPage("order-exceptions","Sipariş istisnaları","Eşleşmeyen, inceleme bekleyen ve güvenli kullanıcı kararı isteyen siparişleri yönetin.",()=>OrderExceptionsPanel.Create(dataDirectory,OpenMarketplaceRoute));
  Page("policy-center","Politika merkezi","Fiyat, stok ve operasyon güvenlik kurallarını yönetin.",PolicyCenterPanel.Create(dataDirectory));
  Page("sync","Senkronizasyon","Kanal, XML ve otomasyon işlerini tek kuyruğa bağlı olarak izleyin.",BuildSync());
  Page("automation","Otomasyon","Zamanlanmış işleri ve kontrollü tekrar denemeleri yönetin.",BuildAutomation());
  Group("MAĞAZALAR");
  marketplaceHome=new MarketplaceAccountHomePanel(dataDirectory,navigate:OpenMarketplaceRoute);
  marketplaceHome.ProductCardRequested += productId => OpenProductCard(false, productId);
  Page("marketplaces","Pazaryeri hesapları","Etkin mağazaları ayrı kartlar halinde açın; ürün ve işlem verileri seçili hesaba bağlı kalır.",marketplaceHome);
  LazyPage("listing-matrix","İlan ve eşleşme matrisi","Ürünlerin exact-account mağaza bağlarını, adaylarını ve yayın durumlarını karşılaştırın.",()=>ChannelListingMatrixPanel.Create(dataDirectory,OpenMarketplaceRoute));
  CompatibilityAccountLink("trendyol","Trendyol","Kayıtlı bir Trendyol hesabını açar.");
  CompatibilityAccountLink("etsy","Etsy","Kayıtlı bir Etsy hesabını açar.");
  Page("bizimhesap","BizimHesap","Ürün/depo okuma ve XML ürünleri için SKU-barkod eşleştirme önizlemesi.",BizimHesapPanel.Create(dataDirectory));
  Page("nes","NES e‑Fatura","Siparişten UBL‑TR hazırlama, NES önizleme, kontrollü gönderim ve belge takibi.",NesPanel.Create(dataDirectory));
  Group("İZLEME");
  LazyPage("data-quality","Veri kalitesi","Eksik, geçersiz veya eşleşmeyen ürün alanlarını inceleyin.",()=>DataQualityPanel.Create(dataDirectory,OpenMarketplaceRoute));
  Page("api-health","API sağlığı","Mağaza bağlantıları ve servis sağlık kontrollerini izleyin.",ApiHealthPanel.Create(dataDirectory,OpenMarketplaceRoute));
  Page("messages","Mesaj merkezi","Operasyon bildirimlerini ve hata mesajlarını tek listede görün.",MessagePanel.Create(dataDirectory,OpenMarketplaceRoute));
  Page("reports","Raporlar ve denetim","Tanılama, denetim kaydı ve operasyon raporlarını inceleyin.",DiagnosticsPanel.Create(dataDirectory,OpenMarketplaceRoute));
  LazyPage("readiness","Üretim hazırlığı","Yerel güvenlik, veri ve bağlantı kontrollerinin üretime hazırlık durumunu inceleyin.",()=>ProductionReadinessPanel.Create(dataDirectory));
  Group("SİSTEM");
  Page("connections","Mağaza bağlantıları","Hesap metadatası, etkinlik, bağlantı durumu ve desteklenen yetenekler.",MarketplaceConnectionsPanel.Create(dataDirectory,OpenMarketplaceRoute,marketplaceHome.Refresh));
  Page("settings","Ayarlar","Hesap, mağaza, otomasyon, yerel veri ve sağlık ayarlarını bölümler halinde yönetin.",new SettingsHubPanel(dataDirectory,backgroundController,OpenMarketplaceRoute));
  LazyPage("locale","Yerel ayarlar","Para birimi, sayı/tarih kültürü ve vergi varsayılanlarını yönetin.",()=>LocaleSettingsPanel.Create(dataDirectory));
  LazyPage("backup","Veri ve yedek","Yerel veri yedekleme ve kontrollü geri yükleme işlemlerini yönetin.",()=>DataBackupPanel.Create(dataDirectory));
  LazyPage("migration","Veri taşıma","Eski dışa aktarımları güvenli önizleme ve atomik uygulama ile içeri alın.",()=>MigrationAssistantPanel.Create(dataDirectory));
  NavigationSearchBox.TextChanged += (_, _) => FilterNavigationItems();
  var parity=ScreenParityAudit.Evaluate(routes.Keys); if(!parity.IsComplete) Log("Ekran paritesi BLOCKED: "+string.Join(", ",parity.MissingRoutes));
  var initial = uiPreferences.Get("last-route");
  if(routes.ContainsKey(initial ?? "")) Navigate(initial!, false);
  else if(IsMarketplaceChannel(initial))
  {
   marketplaceHome.ShowChannel(initial!);
   Navigate("marketplaces", false);
  }
  else Navigate("dashboard", false);

  void CompatibilityAccountLink(string channel,string title,string description)
  {
   var item=new ListBoxItem{Tag=channel,Content=title,ToolTip=description};NavigationList.Items.Add(item);
   item.Selected+=(_,_)=>{if(selectingRoute)return;try{marketplaceHome.ShowChannel(channel);SelectRoute("marketplaces",true,"Pazaryeri hesapları",description);}catch(Exception error){Log(Safe(error));SelectRoute("marketplaces",true);}};
  }
 }

 void OpenMarketplaceRoute(string key)
 {
  const string connectionPrefix="connection:";
  if(key.StartsWith(connectionPrefix,StringComparison.OrdinalIgnoreCase))
  {
   marketplaceHome.ShowConnection(key[connectionPrefix.Length..]);
   SelectRoute("marketplaces",true);
   return;
  }
  if (key.Equals("diagnostics", StringComparison.OrdinalIgnoreCase)) key = "reports";
  if(routes.ContainsKey(key))
  {
   Navigate(key);
   return;
  }
  if(IsMarketplaceChannel(key))
  {
   marketplaceHome.ShowChannel(key);
   SelectRoute("marketplaces",true);
   return;
  }
 }
 static bool IsMarketplaceChannel(string? key) => !string.IsNullOrWhiteSpace(key) && MarketplaceConnectionCatalog.All.Any(x => x.Id.Equals(key,StringComparison.OrdinalIgnoreCase));
 void SelectRoute(string key, bool push, string? title = null, string? description = null)
 {
  if (!routes.TryGetValue(key, out var page)) return;
  if(page.Content is null && routeFactories.TryGetValue(key,out var factory))
  {
   page.Content=factory();
   routeFactories.Remove(key);
  }
  if (push && !selectingRoute && currentRoute is not null && currentRoute != key) routeHistory.Push(currentRoute);
  currentRoute = key;
  uiPreferences.Set("last-route", key);
  var item = NavigationList.Items.OfType<ListBoxItem>().Single(i => i.Tag?.ToString() == key);
  selectingRoute = true;
  try { NavigationList.SelectedItem = item; ModuleTabs.SelectedItem = page; } finally { selectingRoute = false; }
  // Ortak bağlam ve global komutlar yoğun tablolarda da görünür kalır; eski özet şeridi kullanılmaz.
  PageHeader.Visibility = Visibility.Visible;
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



