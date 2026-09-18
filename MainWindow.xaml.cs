using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;
public sealed class MappingEntry { public string Key {get;set;}=""; public string Label {get;set;}=""; public string Path {get;set;}=""; }
public sealed record PriceChoice(string Value,string Label);
public partial class MainWindow : Window
{
 readonly CatalogStore store;
 readonly HttpClient http=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60)};
 readonly SemaphoreSlim gate=new(1,1);
 readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMinutes(1)};
 readonly AsyncSingleFlight<EtsyCredentials> authFlight=new();
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
 readonly DataGrid sources=new(){SelectionMode=DataGridSelectionMode.Single};
 readonly ListBox paths=new();
 readonly ComboBox itemPath=new(){IsEditable=true,IsTextSearchEnabled=true};
 readonly TextBox search=new(){Width=300}, xmlUser=new(), shopId=new(), redirect=new(), callback=new(){Height=70,TextWrapping=TextWrapping.Wrap};
 readonly PasswordBox xmlPassword=new(), apiKey=new(), apiSecret=new(), apiToken=new(), refreshToken=new();
 readonly ComboBox decimalSeparator=new(){ItemsSource=new[]{".",","},SelectedIndex=0}, stockDecimalSeparator=new(){ItemsSource=new[]{".",","},SelectedIndex=0}, listingState=new(){ItemsSource=new[]{"active","draft","inactive","sold_out","expired"},SelectedIndex=0,Width=140};
 readonly StackPanel sourceGeneral=new(),sourceRules=new(),productEditor=new(),templateEditor=new();
 readonly TextBlock previewStatus=Hint("XML'i oku → eşleştir → önizle → seçili ürünleri havuza al."), xmlMappingSummary=Hint(""), apiStatus=Hint("Bağlantı henüz doğrulanmadı."), draftStatus=Hint("Ürün havuzundan bir ürün seç."),listingStatus=Hint(""), productChannelSummary=Hint("Ürün seçince kanal planları burada görünür."), xmlSourceHealth=Hint("Kaynak seçince sağlık, ürün ve son çalışma özeti görünür.");
 readonly ObservableCollection<string> xmlPaths=new();
 readonly TextBox sampleCost=new(){Text="100",Width=130};
 readonly TextBlock calculationStatus=Hint("Alış fiyatını girip hesaplamayı test edebilirsin."),fxStatus=Hint("Kur henüz alınmadı.");
 XmlSource? source; CatalogProduct? edit; EtsyListingTemplate template=new(); EtsyCredentials credentials=new("","","",""); OAuthAttempt? attempt; Func<Task>? beforeScheduledImportHook=null;
 List<MappingEntry> mappings=[]; string xml="",loadedLocation="",previewRevision=""; int listingOffset,listingTotal,productOffset,productTotal,searchRevision,globalSearchRevision; string loadedListingShop="",loadedListingState="";
 public MainWindow():this(null){}
 public MainWindow(string? directory)
 {
  dataDirectory=directory;startupRecovery=new StartupRecovery(directory);store=new CatalogStore(directory);globalSearchIndex=new GlobalSearchIndexService(directory);logPath=Path.Combine(directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop"),"operations.log");
  InitializeComponent();uiPreferences=new UiPreferenceStore(directory);PetshopTedarikXmlSource.Ensure(store);Language=System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
  PreviewKeyDown += MainWindow_PreviewKeyDown;
  GlobalSearchBox.KeyDown += GlobalSearchBox_KeyDown;
  GlobalSearchBox.TextChanged += GlobalSearchBox_TextChanged;
  LogList.ItemsSource=logs;
  try{if(File.Exists(logPath))foreach(var line in File.ReadLines(logPath).TakeLast(100))logs.Insert(0,line);}catch(IOException){}
  BuildProducts();BuildSources();BuildApi();BuildListings();BuildTemplate();BuildNavigation();
  try{template=TemplateStore.Load(directory);templateEditor.DataContext=template;var saved=directory==null?CredentialStore.Load():null;if(saved!=null)SetCredentials(saved);}catch(Exception e){Log(Safe(e));}
  RefreshSources();RefreshProducts();timer.Tick+=async(_,_)=>await ScheduledAsync();timer.Start();searchTimer.Tick+=async(_,_)=>{searchTimer.Stop();await SearchProductsAsync();};globalSearchTimer.Tick+=SearchTimer_Tick;_ = WarmGlobalSearchAsync();
  Log("Global masaüstü hazır. XML otomasyonu yalnız program açıkken çalışır.");
  if (startupRecovery.State.UncleanExit) Log("Önceki çalışma normal kapanmamış; yerel recovery kontrolleri uygulandı.");
 }
 void GlobalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
 {
  globalSearchTimer.Stop();
  if (GlobalSearchBox.Text.Trim().Length >= 2) globalSearchTimer.Start();
 }
 static TextBlock Hint(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(87,112,125)),Margin=new Thickness(4,8,4,8)};
 static TextBlock Heading(string text)=>new(){Text=text,FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(4,8,4,12)};
 Button Button(string text,Action action){var b=new Button{Content=text};b.Click+=(_,_)=>{try{action();}catch(Exception e){Log(Safe(e));}};return b;}
 Button AsyncButton(string text,Func<Task> action)=>Button(text,()=>_=RunAsync(action));
 static void Label(Panel panel,string text,UIElement control){panel.Children.Add(new TextBlock{Text=text,Margin=new Thickness(4,7,4,0)});panel.Children.Add(control);}
 static TextBox Field(Panel panel,string label,string property,int height=0)
 {var box=new TextBox();if(height>0){box.Height=height;box.AcceptsReturn=true;box.TextWrapping=TextWrapping.Wrap;box.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;}box.SetBinding(TextBox.TextProperty,new Binding(property){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,ValidatesOnExceptions=true});Label(panel,label,box);return box;}
 static void Flag(Panel panel,string label,string property){var c=new CheckBox{Content=label};c.SetBinding(CheckBox.IsCheckedProperty,new Binding(property){Mode=BindingMode.TwoWay});panel.Children.Add(c);}
 static ScrollViewer Scroll(UIElement content)=>new(){Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(10)};
 static void Column(DataGrid grid,string label,string property,double width=120){var binding=new Binding(property);if(property is "Price" or "Cost" or "FormulaPriceTry")binding.StringFormat="N2";else if(property=="AppliedTryRate")binding.StringFormat="N4";grid.Columns.Add(new DataGridTextColumn{Header=label,Binding=binding,Width=width});}
 void Tab(string name,UIElement content)=>builtPages.Add(name,content);
 void AddManualProductForm(Panel bar)
 {
  var sku=new TextBox{Width=110,ToolTip="SKU (barkod boşsa zorunlu)"};var barcode=new TextBox{Width=110,ToolTip="Barkod (SKU boşsa zorunlu)"};var newName=new TextBox{Width=160,ToolTip="Ürün adı"};var price=new TextBox{Width=70,ToolTip="Satış fiyatı"};var currency=new TextBox{Text="USD",Width=45};var stock=new TextBox{Text="0",Width=50,ToolTip="Stok"};
  var add=Button("+ Yeni ürün",()=>{
   if(!decimal.TryParse(price.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out var priceValue))throw new InvalidOperationException("Fiyat geçerli bir sayı olmalı.");
   if(!int.TryParse(stock.Text,NumberStyles.Integer,CultureInfo.InvariantCulture,out var stockValue))throw new InvalidOperationException("Stok geçerli bir tam sayı olmalı.");
   var collision=store.PreviewIdentityCollision(sku.Text,barcode.Text);
   if(collision!=null&&MessageBox.Show(this,$"Bu {(collision.Field=="Sku"?"SKU":"barkod")} zaten '{collision.ExistingSku}' / '{collision.ExistingBarcode}' kayıtlı üründe kullanılıyor (büyük/küçük harf ve boşluk farkı gözetmeksizin).\n\nYine de farklı bir SKU/barkod ile devam etmek için iptal edip düzeltin.","SKU/barkod çakışması",MessageBoxButton.OK,MessageBoxImage.Warning)==MessageBoxResult.OK)return;
   var created=store.CreateManual(new CatalogProduct{Sku=sku.Text,Barcode=barcode.Text,Name=newName.Text,Price=priceValue,Currency=currency.Text.Trim().ToUpperInvariant(),Stock=stockValue});
   sku.Clear();barcode.Clear();newName.Clear();price.Clear();stock.Text="0";
   RefreshProducts();Log($"Yeni ürün eklendi: {created.Sku}{(created.Barcode.Length>0?" / "+created.Barcode:"")} · {created.Name}");
  });
  foreach(var pair in new (string Label,Control Control)[]{("SKU",sku),("Barkod",barcode),("Ad",newName),("Fiyat",price),("Döviz",currency),("Stok",stock)}){bar.Children.Add(new TextBlock{Text=pair.Label,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,4,2,4)});bar.Children.Add(pair.Control);}
  bar.Children.Add(add);
 }
 static Grid Split(UIElement left,UIElement right,double rightWidth)
 {var g=new Grid{Margin=new Thickness(10)};g.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});g.ColumnDefinitions.Add(new(){Width=new GridLength(rightWidth==350?330:1,rightWidth==350?GridUnitType.Pixel:GridUnitType.Star)});g.Children.Add(left);Grid.SetColumn(right,1);g.Children.Add(right);return g;}
 static DockPanel Dock(UIElement top,UIElement body){var d=new DockPanel();DockPanel.SetDock(top,System.Windows.Controls.Dock.Top);d.Children.Add(top);d.Children.Add(body);return d;}
 void BuildProducts()
 {
  var bar=BuildProductListHeader();
  foreach(var x in new[]{("Durum","StatusLabel",65d),("Stok kodu / SKU","Sku",135d),("Ürün","Name",200d),("Alış fiyatı","Cost",90d),("Alış döviz","CostCurrency",65d),("Satış fiyatı","Price",90d),("Satış döviz","Currency",65d),("KDV %","VatRate",60d),("Stok","Stock",60d),("Formül TL","FormulaPriceTry",95d),("1 döviz/TL","AppliedTryRate",95d),("Barkod","Barcode",140d),("GTIN","Gtin",140d),("Marka","Brand",120d),("Kategori","Category",150d),("Açıklama","Description",240d),("Etsy ilan ID","EtsyListingId",110d),("XML kaynağı","SourceId",125d),("Son güncelleme","UpdatedUtc",155d)})Column(products,x.Item1,x.Item2,x.Item3);
  Column(products,"Son veri kaynağı","SourceKind",110);Column(products,"Fiyat kaynağı","PriceSource",100);Column(products,"Stok kaynağı","StockSource",100);Column(products,"Medya kaynağı","MediaSource",100);
  products.SelectionMode=DataGridSelectionMode.Extended;products.EnableRowVirtualization=true;products.EnableColumnVirtualization=false;VirtualizingPanel.SetIsVirtualizing(products,true);VirtualizingPanel.SetVirtualizationMode(products,VirtualizationMode.Recycling);products.SelectionChanged+=(_,_)=>{edit=products.SelectedItem is CatalogProduct p?Clone(p):null;productEditor.DataContext=edit;productEditor.IsEnabled=edit!=null;ShowProductChannelStatus(edit);};
  productEditor.Children.Add(Heading("Ürün kartı"));Field(productEditor,"Alış fiyatı","Cost").IsReadOnly=true;Field(productEditor,"Alış para birimi","CostCurrency").IsReadOnly=true;Field(productEditor,"Satış fiyatı","Price");Field(productEditor,"Satış para birimi","Currency").IsReadOnly=true;Field(productEditor,"KDV oranı (%)","VatRate");productEditor.Children.Add(Hint("XML güncellemesinde korunmasını istediğin alanı kilitle."));
  Field(productEditor,"Ürün stok kodu / SKU","Sku").IsReadOnly=true;Field(productEditor,"Barkod","Barcode").IsReadOnly=true;Field(productEditor,"GTIN","Gtin").IsReadOnly=true;Field(productEditor,"Marka","Brand");Field(productEditor,"Kategori","Category");Field(productEditor,"Başlık","Name");Flag(productEditor,"Başlığı kilitle","LockName");Field(productEditor,"Açıklama","Description",90);Flag(productEditor,"Açıklamayı kilitle","LockDescription");Flag(productEditor,"Fiyatı kilitle","LockPrice");Field(productEditor,"Stok","Stock");Flag(productEditor,"Stoğu kilitle","LockStock");Field(productEditor,"Görsel URL'leri","ImageUrls",65);Flag(productEditor,"Görselleri kilitle","LockImages");
  productEditor.Children.Add(Heading("Operasyon bilgileri"));
  Field(productEditor,"Üretici parça kodu / MPN","Mpn").MaxLength=128;
  Field(productEditor,"Faturada kullanılacak ürün adı","InvoiceName").MaxLength=300;
  Field(productEditor,"Alt başlık","Subtitle").MaxLength=300;
  Field(productEditor,"Raf / konum","Shelf").MaxLength=100;
  var expires=new DatePicker();expires.SetBinding(DatePicker.SelectedDateProperty,new Binding("ExpiresOn"){Mode=BindingMode.TwoWay,ValidatesOnExceptions=true});Label(productEditor,"Son kullanma tarihi (isteğe bağlı)",expires);
  productEditor.Children.Add(Button("Tarihi temizle",()=>expires.SelectedDate=null));
  productEditor.Children.Add(Hint("Operasyon bilgileri XML yenilemesinde korunur. Fatura adı yerel kayıttır; fatura entegrasyonuna otomatik gönderilmez."));
  productEditor.Children.Add(Button("Ürünü ve kilitleri kaydet",()=>{ValidBindings(productEditor);if(edit==null)return;store.SaveProduct(edit);RefreshProducts();Log("Ürün ve alan kilitleri kaydedildi.");}));productEditor.IsEnabled=false;
  Column(products,"Ürün ID","ProductIdLabel",85);products.Columns[^1].DisplayIndex=0;Column(products,"Marka ID","BrandIdLabel",85);Column(products,"Kategori ID","CategoryIdLabel",85);Column(products,"Desi","XmlAttributes[Desi]",75);Column(products,"Raf","Shelf",90);Column(products,"MPN","Mpn",110);
  products.MouseDoubleClick+=(_,e)=>{if(e.OriginalSource is DependencyObject origin&&FindParent<DataGridRow>(origin)!=null)OpenSelectedProductCard();};
  ConfigureProductGrid();
  Tab("Ürün havuzu",Dock(bar,BuildProductTable()));
 }
 TabControl BuildProductWorkspace()
 {
  var tabs=new TabControl();
  tabs.Items.Add(new TabItem{Header="Genel",Content=Scroll(productEditor)});
  tabs.Items.Add(new TabItem{Header="Görseller / açıklama",Content=Scroll(ProductReadOnlyFields(("Açıklama","Description"),("Görsel URL'leri","ImageUrls")))});
  tabs.Items.Add(new TabItem{Header="Pazaryerleri",Content=Scroll(new StackPanel{Children={Heading("Kanal ve mağaza bağları"),productChannelSummary,Hint("Eşleştirme ve ilan durumları yerel kanal planlarından okunur; canlı write bu sekmeden başlatılmaz.")}})});
  tabs.Items.Add(new TabItem{Header="XML / provenance",Content=Scroll(ProductReadOnlyFields(("Kaynak kimliği","SourceId"),("Kaynak türü","SourceKind"),("Fiyat kaynağı","PriceSource"),("Stok kaynağı","StockSource"),("Medya kaynağı","MediaSource")))});
  tabs.Items.Add(new TabItem{Header="Sipariş raporu",Content=Scroll(new StackPanel{Children={Heading("Ürün sipariş raporu"),Hint("Ürün seçildiğinde sipariş ve stok hareketleri ilgili operasyon merkezlerinden güvenli şekilde izlenir; bu sekme canlı marketplace çağrısı yapmaz."),ProductReadOnlyFields(("SKU","Sku"),("Ürün adı","Name"))}})});
  return tabs;
 }
 static StackPanel ProductReadOnlyFields(params (string Label,string Property)[] fields)
 {
  var panel=new StackPanel(); foreach(var field in fields){panel.Children.Add(new TextBlock{Text=field.Label,Margin=new Thickness(4,7,4,0)});var box=new TextBox{IsReadOnly=true,MinHeight=28,TextWrapping=TextWrapping.Wrap};box.SetBinding(TextBox.TextProperty,new Binding(field.Property));panel.Children.Add(box);} return panel;
 }
 void ShowProductChannelStatus(CatalogProduct? product){if(product==null){productChannelSummary.Text="Ürün seçince kanal planları burada görünür.";return;}var rows=MarketplaceProductPanelModel.Build(product.Id,dataDirectory).Select(x=>$"{x.Channel} / {x.ShopId}: {x.Status} · {x.Readiness}"+(string.IsNullOrWhiteSpace(x.MappingId)?"":$" · eşleme {x.MappingId}"));productChannelSummary.Text=string.Join("\n",rows);}
 void SetProductActive(bool active){var selected=products.SelectedItems.OfType<CatalogProduct>().ToList();if(selected.Count==0&&edit!=null)selected.Add(edit);if(selected.Count==0)throw new InvalidOperationException("Önce ürün seç.");ValidBindings(productEditor);foreach(var row in selected){var copy=Clone(row);copy.Active=active;store.SaveProduct(copy);}RefreshProducts();Log($"{selected.Count} ürün yerel havuzda {(active?"aktif":"pasif")} yapıldı. Canlı ilan durumu değiştirilmedi.");}
 void DeleteSelectedProduct(){if(edit==null)throw new InvalidOperationException("Önce ürün seç.");if(MessageBox.Show(this,edit.Name+"\n\nÜrün, medya kayıtları ve bu uygulamanın ürüne özel görsel kopyaları silinsin mi? Orijinal dosyalar korunur. XML içinde varsa sonraki alımda yeniden gelir. Etsy ilanı silinmez.","Ürünü sil",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;store.DeleteProduct(edit);RefreshProducts();Log("Ürün yerel havuzdan silindi.");}
 void BuildSources() => BuildXmlWorkspace();
 void BuildPricingEditor()
 {
  var mode=new ComboBox{ItemsSource=new[]{new PriceChoice("Formula","Formül → TL satış → döviz"),new PriceChoice("Simple","Basit kâr / çarpan (önceki model)")},DisplayMemberPath="Label",SelectedValuePath="Value"};
  mode.SetBinding(ComboBox.SelectedValueProperty,new Binding("PriceMode"){Mode=BindingMode.TwoWay});Label(sourceRules,"Fiyatlandırma modeli",mode);
  var shared=new System.Windows.Controls.Primitives.UniformGrid{Columns=3};foreach(var f in new[]{("Alış dövizi (formülde TRY)","CostCurrency"),("Hedef satış dövizi (USD / EUR…)","Currency"),("Minimum satış (hedef döviz)","MinimumPrice")}){var p=new StackPanel();Field(p,f.Item1,f.Item2);shared.Children.Add(p);}sourceRules.Children.Add(shared);
  var formulaPanel=new StackPanel();formulaPanel.Children.Add(Hint("x = XML'deki TL alış fiyatı. Önce formül TL satış fiyatını üretir; sonra 1 dövizin TL karşılığına bölünür. Basit modeldeki kâr/sabit tutar ayrıca eklenmez."));
  var box=Field(formulaPanel,"Satış fiyatı formülü (CASE WHEN veya x * 1.40 + 100)","Formula",135);box.FontFamily=new FontFamily("Consolas");box.FontSize=13;
  formulaPanel.Children.Add(Button("Gönderdiğim CASE WHEN formülünü yükle",()=>{if(source==null)return;source.Formula=PriceFormula.Example;BindSource();previewRevision="";}));
  var auto=new CheckBox{Content="TCMB kurunu otomatik al (önizleme ve zamanlı XML güncellemesinde)"};auto.SetBinding(CheckBox.IsCheckedProperty,new Binding("AutoFx"){Mode=BindingMode.TwoWay});formulaPanel.Children.Add(auto);
  var fxGrid=new System.Windows.Controls.Primitives.UniformGrid{Columns=2};var ratePanel=new StackPanel();var rateBox=Field(ratePanel,"1 hedef döviz kaç TL? (otomatik veya manuel)","TryPerTargetUnit");rateBox.SetBinding(TextBox.IsReadOnlyProperty,new Binding("IsChecked"){Source=auto});fxGrid.Children.Add(ratePanel);
  var kindPanel=new StackPanel();var kind=new ComboBox{ItemsSource=new[]{new PriceChoice("ForexSelling","TCMB döviz satış"),new PriceChoice("ForexBuying","TCMB döviz alış")},DisplayMemberPath="Label",SelectedValuePath="Value"};kind.SetBinding(ComboBox.SelectedValueProperty,new Binding("FxKind"){Mode=BindingMode.TwoWay});Label(kindPanel,"Kur türü",kind);fxGrid.Children.Add(kindPanel);formulaPanel.Children.Add(fxGrid);
  formulaPanel.Children.Add(fxStatus);formulaPanel.Children.Add(AsyncButton("Kuru şimdi güncelle",async()=>{var s=CurrentSource();if(s.PriceMode!="Formula")return;s.AutoFx=true;await UpdateFxAsync(s);BindSource();previewRevision="";}));
  var testPanel=new WrapPanel();testPanel.Children.Add(new TextBlock{Text="Örnek alış TL (x)",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4)});testPanel.Children.Add(sampleCost);testPanel.Children.Add(AsyncButton("Formülü ve kuru test et",TestFormulaAsync));formulaPanel.Children.Add(testPanel);formulaPanel.Children.Add(calculationStatus);
  var simplePanel=new StackPanel();simplePanel.Children.Add(Hint("Önceki hesap: maliyet × çarpan × (1 + kâr / 100) + sabit. Bu mod otomatik kur almaz."));var simpleGrid=new System.Windows.Controls.Primitives.UniformGrid{Columns=3};foreach(var f in new[]{("Döviz çarpanı","ExchangeRate"),("Kâr (%)","MarkupPercent"),("Sabit tutar (hedef döviz)","FixedAmount")}){var p=new StackPanel();Field(p,f.Item1,f.Item2);simpleGrid.Children.Add(p);}simplePanel.Children.Add(simpleGrid);
  mode.SelectionChanged+=(_,_)=>{var formula=mode.SelectedValue?.ToString()=="Formula";formulaPanel.Visibility=formula?Visibility.Visible:Visibility.Collapsed;simplePanel.Visibility=formula?Visibility.Collapsed:Visibility.Visible;};sourceRules.Children.Add(formulaPanel);sourceRules.Children.Add(simplePanel);
 }
 string RateDescription(XmlSource s)=>s.Currency=="TRY"?"Hedef TRY: döviz dönüşümü yok.":s.AutoFx?$"TCMB {(s.FxKind=="ForexSelling"?"döviz satış":"döviz alış")} • yayın tarihi {s.FxRateDate:dd.MM.yyyy} • çekildi {s.FxFetchedUtc.GetValueOrDefault().LocalDateTime:dd.MM.yyyy HH:mm} • 1 {s.Currency} = {s.TryPerTargetUnit:N4} TL":$"Manuel kur (provider yok) • 1 {s.Currency} = {s.TryPerTargetUnit:N4} TL";
 async Task UpdateFxAsync(XmlSource s)
 {
  if(s.PriceMode!="Formula")return;
  if(!s.AutoFx||s.Currency=="TRY"){fxStatus.Text=RateDescription(s);return;}
  try{var revision=CatalogStore.SourceConfigRevision(s);var quote=await new TcmbRates(http).FetchAsync(s.Currency,s.FxKind,lifetime.Token);if(!store.TryRecordAutoFxQuote(s.Id,revision,quote))throw new InvalidOperationException("XML kaynağı silinmiş, devre dışı bırakılmış veya ayarları değişmiş; önizlemeyi yeniden hesaplayın.");s.TryPerTargetUnit=quote.TryPerUnit;s.FxRateDate=quote.RateDate;s.FxFetchedUtc=quote.FetchedUtc;fxStatus.Text=RateDescription(s);Log(fxStatus.Text);}
  catch(InvalidDataException e){throw new InvalidOperationException(e.Message+" Fiyatlar güncellenmedi.");}
  catch(Exception e) when(e is HttpRequestException or OperationCanceledException){throw new InvalidOperationException("TCMB kuru alınamadı. Fiyatlar güncellenmedi; bağlantıyı kontrol edin veya otomatik kuru kapatıp manuel TL karşılığı girin.");}
 }
 async Task TestFormulaAsync()
 {
  var s=CurrentSource();if(!decimal.TryParse(sampleCost.Text,NumberStyles.AllowDecimalPoint,CultureInfo.CurrentCulture,out var cost)&&!decimal.TryParse(sampleCost.Text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out cost))throw new InvalidOperationException("Örnek alış fiyatı geçerli bir sayı olmalı.");
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
  source=s;BindSource();fxStatus.Text=RateDescription(s);calculationStatus.Text="Alış fiyatını girip hesaplamayı test edebilirsin.";itemPath.Text=s.ItemPath;decimalSeparator.SelectedItem=s.DecimalSeparator;stockDecimalSeparator.SelectedItem=s.StockDecimalSeparator;_ = RefreshXmlSourceHealthAsync(s);
  mappings=XmlFieldDefinitions.All.Concat(s.Fields.Keys.Where(key=>!XmlFieldDefinitions.All.Any(d=>d.Key==key)).Select(key=>new XmlFieldDefinition(key,key))).Select(d=>new MappingEntry{Key=d.Key,Label=d.Label,Path=s.Fields.GetValueOrDefault(d.Key,"")}).ToList();ResetXmlSamples();mapping.ItemsSource=mappings;xmlMappingSummary.Text=string.Join("\n",mappings.Select(m=>$"{m.Label}: {(string.IsNullOrWhiteSpace(m.Path)?"eşlenmemiş":m.Path)}"));xml="";loadedLocation="";previewRevision="";var imported=store.Products().Where(p=>p.SourceId==s.Id).OrderBy(p=>p.Name).Take(200).ToList();preview.ItemsSource=imported;previewStatus.Text=imported.Count==0?"Henüz bu kaynaktan ürün havuza alınmadı. XML'i oku → önizleme hesapla ile devam et.":$"Havuzdaki son aktarım: {store.Products().Count(p=>p.SourceId==s.Id):N0} ürün. İlk {imported.Count:N0} satır gösteriliyor; güncel XML için Önizleme hesapla'yı kullan.";paths.ItemsSource=null;xmlPaths.Clear();xmlPaths.Add("");if(string.Equals(s.Location,PetshopTedarikXmlSource.Location,StringComparison.OrdinalIgnoreCase))foreach(var path in PetshopTedarikXmlSource.AvailableFields)xmlPaths.Add(path);foreach(var path in mappings.SelectMany(m=>m.Path.Split("|",StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase))if(!xmlPaths.Contains(path))xmlPaths.Add(path);
  RebuildCompactMappings();RefreshCategoryRows();
  try{var auth=XmlAuthStore.Load(s.Id,dataDirectory);xmlUser.Text=auth.User;xmlPassword.Password=auth.Password;}catch(Exception){xmlUser.Clear();xmlPassword.Clear();Log("XML şifresi açılamadı; yeniden kaydet.");}
 }
 async Task RefreshXmlSourceHealthAsync(XmlSource candidate)
 {
  var id=candidate.Id;
  try
  {
   var summary=await Task.Run(()=>{var count=store.Products().Count(p=>p.SourceId==id);var run=new XmlRunStore(dataDirectory).List(id,1).FirstOrDefault();return (count,run);});
   if(source?.Id!=id)return;
   var runText=summary.run==null?"Henüz çalışmadı":$"{summary.run.Status} · {summary.run.StartedUtc.ToLocalTime():g} · +{summary.run.Added}/~{summary.run.Updated}/={summary.run.Unchanged}";
   xmlSourceHealth.Text=$"Kaynak özeti: {summary.count:N0} ürün · son çalışma {runText}";
  }
  catch(Exception error){if(source?.Id==id)xmlSourceHealth.Text="Kaynak özeti okunamadı: "+MarketplaceConnectionStore.Redact(error.Message);}
 }
 void CloneCurrentSource()
 {
  var current=CurrentSource();var clone=Clone(current);clone.Id=Guid.NewGuid().ToString("N");clone.Name=(string.IsNullOrWhiteSpace(current.Name)?"XML kaynağı":current.Name)+" kopya";clone.AutoImport=false;clone.LastRunUtc=null;clone.LastStatus="Kopyalandı; adres ve yetkilendirme doğrulanmalı.";store.SaveSource(clone);XmlAuthStore.Save(clone.Id,new(),dataDirectory);RefreshSources(false);sources.SelectedItem=clone;SetSource(clone);Log("XML kaynağı eşleme ve kurallarıyla çoğaltıldı; gizli yetkilendirme kopyalanmadı.");
 }
 async Task CheckXmlSourceAsync()
 {
  if(source==null)throw new InvalidOperationException("Önce XML kaynağı seçin.");
  var candidate=source;var location=candidate.Location.Trim();if(location.Length==0)throw new InvalidOperationException("Sağlık kontrolü için XML adresi veya dosyası gerekli.");
  xmlSourceHealth.Text="Kaynak erişilebilirliği, HTTP durumu, timeout ve gzip yanıtı kontrol ediliyor…";
  try
  {
   var text=await new XmlSourceReader(http).ReadAsync(location,new(xmlUser.Text,xmlPassword.Password),lifetime.Token);
   var scan=await Task.Run(()=>XmlCatalog.Inspect(text,string.IsNullOrWhiteSpace(candidate.ItemPath)?null:candidate.ItemPath),lifetime.Token);
   var sampleText=scan.Sample.Count==0?"":" · örnek: "+string.Join("; ",scan.Sample[0].Select(x=>$"{x.Key}={x.Value}"));
   xmlSourceHealth.Text=$"Sağlıklı · {text.Length:N0} karakter · {scan.MatchCount:N0} ürün eşleşti · {scan.Paths.Count:N0} alan yolu · 25 MB sınırı içinde · gzip/HTTPS kontrolü geçti.{sampleText}";
  }
  catch(Exception error){xmlSourceHealth.Text="Sağlık kontrolü başarısız: "+MarketplaceConnectionStore.Redact(error.Message);throw;}
 }
 XmlSource CurrentSource(){CommitCategoryEdits();if(source==null)throw new InvalidOperationException("Önce XML kaynağı seç veya ekle.");ValidBindings(sourceGeneral);ValidBindings(sourceRules);mapping.CommitEdit(DataGridEditingUnit.Cell,true);mapping.CommitEdit(DataGridEditingUnit.Row,true);source.ItemPath=itemPath.Text.Trim();source.DecimalSeparator=decimalSeparator.SelectedItem?.ToString()??".";source.StockDecimalSeparator=stockDecimalSeparator.SelectedItem?.ToString()??".";source.Fields=mappings.Where(m=>!string.IsNullOrWhiteSpace(m.Path)).ToDictionary(m=>m.Key,m=>m.Path.Trim());source.Currency=source.Currency.Trim().ToUpperInvariant();source.CostCurrency=source.CostCurrency.Trim().ToUpperInvariant();XmlCatalog.ValidateSource(source);return source;}
 void SaveSource(){var s=CurrentSource();if(string.IsNullOrWhiteSpace(s.Name)||string.IsNullOrWhiteSpace(s.Location))throw new InvalidOperationException("Kaynak adı ve XML adresi/dosyası gerekli.");var latest=store.Sources().SingleOrDefault(x=>x.Id==s.Id);if(latest!=null){s.LastRunUtc=latest.LastRunUtc;s.LastStatus=latest.LastStatus;}XmlAuthStore.Save(s.Id,new(xmlUser.Text,xmlPassword.Password),dataDirectory);store.SaveSource(s);RefreshSources(false);_ = RefreshXmlSourceHealthAsync(s);Log("XML kaynağı, eşleştirme ve kurallar kaydedildi.");}
 void RefreshSources(bool choose=true){sources.ItemsSource=store.Sources();if(choose&&source==null&&sources.Items.Count>0)sources.SelectedIndex=0;}
 async Task InspectAsync(){var s=CurrentSource();xml="";loadedLocation="";previewRevision="";preview.ItemsSource=null;xml=await new XmlSourceReader(http).ReadAsync(s.Location,new(xmlUser.Text,xmlPassword.Password),lifetime.Token);loadedLocation=s.Location;var selectedPaths=mappings.ToDictionary(m=>m.Key,m=>m.Path);var scan=await Task.Run(()=>XmlCatalog.Inspect(xml,string.IsNullOrWhiteSpace(s.ItemPath)?null:s.ItemPath));itemPath.Text=scan.ItemPath;paths.ItemsSource=scan.Paths;xmlPaths.Clear();xmlPaths.Add("");foreach(var path in scan.Paths)xmlPaths.Add(path);foreach(var m in mappings)m.Path=selectedPaths.GetValueOrDefault(m.Key,"");if(!mappings.Any(m=>!string.IsNullOrWhiteSpace(m.Path)))foreach(var m in mappings)if(scan.SuggestedFields.TryGetValue(m.Key,out var value))m.Path=value;mapping.Items.Refresh();RebuildCompactMappings();itemPaths.Clear();foreach(var path in XmlCatalog.ItemPaths(xml))itemPaths.Add(path);itemPath.Text=scan.ItemPath;previewRevision="";preview.ItemsSource=null;RefreshXmlSamples();Log($"XML okundu; {scan.Paths.Count} alan yolu bulundu.");}
 async Task PreviewAsync(){var s=CurrentSource();if(xml==""||loadedLocation!=s.Location)throw new InvalidOperationException("Bu kaynak adresi için önce XML'i oku.");previewRevision="";preview.ItemsSource=null;previewStatus.Text="Fiyat ve kur hesaplanıyor…";await UpdateFxAsync(s);BindSource();var snapshot=Clone(s);var rows=await Task.Run(()=>XmlCatalog.Preview(xml,snapshot,store));preview.ItemsSource=rows;RefreshCategoryRows(rows);previewRevision=CatalogStore.SourceConfigRevision(s);previewStatus.Text=$"{rows.Count} ürün • Fiyat/stok hesaplandı. Kaydetmeden önce satırları seç. Etsy'ye gönderim yapılmaz.";Log($"XML önizlemesi: {rows.Count} ürün.");}
 async Task ImportAsync(){var s=CurrentSource();if(previewRevision==""||previewRevision!=CatalogStore.SourceConfigRevision(s))throw new InvalidOperationException("Ayarlar değişti veya önizleme yok. Önizlemeyi yeniden hesapla.");CatalogPricing.ValidateRate(s);var rows=preview.SelectedItems.Cast<CatalogProduct>().Select(Clone).ToList();if(rows.Count==0)throw new InvalidOperationException("Önizlemeden en az bir ürün seç.");var runs=new XmlRunStore(dataDirectory);var run=runs.Start(s.Id);try{var result=await Task.Run(()=>store.ImportIfSourceCurrent(s,previewRevision,rows));runs.Complete(run,result);XmlAuthStore.Save(s.Id,new(xmlUser.Text,xmlPassword.Password),dataDirectory);s.LastRunUtc=DateTime.UtcNow;s.LastStatus=$"{result.Added} yeni / {result.Updated} güncel / {result.Unchanged} aynı";if(!store.TryRecordSourceRun(s.Id,s.LastRunUtc.Value,s.LastStatus))Log("XML kaynağı silindi veya devre dışı bırakıldı; eski çalışma durumu kaydedilmedi.");previewRevision="";RefreshSources(false);_ = RefreshXmlSourceHealthAsync(s);RefreshProducts();Log(s.LastStatus);Navigate("products");}catch(Exception e){runs.Fail(run,e.Message);throw;}}
 void ShowProducts(CatalogPage page){var pageText=productPageSize==0?$"Tüm ürünler • {page.Items.Count:N0} satır":$"Sayfa {productOffset/productPageSize+1} / {Math.Max(1,(page.Total+productPageSize-1)/productPageSize)}";productPageLabel.Text=$"{pageText} • {page.Total:N0} ürün";var id=edit?.Id;productTotal=page.Total;products.ItemsSource=page.Items;if(id!=null)products.SelectedItem=page.Items.FirstOrDefault(p=>p.Id==id);var corruptCount=store.CorruptProducts().Count;var corruptSuffix=corruptCount>0?$"   •   ⚠ {corruptCount:N0} bozuk kayıt (incelenmeli)":"";SummaryText.Text=$"{page.Total:N0} sonuç   •   {page.InStock:N0} stokta   •   {page.Linked:N0} Etsy ile eşleşen   •   {pageText}{corruptSuffix}";}
 void RefreshProducts(){QueueProductAssets();globalSearchIndex.Invalidate();searchRevision++;ShowProducts(LoadProductPage(search.Text.Trim(),productOffset,productFilter));}
 async Task SearchProductsAsync(){var revision=++searchRevision;var q=search.Text.Trim();var offset=productOffset;var filter=productFilter;try{var page=await Task.Run(()=>LoadProductPage(q,offset,filter));if(revision==searchRevision)ShowProducts(page);}catch(Exception e){Log(Safe(e));}}
 void Refresh_Click(object sender,RoutedEventArgs e){try{RefreshProducts();}catch(Exception ex){Log(Safe(ex));}}
 EtsyCredentials ReadCredentials(){var key=apiKey.Password.Trim();var secret=apiSecret.Password.Trim();var token=apiToken.Password.Trim();var changedApp=key!=credentials.Key||secret!=credentials.Secret;var changedToken=token!=credentials.Token;var refresh=refreshToken.Password.Trim();return credentials with{Key=key,Secret=secret,Token=token,RefreshToken=(changedApp||changedToken)&&refresh==credentials.RefreshToken?"":refresh,ExpiresAt=changedApp||changedToken?null:credentials.ExpiresAt,ShopId=shopId.Text.Trim(),RedirectUri=redirect.Text.Trim()};}
 void SetCredentials(EtsyCredentials c){credentials=c;apiKey.Password=c.Key;apiSecret.Password=c.Secret;apiToken.Password=c.Token;refreshToken.Password=c.RefreshToken;shopId.Text=c.ShopId;redirect.Text=c.RedirectUri;apiStatus.Text=c.ExpiresAt.HasValue?$"Token son kullanım: {c.ExpiresAt.Value.LocalDateTime:g}":"Bilgiler yüklendi; bağlantıyı test et.";}
 void SetAndSave(EtsyCredentials c){CredentialStore.Save(c);SetCredentials(c);Log("Etsy yetkilendirme bilgileri şifreli kaydedildi.");}
 async Task<EtsyCredentials> AuthorizedAsync()=>await authFlight.RunAsync(async()=>{var c=ReadCredentials();if(c.ExpiresAt<=DateTimeOffset.UtcNow.AddMinutes(1)&&c.RefreshToken!=""){c=await new EtsyOAuth(http).RefreshAsync(c,lifetime.Token);SetAndSave(c);}return c;},lifetime.Token);
 async Task LoadListingsAsync(){var state=listingState.SelectedItem?.ToString()??"active";var c=await AuthorizedAsync();if(loadedListingState!=state||loadedListingShop!=c.ShopId)listingOffset=0;var page=await new EtsyShopClient(http).GetListingsAsync(c,state,listingOffset,lifetime.Token);listings.ItemsSource=page.Listings;listingTotal=page.Count;loadedListingShop=c.ShopId;loadedListingState=state;listingStatus.Text=$"{page.Listings.Count} / {page.Count} ilan • başlangıç {listingOffset}";Log("Etsy ilanları okundu.");}
 CatalogProduct SelectedProduct()=>products.SelectedItem is CatalogProduct p?store.Products().Single(x=>x.Id==p.Id):throw new InvalidOperationException("Önce ürün havuzunda bir ürün seç.");
 void CheckDraft(){ValidBindings(templateEditor);var p=SelectedProduct();var errors=EtsyDrafts.Validate(p,template);if(p.EtsyCreationAttempted&&p.EtsyListingId=="")errors.Add("Önceki oluşturma sonucunu Etsy'den kontrol edip ilanı bağla.");draftStatus.Text=errors.Count==0?$"{p.Sku} • {p.Name}\n{p.Price} {p.Currency} / {p.Stock} adet\nTaslak için yerel kontroller geçti; mağaza dövizi API'de ayrıca kontrol edilir.":string.Join("\n",errors);OpenMarketplaceRoute("etsy");}
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
   var automationDue=new AutomationStore(dataDirectory).List().Where(j=>j.Enabled&&j.NextRunUtc<=DateTime.UtcNow).ToList();
   // XML due=0 must not skip stock/price/health/sync automation jobs that ARE due
   // (and vice versa) - each due list is evaluated independently every tick.
   if(!SchedulerTick.ShouldRun(due,automationDue))return;ModuleTabs.IsEnabled=false;
   foreach(var s in due){var run=new XmlRunStore(dataDirectory).Start(s.Id);try{var text=await new XmlSourceReader(http).ReadAsync(s.Location,XmlAuthStore.Load(s.Id,dataDirectory),lifetime.Token);await UpdateFxAsync(s);var expectedRevision=CatalogStore.SourceConfigRevision(s);var rows=await Task.Run(()=>XmlCatalog.Preview(text,s,store));if(beforeScheduledImportHook is not null)await beforeScheduledImportHook();var result=await Task.Run(()=>store.ImportIfSourceCurrent(s,expectedRevision,rows));new XmlRunStore(dataDirectory).Complete(run,result);s.LastStatus=$"Otomatik: {result.Added} yeni / {result.Updated} güncel / {result.Unchanged} aynı";}catch(Exception e){new XmlRunStore(dataDirectory).Fail(run,e.Message);s.LastStatus=Safe(e);}s.LastRunUtc=DateTime.UtcNow;if(store.TryRecordSourceRun(s.Id,s.LastRunUtc.Value,s.LastStatus))Log(s.LastStatus);else Log("XML kaynağı silindi veya devre dışı bırakıldı; eski çalışma durumu kaydedilmedi.");}
   foreach(var job in automationDue){try{var result=await Task.Run(()=>AutomationRunner.RunDue(store,new AutomationStore(dataDirectory),new SyncStore(dataDirectory),job.Id,DateTime.UtcNow));Log($"Otomasyon {job.Channel}/{job.Shop}: {result.Queued} iş kuyruğa alındı, {result.Errors.Count} hata.");}catch(Exception e){Log(Safe(e));}}
   // Do not replace ItemsSource or editor clones: unsaved manual edits must survive timer ticks.
   Log("Otomatik kontrol bitti. Güncel listeyi görmek için Havuzu yenile düğmesini kullan.");
  }catch(Exception e){Log(Safe(e));}finally{ModuleTabs.IsEnabled=true;gate.Release();}
 }
 async Task RunAsync(Func<Task> action){if(!await gate.WaitAsync(0)){Log("Önceki işlem sürüyor.");return;}ModuleTabs.IsEnabled=false;if(xmlDefinitionDialog!=null)xmlDefinitionDialog.IsEnabled=false;try{await action();}catch(Exception e){Log(Safe(e));apiStatus.Text=Safe(e);}finally{ModuleTabs.IsEnabled=true;if(xmlDefinitionDialog!=null)xmlDefinitionDialog.IsEnabled=true;gate.Release();}}
 static string Safe(Exception e)=>e is InvalidOperationException or ArgumentException?e.Message:"İşlem tamamlanamadı. Dosya biçimini, erişim izinlerini ve bağlantıyı kontrol et.";
 void Log(string text){var safeText=AuditStore.Sanitize(text);StatusText.Text=safeText;var line=$"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {safeText.Replace('\r',' ').Replace('\n',' ')}";logs.Insert(0,line);while(logs.Count>200)logs.RemoveAt(logs.Count-1);try{Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);File.AppendAllText(logPath,line+Environment.NewLine);new AuditStore(dataDirectory).Append(new(){Module="UI",Action="log",Outcome="Info",Detail=safeText});}catch(IOException){}catch(Exception){ } }
 protected override void OnClosing(System.ComponentModel.CancelEventArgs e){lifetime.Cancel();globalSearchCts?.Cancel();base.OnClosing(e);}
 protected override void OnClosed(EventArgs e){marketplaceHome?.Dispose();lifetime.Cancel();timer.Stop();searchTimer.Stop();globalSearchTimer.Stop();globalSearchCts?.Dispose();startupRecovery.Complete();http.Dispose();base.OnClosed(e);}
}












