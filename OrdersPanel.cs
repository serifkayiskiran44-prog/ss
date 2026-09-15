using TrMarketplaceHubDesktop.Catalog;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
namespace TrMarketplaceHubDesktop;
public static class OrdersPanel
{
 public static FrameworkElement Create(string? directory=null,Func<Task<EtsyCredentials>>? authorize=null,Action? catalogChanged=null)
 {
  var store=new OrdersStore(directory);var catalog=new CatalogStore(directory);var root=new DockPanel{Margin=new Thickness(12)};var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
  top.Children.Add(Text("Siparişler ve kargo takibi",22));
  top.Children.Add(Text("Etsy siparişleri salt okunur alınır. Yolda / teslim edildi gözlemleri yerel olarak kaydedilir. Ozon ve Navlungo otomatik takip bağlantısı henüz yok."));
  var bar=new WrapPanel();top.Children.Add(bar);var search=new TextBox{Width=220,ToolTip="Sipariş, mağaza, ürün, SKU veya takip numarası ara"};bar.Children.Add(search);
  var filter=new ComboBox{Width=170,ItemsSource=new[]{"Tümü"}.Concat(OrdersRules.States.Select(OrdersRules.Label)).ToArray(),SelectedIndex=0};bar.Children.Add(filter);
  var marketplaceFilter=new ListBox{Width=130,Height=52,SelectionMode=SelectionMode.Extended,ToolTip="Boş seçim: tüm pazaryerleri. Ctrl/Shift ile çoklu seçim."};bar.Children.Add(marketplaceFilter);
  var shopFilter=new ListBox{Width=140,Height=52,SelectionMode=SelectionMode.Extended,ToolTip="Boş seçim: tüm mağazalar. Ctrl/Shift ile çoklu seçim."};bar.Children.Add(shopFilter);
  var stockFilter=new ComboBox{Width=145,ItemsSource=new[]{"Tümü","Stok düşüldü","Stok bekliyor"},SelectedIndex=0};bar.Children.Add(stockFilter);
  var dateFrom=new DatePicker{Width=115,ToolTip="Başlangıç tarihi (yerel gün, dahil)"};var dateTo=new DatePicker{Width=115,ToolTip="Bitiş tarihi (yerel gün, dahil)"};
  bar.Children.Add(new TextBlock{Text="Tarih",Margin=new Thickness(6,4,2,4),VerticalAlignment=VerticalAlignment.Center});bar.Children.Add(dateFrom);bar.Children.Add(new TextBlock{Text="—",Margin=new Thickness(2,4,2,4),VerticalAlignment=VerticalAlignment.Center});bar.Children.Add(dateTo);
  var clearDates=Button(bar,"Tarihi temizle");clearDates.Click+=(_,_)=>{dateFrom.SelectedDate=null;dateTo.SelectedDate=null;};
  var viewStore=new UiPreferenceStore(directory);var savedViews=new ComboBox{Width=165,DisplayMemberPath="Name"};var viewName=new TextBox{Width=130,ToolTip="Kayıtlı sipariş görünümü adı"};bar.Children.Add(new TextBlock{Text="Görünüm",Margin=new Thickness(8,4,2,4),VerticalAlignment=VerticalAlignment.Center});bar.Children.Add(savedViews);bar.Children.Add(viewName);
  var refresh=Button(bar,"Etsy'den yenile");var cancel=Button(bar,"İptal");cancel.IsEnabled=false;var add=Button(bar,"+ Yerel sipariş");
  var exportTemplates=new OrderExportTemplateStore(directory);var manageExport=Button(bar,"Export şablonları");manageExport.Click+=(_,_)=>ExportTemplateDialog(exportTemplates);
  var status=Text("Kayıtlar yükleniyor…");top.Children.Add(status);
  var layout=new Grid();layout.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});layout.ColumnDefinitions.Add(new(){Width=new GridLength(370)});root.Children.Add(layout);
  var grid=new DataGrid{IsReadOnly=true,AutoGenerateColumns=false,EnableRowVirtualization=true,EnableColumnVirtualization=false,SelectionMode=DataGridSelectionMode.Single};VirtualizingPanel.SetIsVirtualizing(grid,true);VirtualizingPanel.SetVirtualizationMode(grid,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(grid,true);layout.Children.Add(grid);
  foreach(var (label,path,width) in new[]{("Pazaryeri","Marketplace",90),("Mağaza","ShopId",100),("Sipariş","OrderId",110),("Sipariş durumu (kaynak)","RawStatus",150),("Ödeme","PaymentStatus",110),("Stok kararı","StockDecisionLabel",130),("Kargo","DeliveryLabel",210),("Taşıyıcı","Carriers",110),("Takip no","TrackingNumbers",130),("Toplam","TotalLabel",100),("Kaynak","Source",120),("Son başarılı alım","SyncLabel",140)})grid.Columns.Add(new DataGridTextColumn{Header=label,Binding=new Binding(path),MinWidth=width,Width=width});
  var detail=new StackPanel{Margin=new Thickness(12,0,0,0)};var scroll=new ScrollViewer{Content=detail,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetColumn(scroll,1);layout.Children.Add(scroll);
  List<OrderSnapshot> all=[];OrderSnapshot? editing=null;CancellationTokenSource? running=null;
  void ReloadViews()=>savedViews.ItemsSource=viewStore.ListViews("orders");
  void LoadView(SavedUiView view){using var doc=JsonDocument.Parse(view.Payload);var root=doc.RootElement;search.Text=root.GetProperty("search").GetString()??"";filter.SelectedIndex=root.GetProperty("state").GetInt32();stockFilter.SelectedItem=root.GetProperty("stock").GetString()??"Tümü";
   marketplaceFilter.SelectedItems.Clear();if(root.TryGetProperty("marketplaces",out var marketplaceArray))foreach(var value in marketplaceArray.EnumerateArray())if(marketplaceFilter.Items.Contains(value.GetString()))marketplaceFilter.SelectedItems.Add(value.GetString());
   shopFilter.SelectedItems.Clear();if(root.TryGetProperty("shops",out var shopArray))foreach(var value in shopArray.EnumerateArray())if(shopFilter.Items.Contains(value.GetString()))shopFilter.SelectedItems.Add(value.GetString());
   dateFrom.SelectedDate=root.TryGetProperty("dateFrom",out var df)&&df.ValueKind==JsonValueKind.String?DateTime.Parse(df.GetString()!,null,System.Globalization.DateTimeStyles.RoundtripKind):null;
   dateTo.SelectedDate=root.TryGetProperty("dateTo",out var dt)&&dt.ValueKind==JsonValueKind.String?DateTime.Parse(dt.GetString()!,null,System.Globalization.DateTimeStyles.RoundtripKind):null;
  }
  void Filter(){
   string q=search.Text.Trim();var selectedMarketplaces=marketplaceFilter.SelectedItems.Cast<string>().ToList();var selectedShops=shopFilter.SelectedItems.Cast<string>().ToList();var selectedStock=stockFilter.SelectedItem?.ToString()??"Tümü";
   // Inclusive local-day range: SelectedDate is a local midnight; the end date's
   // upper bound is that local day's last instant, both converted to UTC to compare
   // against UpdatedAt (stored UTC) - so "today" always means the viewer's today,
   // not UTC's, and the boundary instants are never silently excluded.
   DateTimeOffset? fromUtc=dateFrom.SelectedDate.HasValue?new DateTimeOffset(dateFrom.SelectedDate.Value.Date,TimeZoneInfo.Local.GetUtcOffset(dateFrom.SelectedDate.Value)).ToUniversalTime():null;
   DateTimeOffset? toUtc=dateTo.SelectedDate.HasValue?new DateTimeOffset(dateTo.SelectedDate.Value.Date.AddDays(1).AddTicks(-1),TimeZoneInfo.Local.GetUtcOffset(dateTo.SelectedDate.Value)).ToUniversalTime():null;
   bool MatchesState(OrderSnapshot o)=>filter.SelectedIndex<=0||o.Shipments.Any(s=>s.State==OrdersRules.States[filter.SelectedIndex-1])||(filter.SelectedIndex==1&&o.Shipments.Count==0);
   grid.ItemsSource=all.Where(o=>MatchesState(o)&&OrderFilterCriteria.Matches(o,q,null,selectedMarketplaces,selectedShops,selectedStock,fromUtc,toUtc)).ToList();
  }
  void Load(){all=store.ReadAll();foreach(var order in all)order.StockDecisionLabel=catalog.GetOrderStockStatus(order.Marketplace,order.ShopId,order.OrderId)==null?"Stok bekliyor":"Stok düşüldü";marketplaceFilter.ItemsSource=all.Select(o=>o.Marketplace).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).ToArray();shopFilter.ItemsSource=all.Select(o=>o.ShopId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).ToArray();Filter();status.Text=$"{all.Count} kayıt · Liste son yükleme: {DateTime.Now:g}. Boş liste varsa Etsy'den yenileyin veya yerel sipariş ekleyin.";}
  void Edit(OrderSnapshot order,bool isNew=false)
  {
   editing=order.Copy();var o=editing;Action captureShipment=()=>{};detail.Children.Clear();detail.Children.Add(Text(isNew?"Yeni yerel sipariş":"Sipariş ayrıntısı",18));
   var marketplace=Field(detail,"Pazaryeri",o.Marketplace,!isNew);var shop=Field(detail,"Mağaza kimliği",o.ShopId,!isNew);var id=Field(detail,"Sipariş numarası",o.OrderId,!isNew);
   bool api=o.Source=="Etsy API";var raw=Field(detail,"Sipariş durumu (ham değer)",o.RawStatus,api);var payment=Field(detail,"Ödeme durumu",o.PaymentStatus,api);
   detail.Children.Add(Text($"Kaynak: {o.Source}\nSon API alımı: {o.SyncLabel}"));detail.Children.Add(Text("Ürünler",16));
   foreach(var i in o.Items)detail.Children.Add(Text($"{i.Quantity} × {i.Title} · SKU: {i.Sku}"));
   if(!api){var product=Field(detail,"Ürün adı (ekle)","");var sku=Field(detail,"SKU","");var qty=Field(detail,"Adet","1");var itemAdd=Button(detail,"Ürünü ekle");itemAdd.Click+=(_,_)=>{if(string.IsNullOrWhiteSpace(product.Text)||!int.TryParse(qty.Text,out int n)||n<=0){status.Text="Ürün adı ve pozitif tam adet girin.";return;}Capture();captureShipment();o.Items.Add(new(){Title=product.Text.Trim(),Sku=sku.Text.Trim(),Quantity=n});Edit(o,isNew);};}
   if(!isNew){
    detail.Children.Add(Text("Merkezi stok işlemi",16));
    var receipt=catalog.GetOrderStockStatus(o.Marketplace,o.ShopId,o.OrderId);
    if(receipt!=null){detail.Children.Add(Text($"Stok işlendi: {receipt.AppliedUtc.ToLocalTime():g}"));foreach(var movement in receipt.Movements)detail.Children.Add(Text($"{movement.Sku}: {movement.StockBefore} → {movement.StockAfter} (−{movement.Quantity})"));}
    else detail.Children.Add(Text("Bu sipariş için stok düşümü kaydı yok. Stoğunu daha önce elle düşürdüğünüz siparişlerde çalıştırmayın."));
    detail.Children.Add(Text("Yalnız kaydedilmiş sipariş satırları kullanılır. SKU tek ürüne eşleşmelidir; eksik/çakışan SKU veya yetersiz stokta tüm işlem geri alınır. İşlenen ürünün XML stok kilidi etkinleştirilir; böylece tedarikçi güncellemesi satılan stoğu geri yazmaz. Pazaryerlerine gönderim yapılmaz."));
    if(receipt==null){
     var catalogProducts=catalog.Products();
     var savedPreview=store.ReadAll().Single(x=>x.Marketplace==o.Marketplace&&x.ShopId==o.ShopId&&x.OrderId==o.OrderId);
     foreach(var line in savedPreview.Items.GroupBy(x=>x.Sku,StringComparer.Ordinal)){
      var matches=catalogProducts.Where(x=>x.Sku==line.Key).ToList();var quantity=line.Sum(x=>(long)x.Quantity);
      detail.Children.Add(Text(matches.Count==1?$"{line.Key}: mevcut {matches[0].Stock}, düşülecek {quantity}, kalan {matches[0].Stock-quantity}":$"{line.Key}: ürün eşleşmesi belirsiz ({matches.Count} kayıt)"));
     }
     detail.Children.Add(Text("Önizleme anlık bilgidir; düğmeye basıldığında stok yeniden doğrulanır."));
    }
    var applyStock=Button(detail,"Kaydedilmiş siparişi stoktan düş");applyStock.IsEnabled=receipt==null;
    applyStock.Click+=(_,_)=>{try{
     var saved=store.ReadAll().Single(x=>x.Marketplace==o.Marketplace&&x.ShopId==o.ShopId&&x.OrderId==o.OrderId);
     var preview=new OrderStockDecisionService(catalog).CreatePreview(saved);var result=new OrderStockDecisionService(catalog).ApplyApproved(preview,true);
     catalogChanged?.Invoke();Edit(saved);status.Text=result.AlreadyApplied?"Bu sipariş zaten işlendi; stok tekrar düşmedi.":"Sipariş stoğu tek işlemde düşüldü. XML stok kilidi etkin; dış pazaryerlerine gönderim yapılmadı.";
    }catch(InvalidOperationException ex){status.Text=ex.Message;}catch(ArgumentException ex){status.Text=ex.Message;}catch{status.Text="Stok işlemi tamamlanamadı; kayıtlar korunur. Yenileyip tekrar deneyin.";}};
   }
   detail.Children.Add(Text("Paketler / gözlem geçmişi",16));
   var shipments=new ComboBox{ItemsSource=o.Shipments,DisplayMemberPath="Label",MinWidth=260};detail.Children.Add(shipments);var shippingArea=new StackPanel();detail.Children.Add(shippingArea);
   void ShipmentEditor(OrderShipment s)
   {
    shippingArea.Children.Clear();var carrier=Field(shippingArea,"Taşıyıcı",s.Carrier);var tracking=Field(shippingArea,"Takip numarası",s.TrackingNumber);var url=Field(shippingArea,"HTTPS takip bağlantısı (isteğe bağlı)",s.TrackingUrl);
    shippingArea.Children.Add(Text("Kargo durumu — kullanıcı gözlemi"));var state=new ComboBox{ItemsSource=OrdersRules.States.Select(x=>new StateChoice(x,OrdersRules.Label(x))).ToArray(),DisplayMemberPath="Label",SelectedValuePath="Value",SelectedValue=s.State};shippingArea.Children.Add(state);
    shippingArea.Children.Add(Text("Etsy gönderim bildirimi taşıyıcının yolda/teslim teyidi değildir. Aşağıdaki geçmiş yalnız kaydedilen yerel gözlemlerdir."));
    foreach(var e in s.Events.OrderByDescending(e=>e.At))shippingArea.Children.Add(Text($"{e.At.ToLocalTime():g} · {OrdersRules.Label(e.State)} · {e.Source}"));
    var open=Button(shippingArea,"Takip bağlantısını aç");open.Click+=(_,_)=>{if(!OrdersRules.SafeTrackingUrl(url.Text.Trim())){status.Text="Geçerli HTTPS takip bağlantısı girin.";return;}try{Process.Start(new ProcessStartInfo(url.Text.Trim()){UseShellExecute=true});}catch{status.Text="Takip bağlantısı tarayıcıda açılamadı.";}};
    captureShipment=()=>{if(s.Carrier!=carrier.Text.Trim()||s.TrackingNumber!=tracking.Text.Trim()||s.TrackingUrl!=url.Text.Trim())s.Source="Yerel / manuel";s.Carrier=carrier.Text.Trim();s.TrackingNumber=tracking.Text.Trim();s.TrackingUrl=url.Text.Trim();s.State=state.SelectedValue as string??"Unknown";};
   }
   shipments.SelectionChanged+=(_,_)=>{captureShipment();if(shipments.SelectedItem is OrderShipment s)ShipmentEditor(s);};if(o.Shipments.Count>0)shipments.SelectedIndex=0;
   var newShipment=Button(detail,"+ Paket ekle");newShipment.Click+=(_,_)=>{Capture();captureShipment();o.Shipments.Add(new(){Id="local:"+Guid.NewGuid().ToString("N")});Edit(o,isNew);};
   var save=Button(detail,"Yerel kaydı / gözlemleri kaydet");save.Click+=(_,_)=>{try{Capture();captureShipment();if(isNew&&store.ReadAll().Any(x=>x.Marketplace==o.Marketplace&&x.ShopId==o.ShopId&&x.OrderId==o.OrderId))throw new ArgumentException("Bu sipariş zaten kayıtlı; listeden açarak düzenleyin.");store.SaveManual(o);Load();var saved=all.First(x=>x.Marketplace==o.Marketplace&&x.ShopId==o.ShopId&&x.OrderId==o.OrderId);Edit(saved);status.Text="Yerel kayıt kaydedildi. Pazaryerine veya taşıyıcıya gönderim yapılmadı.";}catch(ArgumentException ex){status.Text=ex.Message;}catch{status.Text="Yerel kayıt kaydedilemedi; disk erişimini kontrol edin.";}};
   void Capture(){o.Marketplace=marketplace.Text.Trim();o.ShopId=shop.Text.Trim();o.OrderId=id.Text.Trim();o.RawStatus=raw.Text.Trim();o.PaymentStatus=payment.Text.Trim();}
  }
  var saveView=Button(bar,"Görünümü kaydet");saveView.Click+=(_,_)=>{viewStore.SaveView("orders",viewName.Text,JsonSerializer.Serialize(new{search=search.Text,state=filter.SelectedIndex,marketplaces=marketplaceFilter.SelectedItems.Cast<string>().ToArray(),shops=shopFilter.SelectedItems.Cast<string>().ToArray(),stock=stockFilter.SelectedItem?.ToString()??"Tümü",dateFrom=dateFrom.SelectedDate.HasValue?dateFrom.SelectedDate.Value.ToString("O"):null,dateTo=dateTo.SelectedDate.HasValue?dateTo.SelectedDate.Value.ToString("O"):null}));viewName.Clear();ReloadViews();};var deleteView=Button(bar,"Görünümü sil");deleteView.Click+=(_,_)=>{if(savedViews.SelectedItem is not SavedUiView view)throw new InvalidOperationException("Önce görünüm seçin.");viewStore.DeleteView("orders",view.Name);ReloadViews();};grid.SelectionChanged+=(_,_)=>{if(grid.SelectedItem is OrderSnapshot o)Edit(o);};search.TextChanged+=(_,_)=>Filter();filter.SelectionChanged+=(_,_)=>Filter();marketplaceFilter.SelectionChanged+=(_,_)=>Filter();shopFilter.SelectionChanged+=(_,_)=>Filter();stockFilter.SelectionChanged+=(_,_)=>Filter();dateFrom.SelectedDateChanged+=(_,_)=>Filter();dateTo.SelectedDateChanged+=(_,_)=>Filter();savedViews.SelectionChanged+=(_,_)=>{if(savedViews.SelectedItem is SavedUiView view)try{LoadView(view);Filter();}catch{status.Text="Kayıtlı görünüm okunamadı.";}};add.Click+=(_,_)=>Edit(new(){Marketplace="Yerel",ShopId="Mağazam",RawStatus="Açık"},true);
  cancel.Click+=(_,_)=>running?.Cancel();root.Unloaded+=(_,_)=>running?.Cancel();
  refresh.Click+=async(_,_)=>
  {
   if(running!=null)return;using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(3));running=cts;refresh.IsEnabled=add.IsEnabled=false;cancel.IsEnabled=true;detail.IsEnabled=false;status.Text="Etsy siparişleri okunuyor; tüm sayfalar başarılı olunca kayıtlar güncellenecek…";
   try{if(authorize==null)throw new InvalidOperationException("Önce Etsy API sekmesinde mağaza bağlantısını kurun.");var credentials=await authorize();using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(45)};var rows=await new OrdersEtsyClient(http).ReadAsync(credentials,cts.Token);cts.Token.ThrowIfCancellationRequested();await Task.Run(()=>store.SaveBatch(rows),cts.Token);Load();detail.Children.Clear();status.Text=$"Etsy: {rows.Count} sipariş alındı · {DateTime.Now:g}. Taşıyıcı teslimat verisi bu API'de sağlanmaz.";}
   catch(OperationCanceledException){status.Text="Alım iptal edildi veya zaman aşımı; önceki kayıtlar korundu.";}
   catch(InvalidOperationException e){status.Text=e.Message;}
   catch{status.Text="Siparişler alınamadı. Bağlantı, mağaza ve transactions_r iznini kontrol edin. Önceki kayıtlar korundu.";}
   finally{running=null;refresh.IsEnabled=add.IsEnabled=true;cancel.IsEnabled=false;detail.IsEnabled=true;}
  };
  ReloadViews();Load();detail.Children.Add(Text("Ayrıntıları ve paket geçmişini görmek için listeden bir sipariş seçin. İlk kaydı eklemek için + Yerel sipariş düğmesini kullanın."));return root;
 }
 sealed record StateChoice(string Value,string Label);
 static TextBlock Text(string text,int size=12)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,4,3,7),Foreground=Brushes.DarkSlateGray};
 static TextBox Field(Panel parent,string label,string value,bool readOnly=false){parent.Children.Add(Text(label));var box=new TextBox{Text=value,IsReadOnly=readOnly};parent.Children.Add(box);return box;}
 static Button Button(Panel parent,string text){var button=new Button{Content=text,Margin=new Thickness(3,5,3,5)};parent.Children.Add(button);return button;}
 static void ExportTemplateDialog(OrderExportTemplateStore store)
 {
  var win=new Window{Title="Export şablonları",Width=420,Height=520,WindowStartupLocation=WindowStartupLocation.CenterScreen};
  var outer=new DockPanel{Margin=new Thickness(10)};win.Content=outer;
  var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);outer.Children.Add(top);
  var status=Text("");top.Children.Add(status);
  var savedList=new ComboBox{DisplayMemberPath="Name",Margin=new Thickness(0,0,0,6)};top.Children.Add(savedList);
  var nameBox=new TextBox{Margin=new Thickness(0,0,0,6),ToolTip="Şablon adı"};top.Children.Add(nameBox);
  var checks=new StackPanel();var scroll=new ScrollViewer{Content=checks,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};outer.Children.Add(scroll);
  var boxes=new List<CheckBox>();
  foreach(var scope in Enum.GetValues<OrderExportFieldScope>())
  {
   var fields=OrderExportFieldRegistry.Fields.Where(f=>f.Scope==scope).ToList();if(fields.Count==0)continue;
   checks.Children.Add(Text(scope switch{OrderExportFieldScope.Header=>"Sipariş",OrderExportFieldScope.Item=>"Ürün kalemi",_=>"Kargo"},14));
   foreach(var field in fields){var cb=new CheckBox{Content=field.Label+(field.IsPii?" (kişisel veri)":""),Tag=field.Id,IsChecked=field.DefaultSelected,Margin=new Thickness(4,2,4,2)};checks.Children.Add(cb);boxes.Add(cb);}
  }
  string? editingId=null;int editingVersion=0;
  void LoadIntoForm(OrderExportTemplate t){editingId=t.Id;editingVersion=t.Version;nameBox.Text=t.Name;foreach(var cb in boxes)cb.IsChecked=t.FieldIds.Contains((string)cb.Tag);}
  void Reset(){editingId=null;editingVersion=0;nameBox.Clear();foreach(var cb in boxes)cb.IsChecked=OrderExportFieldRegistry.TryGet((string)cb.Tag)?.DefaultSelected??false;}
  void Reload(){savedList.ItemsSource=store.List();}
  Reload();
  savedList.SelectionChanged+=(_,_)=>{if(savedList.SelectedItem is OrderExportTemplate t)LoadIntoForm(t);};
  var bar=new WrapPanel{Margin=new Thickness(0,6,0,0)};DockPanel.SetDock(bar,Dock.Bottom);outer.Children.Add(bar);
  var save=Button(bar,"Kaydet");save.Click+=(_,_)=>
  {
   try
   {
    var selected=boxes.Where(cb=>cb.IsChecked==true).Select(cb=>(string)cb.Tag).ToList();
    var template=new OrderExportTemplate{Id=editingId??Guid.NewGuid().ToString("N"),Name=nameBox.Text,FieldIds=selected,Version=editingVersion};
    var result=store.Save(template);Reload();LoadIntoForm(result);status.Text="Şablon kaydedildi.";
   }
   catch(Exception ex){status.Text=ex.Message;}
  };
  var newTemplate=Button(bar,"Yeni");newTemplate.Click+=(_,_)=>{savedList.SelectedItem=null;Reset();};
  var delete=Button(bar,"Sil");delete.Click+=(_,_)=>
  {
   if(savedList.SelectedItem is not OrderExportTemplate t)return;
   try
   {
    // Delete only the exact version this screen showed; a stale result reloads
    // and asks the user to review and delete again - never a silent retry.
    var outcome=store.Delete(t.Id,t.Version);
    Reload();Reset();
    status.Text=outcome switch{OrderExportTemplateDeleteResult.Deleted=>"Şablon silindi.",OrderExportTemplateDeleteResult.AlreadyDeleted=>"Şablon zaten silinmiş; liste yenilendi.",_=>"Şablon siz görüntüledikten sonra değişti; hiçbir şey silinmedi. Liste yenilendi, güncel sürümü inceleyip tekrar silin."};
   }
   catch(Exception ex){status.Text=ex.Message;Reload();}
  };
  var close=Button(bar,"Kapat");close.Click+=(_,_)=>win.Close();
  win.ShowDialog();
 }
}

