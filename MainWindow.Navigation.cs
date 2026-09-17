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

 void BuildNavigation()
 {
  void Group(string title) => NavigationList.Items.Add(new ListBoxItem { Content=title, IsEnabled=false, Focusable=false, FontSize=10, FontWeight=FontWeights.Bold, Foreground=new SolidColorBrush(Color.FromRgb(130,161,177)), Padding=new Thickness(14,12,4,2) });
  void Page(string key,string title,string description,UIElement content,string? label=null)
  {
   var page=new TabItem{Tag=key,Header=title,Content=content};routes.Add(key,page);routeTitles[key]=title;ModuleTabs.Items.Add(page);
   var item=new ListBoxItem{Tag=key,Content=label??title,ToolTip=description};NavigationList.Items.Add(item);
   item.Selected+=(_,_)=>SelectRoute(key, !selectingRoute, title, description);
  }
  Group("ÇALIŞMA ALANI");
  Page("dashboard","Genel bakış","Ürün, XML otomasyonu ve BizimHesap bağlantısının kısa özeti.",DashboardPanel.Create(dataDirectory,key=>Navigate(key)));
  Page("products","Ürün yönetimi","Tüm ürün bilgilerini yan yana inceleyin, filtreleyin ve düzenleyin.",builtPages["Ürün havuzu"]);
  Page("xml","XML otomasyonu","XML kaynakları, alan eşleme, fiyat formülü, zamanlama ve manuel çalıştırma.",builtPages["XML yönetimi"]);
  Group("ENTEGRASYON");
  Page("bizimhesap","BizimHesap","Ürün/depo okuma ve XML ürünleri için SKU-barkod eşleştirme önizlemesi.",BizimHesapPanel.Create(dataDirectory));
  Group("YÖNETİM");
  var settings=new StackPanel{Margin=new Thickness(20)};
  settings.Children.Add(Heading("Hesaplar ve uygulama ayarları"));
  settings.Children.Add(Hint("Pazaryeri erişim bilgileri ilgili kanalın Bağlantı sekmesindedir. Bağlantı doğrulaması ürün aktarımının etkin olduğu anlamına gelmez."));
  settings.Children.Add(Button("BizimHesap bağlantı ayarları",()=>Navigate("bizimhesap")));
  settings.Children.Add(Heading("Sürüm, yedek ve taşıma"));settings.Children.Add(DataBackupPanel.Create(dataDirectory));
  settings.Children.Add(Heading("Yerel veri ve otomasyon"));settings.Children.Add(Hint("XML kaynakları ve ürün kilitleri XML yönetimi / Ürün yönetimi ekranlarından düzenlenir. Zamanlı XML yenilemesi yalnız uygulama açıkken çalışır. İşlem geçmişi pencerenin altındadır."));
  Page("settings","Ayarlar","Hesap bağlantıları, pazaryeri görselleri ve yerel çalışma bilgileri",Scroll(settings));
  NavigationSearchBox.TextChanged += (_, _) => FilterNavigationItems();
  var parity=ScreenParityAudit.Evaluate(routes.Keys); if(!parity.IsComplete) Log("Ekran paritesi BLOCKED: "+string.Join(", ",parity.MissingRoutes));
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
  // The product-pool summary ("0 sonuç · Havuzu yenile") only means something on the product page.
  ProductSummaryBar.Visibility = key == "products" ? Visibility.Visible : Visibility.Collapsed;
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



