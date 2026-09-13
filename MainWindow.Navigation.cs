using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
 readonly Dictionary<string, UIElement> builtPages = new();
 readonly Dictionary<string, TabItem> routes = new();
 readonly Dictionary<string, string> routeTitles = new();
 readonly Dictionary<string, string> routeDescriptions = new();
 readonly Dictionary<string, string> navLabels = new();
 // #812: collapsed-or-not and the open width are the operator's, persisted per data directory.
 NavigationSidebarState sidebarState = NavigationSidebar.Default;
 // #810: the trail is a context stack, not a list of route keys -- a crumb remembers the store the board was
 // filtered to and the entity the card was counting, so Back restores the view instead of just the screen.
 readonly DrillThroughStack drillStack = new(new DrillTarget("dashboard", "Genel bakış"));
 // #813: the orders panel hands back its own reveal so an order crumb selects the order the way a product crumb
 // selects the product; null until the panel is built.
 Func<string, string, string, bool>? ordersReveal;
 UiPreferenceStore uiPreferences = null!;
 string? currentRoute;
 bool selectingRoute;
 TabControl? etsyTabs;

 void BuildNavigation()
 {
  void Group(string title) => NavigationList.Items.Add(new ListBoxItem { Content=title, IsEnabled=false, Focusable=false, FontSize=DesignTokens.TextCaptionSize, FontWeight=FontWeights.Bold, Foreground=new SolidColorBrush(Color.FromRgb(130,161,177)), Padding=new Thickness(14,12,4,2) });
  void Page(string key,string title,string description,UIElement content,string? label=null)
  {
   var page=new TabItem{Tag=key,Header=title,Content=content};routes.Add(key,page);routeTitles[key]=title;routeDescriptions[key]=description;ModuleTabs.Items.Add(page);
   navLabels[key]=label??title;
   var item=new ListBoxItem{Tag=key,Content=label??title,ToolTip=description};System.Windows.Automation.AutomationProperties.SetName(item,label??title);NavigationList.Items.Add(item);
   item.Selected+=(_,_)=>SelectRoute(key, !selectingRoute, title, description);
  }
  Group("KATALOG VE TEDARİK");
  Page("dashboard","Genel bakış","Ürün, sipariş, XML, bağlantı ve sync durumunu tek ekranda izleyin.",DashboardPanel.Create(dataDirectory,key=>Navigate(key),DrillThrough,SwitchDashboardStore,key=>routes.ContainsKey(key)));
  Page("onboarding","İlk kurulum","Mağaza, XML, stok, fiyat ve Excel başlangıç adımlarını güvenli önizlemeyle tamamlayın.",OnboardingPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("products","Ürün yönetimi","Ortak ürün havuzu • Ürün seçerek kartını, fiyatını ve stok kilitlerini düzenleyin.",builtPages["Ürün havuzu"]);
  Page("bulk-products","Toplu ürün işlemleri","Seçili veya filtrelenmiş ürünleri preview, sürüm kontrolü ve açık onay ile güncelleyin.",BulkProductsPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("media","Görsel / medya","Ürün görsellerini kaynak, doğrulama durumu, sıra ve ana görsel olarak yönetin.",MediaPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("listing-matrix","Kanal yayın matrisi","Ürün, kanal ve mağaza bazında mapping, ilan, sync ve bağlantı durumlarını karşılaştırın.",ChannelListingMatrixPanel.Create(dataDirectory,key=>Navigate(key),()=>AllowedStoreKeys()));
  Page("xml","XML yönetimi","Kaynak bağlantısı → Alan eşleştirme → Fiyat ve stok → Önizleme ve havuza aktarım",builtPages["XML yönetimi"]);
  Page("excel","Excel ürün işlemleri","Excel dışa aktarma ve içe aktarma önizlemesi",BuildExcel());
  Page("migration","Veri geçiş asistanı","Eski Excel, CSV, JSON veya XML dışa aktarımlarını güvenli önizleme ve geri alma günlüğüyle taşıyın.",MigrationAssistantPanel.Create(dataDirectory));
  Page("taxonomy","Kategori / marka / özellik","Yerel sözlük kayıtları ve harici anahtar eşlemeleri",BuildTaxonomy());
  Page("sync","Sync merkezi","Yerel sync kuyruğu, idempotency ve tekrar deneme durumları",BuildSync());
  Page("automation","Otomasyon","Kanal ve mağaza bazlı stok/fiyat zamanlayıcıları",BuildAutomation());
  Group("PAZARYERLERİ");
  etsyTabs=new TabControl();
  etsyTabs.Items.Add(new TabItem{Header="Ürünler",Content=builtPages["Etsy ilanları"]});
  etsyTabs.Items.Add(new TabItem{Header="Şablon ve eşleştirme",Content=builtPages["Global Etsy şablonu"]});
  etsyTabs.Items.Add(new TabItem{Header="Satışa hazırlık",Content=EtsyReadinessPanel.Create(dataDirectory,key=>Navigate(key))});
  etsyTabs.Items.Add(new TabItem{Header="Bağlantı",Content=builtPages["Etsy bağlantısı"]});
  Page("etsy","Etsy","Mağaza ilanları, ortak havuzdan taslak ve hesap yetkilendirmesi. Bağlantı durumu Bağlantı sekmesinde doğrulanır.",etsyTabs);
  foreach(var id in new[]{"ebay","ozon","joom"})
  {
   var channel=MarketplaceRegistry.All.Single(c=>c.Id==id);
   var tabs=new TabControl();
   tabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create(id,dataDirectory)});
   tabs.Items.Add(new TabItem{Header="Bağlantı",Content=id=="joom"?JoomPanel.Create(dataDirectory):MarketplaceSetupPanel.CreateChannel(id,dataDirectory,settingsEditState)});
   Page(id,channel.Name,id=="joom"?"Satıcı kaydı / kabulü bekleniyor • Canlı ürün aktarımı etkin değil.":"Yerel kanal ürün planları • API bağlantı kontrolü ayrı; canlı ürün aktarımı etkin değil.",tabs);
  }
  var amazonTabs=new TabControl();amazonTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("amazon",dataDirectory)});amazonTabs.Items.Add(new TabItem{Header="Bağlantı",Content=AmazonPanel.Create(dataDirectory)});Page("amazon","Amazon","SP-API ayarları ve yerel ürün planları; resmi sözleşme doğrulanana kadar canlı operasyon kapalı.",amazonTabs);
  var trendyolTabs=new TabControl();trendyolTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("trendyol",dataDirectory)});trendyolTabs.Items.Add(new TabItem{Header="Bağlantı",Content=TrendyolPanel.Create(dataDirectory,settingsEditState)});Page("trendyol","Trendyol","Satıcı API ayarları ve yerel ürün planları; resmi sözleşme doğrulanana kadar canlı operasyon kapalı.",trendyolTabs);
  var hepsiTabs=new TabControl();hepsiTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("hepsiburada",dataDirectory)});hepsiTabs.Items.Add(new TabItem{Header="Bağlantı",Content=HepsiburadaPanel.Create(dataDirectory)});Page("hepsiburada","Hepsiburada","Merchant API ayarları ve yerel ürün planları; resmi sözleşme doğrulanana kadar canlı operasyon kapalı.",hepsiTabs);
  var fruugoTabs=new TabControl();fruugoTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("fruugo",dataDirectory)});fruugoTabs.Items.Add(new TabItem{Header="Bağlantı",Content=FruugoPanel.Create(dataDirectory)});Page("fruugo","Fruugo","Retailer ayarları ve yerel ürün planları; resmi sözleşme doğrulanana kadar canlı operasyon kapalı.",fruugoTabs);
  var allegroTabs=new TabControl();allegroTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("allegro",dataDirectory)});allegroTabs.Items.Add(new TabItem{Header="Bağlantı",Content=AllegroPanel.Create(dataDirectory)});Page("allegro","Allegro","Public API read-only ürün/sipariş okuma ve yerel ürün planları.",allegroTabs);
  var wishTabs=new TabControl();wishTabs.Items.Add(new TabItem{Header="Ürünler",Content=ChannelProductsPanel.Create("wish",dataDirectory)});wishTabs.Items.Add(new TabItem{Header="Bağlantı",Content=WishPanel.Create(dataDirectory)});Page("wish","Wish","Merchant ayarları ve yerel ürün planları; resmi sözleşme doğrulanana kadar canlı operasyon kapalı.",wishTabs);
  Page("channels","Diğer pazaryerleri","Wish, Allegro ve Fruugo: hesap başvuruları ve entegrasyon gereksinimleri.",MarketplaceSetupPanel.CreateOther(dataDirectory));
  Page("connections","Mağaza bağlantıları","Tüm kanal ve mağaza kayıtları, yetenekler ve salt okunur bağlantı testleri.",MarketplaceConnectionsPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("api-health","API bağlantı sağlığı","Auth, erişilebilirlik, rate-limit, kota ve son hata durumu",ApiHealthPanel.Create(dataDirectory,key=>Navigate(key)));
  Group("OPERASYON");
  Page("price-policies","Mağaza fiyat kuralları","CASE formülü, kur ve güvenli fiyat önizlemesi",BuildPricePolicies());
  Page("stock-policies","Mağaza stok ayarları","Güvenlik stoğu, üst sınır ve yerel önizleme",BuildStockPolicies());
  Page("policy-center","Stok / fiyat politika merkezi","Kanal + mağaza politikaları, kopyalama ve ürün preview'i",PolicyCenterPanel.Create(dataDirectory));
  Page("locale-settings","Döviz / vergi / yerel ayarlar","Para birimi, KDV, sayı-tarih kültürü ve mağaza kopyalama",LocaleSettingsPanel.Create(dataDirectory,settingsEditState));
  Page("data-quality","Veri kalite merkezi","Duplicate, zorunlu alan, fiyat/stok/döviz, URL ve kaynak hataları",DataQualityPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("orders","Sipariş ve kargo","Sipariş kayıtları, paket ve kargo takibi",OrdersPanel.Create(dataDirectory,AuthorizedAsync,RefreshProducts,reveal=>ordersReveal=reveal));
  Page("order-exceptions","Sipariş istisnaları","Eksik SKU, iptal/iade ve stok kararlarını önizleme/onay ile yönetin.",OrderExceptionsPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("messages","Mesaj merkezi","Müşteri mesajları, sistem bildirimleri ve yerel yanıt şablonları",MessagePanel.Create(dataDirectory,key=>Navigate(key)));
  Page("shipping","Navlungo","Kargo bağlantısı ve mevcut hizmet işlemleri",NavlungoPanel.Create(),"Kargo bağlantısı");
  Group("YÖNETİM");
  Page("readiness","Üretim hazırlığı","Yerel veri, secret güvenliği, connector capability ve API sağlık geçidi",ProductionReadinessPanel.Create(dataDirectory));
  Page("reports","Raporlar","Rapor kataloğu: amaç, veri kapsamı, son çalıştırma, kayıtlı filtre ve çıktı türü",ReportsPanel.Create(dataDirectory,key=>Navigate(key),()=>AllowedStoreKeys()));
  Page("diagnostics","Tanılama / audit","Güvenli sistem sağlık özeti, audit trail ve destek paketi",DiagnosticsPanel.Create(dataDirectory,key=>Navigate(key)));
  // #853: one taxonomy over the settings that exist -- links to the owning screens, inline only for what the shell owns; a channel's credentials open on its own connection tab.
  Page("settings","Ayarlar","Genel, mağaza, bağlantı, içe aktarma, fiyat, bildirim ve tanılama ayarları tek ağaçta",SettingsPanel.Create(new SettingsPanel.Context(dataDirectory,key=>Navigate(key),key=>routes.ContainsKey(key),(route,section)=>{Navigate(route);if(section=="connection"&&routes.TryGetValue(route,out var page)&&page.Content is TabControl tabs)tabs.SelectedIndex=tabs.Items.Count-1;},settingsEditState,()=>SettingsValidation.Collect(dataDirectory,settingsEditState,DateTime.UtcNow)),select=>settingsSelect=select));
  NavigationSearchBox.TextChanged += (_, _) => FilterNavigationItems();
  var parity=ScreenParityAudit.Evaluate(routes.Keys); if(!parity.IsComplete) Log("Ekran paritesi BLOCKED: "+string.Join(", ",parity.MissingRoutes));
  var readiness=PreflightCenter.FromEtsy(new EtsyReadinessService().Build()); Log($"Yayın öncesi preflight: {readiness.Status}; engel={readiness.BlockingItems.Count}");
  var initial = uiPreferences.Get("last-route");
  Navigate(routes.ContainsKey(initial ?? "") ? initial! : "dashboard", false);
  sidebarState = NavigationSidebar.Parse(uiPreferences.Get(NavigationSidebar.PreferenceKey));
  ApplySidebarState();
 }
 // #812: one place turns the state into the shell -- column width, brand, search box, group headers and every
 // entry's content/tooltip/accessible name -- so toggle, drag and restart all render the same way.
 void ApplySidebarState()
 {
  var collapsed = sidebarState.Collapsed;
  SidebarColumn.Width = new GridLength(NavigationSidebar.CurrentWidth(sidebarState));
  SidebarColumn.MinWidth = collapsed ? NavigationSidebar.CollapsedWidth : NavigationSidebar.MinExpandedWidth;
  SidebarColumn.MaxWidth = collapsed ? NavigationSidebar.CollapsedWidth : NavigationSidebar.MaxExpandedWidth;
  SidebarSplitter.IsEnabled = !collapsed;
  SidebarBrand.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
  SidebarFooter.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
  NavigationSearchBox.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
  SidebarToggle.Content = collapsed ? "»" : "«";
  SidebarToggle.ToolTip = collapsed ? "Menüyü genişlet (Ctrl+B)" : "Menüyü daralt (Ctrl+B)";
  foreach (var item in NavigationList.Items.OfType<ListBoxItem>())
  {
   if (item.Tag is not string key) { item.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible; continue; }
   var shown = NavigationSidebar.Present(navLabels.TryGetValue(key, out var label) ? label : key, routeDescriptions.TryGetValue(key, out var description) ? description : "", collapsed);
   item.Content = shown.Content;
   item.ToolTip = shown.ToolTip;
   item.HorizontalContentAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
   System.Windows.Automation.AutomationProperties.SetName(item, shown.AccessibleName);
  }
  if (!collapsed) FilterNavigationItems();
 }
 void SaveSidebarState() { try { uiPreferences.Set(NavigationSidebar.PreferenceKey, NavigationSidebar.Serialize(sidebarState)); } catch (Exception error) { Log("Menü durumu kaydedilemedi: " + error.Message); } }
 void ToggleSidebar()
 {
  sidebarState = NavigationSidebar.Toggle(sidebarState);
  ApplySidebarState();
  SaveSidebarState();
 }
 void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();
 void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
 {
  sidebarState = NavigationSidebar.Resize(sidebarState, SidebarColumn.ActualWidth);
  ApplySidebarState();
  SaveSidebarState();
 }
 void SelectRoute(string key, bool push, string? title = null, string? description = null)
 {
  if (!routes.TryGetValue(key, out var page)) return;
  if (push && !selectingRoute && currentRoute is not null && currentRoute != key)
  {
   // Sidebar navigation stays inside the board's current scope, so it is offered that scope and nothing else.
   var scope = drillStack.CurrentStoreKey;
   var open = drillStack.Open(new DrillTarget(key, routeTitles.TryGetValue(key, out var routeTitle) ? routeTitle : key, scope), new[] { scope });
   if (!open.Allowed) { Log(open.Notice); return; }
  }
  currentRoute = key;
  uiPreferences.Set("last-route", key);
  var item = NavigationList.Items.OfType<ListBoxItem>().Single(i => i.Tag?.ToString() == key);
  selectingRoute = true;
  try { NavigationList.SelectedItem = item; ModuleTabs.SelectedItem = page; } finally { selectingRoute = false; }
  PageTitle.Text = title ?? routeTitles[key];
  PageDescription.Text = description ?? item.ToolTip?.ToString() ?? "";
  BreadcrumbText.Text = drillStack.TrailText();
  BackButton.IsEnabled = drillStack.CanGoBack;
 }
 // #853: the settings shell exposes its category selector so a deep link ("settings/pricing") lands on the category.
 Action<string>? settingsSelect;
 // #854: one edit state for every settings form; the shell's badges read it, the forms write it (labels, never values).
 readonly SettingsEditState settingsEditState = new();
 void Navigate(string key, bool push = true)
 {
  var category = SettingsTaxonomy.ParseDeepLink(key);
  if (category is not null) { SelectRoute("settings", push); settingsSelect?.Invoke(category); return; }
  SelectRoute(key, push);
 }
 // #810: a dashboard card drills through with its context; a wrong-store link is refused before anything moves.
 void DrillThrough(DrillRequest request)
 {
  var open = drillStack.Open(request.Target, request.AllowedStoreKeys);
  if (!open.Allowed) { Log(open.Notice); return; }
  SelectRoute(request.Target.Route, false);
 }
 void SwitchDashboardStore(string storeKey)
 {
  var current = drillStack.SwitchStore(storeKey);
  if (currentRoute != current.Route) SelectRoute(current.Route, false); else { BreadcrumbText.Text = drillStack.TrailText(); BackButton.IsEnabled = drillStack.CanGoBack; }
 }
 // A crumb is only worth returning to if what it pointed at still exists.
 bool DrillEntityAlive(DrillTarget target)
 {
  try
  {
   return target.EntityKind switch
   {
    "product" => new Catalog.CatalogStore(dataDirectory).FindProduct(target.EntityId) is not null,
    "order" => target.EntityId.Split('|') is { Length: 3 } parts && new OrdersStore(dataDirectory).Find(parts[0], parts[1], parts[2]) is not null,
    "source" => new Catalog.CatalogStore(dataDirectory).Sources().Any(x => x.Id == target.EntityId),
    _ => true,
   };
  }
  catch (Exception error) { Log("Geri dönüş kontrolü yapılamadı: " + error.Message); return true; }
 }
 // #813: the stores a link may name are the enabled connections on disk, read when asked; a failure to read
 // them allows nothing but the all-stores scope, so an unreadable store table cannot widen what a link opens.
 IReadOnlyList<string> AllowedStoreKeys()
 {
  try { return new MarketplaceConnectionStore(dataDirectory).List().Where(c => c.Enabled).Select(c => DashboardStoreFilter.KeyFor(c.Channel, c.ShopId)).ToList(); }
  catch (Exception error) { Log("Mağaza listesi okunamadı: " + error.Message); return Array.Empty<string>(); }
 }
 // One entry point for every workspace entity (#813): the same trail, the same refusal, the same reveal.
 bool OpenWorkspaceLink(DrillTarget target)
 {
  var open = drillStack.Open(target, AllowedStoreKeys());
  if (!open.Allowed) { Log(open.Notice); return false; }
  SelectRoute(target.Route, false);
  if (target.EntityId.Length > 0 && !RevealEntity(target)) Log($"{target.EntityLabel} kaydı bulunamadı; {(routeTitles.TryGetValue(target.Route, out var t) ? t : target.Route)} ekranı açıldı.");
  return true;
 }
 // The landing screen selects what the crumb names. True when there was nothing to select (a screen-level
 // crumb) or the selection happened; false only when the entity is gone.
 bool RevealEntity(DrillTarget target)
 {
  try
  {
   switch (target.EntityKind)
   {
    case "product":
    {
     var product = store.FindProduct(target.EntityId);
     if (product is null) return false;
     if (products.Items.OfType<CatalogProduct>().All(p => p.Id != product.Id)) { search.Text = product.Sku; productOffset = 0; RefreshProducts(); }
     var row = products.Items.OfType<CatalogProduct>().FirstOrDefault(p => p.Id == product.Id);
     if (row is null) return false;
     products.SelectedItem = row; products.ScrollIntoView(row); return true;
    }
    case "source":
    {
     // Refresh first, then select the instance the list actually holds -- a row from a separate Sources() call
     // is a different object and the ListBox would not select it.
     RefreshSources(false);
     var row = sources.Items.OfType<XmlSource>().FirstOrDefault(x => x.Id == target.EntityId);
     if (row is null) return false;
     sources.SelectedItem = row; SetSource(Clone(row)); return true;
    }
    case "order":
     return target.EntityId.Split('|') is { Length: 3 } parts && ordersReveal is { } reveal && reveal(parts[0], parts[1], parts[2]);
    default:
     return true;
   }
  }
  catch (Exception error) { Log("Kayıt seçilemedi: " + error.Message); return false; }
 }
 void FilterNavigationItems()
 {
  var query = NavigationSearchBox.Text.Trim();
  foreach (var item in NavigationList.Items.OfType<ListBoxItem>())
  {
   // Group headers stay hidden while the sidebar is collapsed; entries match on their label, not on the
   // glyph that icons-only mode prints in Content.
   if (item.Tag is not string key) { item.Visibility = sidebarState.Collapsed ? Visibility.Collapsed : Visibility.Visible; continue; }
   var label = navLabels.TryGetValue(key, out var l) ? l : item.Content?.ToString() ?? "";
   item.Visibility = query.Length == 0 || label.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
  }
 }
 void Back_Click(object sender, RoutedEventArgs e)
 {
  if (!drillStack.CanGoBack) return;
  var back = drillStack.Back(DrillEntityAlive);
  if (back.DroppedStaleEntity) Log(back.Notice);
  SelectRoute(back.Target.Route, false);
  // #813: Back restores the selection the crumb names, not just the screen.
  if (back.Target.EntityId.Length > 0) RevealEntity(back.Target);
 }
}



