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
 readonly DispatcherTimer searchTimer=new(){Interval=TimeSpan.FromMilliseconds(300)};
 readonly CancellationTokenSource lifetime=new();
 readonly ObservableCollection<string> logs=[];
 readonly string logPath;
 readonly string? dataDirectory;
 readonly DataGrid products=new(), preview=new(){SelectionMode=DataGridSelectionMode.Extended}, mapping=new(){IsReadOnly=false}, listings=new();
 readonly ListBox sources=new(){DisplayMemberPath="Name"}, paths=new();
 readonly TextBox search=new(){Width=300}, itemPath=new(), xmlUser=new(), shopId=new(), redirect=new(), callback=new(){Height=70,TextWrapping=TextWrapping.Wrap};
 readonly PasswordBox xmlPassword=new(), apiKey=new(), apiSecret=new(), apiToken=new(), refreshToken=new();
 readonly ComboBox decimalSeparator=new(){ItemsSource=new[]{".",","},SelectedIndex=0}, listingState=new(){ItemsSource=new[]{"active","draft","inactive","sold_out","expired"},SelectedIndex=0,Width=140};
 readonly StackPanel sourceGeneral=new(),sourceRules=new(),productEditor=new(),templateEditor=new();
 readonly TextBlock previewStatus=Hint("XML'i oku → eşleştir → önizle → seçili ürünleri havuza al."), apiStatus=Hint("Bağlantı henüz doğrulanmadı."), draftStatus=Hint("Ürün havuzundan bir ürün seç."),listingStatus=Hint("");
 readonly ObservableCollection<string> xmlPaths=new();
 readonly TextBox sampleCost=new(){Text="100",Width=130};
 readonly TextBlock calculationStatus=Hint("Alış fiyatını girip hesaplamayı test edebilirsin."),fxStatus=Hint("Kur henüz alınmadı.");
 XmlSource? source; CatalogProduct? edit; EtsyListingTemplate template=new(); EtsyCredentials credentials=new("","","",""); OAuthAttempt? attempt;
 List<MappingEntry> mappings=[]; string xml="",loadedLocation="",previewRevision=""; int listingOffset,listingTotal,productOffset,productTotal,searchRevision; string loadedListingShop="",loadedListingState="";
 public MainWindow():this(null){}
 public MainWindow(string? directory)
 {
  dataDirectory=directory;store=new CatalogStore(directory);logPath=Path.Combine(directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop"),"operations.log");
  InitializeComponent();Language=System.Windows.Markup.XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
  LogList.ItemsSource=logs;
  try{if(File.Exists(logPath))foreach(var line in File.ReadLines(logPath).TakeLast(100))logs.Insert(0,line);}catch(IOException){}
  BuildProducts();BuildSources();BuildApi();BuildListings();BuildTemplate();BuildNavigation();
  try{template=TemplateStore.Load(directory);templateEditor.DataContext=template;var saved=directory==null?CredentialStore.Load():null;if(saved!=null)SetCredentials(saved);}catch(Exception e){Log(Safe(e));}
  RefreshSources();RefreshProducts();timer.Tick+=async(_,_)=>await ScheduledAsync();timer.Start();searchTimer.Tick+=async(_,_)=>{searchTimer.Stop();await SearchProductsAsync();};
  Log("Global masaüstü hazır. XML otomasyonu yalnız program açıkken çalışır.");
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
 static Grid Split(UIElement left,UIElement right,double rightWidth)
 {var g=new Grid{Margin=new Thickness(10)};g.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});g.ColumnDefinitions.Add(new(){Width=new GridLength(rightWidth==350?330:1,rightWidth==350?GridUnitType.Pixel:GridUnitType.Star)});g.Children.Add(left);Grid.SetColumn(right,1);g.Children.Add(right);return g;}
 static DockPanel Dock(UIElement top,UIElement body){var d=new DockPanel();DockPanel.SetDock(top,System.Windows.Controls.Dock.Top);d.Children.Add(top);d.Children.Add(body);return d;}
 void BuildProducts()
 {
  var bar=new WrapPanel();search.ToolTip="SKU, barkod, ürün adı, marka veya kategori";bar.Children.Add(search);bar.Children.Add(Button("Önceki 200",()=>{productOffset=Math.Max(0,productOffset-200);RefreshProducts();}));bar.Children.Add(Button("Sonraki 200",()=>{if(productOffset+200<productTotal)productOffset+=200;RefreshProducts();}));bar.Children.Add(Button("Etsy şablonunu kontrol et",CheckDraft));search.TextChanged+=(_,_)=>{productOffset=0;searchTimer.Stop();searchTimer.Start();};
  AddProductFilters(bar);
  foreach(var x in new[]{("Durum","StatusLabel",65d),("Stok kodu / SKU","Sku",135d),("Ürün","Name",200d),("Alış fiyatı","Cost",90d),("Alış döviz","CostCurrency",65d),("Satış fiyatı","Price",90d),("Satış döviz","Currency",65d),("Stok","Stock",60d),("Formül TL","FormulaPriceTry",95d),("1 döviz/TL","AppliedTryRate",95d),("Barkod","Barcode",140d),("GTIN","Gtin",140d),("Marka","Brand",120d),("Kategori","Category",150d),("Açıklama","Description",240d),("Etsy ilan ID","EtsyListingId",110d)})Column(products,x.Item1,x.Item2,x.Item3);
  products.SelectionMode=DataGridSelectionMode.Single;products.SelectionChanged+=(_,_)=>{edit=products.SelectedItem is CatalogProduct p?Clone(p):null;productEditor.DataContext=edit;productEditor.IsEnabled=edit!=null;};
  bar.Children.Add(Button("Aktife al",()=>SetProductActive(true)));bar.Children.Add(Button("Pasife al",()=>SetProductActive(false)));bar.Children.Add(Button("Ürünü sil",DeleteSelectedProduct));productEditor.Children.Add(Heading("Ürün kartı"));Field(productEditor,"Alış fiyatı","Cost").IsReadOnly=true;Field(productEditor,"Alış para birimi","CostCurrency").IsReadOnly=true;Field(productEditor,"Satış fiyatı","Price");Field(productEditor,"Satış para birimi","Currency").IsReadOnly=true;productEditor.Children.Add(Hint("XML güncellemesinde korunmasını istediğin alanı kilitle."));
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
  Tab("Ürün havuzu",Split(Dock(bar,products),Scroll(productEditor),350));
 }
 void SetProductActive(bool active){if(edit==null)throw new InvalidOperationException("Önce ürün seç.");ValidBindings(productEditor);edit.Active=active;store.SaveProduct(edit);RefreshProducts();Log(active?"Ürün yerel havuzda aktif.":"Ürün yerel havuzda pasif. Etsy ilanının durumu değiştirilmedi.");}
 void DeleteSelectedProduct(){if(edit==null)throw new InvalidOperationException("Önce ürün seç.");if(MessageBox.Show(this,edit.Name+"\n\nYerel havuzdan silinsin mi? XML içinde varsa sonraki alımda yeniden gelir. Etsy ilanı silinmez.","Ürünü sil",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;store.DeleteProduct(edit);RefreshProducts();Log("Ürün yerel havuzdan silindi.");}
 void BuildSources()
 {
  var sourceActions=new StackPanel();sourceActions.Children.Add(Button("+ Yeni XML kaynağı",()=>SetSource(new(){Name="Yeni tedarikçi",PriceMode="Formula",Formula=PriceFormula.Example,AutoFx=true})));sourceActions.Children.Add(Button("XML şablonu aç…",LoadSourceTemplate));sourceActions.Children.Add(Button("Şablonu dışa aktar…",ExportSourceTemplate));var left=Dock(sourceActions,sources);
  sources.SelectionChanged+=(_,_)=>{if(sources.SelectedItem is XmlSource s)SetSource(Clone(s));};
  Field(sourceGeneral,"Tedarikçi / XML adı","Name");Field(sourceGeneral,"HTTPS adresi veya XML dosyası","Location");sourceGeneral.Children.Add(Button("XML dosyası seç",()=>{if(source==null)return;var d=new OpenFileDialog{Filter="XML (*.xml)|*.xml",CheckFileExists=true};if(d.ShowDialog(this)==true){source.Location=d.FileName;BindSource();}}));
  Flag(sourceGeneral,"Kaynak aktif","Enabled");Flag(sourceGeneral,"Program açıkken otomatik havuz güncellemesi","AutoImport");Field(sourceGeneral,"Kontrol aralığı (dakika)","IntervalMinutes");Label(sourceGeneral,"Basic Auth kullanıcı adı (isteğe bağlı)",xmlUser);Label(sourceGeneral,"XML şifresi",xmlPassword);sourceGeneral.Children.Add(Hint("Şifre Windows hesabına özel şifrelenir. XML sınırı 25 MB. Kaynaktan kaybolan ürünler korunur; otomatik Etsy gönderimi yapılmaz."));
  sources.Height=115;
  var mapTop=new StackPanel();Label(mapTop,"Ürün XPath yolu (/Products/Product gibi)",itemPath);Label(mapTop,"Ondalık ayırıcı",decimalSeparator);mapTop.Children.Add(Hint("XML'i oku: alanlar otomatik önerilir. Değiştirmek için listeden seç. Birden fazla görsel yolu gerekiyorsa | ile birleştirebilirsin."));
  mapping.Columns.Add(new DataGridTextColumn{Header="Ürün alanı",Binding=new Binding("Label"),IsReadOnly=true,Width=125});
  var selector=new FrameworkElementFactory(typeof(ComboBox));selector.SetValue(ComboBox.ItemsSourceProperty,xmlPaths);selector.SetValue(ComboBox.IsEditableProperty,true);selector.SetValue(ComboBox.IsTextSearchEnabledProperty,true);selector.SetBinding(ComboBox.TextProperty,new Binding("Path"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});mapping.RowHeight=42;
  mapping.Columns.Add(new DataGridTemplateColumn{Header="XML alanını seç",CellTemplate=new DataTemplate{VisualTree=selector},Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
  
  BuildPricingEditor();
  var priceGrid=new System.Windows.Controls.Primitives.UniformGrid{Columns=3};
  foreach(var f in new[]{("Güvenlik stoğu","SafetyStock"),("Minimum XML stoğu","MinimumStock"),("Maksimum gösterilecek stok","MaximumStock")}){var p=new StackPanel();Field(p,f.Item1,f.Item2);priceGrid.Children.Add(p);}sourceRules.Children.Add(new GroupBox{Header="Stok kuralları",Content=priceGrid});
  Field(sourceRules,"Alınacak markalar (boş: tümü; ayırıcı ;)","BrandFilter");Field(sourceRules,"Alınacak kategoriler (boş: tümü; ayırıcı ;)","CategoryFilter");sourceRules.Children.Add(Hint("Fiyat ve stok, ürün kartında kilitli değilse güncellenir. Yeni ürün tüm eşleşen alanlarla kaydedilir."));Flag(sourceRules,"Tekrar alımda başlığı güncelle","UpdateName");Flag(sourceRules,"Tekrar alımda açıklamayı güncelle","UpdateDescription");Flag(sourceRules,"Tekrar alımda görselleri güncelle","UpdateImages");
  
  var previewTop=new WrapPanel();previewStatus.MaxWidth=460;previewTop.Children.Add(previewStatus);previewTop.Children.Add(Button("Tüm önizleme satırlarını seç",()=>preview.SelectAll()));foreach(var x in new[]{("SKU","Sku",130d),("Ürün","Name",240d),("Alış TL","Cost",90d),("Formül TL","FormulaPriceTry",105d),("1 döviz/TL","AppliedTryRate",100d),("Satış","Price",90d),("Döviz","Currency",65d),("Stok","Stock",60d),("Kategori","Category",150d)})Column(preview,x.Item1,x.Item2,x.Item3);
  
  var actions=new WrapPanel();actions.Children.Add(Button("Kaynağı kaydet",SaveSource));actions.Children.Add(AsyncButton("XML'i oku / alanları bul",InspectAsync));actions.Children.Add(AsyncButton("Önizleme hesapla",PreviewAsync));actions.Children.Add(AsyncButton("Seçilileri havuza al",ImportAsync));
  var settings=new TabControl();
  settings.Items.Add(new TabItem{Header="1  Kaynak ve bağlantı",Content=Split(left,Scroll(sourceGeneral),550)});
  settings.Items.Add(new TabItem{Header="2  Alan eşleştirme",Content=Dock(mapTop,mapping)});
  settings.Items.Add(new TabItem{Header="3  Fiyat, kur ve stok",Content=Scroll(sourceRules)});
  var workspace=new Grid();workspace.RowDefinitions.Add(new(){Height=new GridLength(1.2,GridUnitType.Star),MinHeight=180});workspace.RowDefinitions.Add(new(){Height=new GridLength(5)});workspace.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star),MinHeight=180});workspace.Children.Add(settings);
  var horizontal=new GridSplitter{Height=5,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,Background=Brushes.LightGray};Grid.SetRow(horizontal,1);workspace.Children.Add(horizontal);
  var previewPanel=new GroupBox{Header="Ürünler / hesaplanan fiyat ve stok",Content=Dock(previewTop,preview)};Grid.SetRow(previewPanel,2);workspace.Children.Add(previewPanel);
  Tab("XML yönetimi",Dock(actions,workspace));
 }
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
  source=s;BindSource();fxStatus.Text=RateDescription(s);calculationStatus.Text="Alış fiyatını girip hesaplamayı test edebilirsin.";itemPath.Text=s.ItemPath;decimalSeparator.SelectedItem=s.DecimalSeparator;
  var names=new[]{("Sku","SKU / stok kodu"),("Barcode","Barkod"),("Gtin","GTIN / EAN / UPC"),("Name","Ürün adı"),("Description","Açıklama"),("Cost","Alış fiyatı"),("Stock","Stok"),("Brand","Marka"),("Category","Kategori"),("ImageUrls","Görseller")};
  mappings=names.Select(x=>new MappingEntry{Key=x.Item1,Label=x.Item2,Path=s.Fields.GetValueOrDefault(x.Item1,"")}).ToList();mapping.ItemsSource=mappings;xml="";loadedLocation="";previewRevision="";preview.ItemsSource=null;paths.ItemsSource=null;xmlPaths.Clear();
  try{var auth=XmlAuthStore.Load(s.Id,dataDirectory);xmlUser.Text=auth.User;xmlPassword.Password=auth.Password;}catch(Exception){xmlUser.Clear();xmlPassword.Clear();Log("XML şifresi açılamadı; yeniden kaydet.");}
 }
 XmlSource CurrentSource(){if(source==null)throw new InvalidOperationException("Önce XML kaynağı seç veya ekle.");ValidBindings(sourceGeneral);ValidBindings(sourceRules);mapping.CommitEdit(DataGridEditingUnit.Cell,true);mapping.CommitEdit(DataGridEditingUnit.Row,true);source.ItemPath=itemPath.Text.Trim();source.DecimalSeparator=decimalSeparator.SelectedItem?.ToString()??".";source.Fields=mappings.Where(m=>!string.IsNullOrWhiteSpace(m.Path)).ToDictionary(m=>m.Key,m=>m.Path.Trim());source.Currency=source.Currency.Trim().ToUpperInvariant();source.CostCurrency=source.CostCurrency.Trim().ToUpperInvariant();XmlCatalog.ValidateSource(source);return source;}
 void SaveSource(){var s=CurrentSource();if(string.IsNullOrWhiteSpace(s.Name)||string.IsNullOrWhiteSpace(s.Location))throw new InvalidOperationException("Kaynak adı ve XML adresi/dosyası gerekli.");var latest=store.Sources().SingleOrDefault(x=>x.Id==s.Id);if(latest!=null){s.LastRunUtc=latest.LastRunUtc;s.LastStatus=latest.LastStatus;}XmlAuthStore.Save(s.Id,new(xmlUser.Text,xmlPassword.Password),dataDirectory);store.SaveSource(s);RefreshSources(false);Log("XML kaynağı, eşleştirme ve kurallar kaydedildi.");}
 void RefreshSources(bool choose=true){sources.ItemsSource=store.Sources();if(choose&&source==null&&sources.Items.Count>0)sources.SelectedIndex=0;}
 async Task InspectAsync(){var s=CurrentSource();xml="";loadedLocation="";previewRevision="";preview.ItemsSource=null;xml=await new XmlSourceReader(http).ReadAsync(s.Location,new(xmlUser.Text,xmlPassword.Password),lifetime.Token);loadedLocation=s.Location;var scan=await Task.Run(()=>XmlCatalog.Inspect(xml,string.IsNullOrWhiteSpace(s.ItemPath)?null:s.ItemPath));itemPath.Text=scan.ItemPath;paths.ItemsSource=scan.Paths;xmlPaths.Clear();xmlPaths.Add("");foreach(var path in scan.Paths)xmlPaths.Add(path);foreach(var m in mappings)if(string.IsNullOrWhiteSpace(m.Path)&&scan.SuggestedFields.TryGetValue(m.Key,out var value))m.Path=value;mapping.Items.Refresh();previewRevision="";preview.ItemsSource=null;Log($"XML okundu; {scan.Paths.Count} alan yolu bulundu.");}
 async Task PreviewAsync(){var s=CurrentSource();if(xml==""||loadedLocation!=s.Location)throw new InvalidOperationException("Bu kaynak adresi için önce XML'i oku.");previewRevision="";preview.ItemsSource=null;previewStatus.Text="Fiyat ve kur hesaplanıyor…";await UpdateFxAsync(s);BindSource();var snapshot=Clone(s);var rows=await Task.Run(()=>XmlCatalog.Preview(xml,snapshot));preview.ItemsSource=rows;previewRevision=JsonSerializer.Serialize(snapshot);previewStatus.Text=$"{rows.Count} ürün • Fiyat/stok hesaplandı. Kaydetmeden önce satırları seç. Etsy'ye gönderim yapılmaz.";Log($"XML önizlemesi: {rows.Count} ürün.");}
 async Task ImportAsync(){var s=CurrentSource();if(previewRevision==""||previewRevision!=JsonSerializer.Serialize(s))throw new InvalidOperationException("Ayarlar değişti veya önizleme yok. Önizlemeyi yeniden hesapla.");CatalogPricing.ValidateRate(s);var rows=preview.SelectedItems.Cast<CatalogProduct>().Select(Clone).ToList();if(rows.Count==0)throw new InvalidOperationException("Önizlemeden en az bir ürün seç.");XmlAuthStore.Save(s.Id,new(xmlUser.Text,xmlPassword.Password),dataDirectory);store.SaveSource(s);var result=await Task.Run(()=>store.Import(s,rows));s.LastRunUtc=DateTime.UtcNow;s.LastStatus=$"{result.Added} yeni / {result.Updated} güncel / {result.Unchanged} aynı";store.SaveSource(s);previewRevision="";RefreshSources(false);RefreshProducts();Log(s.LastStatus);Navigate("products");}
 void ShowProducts(CatalogPage page){var id=edit?.Id;productTotal=page.Total;products.ItemsSource=page.Items;if(id!=null)products.SelectedItem=page.Items.FirstOrDefault(p=>p.Id==id);SummaryText.Text=$"{page.Total:N0} sonuç   •   {page.InStock:N0} stokta   •   {page.Linked:N0} Etsy ile eşleşen   •   Sayfa {productOffset/200+1} / {Math.Max(1,(page.Total+199)/200)}";}
 void RefreshProducts(){searchRevision++;ShowProducts(store.Search(search.Text.Trim(),productOffset,200,productFilter));}
 async Task SearchProductsAsync(){var revision=++searchRevision;var q=search.Text.Trim();var offset=productOffset;var filter=productFilter;try{var page=await Task.Run(()=>store.Search(q,offset,200,filter));if(revision==searchRevision)ShowProducts(page);}catch(Exception e){Log(Safe(e));}}
 void Refresh_Click(object sender,RoutedEventArgs e){try{RefreshProducts();}catch(Exception ex){Log(Safe(ex));}}
 EtsyCredentials ReadCredentials(){var key=apiKey.Password.Trim();var secret=apiSecret.Password.Trim();var token=apiToken.Password.Trim();var changedApp=key!=credentials.Key||secret!=credentials.Secret;var changedToken=token!=credentials.Token;var refresh=refreshToken.Password.Trim();return credentials with{Key=key,Secret=secret,Token=token,RefreshToken=(changedApp||changedToken)&&refresh==credentials.RefreshToken?"":refresh,ExpiresAt=changedApp||changedToken?null:credentials.ExpiresAt,ShopId=shopId.Text.Trim(),RedirectUri=redirect.Text.Trim()};}
 void SetCredentials(EtsyCredentials c){credentials=c;apiKey.Password=c.Key;apiSecret.Password=c.Secret;apiToken.Password=c.Token;refreshToken.Password=c.RefreshToken;shopId.Text=c.ShopId;redirect.Text=c.RedirectUri;apiStatus.Text=c.ExpiresAt.HasValue?$"Token son kullanım: {c.ExpiresAt.Value.LocalDateTime:g}":"Bilgiler yüklendi; bağlantıyı test et.";}
 void SetAndSave(EtsyCredentials c){CredentialStore.Save(c);SetCredentials(c);Log("Etsy yetkilendirme bilgileri şifreli kaydedildi.");}
 async Task<EtsyCredentials> AuthorizedAsync(){var c=ReadCredentials();if(c.ExpiresAt<=DateTimeOffset.UtcNow.AddMinutes(1)&&c.RefreshToken!=""){c=await new EtsyOAuth(http).RefreshAsync(c,lifetime.Token);SetAndSave(c);}return c;}
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
   if(due.Count==0)return;ModuleTabs.IsEnabled=false;
   foreach(var s in due){try{var text=await new XmlSourceReader(http).ReadAsync(s.Location,XmlAuthStore.Load(s.Id,dataDirectory),lifetime.Token);await UpdateFxAsync(s);var rows=await Task.Run(()=>XmlCatalog.Preview(text,s));var result=await Task.Run(()=>store.Import(s,rows));s.LastStatus=$"Otomatik: {result.Added} yeni / {result.Updated} güncel / {result.Unchanged} aynı";}catch(Exception e){s.LastStatus=Safe(e);}s.LastRunUtc=DateTime.UtcNow;store.SaveSource(s);Log(s.LastStatus);}
   // Do not replace ItemsSource or editor clones: unsaved manual edits must survive timer ticks.
   Log("Otomatik kontrol bitti. Güncel listeyi görmek için Havuzu yenile düğmesini kullan.");
  }catch(Exception e){Log(Safe(e));}finally{ModuleTabs.IsEnabled=true;gate.Release();}
 }
 async Task RunAsync(Func<Task> action){if(!await gate.WaitAsync(0)){Log("Önceki işlem sürüyor.");return;}ModuleTabs.IsEnabled=false;try{await action();}catch(Exception e){Log(Safe(e));apiStatus.Text=Safe(e);}finally{ModuleTabs.IsEnabled=true;gate.Release();}}
 static string Safe(Exception e)=>e is InvalidOperationException or ArgumentException?e.Message:"İşlem tamamlanamadı. Dosya biçimini, erişim izinlerini ve bağlantıyı kontrol et.";
 void Log(string text){StatusText.Text=text;var line=$"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text.Replace('\r',' ').Replace('\n',' ')}";logs.Insert(0,line);while(logs.Count>200)logs.RemoveAt(logs.Count-1);try{Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);File.AppendAllText(logPath,line+Environment.NewLine);}catch(IOException){} }
 protected override void OnClosed(EventArgs e){timer.Stop();searchTimer.Stop();lifetime.Cancel();http.Dispose();base.OnClosed(e);}
}












