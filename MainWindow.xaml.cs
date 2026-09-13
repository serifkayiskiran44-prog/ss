using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Xml.XPath;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;
public sealed class MappingEntry { public string Key {get;set;}=""; public string Label {get;set;}=""; public string Path {get;set;}=""; /* #824: computed by ImportMappingTable, refreshed on inspect and edit */ public string RequiredMark {get;set;}=""; public string TypeLabel {get;set;}=""; public string Sample {get;set;}=""; public string StatusLabel {get;set;}=""; public string Reason {get;set;}=""; }
public sealed record PriceChoice(string Value,string Label);
public partial class MainWindow : Window
{
 readonly CatalogStore store;
 readonly HttpClient http;
 readonly SemaphoreSlim gate=new(1,1);
 readonly SemaphoreSlim authorizationGate=new(1,1);
 readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMinutes(1)};
 readonly DispatcherTimer searchTimer=new(){Interval=TimeSpan.FromMilliseconds(300)};
 readonly DispatcherTimer globalSearchTimer=new(){Interval=TimeSpan.FromMilliseconds(300)};
 readonly GlobalSearchIndexService globalSearchIndex;
 CancellationTokenSource? globalSearchCts;
 readonly CancellationTokenSource lifetime=new();
 readonly StartupRecovery startupRecovery;
 readonly ObservableCollection<string> logs=[];
 readonly string logPath;
 readonly string? dataDirectory;
 readonly DataGrid products=new(), preview=new(){SelectionMode=DataGridSelectionMode.Extended}, mapping=new(){IsReadOnly=false}, listings=new();
 // #829: the source list is bound to a grouped view; without this the ListBox would silently select the view's current (first) item on every rebind.
 readonly ListBox sources=new(){IsSynchronizedWithCurrentItem=false}, paths=new();
 readonly TextBox search=new(){Width=300}, itemPath=new(), xmlUser=new(), shopId=new(), redirect=new(), callback=new(){Height=70,TextWrapping=TextWrapping.Wrap};
 readonly PasswordBox xmlPassword=new(), apiKey=new(), apiSecret=new(), apiToken=new(), refreshToken=new();
 readonly ComboBox decimalSeparator=new(){ItemsSource=new[]{".",","},SelectedIndex=0}, listingState=new(){ItemsSource=new[]{"active","draft","inactive","sold_out","expired"},SelectedIndex=0,Width=140};
 readonly StackPanel sourceGeneral=new(),sourceRules=new(),productEditor=new(),templateEditor=new();
 readonly TextBlock previewStatus=Hint("XML'i oku → eşleştir → önizle → seçili ürünleri havuza al."), apiStatus=Hint("Bağlantı henüz doğrulanmadı."), draftStatus=Hint("Ürün havuzundan bir ürün seç."),listingStatus=Hint(""), productChannelSummary=Hint("Ürün seçince kanal planları burada görünür."), productChannelSummaryMirror=Hint("Ürün seçince kanal planları burada görünür."), productOrderSummary=Hint("Ürün seçince yerel sipariş özeti burada görünür."), xmlSourceHealth=Hint("Kaynak seçince sağlık, ürün ve son çalışma özeti görünür.");
 readonly StackPanel priceFieldsList=new();
 readonly ObservableCollection<string> xmlPaths=new();
 readonly TextBox sampleCost=new(){Text="100",Width=130};
 readonly TextBlock calculationStatus=Hint("Alış fiyatını girip hesaplamayı test edebilirsin."),fxStatus=Hint("Kur henüz alınmadı.");
 XmlSource? source; CatalogProduct? edit; EtsyListingTemplate template=new(); EtsyCredentials credentials=new("","","",""); OAuthAttempt? attempt;
 List<MappingEntry> mappings=[]; string xml="",loadedLocation="",previewRevision="",previewMappingShapeFingerprint=""; int listingOffset,listingTotal,productOffset,productTotal,searchRevision,globalSearchRevision; string loadedListingShop="",loadedListingState="";
 public MainWindow():this(null){}
 // httpClient lets a test substitute a fake handler (e.g. to drive AuthorizedAsync's OAuth refresh path
 // without a real network call); null preserves the exact real client used before this parameter existed.
 public MainWindow(string? directory, HttpClient? httpClient = null)
 {
  http=httpClient??new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60)};
  dataDirectory=directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");startupRecovery=new StartupRecovery(dataDirectory);store=new CatalogStore(dataDirectory);new SyncStore(dataDirectory).RecoverAbandonedRunning(TimeSpan.FromHours(1));new XmlRunStore(dataDirectory).RecoverAbandonedRunning(TimeSpan.FromMinutes(10));globalSearchIndex=new GlobalSearchIndexService(dataDirectory);logPath=Path.Combine(dataDirectory,"operations.log");
  InitializeComponent();FontFamily=DesignTokens.FontFamilyBody;FontSize=DesignTokens.TextBodySize;IconStyles.ApplyIconButton(BackButton,IconRole.Inline);BackButton.ToolTip = KeyboardShortcuts.Hint("Geri", "back"); GlobalSearchBox.ToolTip = KeyboardShortcuts.Hint("Genel arama", "global-search"); ShortcutsButton.ToolTip = KeyboardShortcuts.Hint("Klavye kısayolları", "shortcut-reference"); IconStyles.ApplyIconButton(ShortcutsButton, IconRole.Inline);RefreshBackButton();uiPreferences=new UiPreferenceStore(directory);Language=System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
  PreviewKeyDown += MainWindow_PreviewKeyDown;
  GlobalSearchBox.KeyDown += GlobalSearchBox_KeyDown;
  GlobalSearchBox.TextChanged += GlobalSearchBox_TextChanged;
  LogList.ItemsSource=logs;
  try{if(File.Exists(logPath))foreach(var line in File.ReadLines(logPath).TakeLast(100))logs.Insert(0,line);}catch(IOException){}
  BuildProducts();BuildSources();BuildApi();BuildListings();BuildTemplate();BuildNavigation();
  if (directory is null && new OnboardingStore(dataDirectory).ShouldPrompt()) Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) OnboardingPanel.ShowWizard(this, dataDirectory, key => Navigate(key)); }));
  try{template=TemplateStore.Load(directory);templateEditor.DataContext=template;var saved=CredentialStore.Load(dataDirectory);if(saved!=null)SetCredentials(saved);}catch(Exception e){Log(Safe(e), NotificationSeverity.Error);}
  RefreshSources();RefreshProducts();timer.Tick+=async(_,_)=>await ScheduledAsync();timer.Start();searchTimer.Tick+=async(_,_)=>{searchTimer.Stop();await SearchProductsAsync();};globalSearchTimer.Tick+=SearchTimer_Tick;_ = WarmGlobalSearchAsync();
  Log("Global masaüstü hazır. XML otomasyonu yalnız program açıkken çalışır.");
  if (startupRecovery.State.UncleanExit) Log("Önceki çalışma normal kapanmamış; yerel recovery kontrolleri uygulandı.");
 }
 void GlobalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
 {
  globalSearchTimer.Stop();
  if (GlobalSearchBox.Text.Trim().Length >= 2) globalSearchTimer.Start();
 }
 static TextBlock Hint(string text)=>TextStyles.Apply(new TextBlock{Text=text,Margin=Spacing.HintBlock},TextRole.Hint);
 static TextBlock Heading(string text)=>TextStyles.Apply(new TextBlock{Text=text,Margin=Spacing.TitleBlock},TextRole.SectionTitle);
 Button Button(string text,Action action){var b=new Button{Content=text};b.Click+=(_,_)=>{try{action();}catch(Exception e){Log(Safe(e), NotificationSeverity.Error);}};return b;}
 Button AsyncButton(string text,Func<Task> action)=>Button(text,()=>_=RunAsync(action));
 static void Label(Panel panel,string text,UIElement control){panel.Children.Add(new TextBlock{Text=text,Margin=new Thickness(4,7,4,0)});panel.Children.Add(control);}
 // #819: every product/source text row is the shared form row -- label above, "zorunlu" spoken for the fields the
 // store refuses without (ProductValidation's blocking rules), help under the input, a validation slot under that.
 static bool RequiredFor(string property)=>property is "Name" or "Currency";
 static string HelpFor(string property)=>property switch{
  "Name"=>"Mağazada görünen ad; en fazla 200 karakter.",
  "Sku"=>"SKU veya barkoddan en az biri zorunlu.",
  "Barcode"=>"SKU yoksa barkod zorunlu.",
  "Currency"=>"Üç harfli kod, örneğin TRY.",
  "Price"=>"Satış fiyatı; negatif olamaz.",
  "Cost"=>"Alış fiyatı; negatif olamaz.",
  "Stock"=>"Negatif olamaz.",
  _=>""};
 // #820: every row the form builds is registered so a save can write a message under the input and focus it.
 readonly Dictionary<(Panel Form,string Property),FormFieldControl> formRows=new();
 TextBox Field(Panel panel,string label,string property,int height=0)
 {var box=new TextBox();if(height>0){box.Height=height;box.AcceptsReturn=true;box.TextWrapping=TextWrapping.Wrap;box.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;}box.SetBinding(TextBox.TextProperty,new Binding(property){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,ValidatesOnExceptions=true});var row=FormField.Build(new FormFieldSpec(label,RequiredFor(property),HelpFor(property)),box);formRows[(panel,property)]=row;panel.Children.Add(row.Root);return box;}
 static void Flag(Panel panel,string label,string property){var c=new CheckBox{Content=label};c.SetBinding(CheckBox.IsCheckedProperty,new Binding(property){Mode=BindingMode.TwoWay});panel.Children.Add(c);}
 static ScrollViewer Scroll(UIElement content)=>new(){Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(10)};
 static DataGridTextColumn ReadOnlyColumn(string header,string path,double width){var column=GridColumns.Text(header,path,width);column.IsReadOnly=true;return column;}
 static void Column(DataGrid grid,string label,string property,double width=120){var format=property is "Price" or "Cost" or "FormulaPriceTry"?"N2":property=="AppliedTryRate"?"N4":null;grid.Columns.Add(GridColumns.Text(label,property,width,format:format));}
 void Tab(string name,UIElement content)=>builtPages.Add(name,content);
 static Grid Split(UIElement left,UIElement right,double rightWidth)
 {var g=new Grid{Margin=new Thickness(10)};g.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});g.ColumnDefinitions.Add(new(){Width=new GridLength(rightWidth==350?330:1,rightWidth==350?GridUnitType.Pixel:GridUnitType.Star)});g.Children.Add(left);Grid.SetColumn(right,1);g.Children.Add(right);return g;}
 static DockPanel Dock(UIElement top,UIElement body){var d=new DockPanel();DockPanel.SetDock(top,System.Windows.Controls.Dock.Top);d.Children.Add(top);d.Children.Add(body);return d;}
 // Two stacked headers above a filling body: the toolbar, then the sticky selection summary bar (#795).
 static DockPanel Dock(UIElement top,UIElement second,UIElement body){var d=new DockPanel();DockPanel.SetDock(top,System.Windows.Controls.Dock.Top);d.Children.Add(top);DockPanel.SetDock(second,System.Windows.Controls.Dock.Top);d.Children.Add(second);d.Children.Add(body);return d;}
 void BuildProducts()
 {
  var bar=new WrapPanel();search.ToolTip="SKU, barkod, ürün adı, marka veya kategori";bar.Children.Add(search);bar.Children.Add(Button("Önceki 200",()=>{productOffset=Math.Max(0,productOffset-200);RefreshProducts();}));bar.Children.Add(Button("Sonraki 200",()=>{if(productOffset+200<productTotal)productOffset+=200;RefreshProducts();}));bar.Children.Add(Button("Etsy şablonunu kontrol et",CheckDraft));search.TextChanged+=(_,_)=>{productOffset=0;searchTimer.Stop();searchTimer.Start();};
  AddProductFilters(bar);
  // The state column shows the #794 semantic badge (glyph + word), not just Aktif/Pasif, so the row's condition
  // is readable without colour; the row border and tooltip below come from the same classification.
  products.Columns.Add(new DataGridTextColumn{Header="Durum",Binding=new Binding(".") {Converter=new ProductRowStateBadgeConverter()},Width=95});
  foreach(var x in new[]{("Stok kodu / SKU","Sku",135d),("Ürün","Name",200d),("Alış fiyatı","Cost",90d),("Alış döviz","CostCurrency",65d),("Satış fiyatı","Price",90d),("Satış döviz","Currency",65d),("KDV %","VatRate",60d),("Stok","Stock",72d),("Formül TL","FormulaPriceTry",95d),("1 döviz/TL","AppliedTryRate",95d),("Barkod","Barcode",140d),("GTIN","Gtin",140d),("Marka","Brand",120d),("Kategori","Category",150d),("Açıklama","Description",240d),("Etsy ilan ID","EtsyListingId",110d),("XML kaynağı","SourceId",125d),("Son güncelleme","UpdatedUtc",155d)})Column(products,x.Item1,x.Item2,x.Item3);
  Column(products,"Son veri kaynağı","SourceKind",110);Column(products,"Fiyat kaynağı","PriceSource",100);Column(products,"Stok kaynağı","StockSource",100);Column(products,"Medya kaynağı","MediaSource",100);
  products.SelectionMode=DataGridSelectionMode.Extended;products.EnableRowVirtualization=true;products.EnableColumnVirtualization=false;VirtualizingPanel.SetIsVirtualizing(products,true);VirtualizingPanel.SetVirtualizationMode(products,VirtualizationMode.Recycling);products.SelectionChanged+=ProductSelectionChanged;products.SelectionChanged+=(_,_)=>UpdateProductSelectionSummary();bar.Children.Add(BuildProductDensitySelector());InitializeProductRowStates();InitializeProductLayout();
  bar.Children.Add(Button("Aktife al",()=>SetProductActive(true)));bar.Children.Add(Button("Pasife al",()=>SetProductActive(false)));bar.Children.Add(Button("Ürünü sil",DeleteSelectedProduct));bar.Children.Add(Button("İçerik değişikliklerini önizle",OpenContentPreview));bar.Children.Add(AsyncButton("Ürünleri XML'e aktar",ExportProductsToXmlAsync));productEditor.Children.Add(Heading("Ürün kartı"));productEditor.Children.Add(productValidationPanel);productEditor.Children.Add(productDirtyBar);
  // Bindings write back on focus loss, so the indicator is refreshed when focus leaves an edited field (#802).
  productEditor.AddHandler(LostKeyboardFocusEvent,new System.Windows.Input.KeyboardFocusChangedEventHandler((_,_)=>RefreshProductDirtyIndicator()),true);
  productEditor.Children.Add(productPriceSummaryPanel);productProvenanceExpander.Content=productProvenanceBody;productEditor.Children.Add(productProvenanceExpander);Field(productEditor,"Alış fiyatı","Cost").IsReadOnly=true;Field(productEditor,"Alış para birimi","CostCurrency").IsReadOnly=true;Field(productEditor,"Satış fiyatı","Price");Field(productEditor,"Satış para birimi","Currency").IsReadOnly=true;Field(productEditor,"KDV oranı (%)","VatRate");Field(productEditor,"Desi (kargo)","Desi");productEditor.Children.Add(Hint("XML güncellemesinde korunmasını istediğin alanı kilitle."));
  BuildPriceFieldsPanel(productEditor);
  Field(productEditor,"Ürün stok kodu / SKU","Sku").IsReadOnly=true;Field(productEditor,"Barkod","Barcode").IsReadOnly=true;Field(productEditor,"GTIN","Gtin").IsReadOnly=true;Field(productEditor,"Marka","Brand");Field(productEditor,"Kategori","Category");Field(productEditor,"Başlık","Name");Flag(productEditor,"Başlığı kilitle","LockName");Field(productEditor,"Açıklama","Description",90);Flag(productEditor,"Açıklamayı kilitle","LockDescription");Flag(productEditor,"Fiyatı kilitle","LockPrice");productEditor.Children.Add(productStockSummaryPanel);Field(productEditor,"Stok","Stock");Flag(productEditor,"Stoğu kilitle","LockStock");Field(productEditor,"Görsel URL'leri","ImageUrls",65);Flag(productEditor,"Görselleri kilitle","LockImages");
  productEditor.Children.Add(Heading("Kanal durumları / yerel planlar"));productEditor.Children.Add(productChannelSummary);productEditor.Children.Add(Hint("Buradaki planlar yerel eşleme ve son durum bilgisidir; canlı marketplace yazımı yalnız açık önizleme/onay akışlarından yapılır."));
  productEditor.Children.Add(Heading("Operasyon bilgileri"));
  Field(productEditor,"Üretici parça kodu / MPN","Mpn").MaxLength=128;
  Field(productEditor,"Faturada kullanılacak ürün adı","InvoiceName").MaxLength=300;
  Field(productEditor,"Alt başlık","Subtitle").MaxLength=300;
  Field(productEditor,"Raf / konum","Shelf").MaxLength=100;
  var expires=new DatePicker();expires.SetBinding(DatePicker.SelectedDateProperty,new Binding("ExpiresOn"){Mode=BindingMode.TwoWay,ValidatesOnExceptions=true});Label(productEditor,"Son kullanma tarihi (isteğe bağlı)",expires);
  productEditor.Children.Add(Button("Tarihi temizle",()=>expires.SelectedDate=null));
  productEditor.Children.Add(Hint("Operasyon bilgileri XML yenilemesinde korunur. Fatura adı yerel kayıttır; fatura entegrasyonuna otomatik gönderilmez."));
  productEditor.Children.Add(Button("Ürünü ve kilitleri kaydet",()=>{SaveProductEdit();RefreshProducts();Log("Ürün ve alan kilitleri kaydedildi.");}));CommandState.Apply(productEditor,DisabledReason.Selection("Önce ürün seçin."));
  Tab("Ürün havuzu",Split(Dock(bar,BuildProductSelectionBar(),BuildProductInspectHost(products)),BuildProductWorkspace(),350));
 }
 // #801: every workspace tab carries its catalogue key in Tag, so a deep link ("products#media"), the tab strip
 // and the restore-on-return path all address the same section by the same name.
 TabControl? productWorkspaceTabs;
 readonly ProductWorkspaceSectionMemory productSectionMemory = new();
 internal void SelectProductSection(string? sectionKey)
 {
  if (productWorkspaceTabs is null) return;
  var key = productSectionMemory.Resolve(sectionKey);
  var tab = productWorkspaceTabs.Items.OfType<TabItem>().FirstOrDefault(t => (t.Tag as string) == key);
  if (tab is null) return;
  productWorkspaceTabs.SelectedItem = tab;
  // Focus restore: the section the operator returns to is the one that takes the keyboard, not the first tab.
  tab.Dispatcher.BeginInvoke(new Action(() => (tab.Content as UIElement)?.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First))), System.Windows.Threading.DispatcherPriority.Input);
 }
 TabControl BuildProductWorkspace()
 {
  var tabs=new TabControl();productWorkspaceTabs=tabs;
  tabs.SelectionChanged+=(_,e)=>{if(e.OriginalSource==tabs&&tabs.SelectedItem is TabItem selected)productSectionMemory.Remember(selected.Tag as string);};
  tabs.SetBinding(FrameworkElement.DataContextProperty,new Binding("DataContext"){Source=productEditor,Mode=BindingMode.OneWay});
  tabs.Items.Add(new TabItem{Header="Kimlik",Tag="identity",Content=Scroll(productEditor)});
  tabs.Items.Add(new TabItem{Header="İçerik",Tag="content",Content=Scroll(ProductReadOnlyFields(("Başlık","Name"),("Açıklama","Description"),("Marka","Brand"),("Kategori","Category")))});
  tabs.Items.Add(new TabItem{Header="Fiyat / stok",Tag="price-stock",Content=Scroll(ProductReadOnlyFields(("Satış fiyatı","Price"),("Satış para birimi","Currency"),("Alış fiyatı","Cost"),("Alış para birimi","CostCurrency"),("KDV oranı (%)","VatRate"),("Stok","Stock"),("Fiyat kaynağı","PriceSource"),("Stok kaynağı","StockSource")))});
  tabs.Items.Add(new TabItem{Header="Görseller",Tag="media",Content=Scroll(ProductReadOnlyFields(("Görsel URL'leri","ImageUrls"),("Medya kaynağı","MediaSource")))});
  tabs.Items.Add(new TabItem{Header="Kanallar",Tag="channel",Content=Scroll(new StackPanel{Children={Heading("Kanal ve mağaza bağları"),productChannelSummaryMirror,Hint("Eşleştirme ve ilan durumları yerel kanal planlarından okunur; canlı write bu sekmeden başlatılmaz.")}})});
  tabs.Items.Add(new TabItem{Header="Geçmiş",Tag="audit",Content=Scroll(new StackPanel{Children={Heading("Değişiklik geçmişi"),productAuditTimelinePanel,Heading("Ürün sipariş raporu"),productOrderSummary,Hint("Özet yalnız yerel sipariş kayıtlarından üretilir; bu sekme canlı marketplace çağrısı yapmaz."),ProductReadOnlyFields(("Kaynak kimliği","SourceId"),("Kaynak türü","SourceKind"),("Son güncelleme","UpdatedUtc"))}})});
  return tabs;
 }
 static StackPanel ProductReadOnlyFields(params (string Label,string Property)[] fields)
 {
  var panel=new StackPanel(); foreach(var field in fields){panel.Children.Add(new TextBlock{Text=field.Label,Margin=new Thickness(4,7,4,0)});var box=new TextBox{IsReadOnly=true,MinHeight=28,TextWrapping=TextWrapping.Wrap};box.SetBinding(TextBox.TextProperty,new Binding(field.Property));panel.Children.Add(box);} return panel;
 }
 void BuildPriceFieldsPanel(Panel parent)
 {
  parent.Children.Add(Heading("Bağımsız fiyat alanları"));
  parent.Children.Add(Hint("Kanal bazında formül yerine kullanılabilecek sabit fiyatlar (örn. \"Etsy sabit\"). Fiyat kuralında PriceFieldName ile seçilir."));
  parent.Children.Add(priceFieldsList);
  var name=new TextBox{Width=140,ToolTip="Alan adı"};var value=new TextBox{Width=80,ToolTip="Değer"};var currency=new TextBox{Width=50,Text="TRY",ToolTip="Para birimi"};
  var addRow=new WrapPanel();addRow.Children.Add(name);addRow.Children.Add(value);addRow.Children.Add(currency);
  addRow.Children.Add(Button("Fiyat alanı ekle",()=>{
   if(edit==null)throw new InvalidOperationException("Önce ürün seç.");
   if(!decimal.TryParse(value.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out var v))throw new InvalidOperationException("Değer sayısal olmalı.");
   store.AddPriceField(edit.Id,new(name.Text.Trim(),v,currency.Text.Trim().ToUpperInvariant()));
   edit=store.FindProduct(edit.Id);productEditBaseline=JsonSerializer.Serialize(edit);name.Clear();value.Clear();RefreshPriceFieldsPanel();
  }));
  parent.Children.Add(addRow);
 }
 void RefreshPriceFieldsPanel()
 {
  priceFieldsList.Children.Clear();
  if(edit==null)return;
  foreach(var field in edit.PriceFields)
  {
   var row=new WrapPanel();row.Children.Add(new TextBlock{Text=$"{field.Name}: {field.Value} {field.Currency}",Margin=new Thickness(2,4,8,4),VerticalAlignment=VerticalAlignment.Center});
   var capturedName=field.Name;
   row.Children.Add(Button("Sil",()=>{store.RemovePriceField(edit!.Id,capturedName);edit=store.FindProduct(edit.Id);productEditBaseline=JsonSerializer.Serialize(edit);RefreshPriceFieldsPanel();}));
   priceFieldsList.Children.Add(row);
  }
 }
 void ShowProductChannelStatus(CatalogProduct? product){if(product==null){productChannelSummary.Text="Ürün seçince kanal planları burada görünür.";productChannelSummaryMirror.Text=productChannelSummary.Text;productOrderSummary.Text="Ürün seçince yerel sipariş özeti burada görünür.";return;}var rows=MarketplaceProductPanelModel.Build(product.Id,dataDirectory).Select(x=>$"{x.Channel} / {x.ShopId}: {x.Status} · {x.Readiness}"+(string.IsNullOrWhiteSpace(x.MappingId)?"":$" · eşleme {x.MappingId}"));var text=string.Join("\n",rows);productChannelSummary.Text=text;productChannelSummaryMirror.Text=text;var report=ProductOrderReport.Build(product.Sku,new OrdersStore(dataDirectory).ReadAll());productOrderSummary.Text=$"Sipariş: {report.OrderCount} · Adet: {report.Quantity} · Son sipariş: {report.LatestOrderId} · {report.LatestChannelShop}";}
 void SetProductActive(bool active){var selected=products.SelectedItems.OfType<CatalogProduct>().ToList();if(selected.Count==0&&edit!=null)selected.Add(edit);if(selected.Count==0)throw new InvalidOperationException("Önce ürün seç.");ValidBindings(productEditor);
  // #821: deactivating many products is high-impact and local; the operator types the count, re-checked at the click.
  if(!active&&DestructiveConfirmation.RequiresTyping(new DestructiveIntent("Pasife al","seçili ürünler",selected.Count))){var intent=new DestructiveIntent("Pasife al","seçili ürünler",selected.Count);if(!DestructiveConfirmDialog.Show(this,intent,()=>{var now=products.SelectedItems.OfType<CatalogProduct>().Count();return now==0&&edit!=null?1:now;}))return;}foreach(var row in selected){var copy=Clone(row);copy.Active=active;store.SaveProduct(copy);}RefreshProducts();Log($"{selected.Count} ürün yerel havuzda {(active?"aktif":"pasif")} yapıldı. Canlı ilan durumu değiştirilmedi.");}
 void DeleteSelectedProduct(){if(edit==null)throw new InvalidOperationException("Önce ürün seç.");if(!DialogShell.Confirm(this,"Ürünü sil",edit.Name+"\n\nYerel havuzdan silinsin mi? XML içinde varsa sonraki alımda yeniden gelir. Etsy ilanı silinmez.","Sil"))return;store.DeleteProduct(edit);RefreshProducts();Log("Ürün yerel havuzdan silindi.");}
 async Task ExportProductsToXmlAsync(){var dialog=new SaveFileDialog{Filter="XML (*.xml)|*.xml",FileName="urunler.xml"};if(dialog.ShowDialog(this)!=true)return;await ExportProductsToXmlFileAsync(dialog.FileName);}
 // Split from ExportProductsToXmlAsync so tests can drive the actual export without the real SaveFileDialog.
 async Task ExportProductsToXmlFileAsync(string path)
 {
  var started=DateTime.UtcNow;var q=search.Text.Trim();var filter=productFilter;
  var first=await Task.Run(()=>store.Search(q,0,1000,filter));
  var all=new List<CatalogProduct>(first.Items);
  for(var offset=1000;offset<first.Total;offset+=1000)all.AddRange((await Task.Run(()=>store.Search(q,offset,1000,filter))).Items);
  if(all.Count==0)throw new InvalidOperationException("Aktarılacak ürün yok.");
  var xml=await XmlCatalogExporter.ExportAsync(all,XmlCatalogExporter.StandardTemplate);
  File.WriteAllText(path,xml);
  new ReportRunStore(dataDirectory).Record("products-xml",started,ReportRunState.Succeeded,all.Count);
  Log($"{all.Count} ürün XML olarak dışa aktarıldı: {path}");
 }

 // #822: the import workflow stepper -- six stages derived from the flow's real state, never a fake percentage.
 readonly StackPanel importStepper=new(){Orientation=Orientation.Horizontal,Margin=new Thickness(2,2,2,8)};
 bool importRunning,importCancelled,importFailed;
 ImportFlowState CurrentImportFlowState(){
  var s=source;var loaded=s!=null&&xml!=""&&loadedLocation==s.Location;var mappingReady=false;var problem="";var fresh=false;
  if(loaded){try{var snapshot=Clone(s!);var mappingSnapshot=XmlCatalog.MappingSnapshot(xml,snapshot);XmlCatalog.EnsureMappingReady(snapshot,mappingSnapshot,scheduled:false);mappingReady=true;fresh=previewRevision!=""&&previewRevision==XmlPreviewFingerprint.Create(xml,JsonSerializer.Serialize(snapshot))&&previewMappingShapeFingerprint==mappingSnapshot.Fingerprint;}catch(Exception e){problem=Safe(e);}}
  return new(){HasSource=s!=null,SourceSaved=s!=null&&store.Sources().Any(x=>x.Id==s.Id),XmlLoaded=loaded,MappingReady=mappingReady,MappingProblem=problem,PreviewExists=previewRevision!="",PreviewFresh=fresh,ApplyRunning=importRunning,ApplyCancelled=importCancelled,ResultStatus=importRunning?"":(s?.LastStatus??""),ResultFailed=importFailed};}
 void RefreshImportStepper(){
  importStepper.Children.Clear();
  IReadOnlyList<ImportStep> steps;try{var state=CurrentImportFlowState();lastFlowState=state;steps=ImportStepper.Compute(state);}catch(Exception e){lastFlowState=null;Log("Aktarım adımları hesaplanamadı: "+Safe(e));return;}
  foreach(var step in steps){
   var (glyph,word)=step.Status switch{ImportStageStatus.Done=>("✔","tamam"),ImportStageStatus.Current=>("●","sırada"),ImportStageStatus.Blocked=>("✖","engelli"),ImportStageStatus.Stale=>("⚠","geçersiz"),ImportStageStatus.Running=>("⟳","sürüyor"),_=>("○","bekliyor")};
   var chip=new Button{Content=$"{glyph} {step.Label}",Margin=new Thickness(0,0,6,0),Padding=new Thickness(8,3,8,3),ToolTip=step.Reason.Length>0?step.Reason:$"{step.Label}: {word}",Tag=step.Stage,Focusable=true};CommandState.Apply(chip,step.CanJump?null:DisabledReason.StoreState(step.Reason.Length>0?step.Reason:$"{step.Label}: {word}"));
   System.Windows.Automation.AutomationProperties.SetName(chip,$"{step.Label}: {word}"+(step.Reason.Length>0?". "+step.Reason:""));IconStyles.ApplyIconButton(chip,IconRole.Status);
   chip.Click+=(_,_)=>JumpToImportStage((ImportStage)chip.Tag);
   importStepper.Children.Add(chip);}
  RefreshPreviewToolbar();}
 void JumpToImportStage(ImportStage stage){
  switch(stage){case ImportStage.Source:sources.Focus();break;case ImportStage.Mapping:mapping.Focus();break;case ImportStage.Preview:preview.Focus();break;default:previewStatus.BringIntoView();break;}}

 // #823: the import start screen -- rows from the source catalog (support by the reader's real rule, last-used
 // first, health from the source's own fields, locations masked), and a type picker that offers only what this
 // build can import.
 Dictionary<string,ImportSourceRow> sourceRows=new();
 readonly ComboBox sourceTypePicker=new(){DisplayMemberPath="Label",Width=190,ToolTip="Yeni kaynak türü"};
 sealed class SourceRowConverter:System.Windows.Data.IValueConverter{readonly Func<XmlSource,string> text;public SourceRowConverter(Func<XmlSource,string> text)=>this.text=text;public object Convert(object value,Type t,object p,System.Globalization.CultureInfo c)=>value is XmlSource s?text(s):"";public object ConvertBack(object value,Type t,object p,System.Globalization.CultureInfo c)=>throw new NotSupportedException();}
 void RefreshSourceTypePicker(){var current=(sourceTypePicker.SelectedItem as ImportSourceType)?.Kind;var types=ImportSourceCatalog.SupportedTypes(key=>routes.ContainsKey(key));sourceTypePicker.ItemsSource=types;sourceTypePicker.SelectedItem=types.FirstOrDefault(t=>t.Kind==current)??types[0];}
 void AddSourceOfSelectedType(){RefreshSourceTypePicker();var type=(ImportSourceType)sourceTypePicker.SelectedItem;if(type.Kind==ImportSourceKind.Excel){Navigate("excel");return;}SetSource(new(){Name=type.Kind==ImportSourceKind.XmlFile?"Yeni dosya kaynağı":"Yeni tedarikçi",PriceMode="Formula",Formula=PriceFormula.Example,AutoFx=true});Log(type.Hint);}

 // #824: the mapping table made readable -- required / type / sample / status per row, a summary above, and the
 // first problem one click away. Samples come from the first scanned item and are masked before they are shown.
 System.Xml.Linq.XElement? mappingSampleItem; readonly TextBlock mappingSummary=new(){TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.SemiBold,Margin=new Thickness(3,4,3,2)}; readonly Button mappingFirstProblem=new(){Content="İlk soruna git",Margin=new Thickness(3,0,3,6),Padding=Spacing.Chip,HorizontalAlignment=HorizontalAlignment.Left,Visibility=Visibility.Collapsed};
 string? MappingSampleFor(string path){var item=mappingSampleItem;if(item==null)return null;System.Xml.Linq.XElement? current=item;var segments=path.Split('/',StringSplitOptions.RemoveEmptyEntries);for(var i=0;i<segments.Length;i++){var seg=segments[i];if(seg.StartsWith('@'))return i==segments.Length-1?current?.Attribute(seg[1..])?.Value:null;current=current?.Element(seg);if(current==null)return null;}return current?.Value;}
 void RefreshMappingTable(){
  var known=xmlPaths.Where(x=>!string.IsNullOrWhiteSpace(x)).ToList();
  var view=ImportMappingTable.Compose(mappings.Select(m=>new MappingRowInput(m.Key,m.Label,m.Path)).ToList(),known,MappingSampleFor);
  foreach(var (entry,row) in mappings.Zip(view.Rows)){entry.RequiredMark=ImportMappingTable.RequiredLabel(row.Key);entry.TypeLabel=row.TypeLabel;entry.Sample=row.Sample;entry.StatusLabel=row.StatusLabel;entry.Reason=row.Reason;}
  mapping.Items.Refresh();mappingSummary.Text=view.Summary;mappingSummary.Foreground=SeverityStyle.AccentBrush(view.HasBlocking?SeverityLevel.Blocking:SeverityLevel.Success,SeverityStyle.IsHighContrast);
  mappingFirstProblem.Visibility=view.FirstProblemKey==null?Visibility.Collapsed:Visibility.Visible;mappingFirstProblem.Tag=view.FirstProblemKey;
  System.Windows.Automation.AutomationProperties.SetName(mappingSummary,"Eşleme özeti: "+view.Summary);}
 void FocusFirstMappingProblem(){if(mappingFirstProblem.Tag is not string key)return;var entry=mappings.FirstOrDefault(m=>m.Key==key);if(entry==null)return;mapping.SelectedItem=entry;mapping.ScrollIntoView(entry);mapping.Focus();}

 // #825: filters over the preview's validation results. Rows stay CatalogProduct (the import reads the grid's
 // selection), the flags live beside them, and the counts always describe what the grid shows.
 List<CatalogProduct> previewRows=new(); IReadOnlyList<ImportValidationRow> previewValidation=Array.Empty<ImportValidationRow>(); readonly Dictionary<CatalogProduct,ImportValidationRow> previewFlags=new(ReferenceEqualityComparer.Instance);
 readonly ComboBox previewSeverity=new(){Width=110,ToolTip="Önem"},previewField=new(){Width=150,ToolTip="Alan"},previewReason=new(){Width=170,ToolTip="Neden kodu"}; readonly CheckBox previewChangedOnly=new(){Content="Yalnız değişenler",VerticalAlignment=VerticalAlignment.Center}; readonly TextBlock previewCounts=new(){VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,0,0),FontWeight=FontWeights.SemiBold}; bool applyingPreviewFilter;
 sealed class PreviewFlagConverter:System.Windows.Data.IValueConverter{readonly Func<CatalogProduct,string> text;public PreviewFlagConverter(Func<CatalogProduct,string> text)=>this.text=text;public object Convert(object v,Type t,object p,System.Globalization.CultureInfo c)=>v is CatalogProduct row?text(row):"";public object ConvertBack(object v,Type t,object p,System.Globalization.CultureInfo c)=>throw new NotSupportedException();}
 void EvaluatePreviewRows(List<CatalogProduct> rows){
  previewRows=rows;previewEvaluatedUtc=DateTime.UtcNow;previewSourceId=source?.Id??"";var pool=store.Products();var bySku=pool.Where(x=>!string.IsNullOrWhiteSpace(x.Sku)).GroupBy(x=>x.Sku).ToDictionary(g=>g.Key,g=>g.First());var byBarcode=pool.Where(x=>!string.IsNullOrWhiteSpace(x.Barcode)).GroupBy(x=>x.Barcode).ToDictionary(g=>g.Key,g=>g.First());
  previewValidation=ImportValidationFilter.Evaluate(rows,r=>(!string.IsNullOrWhiteSpace(r.Sku)&&bySku.TryGetValue(r.Sku,out var e))?e:(!string.IsNullOrWhiteSpace(r.Barcode)&&byBarcode.TryGetValue(r.Barcode,out var b))?b:null);
  previewFlags.Clear();for(var i=0;i<rows.Count;i++)previewFlags[rows[i]]=previewValidation[i];
  applyingPreviewFilter=true;try{var result=ImportValidationFilter.Apply(previewValidation,new());
   previewSeverity.ItemsSource=new[]{"Tümü","Engel","Uyarı","Temiz"};previewSeverity.SelectedIndex=0;
   previewField.ItemsSource=new[]{"Tüm alanlar"}.Concat(result.FieldOptions).ToArray();previewField.SelectedIndex=0;
   previewReason.ItemsSource=new[]{"Tüm nedenler"}.Concat(result.ReasonCodeOptions).ToArray();previewReason.SelectedIndex=0;previewChangedOnly.IsChecked=false;}finally{applyingPreviewFilter=false;}
  ApplyPreviewFilter();}
 ImportValidationFilterState CurrentPreviewFilter(){SeverityLevel? severity=previewSeverity.SelectedIndex switch{1=>SeverityLevel.Blocking,2=>SeverityLevel.Warning,3=>SeverityLevel.Info,_=>null};var field=previewField.SelectedIndex>0?previewField.SelectedItem?.ToString():null;var code=previewReason.SelectedIndex>0?previewReason.SelectedItem?.ToString():null;return new(severity,field,code,previewChangedOnly.IsChecked==true);}
 void ApplyPreviewFilter(){if(applyingPreviewFilter||previewValidation.Count==0)return;var state=CurrentPreviewFilter();
  // "Temiz" is the rows with no finding: the model's Info level with no fields; map it after Apply so counts stay the model's.
  var result=ImportValidationFilter.Apply(previewValidation,state with{Severity=state.Severity==SeverityLevel.Info?null:state.Severity});
  var indices=state.Severity==SeverityLevel.Info?result.MatchingIndices.Where(i=>previewValidation[i].Fields.Count==0).ToList():result.MatchingIndices.ToList();
  preview.ItemsSource=indices.Select(i=>previewRows[i]).ToList();
  var shown=state.Severity==SeverityLevel.Info?result.Counts with{Matching=indices.Count}:result.Counts;
  previewCounts.Text=shown.Label+(indices.Count==0&&shown.Total>0?" · filtre hiçbir satırla eşleşmiyor":"");
  System.Windows.Automation.AutomationProperties.SetName(previewCounts,"Önizleme sayaçları: "+previewCounts.Text);RefreshPreviewToolbar();}

 // #827: import progress as six stages fed by real events -- a stage starts, counts against a total when one is
 // known, and ends done, failed or cancelled. An unknown total is an indeterminate bar with a count, never a
 // percentage. The reporter is created on the UI thread so background stages marshal here.
 readonly ImportProgressState importProgress=new(); readonly StackPanel importProgressPanel=new(){Margin=new Thickness(2,0,2,8)}; IProgress<ImportProgressEvent>? importProgressReporter; CancellationTokenSource? importCts;
 readonly Button importCancelButton=new(){Content="İptal",Padding=Spacing.Chip,Margin=new Thickness(0,0,6,0),Visibility=Visibility.Collapsed}; readonly Button importRetryButton=new(){Content="Yeniden dene",Padding=Spacing.Chip,Visibility=Visibility.Collapsed};
 IProgress<ImportProgressEvent> ImportReporter()=>importProgressReporter??=new Progress<ImportProgressEvent>(e=>{importProgress.Apply(e);RenderImportProgress();});
 void ReportImport(ImportProgressStage stage,ImportProgressStatus status,long done=0,long? total=null,string note="")=>ImportReporter().Report(new(stage,status,done,total,DateTime.UtcNow,note));
 CancellationToken BeginImportStage(){importCts?.Dispose();importCts=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);return importCts.Token;}
 void RenderImportProgress(){
  importProgressPanel.Children.Clear();var now=DateTime.UtcNow;var snapshot=importProgress.Snapshot(now);
  foreach(var stage in snapshot){
   var row=new DockPanel{Margin=new Thickness(0,1,0,1)};var glyph=stage.Status switch{ImportProgressStatus.Done=>"✔",ImportProgressStatus.Running=>"⟳",ImportProgressStatus.Failed=>"✖",ImportProgressStatus.Cancelled=>"⏹",_=>"○"};
   var label=new TextBlock{Text=$"{glyph} {stage.Label}",Width=150,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(label,System.Windows.Controls.Dock.Left);row.Children.Add(label);
   var counter=new TextBlock{Text=(stage.Counter.Length>0?stage.Counter+" · ":"")+ImportProgressState.StatusWord(stage.Status)+(stage.Elapsed>TimeSpan.Zero?$" · {stage.Elapsed.TotalSeconds:0.#} sn":"")+(stage.Note.Length>0?" · "+stage.Note:""),Width=260,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,FontSize=DesignTokens.TextCaptionSize};DockPanel.SetDock(counter,System.Windows.Controls.Dock.Right);row.Children.Add(counter);
   var bar=new ProgressBar{Height=10,Margin=new Thickness(6,0,6,0),Minimum=0,Maximum=100,IsIndeterminate=stage.IsIndeterminate,Value=stage.Percent??(stage.Status==ImportProgressStatus.Done?100:0),Tag=stage.Stage};
   System.Windows.Automation.AutomationProperties.SetName(bar,$"{stage.Label}: {ImportProgressState.StatusWord(stage.Status)}"+(stage.Percent is {} pc?$", yüzde {pc:0}":stage.Counter.Length>0?", "+stage.Counter:""));
   row.Children.Add(bar);importProgressPanel.Children.Add(row);}
  importCancelButton.Visibility=importProgress.Running==null?Visibility.Collapsed:Visibility.Visible;importRetryButton.Visibility=importProgress.Failed==null?Visibility.Collapsed:Visibility.Visible;}
 async void RetryImportStage(){var stage=importProgress.Retry();RenderImportProgress();if(stage==null)return;try{switch(stage.Value){case ImportProgressStage.Download:case ImportProgressStage.Read:await InspectAsync();break;case ImportProgressStage.Apply:await ImportAsync();break;default:await PreviewAsync();break;}}catch(Exception e){Log(Safe(e),NotificationSeverity.Error);}}

 // #828: the completion summary, composed only from the import's terminal result (the store's summary, the
 // error, or the cancellation) and rendered on the page so the person sees what happened before going anywhere.
 readonly Border importCompletionPanel=new(){Visibility=Visibility.Collapsed,Padding=new Thickness(10,8,10,8),Margin=new Thickness(2,0,2,8)}; ImportCompletion? lastImportCompletion; DateTime importStartedUtc;
 void CompleteImport(ImportCompletionInput input){
  lastImportCompletion=ImportCompletionSummary.Compose(input,key=>routes.ContainsKey(key));RenderImportCompletion(lastImportCompletion);
  var c=lastImportCompletion;var severity=c.Level switch{SeverityLevel.Success=>NotificationSeverity.Success,SeverityLevel.Warning=>NotificationSeverity.Warning,SeverityLevel.Blocking=>NotificationSeverity.Error,_=>NotificationSeverity.Info};
  var go=c.NextActions.FirstOrDefault(a=>a.Route.Length>0);Notify(new(severity,c.Headline+" · "+c.CountsLine,go?.Label??"",go?.Route??""));}
 void RenderImportCompletion(ImportCompletion c){
  var style=SeverityStyle.For(c.Level,SeverityStyle.IsHighContrast);var accent=SeverityStyle.AccentBrush(c.Level,SeverityStyle.IsHighContrast);
  var body=new StackPanel();
  body.Children.Add(new TextBlock{Text=$"{style.Glyph} {c.Headline}",FontWeight=FontWeights.SemiBold,Foreground=accent,TextWrapping=TextWrapping.Wrap});
  body.Children.Add(new TextBlock{Text=c.CountsLine,TextWrapping=TextWrapping.Wrap,Margin=Spacing.AboveInline});
  body.Children.Add(new TextBlock{Text=c.DurationLine+" · "+c.Revision,TextWrapping=TextWrapping.Wrap,FontSize=DesignTokens.TextCaptionSize,Opacity=0.85});
  body.Children.Add(new TextBlock{Text=c.Detail,TextWrapping=TextWrapping.Wrap,Margin=Spacing.AboveInline});
  var actions=new WrapPanel{Margin=new Thickness(0,6,0,0)};
  foreach(var action in c.NextActions){var b=new Button{Content=action.Label,Padding=Spacing.Chip,Margin=new Thickness(0,0,6,0),Tag=action.Kind};b.Click+=(_,_)=>RunImportNextAction((ImportNextActionKind)b.Tag);actions.Children.Add(b);}
  body.Children.Add(actions);
  importCompletionPanel.BorderBrush=accent;importCompletionPanel.BorderThickness=new Thickness(style.BorderWeight);importCompletionPanel.Child=body;importCompletionPanel.Visibility=Visibility.Visible;
  System.Windows.Automation.AutomationProperties.SetName(importCompletionPanel,"Aktarım sonucu: "+c.Headline+". "+c.CountsLine);}
 void RunImportNextAction(ImportNextActionKind kind){
  switch(kind){
   case ImportNextActionKind.OpenProducts:Navigate("products");break;
   case ImportNextActionKind.OpenDiagnostics:Navigate("diagnostics");break;
   case ImportNextActionKind.ShowRejected:previewSeverity.SelectedIndex=1;break;
   case ImportNextActionKind.ShowWarnings:previewSeverity.SelectedIndex=2;break;
   case ImportNextActionKind.Retry:if(importProgress.Failed!=null)RetryImportStage();else _=RunAsync(ImportAsync);break;
   case ImportNextActionKind.Reread:_=RunAsync(InspectAsync);break;}}

 // #829: the source list grouped by health band from recorded state (health check, run store, feed state), with
 // band chips and a text filter over the title and the masked address, plus a scan that refreshes every enabled
 // source's reachability. Group headers come from the view; the items stay XmlSource so selection is unchanged.
 bool changingSourceList; SourceListFilter sourceListFilter=new(); Dictionary<string,SourceListEntry> sourceEntries=new(); readonly WrapPanel sourceBandChips=new(){Margin=new Thickness(0,0,0,2)}; readonly TextBox sourceSearch=new(){Width=170,Margin=new Thickness(0,0,6,0),ToolTip="Kaynak ara: ad veya adres (gizli parametreler aranamaz)"}; readonly TextBlock sourceScanStatus=Hint(""); bool sourceScanRunning;
 sealed class SourceGroupHeaderConverter:System.Windows.Data.IValueConverter{public object Convert(object value,Type t,object p,System.Globalization.CultureInfo c)=>value is System.Windows.Data.CollectionViewGroup g?$"{g.Name} ({g.ItemCount})":"";public object ConvertBack(object v,Type t,object p,System.Globalization.CultureInfo c)=>throw new NotSupportedException();}
 void RenderSourceBandChips(IReadOnlyDictionary<SourceHealthBand,int> counts,int total){
  sourceBandChips.Children.Clear();
  void Chip(string label,int count,SourceHealthBand? band){var selected=sourceListFilter.Band==band;var b=new Button{Content=$"{label} ({count})",Padding=new Thickness(7,2,7,2),Margin=new Thickness(0,0,4,2),FontWeight=selected?FontWeights.Bold:FontWeights.Normal,Tag=band,ToolTip=selected?"Seçili filtre":"Bu gruba göre süz"};
   System.Windows.Automation.AutomationProperties.SetName(b,$"Filtre {label}: {count} kaynak"+(selected?", seçili":""));b.Click+=(_,_)=>{sourceListFilter=sourceListFilter with{Band=(SourceHealthBand?)b.Tag};RefreshSources(false);};sourceBandChips.Children.Add(b);}
  Chip("Tümü",total,null);foreach(var (band,label) in XmlSourceListGrouping.Bands)if(counts.TryGetValue(band,out var n)&&n>0)Chip(label,n,band);}
 async Task ScanSourceHealthAsync(){
  if(sourceScanRunning){Log("Sağlık taraması zaten sürüyor.");return;}sourceScanRunning=true;var targets=store.Sources().Where(x=>x.Enabled&&ImportSourceCatalog.Classify(x.Location,System.IO.File.Exists).Kind!=ImportSourceKind.Unsupported).ToList();
  sourceScanStatus.Text=$"Sağlık taraması: {targets.Count} kaynak denetleniyor…";var problems=0;var done=0;
  try{using var limit=new SemaphoreSlim(4);
   await Task.WhenAll(targets.Select(async s=>{await limit.WaitAsync(lifetime.Token);try{var health=await XmlSourceHealthChecker.CheckAsync(http,s,lifetime.Token,TimeSpan.FromSeconds(10));
    await Dispatcher.InvokeAsync(()=>{s.LastHealthCheckUtc=health.CheckedUtc;s.LastHealthState=health.State;s.LastHealthHttpStatus=health.HttpStatus;s.LastHealthLatencyMs=health.LatencyMs;s.LastHealthError=MarketplaceConnectionStore.Redact(health.ErrorMessage);store.SaveSource(s);if(health.State!="HEALTHY"||health.LatencyMs>=XmlSourceListGrouping.DegradedLatencyMs)problems++;done++;sourceScanStatus.Text=$"Sağlık taraması: {done}/{targets.Count} · {problems} sorunlu";});}
    finally{limit.Release();}}));}
  finally{sourceScanRunning=false;}
  RefreshSources(false);sourceScanStatus.Text=$"Sağlık taraması bitti: {targets.Count} kaynak · {problems} sorunlu · {DateTime.Now:HH:mm}";Log(sourceScanStatus.Text,problems>0?NotificationSeverity.Warning:NotificationSeverity.Success);}

 // #830: the source detail health panel -- one verdict and six lines composed from recorded facts (health check,
 // run store, quarantine, mapping revisions, the page's own preview and import state). Nothing here probes; the
 // "Erişimi kontrol et" action runs the checker and records, then the panel is recomposed.
 readonly Border xmlSourceHealthPanel=new(){Padding=new Thickness(8,6,8,6),Margin=new Thickness(0,4,0,4)}; readonly StackPanel xmlSourceHealthLines=new(); readonly WrapPanel xmlSourceHealthActions=new(){Margin=Spacing.AboveInline}; readonly TextBlock xmlSourceHealthHeadline=new(){FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};
 SourceHealthPanelModel? lastSourceHealth; DateTime? previewEvaluatedUtc; string previewSourceId="";
 void RenderSourceHealth(SourceHealthPanelModel m){
  var accent=SeverityStyle.AccentBrush(m.Level,SeverityStyle.IsHighContrast);var style=SeverityStyle.For(m.Level,SeverityStyle.IsHighContrast);
  xmlSourceHealthHeadline.Text=$"{style.Glyph} Kaynak sağlığı: {m.Headline}";xmlSourceHealthHeadline.Foreground=accent;xmlSourceHealthPanel.BorderBrush=accent;xmlSourceHealthPanel.BorderThickness=new Thickness(style.BorderWeight);
  xmlSourceHealthLines.Children.Clear();
  foreach(var line in m.Lines){var lineStyle=SeverityStyle.For(line.Level,SeverityStyle.IsHighContrast);var row=new DockPanel{Margin=new Thickness(0,1,0,1)};var label=new TextBlock{Text=line.Label,Width=120,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Top};DockPanel.SetDock(label,System.Windows.Controls.Dock.Left);row.Children.Add(label);
   var body=new StackPanel();body.Children.Add(new TextBlock{Text=$"{lineStyle.Glyph} {line.Value}",TextWrapping=TextWrapping.Wrap,Foreground=SeverityStyle.AccentBrush(line.Level,SeverityStyle.IsHighContrast)});if(line.Detail.Length>0)body.Children.Add(new TextBlock{Text=line.Detail,TextWrapping=TextWrapping.Wrap,FontSize=DesignTokens.TextCaptionSize,Opacity=0.85});row.Children.Add(body);
   System.Windows.Automation.AutomationProperties.SetName(row,$"{line.Label}: {line.Value}"+(line.Detail.Length>0?". "+line.Detail:""));xmlSourceHealthLines.Children.Add(row);}
  xmlSourceHealthActions.Children.Clear();
  foreach(var action in m.Actions){var b=new Button{Content=action.Label,Padding=Spacing.Chip,Margin=new Thickness(0,0,6,0),Tag=action.Kind};b.Click+=(_,_)=>RunSourceHealthAction((SourceHealthActionKind)b.Tag);xmlSourceHealthActions.Children.Add(b);}
  System.Windows.Automation.AutomationProperties.SetName(xmlSourceHealthPanel,"Kaynak sağlığı: "+m.Headline+". "+string.Join(". ",m.Lines.Select(l=>l.Label+": "+l.Value)));}
 void RunSourceHealthAction(SourceHealthActionKind kind){
  switch(kind){
   case SourceHealthActionKind.Refresh:if(source!=null)_=RefreshXmlSourceHealthAsync(source);break;
   case SourceHealthActionKind.Check:_=RunAsync(CheckSourceReachabilityAsync);break;
   case SourceHealthActionKind.Restart:if(importProgress.Failed!=null)RetryImportStage();else _=RunAsync(InspectAsync);break;
   case SourceHealthActionKind.Preview:_=RunAsync(PreviewAsync);break;}}
 async Task CheckSourceReachabilityAsync(){
  if(source==null)throw new InvalidOperationException("Önce XML kaynağı seçin.");var candidate=source;
  var health=await XmlSourceHealthChecker.CheckAsync(http,candidate,lifetime.Token,TimeSpan.FromSeconds(10));
  void Record(XmlSource s){s.LastHealthCheckUtc=health.CheckedUtc;s.LastHealthState=health.State;s.LastHealthHttpStatus=health.HttpStatus;s.LastHealthLatencyMs=health.LatencyMs;s.LastHealthError=MarketplaceConnectionStore.Redact(health.ErrorMessage);}
  Record(candidate);var persisted=store.Sources().FirstOrDefault(x=>x.Id==candidate.Id);if(persisted!=null){Record(persisted);store.SaveSource(persisted);}
  RefreshSources(false);await RefreshXmlSourceHealthAsync(candidate);
  Log($"Erişim kontrolü {candidate.Name}: {XmlSourceListGrouping.HealthWord(health.State)}"+(health.HttpStatus.HasValue?$" (HTTP {health.HttpStatus})":"")+$" · {health.LatencyMs} ms.",health.State=="HEALTHY"?NotificationSeverity.Success:NotificationSeverity.Warning);}

 // #831: the preview's sticky toolbar -- docked above the filter bar and the grid, so it stays put while the grid
 // scrolls. Composed from the flow state the stepper already computes (cached: a selection change must not re-scan
 // the XML) and the selection's findings. Apply here is the same ImportAsync with its own revision check.
 readonly Border previewToolbar=new(){Padding=new Thickness(8,4,8,4),Margin=Spacing.BelowInline}; readonly TextBlock previewToolbarCounts=new(){TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)}, previewToolbarValidation=new(){TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)}, previewToolbarStatus=new(){TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)};
 readonly Button previewApplyButton=new(){Padding=new Thickness(10,3,10,3),Margin=new Thickness(0,0,6,0),FontWeight=FontWeights.SemiBold}, previewCancelButton=new(){Content="İptal",Padding=new Thickness(8,3,8,3),Margin=new Thickness(0,0,6,0),Visibility=Visibility.Collapsed}, previewRecomputeButton=new(){Content="Önizlemeyi yenile",Padding=new Thickness(8,3,8,3),Margin=new Thickness(0,0,6,0)};
 ImportFlowState? lastFlowState; PreviewToolbarModel? lastPreviewToolbar;
 void RefreshPreviewToolbar(){
  ImportFlowState state;try{state=lastFlowState??CurrentImportFlowState();}catch(Exception e){Log("Önizleme araç çubuğu hesaplanamadı: "+Safe(e));return;}
  var stale=state.PreviewExists&&!state.PreviewFresh?ImportStepper.Compute(state).First(x=>x.Stage==ImportStage.Preview).Reason:"";
  var selected=preview.SelectedItems.Cast<CatalogProduct>().Select(x=>previewFlags.TryGetValue(x,out var f)?f:null).ToList();
  var m=PreviewActionToolbar.Compose(new(previewRows.Count,preview.Items.Count,selected.Count,selected.Count(f=>f?.Change==ImportRowChange.New),selected.Count(f=>f?.Change==ImportRowChange.Changed),selected.Count(f=>f?.Change==ImportRowChange.Unchanged),selected.Count(f=>f?.Highest==SeverityLevel.Blocking),selected.Count(f=>f?.Highest==SeverityLevel.Warning),state.XmlLoaded,state.PreviewExists,state.PreviewFresh,state.ApplyRunning,stale));
  lastPreviewToolbar=m;var hc=SeverityStyle.IsHighContrast;
  previewToolbarCounts.Text=m.CountsText;previewToolbarValidation.Text=$"{SeverityStyle.For(m.ValidationLevel,hc).Glyph} {m.ValidationText}";previewToolbarValidation.Foreground=SeverityStyle.AccentBrush(m.ValidationLevel,hc);
  previewToolbarStatus.Text=$"{SeverityStyle.For(m.StatusLevel,hc).Glyph} {m.StatusText}";previewToolbarStatus.Foreground=SeverityStyle.AccentBrush(m.StatusLevel,hc);previewToolbar.BorderBrush=SeverityStyle.AccentBrush(m.StatusLevel,hc);previewToolbar.BorderThickness=new Thickness(SeverityStyle.For(m.StatusLevel,hc).BorderWeight);
  previewApplyButton.Content=m.ApplyLabel;previewApplyButton.ToolTip="Seçili satırları yerel ürün havuzuna yazar; pazaryerine gönderim yapılmaz.";CommandState.Apply(previewApplyButton,m.CanApply?null:DisabledReason.StoreState(m.ApplyReason));
  previewCancelButton.Visibility=m.CanCancel?Visibility.Visible:Visibility.Collapsed;CommandState.Apply(previewRecomputeButton,m.CanRecompute?null:DisabledReason.StoreState("Yeniden hesaplanacak önizleme yok."));CommandState.Apply(previewDiffButton,!state.PreviewExists?DisabledReason.StoreState("Henüz önizleme yok."):preview.SelectedItems.Count!=1?DisabledReason.Selection("Farkı görmek için önizlemede tek satır seçin."):null);
  System.Windows.Automation.AutomationProperties.SetName(previewToolbar,$"Önizleme araç çubuğu: {m.CountsText}. {m.ValidationText}. {m.StatusText}."+(m.CanApply?"":" "+m.ApplyReason));}

 // #832: the XML row diff -- the selected preview row against the pool product it would touch (by SKU, else
 // barcode), rendered by the shared FieldDiffRenderer so added / removed / changed / unchanged read without colour.
 readonly Button previewDiffButton=new(){Content="Satır farkı",Padding=new Thickness(8,3,8,3),Margin=new Thickness(0,0,6,0),IsEnabled=false,ToolTip="Seçili satırın havuzdaki ürüne göre alan alan farkı"};
 CatalogProduct? ExistingProductFor(CatalogProduct row){var pool=store.Products();if(!string.IsNullOrWhiteSpace(row.Sku)){var bySku=pool.FirstOrDefault(x=>x.Sku==row.Sku);if(bySku!=null)return bySku;}return string.IsNullOrWhiteSpace(row.Barcode)?null:pool.FirstOrDefault(x=>x.Barcode==row.Barcode);}
 Window BuildPreviewRowDiff(){
  if(preview.SelectedItems.Count!=1||preview.SelectedItem is not CatalogProduct row)throw new InvalidOperationException("Fark için önizlemeden tek bir satır seçin.");
  var existing=ExistingProductFor(row);var rows=FieldDiff.Build(existing,row,FieldDiff.ProductFields,includeUnchanged:true);
  var body=new StackPanel{Margin=new Thickness(14)};
  body.Children.Add(new TextBlock{Text=existing==null?"Havuzda eşleşen ürün yok: satır yeni ürün olarak eklenir.":$"Havuzdaki ürünle karşılaştırma ({(string.IsNullOrWhiteSpace(existing.Sku)?"barkod":"SKU")} eşleşmesi). Hiçbir şey yazılmaz.",TextWrapping=TextWrapping.Wrap,Margin=Spacing.BelowControl});
  body.Children.Add(FieldDiffRenderer.Render(rows,SeverityStyle.IsHighContrast));
  var title="Satır farkı"+(string.IsNullOrWhiteSpace(row.Sku)?"":" · "+AuditStore.Redact(row.Sku.Trim()));
  return DialogShell.Create(this,title,body,new DialogShell.Action[]{new("Kapat",IsCancel:true)},640,520);}
 void OpenPreviewRowDiff(){BuildPreviewRowDiff().ShowDialog();}
 void BuildSources()
 {
   var sourceActions=new StackPanel();sourceActions.Children.Add(importStepper);var importProgressHead=new WrapPanel();importProgressHead.Children.Add(new TextBlock{Text="Aktarım ilerlemesi",FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center});importCancelButton.Click+=(_,_)=>{importCts?.Cancel();importProgress.Cancel(DateTime.UtcNow);RenderImportProgress();};importRetryButton.Click+=(_,_)=>RetryImportStage();importProgressHead.Children.Add(importCancelButton);importProgressHead.Children.Add(importRetryButton);sourceActions.Children.Add(importProgressHead);sourceActions.Children.Add(importProgressPanel);sourceActions.Children.Add(importCompletionPanel);RenderImportProgress();var addRow=new WrapPanel();sourceTypePicker.DropDownOpened+=(_,_)=>RefreshSourceTypePicker();addRow.Children.Add(sourceTypePicker);addRow.Children.Add(Button("+ Yeni kaynak",AddSourceOfSelectedType));sourceActions.Children.Add(addRow);sourceActions.Children.Add(Button("Kaynağı çoğalt",CloneCurrentSource));sourceActions.Children.Add(Button("XML şablonu aç…",LoadSourceTemplate));sourceActions.Children.Add(Button("Şablonu dışa aktar…",ExportSourceTemplate));var xmlViewStore=new UiPreferenceStore(dataDirectory);var xmlViewName=new TextBox{Width=145,ToolTip="Kaynak görünümü adı"};var xmlViews=new ComboBox{Width=175,DisplayMemberPath="Name"};var saveXmlView=Button("Görünümü kaydet",()=>{if(source==null)throw new InvalidOperationException("Önce XML kaynağı seçin.");xmlViewStore.SaveView("xml",xmlViewName.Text,JsonSerializer.Serialize(new{sourceId=source.Id}));xmlViewName.Clear();xmlViews.ItemsSource=xmlViewStore.ListViews("xml");});var deleteXmlView=Button("Görünümü sil",()=>{if(xmlViews.SelectedItem is not SavedUiView view)throw new InvalidOperationException("Önce görünüm seçin.");xmlViewStore.DeleteView("xml",view.Name);xmlViews.ItemsSource=xmlViewStore.ListViews("xml");});xmlViews.SelectionChanged+=(_,_)=>{if(xmlViews.SelectedItem is not SavedUiView view)return;try{using var doc=JsonDocument.Parse(view.Payload);var id=doc.RootElement.GetProperty("sourceId").GetString();var row=store.Sources().FirstOrDefault(x=>x.Id==id);if(row!=null){sources.ItemsSource=store.Sources();sources.SelectedItem=row;SetSource(Clone(row));}}catch{Log("XML görünümü okunamadı.");}};var xmlViewRow=new WrapPanel();xmlViewRow.Children.Add(new TextBlock{Text="Görünüm",Margin=new Thickness(3,5,2,3),VerticalAlignment=VerticalAlignment.Center});xmlViewRow.Children.Add(xmlViews);xmlViewRow.Children.Add(xmlViewName);xmlViewRow.Children.Add(saveXmlView);xmlViewRow.Children.Add(deleteXmlView);sourceActions.Children.Add(xmlViewRow);xmlViews.ItemsSource=xmlViewStore.ListViews("xml");var sourceListHead=new StackPanel();var sourceSearchRow=new WrapPanel();sourceSearchRow.Children.Add(sourceSearch);sourceSearch.TextChanged+=(_,_)=>{sourceListFilter=sourceListFilter with{Text=sourceSearch.Text};RefreshSources(false);};sourceSearchRow.Children.Add(AsyncButton("Sağlık taraması",ScanSourceHealthAsync));sourceListHead.Children.Add(sourceSearchRow);sourceListHead.Children.Add(sourceBandChips);sourceListHead.Children.Add(sourceScanStatus);var left=Dock(sourceActions,Dock(sourceListHead,sources));
  var rowText=new FrameworkElementFactory(typeof(TextBlock));rowText.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);
  rowText.SetBinding(TextBlock.TextProperty,new Binding("."){Converter=new SourceRowConverter(x=>sourceRows.TryGetValue(x.Id,out var r)?r.Summary:x.Name)});
  rowText.SetBinding(FrameworkElement.ToolTipProperty,new Binding("."){Converter=new SourceRowConverter(x=>sourceRows.TryGetValue(x.Id,out var r)?(r.MaskedLocation+(r.Reason.Length>0?"\n"+r.Reason:"")+(sourceEntries.TryGetValue(x.Id,out var e)&&e.Problem.Length>0?"\n"+e.Problem:"")):"")});
  rowText.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,new Binding("."){Converter=new SourceRowConverter(x=>sourceRows.TryGetValue(x.Id,out var r)?r.Summary+(sourceEntries.TryGetValue(x.Id,out var e)?". "+e.Detail:""):x.Name)});
  var rowDetail=new FrameworkElementFactory(typeof(TextBlock));rowDetail.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);rowDetail.SetValue(TextBlock.FontSizeProperty,11d);rowDetail.SetValue(UIElement.OpacityProperty,0.8);
  rowDetail.SetBinding(TextBlock.TextProperty,new Binding("."){Converter=new SourceRowConverter(x=>sourceEntries.TryGetValue(x.Id,out var e)?e.Detail:"")});
  var rowStack=new FrameworkElementFactory(typeof(StackPanel));rowStack.AppendChild(rowText);rowStack.AppendChild(rowDetail);
  sources.ItemTemplate=new DataTemplate{VisualTree=rowStack};
  var groupHeader=new FrameworkElementFactory(typeof(TextBlock));groupHeader.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);groupHeader.SetValue(FrameworkElement.MarginProperty,new Thickness(0,4,0,1));groupHeader.SetBinding(TextBlock.TextProperty,new Binding("."){Converter=new SourceGroupHeaderConverter()});
  sources.GroupStyle.Add(new GroupStyle{HeaderTemplate=new DataTemplate{VisualTree=groupHeader}});
  RowActionMenu.Attach(sources,SourceRowActions,ex=>Log("İşlem başarısız: "+AuditStore.Redact(ex.Message)));
  sources.SelectionChanged+=(_,_)=>{if(changingSourceList)return;if(sources.SelectedItem is XmlSource s)SetSource(Clone(s));};
  Field(sourceGeneral,"Tedarikçi / XML adı","Name");Field(sourceGeneral,"HTTPS adresi veya XML dosyası","Location");Field(sourceGeneral,"Sayı kültürü (zorunlu, örn. tr-TR / en-US)","NumberCultureName");Field(sourceGeneral,"Eksik kaynak grace süresi (dakika)","MissingSourceGraceMinutes");sourceGeneral.Children.Add(Button("XML dosyası seç",()=>{if(source==null)return;var d=new OpenFileDialog{Filter="XML (*.xml)|*.xml",CheckFileExists=true};if(d.ShowDialog(this)==true){source.Location=d.FileName;BindSource();}}));
  Flag(sourceGeneral,"Kaynak aktif","Enabled");Flag(sourceGeneral,"Program açıkken otomatik havuz güncellemesi","AutoImport");Field(sourceGeneral,"Kontrol aralığı (dakika)","IntervalMinutes");Label(sourceGeneral,"Basic Auth kullanıcı adı (isteğe bağlı)",xmlUser);Label(sourceGeneral,"XML şifresi",xmlPassword);var healthBody=new StackPanel();healthBody.Children.Add(xmlSourceHealthHeadline);healthBody.Children.Add(xmlSourceHealthLines);healthBody.Children.Add(xmlSourceHealth);healthBody.Children.Add(xmlSourceHealthActions);xmlSourceHealthPanel.Child=healthBody;sourceGeneral.Children.Add(xmlSourceHealthPanel);sourceGeneral.Children.Add(AsyncButton("Kaynak sağlığını kontrol et (tam okuma)",CheckXmlSourceAsync));sourceGeneral.Children.Add(Hint("Şifre Windows hesabına özel şifrelenir. XML sınırı 25 MB; HTTPS, timeout, HTTP durum kodu ve gzip yanıtı denetlenir. Kaynaktan kaybolan ürünler korunur; otomatik Etsy gönderimi yapılmaz."));
  sources.Height=115;
  var mapTop=new StackPanel();Label(mapTop,"Ürün XPath yolu (/Products/Product gibi)",itemPath);Label(mapTop,"Ondalık ayırıcı",decimalSeparator);mapTop.Children.Add(Hint("XML'i oku: alanlar otomatik önerilir. Değiştirmek için listeden seç. Birden fazla görsel yolu gerekiyorsa | ile birleştirebilirsin."));
  mapTop.Children.Add(mappingSummary);mappingFirstProblem.Click+=(_,_)=>FocusFirstMappingProblem();mapTop.Children.Add(mappingFirstProblem);
  mapping.Columns.Add(ReadOnlyColumn("Ürün alanı","Label",125));
  mapping.Columns.Add(ReadOnlyColumn("Zorunlu","RequiredMark",80));
  mapping.Columns.Add(ReadOnlyColumn("Tür","TypeLabel",110));
  var selector=new FrameworkElementFactory(typeof(ComboBox));selector.SetValue(ComboBox.ItemsSourceProperty,xmlPaths);selector.SetValue(ComboBox.IsEditableProperty,true);selector.SetValue(ComboBox.IsTextSearchEnabledProperty,true);selector.SetBinding(ComboBox.TextProperty,new Binding("Path"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});mapping.RowHeight=42;
  mapping.Columns.Add(new DataGridTemplateColumn{Header="XML alanını seç",CellTemplate=new DataTemplate{VisualTree=selector},Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
  mapping.Columns.Add(ReadOnlyColumn("Örnek (ilk kayıt)","Sample",180));
  mapping.Columns.Add(ReadOnlyColumn("Durum","StatusLabel",150));
  var mappingRowStyle=RowSelection.AddToRowStyle(FocusStyles.AddTo(new Style(typeof(DataGridRow))));mappingRowStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding("Reason")));mappingRowStyle.Setters.Add(new Setter(ToolTipService.ShowsToolTipOnKeyboardFocusProperty,true));mapping.RowStyle=mappingRowStyle;
  mapping.CellEditEnding+=(_,_)=>Dispatcher.BeginInvoke(new Action(RefreshMappingTable),System.Windows.Threading.DispatcherPriority.Background);
  
  BuildPricingEditor();
  var priceGrid=new System.Windows.Controls.Primitives.UniformGrid{Columns=3};
  foreach(var f in new[]{("Güvenlik stoğu","SafetyStock"),("Minimum XML stoğu","MinimumStock"),("Maksimum gösterilecek stok","MaximumStock")}){var p=new StackPanel();Field(p,f.Item1,f.Item2);priceGrid.Children.Add(p);}sourceRules.Children.Add(new GroupBox{Header="Stok kuralları",Content=priceGrid});
  Field(sourceRules,"Alınacak markalar (boş: tümü; ayırıcı ;)","BrandFilter");Field(sourceRules,"Alınacak kategoriler (boş: tümü; ayırıcı ;)","CategoryFilter");sourceRules.Children.Add(Hint("Fiyat ve stok, ürün kartında kilitli değilse güncellenir. Yeni ürün tüm eşleşen alanlarla kaydedilir."));Flag(sourceRules,"Tekrar alımda başlığı güncelle","UpdateName");Flag(sourceRules,"Tekrar alımda açıklamayı güncelle","UpdateDescription");Flag(sourceRules,"Tekrar alımda görselleri güncelle","UpdateImages");
  
  var previewTop=new WrapPanel();previewStatus.MaxWidth=460;previewTop.Children.Add(previewStatus);
  foreach(var (label,control) in new (string,UIElement)[]{("Önem",previewSeverity),("Alan",previewField),("Neden",previewReason)}){var g=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(4,0,0,0)};g.Children.Add(new TextBlock{Text=label,Margin=Spacing.RightInline,VerticalAlignment=VerticalAlignment.Center});g.Children.Add(control);previewTop.Children.Add(g);}
  previewTop.Children.Add(previewChangedOnly);previewTop.Children.Add(previewCounts);
  previewSeverity.SelectionChanged+=(_,_)=>ApplyPreviewFilter();previewField.SelectionChanged+=(_,_)=>ApplyPreviewFilter();previewReason.SelectionChanged+=(_,_)=>ApplyPreviewFilter();previewChangedOnly.Click+=(_,_)=>ApplyPreviewFilter();
  var previewStatusColumn=new DataGridTextColumn{Header="Doğrulama",IsReadOnly=true,Width=170,Binding=new Binding("."){Converter=new PreviewFlagConverter(row=>previewFlags.TryGetValue(row,out var f)?f.StatusLabel:"")}};preview.Columns.Add(previewStatusColumn);previewTop.Children.Add(Button("Tüm önizleme satırlarını seç",()=>preview.SelectAll()));foreach(var x in new[]{("SKU","Sku",130d),("Ürün","Name",240d),("Alış TL","Cost",90d),("Formül TL","FormulaPriceTry",105d),("1 döviz/TL","AppliedTryRate",100d),("Satış","Price",90d),("Döviz","Currency",65d),("Stok","Stock",72d),("Kategori","Category",150d)})Column(preview,x.Item1,x.Item2,x.Item3);
  
  var actions=new WrapPanel();actions.Children.Add(Button("Kaynağı kaydet",SaveSource));actions.Children.Add(AsyncButton("XML'i oku / alanları bul",InspectAsync));actions.Children.Add(AsyncButton("Önizleme hesapla",PreviewAsync));actions.Children.Add(AsyncButton("Seçilileri havuza al",ImportAsync));
  var settings=new TabControl();
  settings.Items.Add(new TabItem{Header="1  Kaynak ve bağlantı",Content=Split(left,Scroll(sourceGeneral),550)});
  settings.Items.Add(new TabItem{Header="2  Alan eşleştirme",Content=Dock(mapTop,mapping)});
  settings.Items.Add(new TabItem{Header="3  Fiyat, kur ve stok",Content=Scroll(sourceRules)});
  var workspace=new Grid();workspace.RowDefinitions.Add(new(){Height=new GridLength(1.2,GridUnitType.Star),MinHeight=180});workspace.RowDefinitions.Add(new(){Height=new GridLength(5)});workspace.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star),MinHeight=180});workspace.Children.Add(settings);
  var horizontal=new GridSplitter{Height=5,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,Background=Brushes.LightGray};Grid.SetRow(horizontal,1);workspace.Children.Add(horizontal);
  var previewToolbarRow=new WrapPanel();previewApplyButton.Click+=(_,_)=>_=RunAsync(ImportAsync);previewCancelButton.Click+=(_,_)=>{importCts?.Cancel();importProgress.Cancel(DateTime.UtcNow);RenderImportProgress();};previewRecomputeButton.Click+=(_,_)=>_=RunAsync(PreviewAsync);previewToolbarRow.Children.Add(previewApplyButton);previewToolbarRow.Children.Add(previewCancelButton);previewToolbarRow.Children.Add(previewRecomputeButton);previewDiffButton.Click+=(_,_)=>{try{OpenPreviewRowDiff();}catch(Exception e){Log(Safe(e),NotificationSeverity.Error);}};previewToolbarRow.Children.Add(previewDiffButton);previewToolbarRow.Children.Add(previewToolbarCounts);previewToolbarRow.Children.Add(previewToolbarValidation);previewToolbarRow.Children.Add(previewToolbarStatus);previewToolbar.Child=previewToolbarRow;preview.SelectionChanged+=(_,_)=>RefreshPreviewToolbar();var previewPanel=new GroupBox{Header="Ürünler / hesaplanan fiyat ve stok",Content=Dock(previewToolbar,previewTop,preview)};Grid.SetRow(previewPanel,2);workspace.Children.Add(previewPanel);
  Tab("XML yönetimi",Dock(actions,workspace));
 }
 void BuildPricingEditor()
 {
  var mode=new ComboBox{ItemsSource=new[]{new PriceChoice("Formula","Formül → TL satış → döviz"),new PriceChoice("Simple","Basit kâr / çarpan (önceki model)")},DisplayMemberPath="Label",SelectedValuePath="Value"};
  mode.SetBinding(ComboBox.SelectedValueProperty,new Binding("PriceMode"){Mode=BindingMode.TwoWay});Label(sourceRules,"Fiyatlandırma modeli",mode);
  var shared=new System.Windows.Controls.Primitives.UniformGrid{Columns=3};foreach(var f in new[]{("Alış dövizi (formülde TRY)","CostCurrency"),("Hedef satış dövizi (USD / EUR…)","Currency"),("Minimum satış (hedef döviz)","MinimumPrice")}){var p=new StackPanel();Field(p,f.Item1,f.Item2);shared.Children.Add(p);}sourceRules.Children.Add(shared);
  var formulaPanel=new StackPanel();formulaPanel.Children.Add(Hint("x = XML'deki TL alış fiyatı. Önce formül TL satış fiyatını üretir; sonra 1 dövizin TL karşılığına bölünür. Basit modeldeki kâr/sabit tutar ayrıca eklenmez."));
  var box=Field(formulaPanel,"Satış fiyatı formülü (CASE WHEN veya x * 1.40 + 100)","Formula",135);TextStyles.ApplyMono(box);
  formulaPanel.Children.Add(Button("Gönderdiğim CASE WHEN formülünü yükle",()=>{if(source==null)return;source.Formula=PriceFormula.Example;BindSource();previewRevision="";}));
  var auto=new CheckBox{Content="TCMB kurunu otomatik al (önizleme ve zamanlı XML güncellemesinde)"};auto.SetBinding(CheckBox.IsCheckedProperty,new Binding("AutoFx"){Mode=BindingMode.TwoWay});formulaPanel.Children.Add(auto);
  var fxGrid=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};var ratePanel=new StackPanel();var rateBox=Field(ratePanel,"1 hedef döviz kaç TL? (otomatik veya manuel)","TryPerTargetUnit");rateBox.SetBinding(TextBox.IsReadOnlyProperty,new Binding("IsChecked"){Source=auto});fxGrid.Children.Add(ratePanel);
  var kindPanel=new StackPanel();var kind=new ComboBox{ItemsSource=new[]{new PriceChoice("ForexSelling","TCMB döviz satış"),new PriceChoice("ForexBuying","TCMB döviz alış")},DisplayMemberPath="Label",SelectedValuePath="Value"};kind.SetBinding(ComboBox.SelectedValueProperty,new Binding("FxKind"){Mode=BindingMode.TwoWay});Label(kindPanel,"Kur türü",kind);fxGrid.Children.Add(kindPanel);formulaPanel.Children.Add(fxGrid);
  formulaPanel.Children.Add(fxStatus);formulaPanel.Children.Add(AsyncButton("Kuru şimdi güncelle",async()=>{var s=CurrentSource();if(s.PriceMode!="Formula")return;s.AutoFx=true;await UpdateFxAsync(s);BindSource();previewRevision="";}));
  var testPanel=new WrapPanel();testPanel.Children.Add(new TextBlock{Text="Örnek alış TL (x)",VerticalAlignment=VerticalAlignment.Center,Margin=Spacing.Inline});testPanel.Children.Add(sampleCost);testPanel.Children.Add(AsyncButton("Formülü ve kuru test et",TestFormulaAsync));formulaPanel.Children.Add(testPanel);formulaPanel.Children.Add(calculationStatus);
  var simplePanel=new StackPanel();simplePanel.Children.Add(Hint("Önceki hesap: maliyet × çarpan × (1 + kâr / 100) + sabit. Bu mod otomatik kur almaz."));var simpleGrid=new System.Windows.Controls.Primitives.UniformGrid{Columns=3};foreach(var f in new[]{("Döviz çarpanı","ExchangeRate"),("Kâr (%)","MarkupPercent"),("Sabit tutar (hedef döviz)","FixedAmount")}){var p=new StackPanel();Field(p,f.Item1,f.Item2);simpleGrid.Children.Add(p);}simplePanel.Children.Add(simpleGrid);
  mode.SelectionChanged+=(_,_)=>{var formula=mode.SelectedValue?.ToString()=="Formula";formulaPanel.Visibility=formula?Visibility.Visible:Visibility.Collapsed;simplePanel.Visibility=formula?Visibility.Collapsed:Visibility.Visible;};sourceRules.Children.Add(formulaPanel);sourceRules.Children.Add(simplePanel);
 }
 string RateDescription(XmlSource s)=>s.Currency=="TRY"?"Hedef TRY: döviz dönüşümü yok.":s.AutoFx?$"TCMB {(s.FxKind=="ForexSelling"?"döviz satış":"döviz alış")} • yayın tarihi {s.FxRateDate:dd.MM.yyyy} • 1 {s.Currency} = {s.TryPerTargetUnit:N4} TL":$"Manuel kur • 1 {s.Currency} = {s.TryPerTargetUnit:N4} TL";
 async Task UpdateFxAsync(XmlSource s)
 {
  if(s.PriceMode!="Formula")return;
  if(!s.AutoFx||s.Currency=="TRY"){fxStatus.Text=RateDescription(s);return;}
  try{var quote=await new TcmbRates(http).FetchAsync(s.Currency,s.FxKind,lifetime.Token);s.TryPerTargetUnit=quote.TryPerUnit;s.FxRateDate=quote.RateDate;s.FxFetchedUtc=quote.FetchedUtc;fxStatus.Text=RateDescription(s);Log(fxStatus.Text);}
  catch(InvalidDataException e){throw new InvalidOperationException(e.Message+" Fiyatlar güncellenmedi.");}
  catch(Exception e) when(e is HttpRequestException or OperationCanceledException){throw new InvalidOperationException("TCMB kuru alınamadı. Fiyatlar güncellenmedi; bağlantıyı kontrol edin veya otomatik kuru kapatıp manuel TL karşılığı girin.");}
 }
 async Task TestFormulaAsync()
 {
   var s=CurrentSource();var parsedCost=DeterministicNumberParser.Decimal(sampleCost.Text,s.NumberCultureName,"Örnek alış fiyatı");if(!parsedCost.Success)throw new InvalidOperationException(parsedCost.Message+" ("+parsedCost.Code+").");var cost=parsedCost.Value;
  calculationStatus.Text="Hesaplanıyor…";try{await UpdateFxAsync(s);BindSource();var result=CatalogPricing.Calculate(cost,s);calculationStatus.Text=$"Alış {cost:N2} TL → Formül {result.SourcePrice:N4} TL → Satış {result.FinalPrice:N2} {s.Currency}\n{RateDescription(s)}";}catch{calculationStatus.Text="Hesaplama yapılamadı; formülü ve kur bilgisini kontrol edin.";throw;}
 }
 void BuildApi()
 {
  var left=new StackPanel();left.Children.Add(Heading("Etsy mağaza bağlantısı"));Label(left,"Keystring",apiKey);Label(left,"Shared secret",apiSecret);Label(left,"Shop ID",shopId);Label(left,"Access token",apiToken);Label(left,"Refresh token",refreshToken);
  left.Children.Add(Button("Şifreli kaydet",()=>{SetAndSave(ReadCredentials());apiStatus.Text="Şifreli kaydedildi; bağlantıyı test edin.";}));left.Children.Add(AsyncButton("Bağlantıyı test et",async()=>{var name=await new EtsyConnector(http).TestAsync(ReadCredentials());apiStatus.Text=$"Mağaza API'sine erişildi: {name}. Yazma izni bu testte doğrulanmaz.";}));left.Children.Add(AsyncButton("Token yenile",async()=>SetAndSave(await new EtsyOAuth(http).RefreshAsync(ReadCredentials(),lifetime.Token))));left.Children.Add(apiStatus);
  var right=new StackPanel();right.Children.Add(Heading("Hesabı yetkilendir"));right.Children.Add(Hint("Etsy Developer uygulamana kayıtlı HTTPS dönüş adresini gir. Yalnız Etsy onayı için tarayıcı açılır."));Label(right,"Redirect URI",redirect);
  right.Children.Add(Button("1. Etsy yetkilendirmesini aç",()=>{attempt=new EtsyOAuth(http).Begin(apiKey.Password.Trim(),redirect.Text.Trim());System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(attempt.AuthorizeUrl){UseShellExecute=true});apiStatus.Text="Etsy onayından sonra tam dönüş adresini yapıştır.";}));right.Children.Add(Hint("Onay sonrasında adres çubuğundaki tam dönüş URL'sini yapıştır. Kod ve state kontrol edilir; dönüş URL'si kaydedilmez."));right.Children.Add(callback);right.Children.Add(AsyncButton("2. Yetkilendirmeyi tamamla",async()=>{if(attempt==null)throw new InvalidOperationException("Önce yetkilendirmeyi başlat.");SetAndSave(await new EtsyOAuth(http).ExchangeAsync(ReadCredentials(),attempt,callback.Text.Trim(),lifetime.Token));callback.Clear();attempt=null;}));
  Tab("Etsy bağlantısı",Split(Scroll(left),Scroll(right),580));
 }
 void BuildListings()
 {
  var top=new WrapPanel();top.Children.Add(listingState);top.Children.Add(AsyncButton("İlanları Etsy'den getir",async()=>{listingOffset=0;await LoadListingsAsync();}));top.Children.Add(AsyncButton("Önceki 100",async()=>{listingOffset=Math.Max(0,listingOffset-100);await LoadListingsAsync();}));top.Children.Add(AsyncButton("Sonraki 100",async()=>{if(listingOffset+100<listingTotal)listingOffset+=100;await LoadListingsAsync();}));top.Children.Add(listingStatus);
  foreach(var c in new[]{("İlan ID","ListingId",130d),("Başlık","Title",430d),("Durum","State",100d),("SKU","Sku",160d),("Fiyat","Price",90d),("Döviz","Currency",70d),("Adet","Quantity",70d)})Column(listings,c.Item1,c.Item2,c.Item3);Tab("Etsy ilanları",Dock(top,listings));
 }
 void BuildTemplate()
 {
  templateEditor.DataContext=template;templateEditor.Children.Add(Heading("Global fiziksel ürün şablonu"));foreach(var x in new[]{("Mağaza para birimi","Currency"),("Etsy kategori ID","TaxonomyId"),("Kargo profili ID","ShippingProfileId"),("Hazırlık profili ID","ReadinessStateId")})Field(templateEditor,x.Item1,x.Item2);
  var who=new ComboBox{ItemsSource=EtsyDrafts.WhoMadeValues};who.SetBinding(ComboBox.SelectedItemProperty,new Binding("WhoMade"){Mode=BindingMode.TwoWay});Label(templateEditor,"Üretici: i_did / someone_else / collective",who);
  var when=new ComboBox{ItemsSource=EtsyDrafts.WhenMadeValues};when.SetBinding(ComboBox.SelectedItemProperty,new Binding("WhenMade"){Mode=BindingMode.TwoWay});Label(templateEditor,"Üretim dönemi",when);Flag(templateEditor,"El işi tedarik malzemesi (is_supply)","IsSupply");Field(templateEditor,"Başlık öneki","TitlePrefix");Field(templateEditor,"Etiketler (virgülle ayır, en fazla 13)","Tags");Field(templateEditor,"Malzemeler (virgülle ayır)","Materials");templateEditor.Children.Add(Button("Şablonu kaydet",()=>{ValidBindings(templateEditor);TemplateStore.Save(template,dataDirectory);Log("Global Etsy şablonu kaydedildi.");}));
  var right=new StackPanel();right.Children.Add(Heading("Havuz → Etsy taslağı"));right.Children.Add(Hint("Havuzda ürün seç, şablonu kontrol et ve taslağı oluştur. Ürün başlığı ve açıklaması havuzdaki düzenlenmiş değerlerden alınır."));right.Children.Add(Button("Seçili ürünü kontrol et",CheckDraft));right.Children.Add(draftStatus);right.Children.Add(AsyncButton("Seçili ürüne Etsy taslağı oluştur",CreateDraftAsync));right.Children.Add(Hint("Taslak yayınlanmaz. Ürünün ilk görseli yüklenir (adresler | ile ayrılır; HTTPS veya yerel file:/// desteklenir). Ek görseller/video ve varyant/SKU envanteri Etsy üzerinden tamamlanmalı."));right.Children.Add(Heading("İlanı elle eşleştir"));right.Children.Add(Hint("Sonucu belirsiz bir oluşturma işleminde Etsy ilanları sekmesinden oluşan ilanı kontrol et. İlanı ve havuz ürününü seçip bağla."));right.Children.Add(Button("Seçili Etsy ilanını seçili ürüne bağla",LinkListing));Tab("Global Etsy şablonu",Split(Scroll(templateEditor),Scroll(right),550));
 }
 void LoadSourceTemplate(){var dialog=new OpenFileDialog{Filter="MonoBridge XML şablonu (*.json)|*.json",InitialDirectory=Path.Combine(AppContext.BaseDirectory,"templates")};if(dialog.ShowDialog(this)!=true)return;if(new FileInfo(dialog.FileName).Length>1024*1024)throw new InvalidOperationException("Şablon dosyası 1 MB sınırını aşıyor.");var s=JsonSerializer.Deserialize<XmlSource>(File.ReadAllText(dialog.FileName))??throw new InvalidOperationException("Şablon okunamadı.");s.Id=Guid.NewGuid().ToString("N");s.AutoImport=false;s.LastRunUtc=null;s.LastStatus="Şablondan açıldı; adresi ve eşleştirmeyi kontrol edin.";XmlCatalog.ValidateSource(s);SetSource(s);Log("XML şablonu açıldı; kaynak adresini gir ve önizle.");}
 void ExportSourceTemplate(){var s=Clone(CurrentSource());s.AutoImport=false;s.LastRunUtc=null;s.LastStatus="";var dialog=new SaveFileDialog{Filter="XML şablonu (*.json)|*.json",FileName="xml-sablonu.json"};if(dialog.ShowDialog(this)==true){File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(s,new JsonSerializerOptions{WriteIndented=true}));Log("XML alan ve kural şablonu kaydedildi. Şifreler dışa aktarılmadı.");}}
 static T Clone<T>(T item)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(item))!;
 static void ValidBindings(DependencyObject root){if(Validation.GetHasError(root))throw new InvalidOperationException("Kırmızı işaretli sayı alanlarını düzelt.");for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)ValidBindings(VisualTreeHelper.GetChild(root,i));}
 void BindSource(){sourceGeneral.DataContext=null;sourceRules.DataContext=null;sourceGeneral.DataContext=source;sourceRules.DataContext=source;}
 void SetSource(XmlSource s)
 {
   source=s;BindSource();RefreshImportStepper();if(store.Sources().Any(x=>x.Id==s.Id)){try{uiPreferences.Set(ImportSourceCatalog.RecentPreferenceKey,s.Id);}catch(Exception e){Log("Son kaynak kaydedilemedi: "+Safe(e));}}fxStatus.Text=RateDescription(s);calculationStatus.Text="Alış fiyatını girip hesaplamayı test edebilirsin.";itemPath.Text=s.ItemPath;decimalSeparator.SelectedItem=s.DecimalSeparator;previewMappingShapeFingerprint=""; _ = RefreshXmlSourceHealthAsync(s);
  var names=new[]{("Sku","SKU / stok kodu"),("Barcode","Barkod"),("Gtin","GTIN / EAN / UPC"),("Name","Ürün adı"),("Description","Açıklama"),("Cost","Alış fiyatı"),("Stock","Stok"),("Brand","Marka"),("Category","Kategori"),("ImageUrls","Görseller")};
  mappings=names.Select(x=>new MappingEntry{Key=x.Item1,Label=x.Item2,Path=s.Fields.GetValueOrDefault(x.Item1,"")}).ToList();mapping.ItemsSource=mappings;mappingSampleItem=null;RefreshMappingTable();xml="";loadedLocation="";previewRevision="";preview.ItemsSource=null;paths.ItemsSource=null;xmlPaths.Clear();
  try{var auth=XmlAuthStore.Load(s.Id,dataDirectory);xmlUser.Text=auth.User;xmlPassword.Password=auth.Password;}catch(Exception){xmlUser.Clear();xmlPassword.Clear();Log("XML şifresi açılamadı; yeniden kaydet.");}
 }
 async Task RefreshXmlSourceHealthAsync(XmlSource candidate)
 {
  var id=candidate.Id;
  try
  {
   var summary=await Task.Run(()=>{var count=store.Products().Count(p=>p.SourceId==id);var run=new XmlRunStore(dataDirectory).List(id,1).FirstOrDefault();var quarantine=new SourceMissingQuarantine(dataDirectory).List(id);return (count,run,pending:quarantine.Count(x=>x.State=="PENDING_ACTION"),warning:quarantine.Count(x=>x.State=="WARNING"),persisted:store.Sources().FirstOrDefault(x=>x.Id==id));});
   if(source?.Id!=id)return;
    var runText=summary.run==null?"Henüz çalışmadı":$"{summary.run.Status} · {TimeDisplay.Format(summary.run.StartedUtc)} · +{summary.run.Added}/~{summary.run.Updated}/={summary.run.Unchanged}";
    var pending=summary.pending;var warning=summary.warning;var persisted=summary.persisted;
    xmlSourceHealth.Text=$"Kaynak özeti: {summary.count:N0} ürün · son çalışma {runText} · source-health: {warning} uyarı / {pending} bekleyen · durum {persisted?.LastFeedState ?? candidate.LastFeedState}";
    var h=persisted??candidate;var runSnapshot=summary.run==null?null:new SourceRunSnapshot(summary.run.Status,summary.run.StartedUtc,summary.run.FinishedUtc,summary.run.LeaseUntilUtc,summary.run.Error);
    var previewIsThis=previewSourceId==id&&previewValidation.Count>0;var shapeKnown=xml!=""&&loadedLocation==candidate.Location&&previewMappingShapeFingerprint!="";
    var facts=new SourceHealthFacts(h.LastHealthState,h.LastHealthCheckUtc,h.LastHealthLatencyMs,h.LastHealthHttpStatus,h.LastHealthError,h.LastSuccessfulFeedUtc,h.LastSuccessfulFeedCount,h.LastFeedState,candidate.MappingRevision,h.LastAppliedMappingRevision,h.LastMappingShapeFingerprint,shapeKnown?previewMappingShapeFingerprint:null,runSnapshot,summary.count,pending,warning,
     previewIsThis?previewValidation.Count(v=>v.Highest==SeverityLevel.Blocking):null,previewIsThis?previewValidation.Count(v=>v.Highest==SeverityLevel.Warning):null,previewIsThis?previewValidation.Count:null,previewIsThis?previewEvaluatedUtc:null,importProgress.Failed,importRunning);
    lastSourceHealth=XmlSourceHealthPanel.Compose(facts,DateTime.UtcNow);RenderSourceHealth(lastSourceHealth);
  }
  catch(Exception error){if(source?.Id==id)xmlSourceHealth.Text="Kaynak özeti okunamadı: "+MarketplaceConnectionStore.Redact(error.Message);}
 }
 void CloneCurrentSource()
 {
  var current=CurrentSource();var clone=Clone(current);clone.Id=Guid.NewGuid().ToString("N");clone.Name=(string.IsNullOrWhiteSpace(current.Name)?"XML kaynağı":current.Name)+" kopya";clone.AutoImport=false;clone.LastRunUtc=null;clone.LastStatus="Kopyalandı; adres ve yetkilendirme doğrulanmalı.";store.SaveSource(clone);XmlAuthStore.Save(clone.Id,new(),dataDirectory);RefreshSources(false);sources.SelectedItem=clone;SetSource(clone);Log("XML kaynağı eşleme ve kurallarıyla çoğaltıldı; gizli yetkilendirme kopyalanmadı.");
 }
 /// <summary>#870: the source list's row menu; both actions are the page's own buttons, so they carry the same guards.</summary>
 IReadOnlyList<RowAction> SourceRowActions(){var none=sources.SelectedItem is XmlSource?null:"Önce kaynak seçin.";return new RowAction[]{new("source-inspect","XML'i oku / alanları bul",()=>RunGuarded(InspectAsync),none),new("source-health","Kaynak sağlığını kontrol et (tam okuma)",()=>RunGuarded(CheckXmlSourceAsync),none)};}
 async void RunGuarded(Func<Task> work){try{await work();}catch(Exception ex){Log("İşlem başarısız: "+AuditStore.Redact(ex.Message));}}
 async Task CheckXmlSourceAsync()
 {
  if(source==null)throw new InvalidOperationException("Önce XML kaynağı seçin.");
  var candidate=source;var location=candidate.Location.Trim();if(location.Length==0)throw new InvalidOperationException("Sağlık kontrolü için XML adresi veya dosyası gerekli.");
  xmlSourceHealth.Text="Kaynak erişilebilirliği, HTTP durumu, timeout ve gzip yanıtı kontrol ediliyor…";
  try
  {
   var text=await new XmlSourceReader(http).ReadAsync(location,new(xmlUser.Text,xmlPassword.Password),lifetime.Token);
   var scan=await Task.Run(()=>XmlCatalog.Inspect(text,string.IsNullOrWhiteSpace(candidate.ItemPath)?null:candidate.ItemPath),lifetime.Token);
   xmlSourceHealth.Text=$"Sağlıklı · {text.Length:N0} karakter · {scan.Paths.Count:N0} alan yolu · 25 MB sınırı içinde · gzip/HTTPS kontrolü geçti.";
  }
  catch(Exception error){xmlSourceHealth.Text="Sağlık kontrolü başarısız: "+MarketplaceConnectionStore.Redact(error.Message);throw;}
 }
 XmlSource CurrentSource(){if(source==null)throw new InvalidOperationException("Önce XML kaynağı seç veya ekle.");ValidBindings(sourceGeneral);ValidBindings(sourceRules);mapping.CommitEdit(DataGridEditingUnit.Cell,true);mapping.CommitEdit(DataGridEditingUnit.Row,true);source.ItemPath=itemPath.Text.Trim();source.DecimalSeparator=decimalSeparator.SelectedItem?.ToString()??".";source.Fields=mappings.Where(m=>!string.IsNullOrWhiteSpace(m.Path)).ToDictionary(m=>m.Key,m=>m.Path.Trim());source.Currency=source.Currency.Trim().ToUpperInvariant();source.CostCurrency=source.CostCurrency.Trim().ToUpperInvariant();XmlCatalog.ValidateSource(source);return source;}
 void SaveSource(){var s=CurrentSource();
  // #820: the same form-level rule as the product card -- messages under the inputs, focus on the first, save refused.
  var sourceFindings=new List<(string Property,string Message)>();if(string.IsNullOrWhiteSpace(s.Name))sourceFindings.Add(("Name","Tedarikçi / XML adı zorunlu."));if(string.IsNullOrWhiteSpace(s.Location))sourceFindings.Add(("Location","HTTPS adresi veya XML dosyası zorunlu."));
  foreach(var row in formRows.Where(r=>r.Key.Form==sourceGeneral))row.Value.SetValidation("");
  if(sourceFindings.Count>0){foreach(var (property,message) in sourceFindings)if(formRows.TryGetValue((sourceGeneral,property),out var r))r.SetValidation(message);FocusField(sourceGeneral,sourceFindings[0].Property,null);throw new InvalidOperationException(sourceFindings.Count==1?"1 engel: kaydetmeden önce düzeltin. "+sourceFindings[0].Message:$"{sourceFindings.Count} engel: kaydetmeden önce düzeltin. "+sourceFindings[0].Message);}var latest=store.Sources().SingleOrDefault(x=>x.Id==s.Id);if(latest!=null){s.LastRunUtc=latest.LastRunUtc;s.LastStatus=latest.LastStatus;}XmlAuthStore.Save(s.Id,new(xmlUser.Text,xmlPassword.Password),dataDirectory);store.SaveSource(s);RefreshSources(false);_ = RefreshXmlSourceHealthAsync(s);Log("XML kaynağı, eşleştirme ve kurallar kaydedildi.");}
 void RefreshSources(bool choose=true){
  var recent=uiPreferences.Get(ImportSourceCatalog.RecentPreferenceKey);var all=store.Sources();
  var rows=ImportSourceCatalog.Options(all,recent,id=>{var h=all.FirstOrDefault(x=>x.Id==id);return h==null?null:new SourceHealthSummary(h.LastFeedState??"",h.LastSuccessfulFeedUtc,null,"");},DateTime.UtcNow,System.IO.File.Exists);
  sourceRows=rows.ToDictionary(r=>r.Id);var now=DateTime.UtcNow;IReadOnlyDictionary<string,SourceRunSnapshot> latestRuns;try{latestRuns=XmlSourceListGrouping.LatestRuns(new XmlRunStore(dataDirectory).List(limit:1000));}catch(Exception e){latestRuns=new Dictionary<string,SourceRunSnapshot>();Log("Çalıştırma geçmişi okunamadı: "+Safe(e));}
  var byId=all.ToDictionary(x=>x.Id);var entries=rows.Select(r=>XmlSourceListGrouping.Describe(byId[r.Id],r,latestRuns.TryGetValue(r.Id,out var run)?run:null,now)).ToList();sourceEntries=entries.ToDictionary(e=>e.Row.Id);
  var groups=XmlSourceListGrouping.Group(entries,sourceListFilter);var ordered=groups.SelectMany(g=>g.Entries).Select(e=>byId[e.Row.Id]).ToList();
  // Only the editor's own source is restored after the rebind (a filter or scan must not drop the draft); a list selection with no editor behind it is left cleared, so selecting it again binds it as before.
  var selectedId=source?.Id;var view=new System.Windows.Data.ListCollectionView(ordered);view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(null,new SourceRowConverter(x=>sourceEntries.TryGetValue(x.Id,out var e)?e.BandLabel:"")));view.MoveCurrentTo(null);
  var keyboard=FocusRestore.Capture(sources,x=>((XmlSource)x).Id);changingSourceList=true;try{sources.ItemsSource=view;if(selectedId!=null)sources.SelectedItem=ordered.FirstOrDefault(x=>x.Id==selectedId);}finally{changingSourceList=false;}FocusRestore.Restore(sources,keyboard,x=>((XmlSource)x).Id);
  RenderSourceBandChips(XmlSourceListGrouping.Counts(entries),entries.Count);
  if(choose&&source==null&&sources.Items.Count>0)sources.SelectedItem=sources.Items.OfType<XmlSource>().FirstOrDefault(x=>x.Id==recent)??sources.Items[0];}
  async Task InspectAsync(){var s=CurrentSource();xml="";loadedLocation="";previewRevision="";previewMappingShapeFingerprint="";preview.ItemsSource=null;var importToken=BeginImportStage();ReportImport(ImportProgressStage.Download,ImportProgressStatus.Running);try{xml=await new XmlSourceReader(http).ReadAsync(s.Location,new(xmlUser.Text,xmlPassword.Password),importToken);}catch(OperationCanceledException){ReportImport(ImportProgressStage.Download,ImportProgressStatus.Cancelled);throw;}catch(Exception e){ReportImport(ImportProgressStage.Download,ImportProgressStatus.Failed,note:Safe(e));throw;}ReportImport(ImportProgressStage.Download,ImportProgressStatus.Done,xml.Length,xml.Length,"karakter");loadedLocation=s.Location;ReportImport(ImportProgressStage.Read,ImportProgressStatus.Running);XmlScan scan;try{scan=await Task.Run(()=>XmlCatalog.Inspect(xml,string.IsNullOrWhiteSpace(s.ItemPath)?null:s.ItemPath),importToken);}catch(Exception e){ReportImport(ImportProgressStage.Read,ImportProgressStatus.Failed,note:Safe(e));throw;}ReportImport(ImportProgressStage.Read,ImportProgressStatus.Done,scan.Paths.Count,scan.Paths.Count,"alan");itemPath.Text=scan.ItemPath;paths.ItemsSource=scan.Paths;xmlPaths.Clear();xmlPaths.Add("");foreach(var path in scan.Paths)xmlPaths.Add(path);foreach(var m in mappings)if(string.IsNullOrWhiteSpace(m.Path)&&scan.SuggestedFields.TryGetValue(m.Key,out var value))m.Path=value;mapping.Items.Refresh();previewRevision="";preview.ItemsSource=null;Log($"XML okundu; {scan.Paths.Count} alan yolu bulundu.");try{mappingSampleItem=System.Xml.Linq.XDocument.Parse(xml).XPathSelectElement(scan.ItemPath);}catch(Exception){mappingSampleItem=null;}RefreshMappingTable();RefreshImportStepper();}
  async Task PreviewAsync(){var s=CurrentSource();if(xml==""||loadedLocation!=s.Location)throw new InvalidOperationException("Bu kaynak adresi için önce XML'i oku.");previewRevision="";previewMappingShapeFingerprint="";preview.ItemsSource=null;previewStatus.Text="Fiyat ve kur hesaplanıyor…";await UpdateFxAsync(s);BindSource();var snapshot=Clone(s);var snapshotJson=JsonSerializer.Serialize(snapshot);var previewToken=BeginImportStage();ReportImport(ImportProgressStage.Parse,ImportProgressStatus.Running);XmlMappingSnapshot mappingSnapshot;try{mappingSnapshot=await Task.Run(()=>XmlCatalog.MappingSnapshot(xml,snapshot),previewToken);}catch(Exception e){ReportImport(ImportProgressStage.Parse,ImportProgressStatus.Failed,note:Safe(e));throw;}ReportImport(ImportProgressStage.Parse,ImportProgressStatus.Done,mappingSnapshot.ItemCount,mappingSnapshot.ItemCount,"ürün");ReportImport(ImportProgressStage.Validate,ImportProgressStatus.Running);try{XmlCatalog.EnsureMappingReady(snapshot,mappingSnapshot,scheduled:false);}catch(Exception e){ReportImport(ImportProgressStage.Validate,ImportProgressStatus.Failed,note:Safe(e));throw;}ReportImport(ImportProgressStage.Validate,ImportProgressStatus.Done);ReportImport(ImportProgressStage.Preview,ImportProgressStatus.Running,0,mappingSnapshot.ItemCount);List<CatalogProduct> rows;try{var sink=ImportReporter();rows=await Task.Run(()=>XmlCatalog.Preview(xml,snapshot,sink),previewToken);}catch(OperationCanceledException){ReportImport(ImportProgressStage.Preview,ImportProgressStatus.Cancelled);throw;}catch(Exception e){ReportImport(ImportProgressStage.Preview,ImportProgressStatus.Failed,note:Safe(e));throw;}ReportImport(ImportProgressStage.Preview,ImportProgressStatus.Done,rows.Count,rows.Count,"ürün");EvaluatePreviewRows(rows);previewRevision=XmlPreviewFingerprint.Create(xml,snapshotJson);previewMappingShapeFingerprint=mappingSnapshot.Fingerprint;previewStatus.Text=$"{rows.Count} ürün • Fiyat/stok hesaplandı. Eşleme revizyonu {snapshot.MappingRevision}. Kaydetmeden önce satırları seç. Etsy'ye gönderim yapılmaz.";Log($"XML önizlemesi: {rows.Count} ürün.");RefreshImportStepper();}
  async Task ImportAsync(){var s=CurrentSource();var snapshotJson=JsonSerializer.Serialize(s);if(previewRevision==""||previewRevision!=XmlPreviewFingerprint.Create(xml,snapshotJson))throw new InvalidOperationException("XML veya eşleme ayarları değişti; önizleme geçersiz. Yeniden oku ve önizle.");var mappingSnapshot=await Task.Run(()=>XmlCatalog.MappingSnapshot(xml,s));XmlCatalog.EnsureMappingReady(s,mappingSnapshot,scheduled:false);if(previewMappingShapeFingerprint!=mappingSnapshot.Fingerprint)throw new InvalidOperationException("XML yapısı önizlemeden sonra değişti; yeniden önizleyin.");CatalogPricing.ValidateRate(s);var selectedFlags=preview.SelectedItems.Cast<CatalogProduct>().Select(p=>previewFlags.TryGetValue(p,out var f)?f:null).ToList();var rejectedCount=selectedFlags.Count(f=>f?.Highest==SeverityLevel.Blocking);var warningCount=selectedFlags.Count(f=>f?.Highest==SeverityLevel.Warning);var rows=preview.SelectedItems.Cast<CatalogProduct>().Select(Clone).ToList();if(rows.Count==0)throw new InvalidOperationException("Önizlemeden en az bir ürün seç.");var complete=rows.Count==preview.Items.Count;var feedHash=complete?XmlPreviewFingerprint.FeedHash(xml):"";XmlAuthStore.Save(s.Id,new(xmlUser.Text,xmlPassword.Password),dataDirectory);store.SaveSource(s);var runs=new XmlRunStore(dataDirectory);var run=runs.Start(s.Id,feedHash,TimeSpan.FromMinutes(10));importRunning=true;importCancelled=false;importFailed=false;importStartedUtc=DateTime.UtcNow;importCompletionPanel.Visibility=Visibility.Collapsed;RefreshImportStepper();try{runs.Heartbeat(run,TimeSpan.FromMinutes(10));var context=new XmlImportContext{FeedHash=feedHash,MappingShapeFingerprint=mappingSnapshot.Fingerprint,PreviewFingerprint=previewRevision,CompleteFeed=complete,AllowMappingRevisionChange=true,ObservedAtUtc=DateTimeOffset.UtcNow};var applyToken=BeginImportStage();ReportImport(ImportProgressStage.Apply,ImportProgressStatus.Running,0,rows.Count);var applySink=ImportReporter();ImportSummary result;try{result=await Task.Run(()=>store.Import(s,rows,applyToken,context,applySink),applyToken);}catch(OperationCanceledException){ReportImport(ImportProgressStage.Apply,ImportProgressStatus.Cancelled);throw;}catch(Exception e){ReportImport(ImportProgressStage.Apply,ImportProgressStatus.Failed,note:Safe(e));throw;}ReportImport(ImportProgressStage.Apply,ImportProgressStatus.Done,rows.Count,rows.Count,$"{result.Added} yeni / {result.Updated} güncel");runs.Complete(run,result);s.LastRunUtc=DateTime.UtcNow;s.LastStatus=$"{result.Added} yeni / {result.Updated} güncel / {result.Unchanged} aynı";store.SaveSource(s);previewRevision="";RefreshSources(false);_ = RefreshXmlSourceHealthAsync(s);RefreshProducts();Log(s.LastStatus);CompleteImport(new(s.Name,s.MappingRevision,feedHash,complete,rows.Count,rejectedCount,warningCount,result,"",false,importStartedUtc,DateTime.UtcNow));}catch(OperationCanceledException){importCancelled=true;runs.Fail(run,"İptal edildi");CompleteImport(new(s.Name,s.MappingRevision,feedHash,complete,rows.Count,rejectedCount,warningCount,null,"",true,importStartedUtc,DateTime.UtcNow));throw;}catch(Exception e){importFailed=true;runs.Fail(run,e.Message);CompleteImport(new(s.Name,s.MappingRevision,feedHash,complete,rows.Count,rejectedCount,warningCount,null,Safe(e),false,importStartedUtc,DateTime.UtcNow));throw;}finally{importRunning=false;RefreshImportStepper();}}
 void ShowProducts(CatalogPage page){var baseline=productEditBaseline;if(!ResolveProductEdit())return;if(baseline!=productEditBaseline)page=store.Search(search.Text.Trim(),productOffset,200,productFilter);var id=edit?.Id;productTotal=page.Total;var keyboard=FocusRestore.Capture(products,x=>((CatalogProduct)x).Id);changingProductSelection=true;try{products.ItemsSource=page.Items;if(id!=null)products.SelectedItem=page.Items.FirstOrDefault(p=>p.Id==id);}finally{changingProductSelection=false;}BindProductEdit(products.SelectedItem as CatalogProduct);FocusRestore.Restore(products,keyboard,x=>((CatalogProduct)x).Id);SummaryText.Text=$"{page.Total:N0} sonuç   •   {page.InStock:N0} stokta   •   {page.Linked:N0} Etsy ile eşleşen   •   Sayfa {productOffset/200+1} / {Math.Max(1,(page.Total+199)/200)}";}
 void RefreshProducts(){globalSearchIndex.Invalidate();searchRevision++;ShowProducts(store.Search(search.Text.Trim(),productOffset,200,productFilter));SaveProductLayout();}
 async Task SearchProductsAsync(){var revision=++searchRevision;var q=search.Text.Trim();var offset=productOffset;var filter=productFilter;try{var page=await Task.Run(()=>store.Search(q,offset,200,filter));if(revision==searchRevision)ShowProducts(page);}catch(Exception e){Log(Safe(e), NotificationSeverity.Error);}}
 void Refresh_Click(object sender,RoutedEventArgs e){try{RefreshProducts();}catch(Exception ex){Log(Safe(ex), NotificationSeverity.Error);}}
 EtsyCredentials ReadCredentials(){var key=apiKey.Password.Trim();var secret=apiSecret.Password.Trim();var token=apiToken.Password.Trim();var changedApp=key!=credentials.Key||secret!=credentials.Secret;var changedToken=token!=credentials.Token;var refresh=refreshToken.Password.Trim();return credentials with{Key=key,Secret=secret,Token=token,RefreshToken=(changedApp||changedToken)&&refresh==credentials.RefreshToken?"":refresh,ExpiresAt=changedApp||changedToken?null:credentials.ExpiresAt,ShopId=shopId.Text.Trim(),RedirectUri=redirect.Text.Trim()};}
 void SetCredentials(EtsyCredentials c){credentials=c;apiKey.Password=c.Key;apiSecret.Password=c.Secret;apiToken.Password=c.Token;refreshToken.Password=c.RefreshToken;shopId.Text=c.ShopId;redirect.Text=c.RedirectUri;apiStatus.Text=c.ExpiresAt.HasValue?$"Token son kullanım: {c.ExpiresAt.Value.LocalDateTime:g}":"Bilgiler yüklendi; bağlantıyı test et.";}
 void SetAndSave(EtsyCredentials c){CredentialStore.Save(c,dataDirectory);SetCredentials(c);Log("Etsy yetkilendirme bilgileri şifreli kaydedildi.");}
 async Task<EtsyCredentials> AuthorizedAsync(){await authorizationGate.WaitAsync(lifetime.Token);try{var c=ReadCredentials();if(c.ExpiresAt<=DateTimeOffset.UtcNow.AddMinutes(1)&&c.RefreshToken!=""){var latest=ReadCredentials();if(latest.ExpiresAt<=DateTimeOffset.UtcNow.AddMinutes(1)){c=await new EtsyOAuth(http).RefreshAsync(latest,lifetime.Token);SetAndSave(c);}else c=latest;}return c;}finally{authorizationGate.Release();}}
 async Task LoadListingsAsync(){var state=listingState.SelectedItem?.ToString()??"active";var c=await AuthorizedAsync();if(loadedListingState!=state||loadedListingShop!=c.ShopId)listingOffset=0;var page=await new EtsyShopClient(http).GetListingsAsync(c,state,listingOffset,lifetime.Token);listings.ItemsSource=page.Listings;listingTotal=page.Count;loadedListingShop=c.ShopId;loadedListingState=state;listingStatus.Text=$"{page.Listings.Count} / {page.Count} ilan • başlangıç {listingOffset}";Log("Etsy ilanları okundu.");}
 CatalogProduct SelectedProduct()=>products.SelectedItem is CatalogProduct p?store.Products().Single(x=>x.Id==p.Id):throw new InvalidOperationException("Önce ürün havuzunda bir ürün seç.");
 void CheckDraft(){ValidBindings(templateEditor);var p=SelectedProduct();var errors=EtsyDrafts.Validate(p,template);if(p.EtsyCreationAttempted&&p.EtsyListingId=="")errors.Add("Önceki oluşturma sonucunu Etsy'den kontrol edip ilanı bağla.");draftStatus.Text=errors.Count==0?$"{p.Sku} • {p.Name}\n{p.Price} {p.Currency} / {p.Stock} adet\nTaslak için yerel kontroller geçti; mağaza dövizi API'de ayrıca kontrol edilir.":string.Join("\n",errors);Navigate("etsy");if(etsyTabs!=null)etsyTabs.SelectedIndex=1;}
 async Task CreateDraftAsync()
 {
  ValidBindings(templateEditor);var p=SelectedProduct();var errors=EtsyDrafts.Validate(p,template);if(errors.Count>0)throw new InvalidOperationException(string.Join("\n",errors));if(p.EtsyCreationAttempted)throw new InvalidOperationException("Bu ürün için daha önce oluşturma başlatıldı. Mükerrer oluşturmadan önce Etsy ilanlarını kontrol et ve eşleştir.");
  var c=await AuthorizedAsync();if(c.Token==""||c.Key==""||c.Secret==""||!long.TryParse(c.ShopId,out var id)||id<=0)throw new InvalidOperationException("Önce Etsy bağlantı bilgilerini tamamla.");
  if(MessageBox.Show(this,$"{p.Name}\n{p.Price} {p.Currency}, {p.Stock} adet\n\nEtsy mağazanda taslak oluşturulsun mu? Yayınlanmayacak.","Etsy taslağı",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
  var result=await new EtsyDrafts(http).CreateWithFirstImageAsync(c,p,Clone(template),()=>{p.EtsyCreationAttempted=true;store.SaveProduct(p);return Task.CompletedTask;},listingId=>{p.EtsyListingId=listingId.ToString(CultureInfo.InvariantCulture);draftStatus.Text=$"Etsy taslağı oluşturuldu: {listingId}. Görsel işlemi devam ediyor.";store.SaveProduct(p);RefreshProducts();return Task.CompletedTask;},lifetime.Token);
  draftStatus.Text=result.ImageError??$"Etsy taslağı oluşturuldu: {result.ListingId}. "+(result.ImageId.HasValue?"İlk görsel yüklendi.":"Üründe görsel yok; Etsy'den tamamlayın.")+" Ek görseller/video ve varyant/SKU envanteri Etsy'den tamamlanmalı. Taslak yayınlanmadı.";draftStatus.Text += " " + result.ImageStatus;Log(draftStatus.Text);
 }
 void LinkListing(){var p=SelectedProduct();if(listings.SelectedItem is not EtsyListing l||loadedListingShop!=ReadCredentials().ShopId)throw new InvalidOperationException("Aynı mağazadan Etsy ilanlarını getir ve doğru ilanı seç.");if(store.Products().Any(x=>x.Id!=p.Id&&x.EtsyListingId==l.ListingId.ToString(CultureInfo.InvariantCulture)))throw new InvalidOperationException("Bu Etsy ilanı başka bir havuz ürününe bağlı.");if(MessageBox.Show(this,$"{p.Name}\n↔ {l.Title} (#{l.ListingId})\n\nBu eşleştirme doğru mu?","İlan eşleştirme",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;p.EtsyListingId=l.ListingId.ToString(CultureInfo.InvariantCulture);p.EtsyCreationAttempted=true;store.SaveProduct(p);RefreshProducts();Log("Etsy ilanı havuz ürününe bağlandı.");}
 async Task ScheduledAsync()
 {
  if(!await gate.WaitAsync(0))return;
  try{
   var due=store.Sources().Where(s=>s.Enabled&&s.AutoImport&&DateTime.UtcNow-(s.LastRunUtc??DateTime.MinValue)>=TimeSpan.FromMinutes(s.IntervalMinutes)).ToList();
   var dueJobs=new AutomationStore(dataDirectory).List().Where(j=>j.Enabled&&j.NextRunUtc<=DateTime.UtcNow).ToList();
   // Reachability/latency health (#692) checks every enabled source on its own cadence, independent of
   // AutoImport -- a source someone disabled auto-import for can still be worth watching for connectivity.
   var dueHealth=store.Sources().Where(s=>s.Enabled&&DateTimeOffset.UtcNow-(s.LastHealthCheckUtc??DateTimeOffset.MinValue)>=TimeSpan.FromMinutes(s.IntervalMinutes)).ToList();
   // XML having no due source must never skip due Stock/Price/Health/Sync automation jobs (#307): evaluate all three independently before deciding to skip the tick.
   if(due.Count==0&&dueJobs.Count==0&&dueHealth.Count==0)return;
   ModuleTabs.IsEnabled=false;
    foreach(var s in due){var runs=new XmlRunStore(dataDirectory);string run="";try{var text=await new XmlSourceReader(http).ReadAsync(s.Location,XmlAuthStore.Load(s.Id,dataDirectory),lifetime.Token);var feedHash=XmlPreviewFingerprint.FeedHash(text);run=runs.Start(s.Id,feedHash,TimeSpan.FromMinutes(10));var mappingSnapshot=await Task.Run(()=>XmlCatalog.MappingSnapshot(text,s),lifetime.Token);XmlCatalog.EnsureMappingReady(s,mappingSnapshot,scheduled:true);await UpdateFxAsync(s);var rows=await Task.Run(()=>XmlCatalog.Preview(text,s),lifetime.Token);runs.Heartbeat(run,TimeSpan.FromMinutes(10));var context=new XmlImportContext{FeedHash=feedHash,MappingShapeFingerprint=mappingSnapshot.Fingerprint,CompleteFeed=true,AllowMappingRevisionChange=false,ObservedAtUtc=DateTimeOffset.UtcNow};var result=await Task.Run(()=>store.Import(s,rows,lifetime.Token,context),lifetime.Token);runs.Complete(run,result);s.LastStatus=result.AlreadyApplied?$"Otomatik: aynı feed tekrarlandı ({result.FeedHash[..Math.Min(12,result.FeedHash.Length)]})":$"Otomatik: {result.Added} yeni / {result.Updated} güncel / {result.Unchanged} aynı";}catch(Exception e){if(run!="")try{runs.Fail(run,e.Message);}catch{}s.LastStatus=Safe(e);}s.LastRunUtc=DateTime.UtcNow;store.SaveSource(s);_ = RefreshXmlSourceHealthAsync(s);Log(s.LastStatus);}
    foreach(var s in dueHealth){if(due.Any(x=>x.Id==s.Id))continue;var health=await XmlSourceHealthChecker.CheckAsync(http,s,lifetime.Token);s.LastHealthCheckUtc=health.CheckedUtc;s.LastHealthState=health.State;s.LastHealthHttpStatus=health.HttpStatus;s.LastHealthLatencyMs=health.LatencyMs;s.LastHealthError=MarketplaceConnectionStore.Redact(health.ErrorMessage);store.SaveSource(s);if(source?.Id==s.Id)_ = RefreshXmlSourceHealthAsync(s);Log($"Kaynak sağlığı {s.Name}: {health.State}"+(health.HttpStatus.HasValue?$" (HTTP {health.HttpStatus})":"")+$" · {health.LatencyMs}ms.");}
   foreach(var job in dueJobs){try{var result=await Task.Run(()=>AutomationRunner.RunDue(store,new AutomationStore(dataDirectory),new SyncStore(dataDirectory),job.Id,DateTime.UtcNow));Log($"Otomasyon {job.Channel}/{job.Shop}: {result.Queued} iş kuyruğa alındı, {result.Errors.Count} hata.");}catch(Exception e){Log(Safe(e), NotificationSeverity.Error);}}
   // Do not replace ItemsSource or editor clones: unsaved manual edits must survive timer ticks.
   Log("Otomatik kontrol bitti. Güncel listeyi görmek için Havuzu yenile düğmesini kullan.");
  }catch(Exception e){Log(Safe(e), NotificationSeverity.Error);}finally{ModuleTabs.IsEnabled=true;gate.Release();}
 }
 async Task RunAsync(Func<Task> action){if(!await gate.WaitAsync(0)){Log("Önceki işlem sürüyor.");return;}ModuleTabs.IsEnabled=false;try{await action();}catch(Exception e){Log(Safe(e), NotificationSeverity.Error);apiStatus.Text=Safe(e);}finally{ModuleTabs.IsEnabled=true;gate.Release();}}
 static string Safe(Exception e)=>SqliteBusyDiagnostics.IsBusyOrLocked(e)?SqliteBusyDiagnostics.Describe(e):e is InvalidOperationException or ArgumentException?e.Message:"İşlem tamamlanamadı. Dosya biçimini, erişim izinlerini ve bağlantıyı kontrol et.";
 void Log(string text){StatusText.Text=text;var line=$"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text.Replace('\r',' ').Replace('\n',' ')}";logs.Insert(0,line);while(logs.Count>200)logs.RemoveAt(logs.Count-1);try{Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);File.AppendAllText(logPath,line+Environment.NewLine);new AuditStore(dataDirectory).Append(new(){Module="UI",Action="log",Outcome="Info",Detail=text});}catch(IOException){}catch(Exception){ } }
 protected override void OnClosing(System.ComponentModel.CancelEventArgs e){if(!ResolveProductEdit()){e.Cancel=true;base.OnClosing(e);return;}base.OnClosing(e);if(e.Cancel)return;lifetime.Cancel();globalSearchCts?.Cancel();excelCts?.Cancel();}
 protected override void OnClosed(EventArgs e){timer.Stop();searchTimer.Stop();globalSearchTimer.Stop();globalSearchCts?.Dispose();excelCts?.Dispose();excelCoordinator.Dispose();startupRecovery.Complete();http.Dispose();base.OnClosed(e);}
}
