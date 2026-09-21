using TrMarketplaceHubDesktop.Catalog;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
namespace TrMarketplaceHubDesktop;
public static class OrdersPanel
{
 public static FrameworkElement Create(string? directory=null,Func<Task<EtsyCredentials>>? authorize=null,Action? catalogChanged=null)
 {
  var store=new OrdersStore(directory);var catalog=new CatalogStore(directory);var root=new DockPanel{Margin=new Thickness(12)};var top=new StackPanel();DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
  top.Children.Add(Text("Siparişler ve kargo takibi",22));
  top.Children.Add(Text("Etsy siparişleri salt okunur alınır. Yolda / teslim edildi gözlemleri yerel olarak kaydedilir. Ozon ve Navlungo otomatik takip bağlantısı henüz yok."));
  var actionBar=new WrapPanel{Margin=new Thickness(0,4,0,4)};top.Children.Add(actionBar);var search=new TextBox{Width=260,Margin=new Thickness(3,5,3,5),ToolTip="Sipariş, mağaza, müşteri, ürün, SKU veya takip numarası ara"};actionBar.Children.Add(search);
  var bar=new WrapPanel{Margin=new Thickness(0,4,0,4)};var filters=new Expander{Header="Ayrıntılı filtreler",Content=bar,IsExpanded=false,Margin=new Thickness(0,2,0,4)};top.Children.Add(filters);
  var filter=new ComboBox{Width=170,ItemsSource=new[]{"Tümü"}.Concat(OrdersRules.States.Select(OrdersRules.Label)).ToArray(),SelectedIndex=0};bar.Children.Add(filter);
  var marketplaceFilter=new ListBox{Width=130,Height=52,SelectionMode=SelectionMode.Extended,ToolTip="Boş seçim: tüm pazaryerleri. Ctrl/Shift ile çoklu seçim."};bar.Children.Add(marketplaceFilter);
  var shopFilter=new ListBox{Width=140,Height=52,SelectionMode=SelectionMode.Extended,ToolTip="Boş seçim: tüm mağazalar. Ctrl/Shift ile çoklu seçim."};bar.Children.Add(shopFilter);
  var stockFilter=new ComboBox{Width=145,ItemsSource=new[]{"Tümü","Stok düşüldü","Stok bekliyor"},SelectedIndex=0};bar.Children.Add(stockFilter);
  var paymentFilter=new ComboBox{Width=145,ItemsSource=new[]{"Tümü"},SelectedIndex=0,ToolTip="Ödeme durumu"};bar.Children.Add(paymentFilter);
  var sourceFilter=new ComboBox{Width=145,ItemsSource=new[]{"Tümü"},SelectedIndex=0,ToolTip="Sipariş kaynağı"};bar.Children.Add(sourceFilter);
  var invoiced=new CheckBox{Content="Faturası olan",Margin=new Thickness(6),VerticalAlignment=VerticalAlignment.Center};var uninvoiced=new CheckBox{Content="Faturası olmayan",Margin=new Thickness(6),VerticalAlignment=VerticalAlignment.Center};bar.Children.Add(invoiced);bar.Children.Add(uninvoiced);
  var dateFrom=new DatePicker{Width=115,ToolTip="Başlangıç tarihi (yerel gün, dahil)"};var dateTo=new DatePicker{Width=115,ToolTip="Bitiş tarihi (yerel gün, dahil)"};
  bar.Children.Add(new TextBlock{Text="Tarih",Margin=new Thickness(6,4,2,4),VerticalAlignment=VerticalAlignment.Center});bar.Children.Add(dateFrom);bar.Children.Add(new TextBlock{Text="—",Margin=new Thickness(2,4,2,4),VerticalAlignment=VerticalAlignment.Center});bar.Children.Add(dateTo);
  var clearDates=Button(bar,"Tarihi temizle");clearDates.Click+=(_,_)=>{dateFrom.SelectedDate=null;dateTo.SelectedDate=null;};
  var viewStore=new UiPreferenceStore(directory);var savedViews=new ComboBox{Width=165,DisplayMemberPath="Name"};var viewName=new TextBox{Width=130,ToolTip="Kayıtlı sipariş görünümü adı"};bar.Children.Add(new TextBlock{Text="Görünüm",Margin=new Thickness(8,4,2,4),VerticalAlignment=VerticalAlignment.Center});bar.Children.Add(savedViews);bar.Children.Add(viewName);
  var openCard=Button(actionBar,"Sipariş kartını aç");var add=Button(actionBar,"+ Yeni sipariş");var excelImport=Button(actionBar,"Excel'den sipariş al");var excelExport=Button(actionBar,"Ayrıntılı Excel");var returns=Button(actionBar,"İadeler");
  var refresh=Button(actionBar,"Etsy'den yenile");var refreshTrendyol=Button(actionBar,"Trendyol'dan yenile");refreshTrendyol.Name="RefreshTrendyolOrders";var refreshNes=Button(actionBar,"NES fatura numaralarını yenile");refreshNes.Name="RefreshNesInvoiceNumbers";var invoiceSelected=Button(actionBar,"Fatura işlemleri");invoiceSelected.Name="InvoiceSelectedOrder";var cancel=Button(actionBar,"İptal");cancel.IsEnabled=false;
  var batchNes=Button(actionBar,"Seçilileri NES kuyruğuna ekle");batchNes.Name="QueueSelectedNesInvoices";var openNesQueue=Button(actionBar,"NES işlem kuyruğu");openNesQueue.Name="OpenNesInvoiceQueue";
  var bulkAction=new ComboBox{Width=195,Margin=new Thickness(3,5,3,5),ItemsSource=new[]{"Toplu işlem seçin","Kargo hazırlık listesi","Stok önizlemesi","İade kayıtlarını incele"},SelectedIndex=0,ToolTip="Seçili siparişler için yalnız yerel önizleme oluşturur."};actionBar.Children.Add(bulkAction);
  var bulkPreview=Button(actionBar,"Seçilileri önizle");
  var exportTemplates=new OrderExportTemplateStore(directory);var manageExport=Button(actionBar,"Excel çıktı alanları");manageExport.Click+=(_,_)=>ExportTemplateDialog(exportTemplates);
  var columnButton=Button(actionBar,"Sütunlar");columnButton.Name="OrderColumns";columnButton.ToolTip="Sipariş tablosunda sürekli görünecek alanları seçin";
  var status=Text("Kayıtlar yükleniyor…");top.Children.Add(status);
  var summary=Text("");summary.FontWeight=FontWeights.SemiBold;top.Children.Add(summary);
  var layout=new Grid();root.Children.Add(layout);
  var grid=new DataGrid{IsReadOnly=true,AutoGenerateColumns=false,EnableRowVirtualization=true,EnableColumnVirtualization=false,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,RowHeight=30,Background=Brushes.White,FrozenColumnCount=3};VirtualizingPanel.SetIsVirtualizing(grid,true);VirtualizingPanel.SetVirtualizationMode(grid,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(grid,true);layout.Children.Add(grid);
  var rowStyle=new Style(typeof(DataGridRow));var returnTrigger=new DataTrigger{Binding=new Binding("IsReturn"),Value=true};returnTrigger.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(255,224,178))));returnTrigger.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.DarkSlateGray));rowStyle.Triggers.Add(returnTrigger);var cancelledTrigger=new DataTrigger{Binding=new Binding("IsCancelled"),Value=true};cancelledTrigger.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(220,38,38))));cancelledTrigger.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.White));cancelledTrigger.Setters.Add(new Setter(Control.FontWeightProperty,FontWeights.SemiBold));rowStyle.Triggers.Add(cancelledTrigger);grid.RowStyle=rowStyle;
  var openCardMenu=new MenuItem{Header="Sipariş kartını aç"};var invoiceMenu=new MenuItem{Header="Fatura işlemleri"};var createInvoiceMenu=new MenuItem{Header="NES faturası hazırla / gönder"};var openInvoiceMenu=new MenuItem{Header="Faturayı aç"};var uploadInvoiceMenu=new MenuItem{Header="Trendyol'a faturayı yükle"};invoiceMenu.Items.Add(createInvoiceMenu);invoiceMenu.Items.Add(openInvoiceMenu);invoiceMenu.Items.Add(uploadInvoiceMenu);grid.ContextMenu=new ContextMenu();grid.ContextMenu.Items.Add(openCardMenu);grid.ContextMenu.Items.Add(new Separator());grid.ContextMenu.Items.Add(invoiceMenu);
  var columnDefinitions=OrderColumns();var visibleColumns=OrderColumnPreferences.Resolve(viewStore.Get(OrderColumnPreferences.StorageKey),columnDefinitions);var gridColumns=new Dictionary<string,DataGridColumn>(StringComparer.Ordinal);
  foreach(var definition in columnDefinitions)
  {
   var column=new DataGridTextColumn{Header=definition.Header,Binding=new Binding(definition.Path),MinWidth=definition.Width,Width=definition.Width,Visibility=visibleColumns.Contains(definition.Key)?Visibility.Visible:Visibility.Collapsed};
   grid.Columns.Add(column);gridColumns[definition.Key]=column;
  }
  void ApplyVisibleColumns(HashSet<string> selected)
  {
   foreach(var definition in columnDefinitions)gridColumns[definition.Key].Visibility=selected.Contains(definition.Key)?Visibility.Visible:Visibility.Collapsed;
   visibleColumns=selected;viewStore.Set(OrderColumnPreferences.StorageKey,OrderColumnPreferences.Serialize(selected));
  }
  columnButton.Click+=(_,_)=>ColumnDialog(Window.GetWindow(root),columnDefinitions,visibleColumns,ApplyVisibleColumns);
  var detail=new StackPanel();
  List<OrderSnapshot> all=[];OrderSnapshot? editing=null;CancellationTokenSource? running=null;bool automaticTrendyolSyncStarted=false;
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
   var payment=paymentFilter.SelectedItem?.ToString()??"Tümü";var source=sourceFilter.SelectedItem?.ToString()??"Tümü";
   grid.ItemsSource=all.Where(o=>MatchesState(o)&&OrderFilterCriteria.Matches(o,q,null,selectedMarketplaces,selectedShops,selectedStock,fromUtc,toUtc)&&OrderAdvancedFilterCriteria.Matches(o,payment,source,invoiced.IsChecked==true?true:null,uninvoiced.IsChecked==true?true:null)).ToList();
   }
   void Load(){all=store.ReadAll();foreach(var order in all)order.StockDecisionLabel=catalog.GetOrderStockStatus(order.Marketplace,order.ShopId,order.OrderId)==null?"Stok bekliyor":"Stok düşüldü";marketplaceFilter.ItemsSource=all.Select(o=>o.Marketplace).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).ToArray();shopFilter.ItemsSource=all.Select(o=>o.ShopId).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x).ToArray();paymentFilter.ItemsSource=new[]{"Tümü"}.Concat(all.Select(o=>o.PaymentStatus).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x)).ToArray();sourceFilter.ItemsSource=new[]{"Tümü"}.Concat(all.Select(o=>o.Source).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x)).ToArray();paymentFilter.SelectedIndex=0;sourceFilter.SelectedIndex=0;Filter();summary.Text=OrderWorkspaceSummary.From(all).Label;status.Text=$"{all.Count} kayıt · Liste son yükleme: {DateTime.Now:g}. Satıra çift tıklayarak sipariş kartını açın.";}
  root.Loaded+=async(_,_)=>
  {
   Load();if(automaticTrendyolSyncStarted)return;automaticTrendyolSyncStarted=true;
   try
   {
    var settingsPath=directory is null?null:Path.Combine(directory,"trendyol.bin");
     if(new TrendyolSettingsStore(settingsPath).Load()!=null)await RefreshTrendyol();
     await ReconcileNes(false);
   }
   catch(Exception ex){status.Text="Trendyol otomatik yenilemesi başlatılamadı: "+MarketplaceConnectionStore.Redact(ex.Message);}
  };
  void Edit(OrderSnapshot order,bool isNew=false)
  {
   editing=order.Copy();var o=editing;Action captureShipment=()=>{};detail.Children.Clear();detail.Children.Add(Text(isNew?"Yeni yerel sipariş":"Sipariş ayrıntısı",18));
   var marketplace=Field(detail,"Pazaryeri",o.Marketplace,!isNew);var shop=Field(detail,"Mağaza kimliği",o.ShopId,!isNew);var id=Field(detail,"Sipariş numarası",o.OrderId,!isNew);
   bool api=o.Source=="Etsy API";var raw=Field(detail,"Sipariş durumu (ham değer)",o.RawStatus,api);var payment=Field(detail,"Ödeme durumu",o.PaymentStatus,api);
   detail.Children.Add(Text($"Kaynak: {o.Source}\nSon API alımı: {o.SyncLabel}"));detail.Children.Add(Text("Ürünler",16));
   foreach(var i in o.Items)detail.Children.Add(Text($"{i.Quantity} × {i.Title} · SKU: {i.Sku}"+(i.UnitPrice.HasValue?$" · {i.UnitPrice:0.00} {o.Currency} · KDV %{i.VatRate}":"")));
   if(o.CustomerName.Length>0)detail.Children.Add(Text("Müşteri: "+o.CustomerName));
   if(o.BillingName.Length+o.BillingAddress.Length>0)detail.Children.Add(Text($"Fatura: {o.BillingName} · {o.BillingAddress} · {o.BillingDistrict} / {o.BillingCity}"));
   if(o.ShippingName.Length+o.ShippingAddress.Length>0)detail.Children.Add(Text($"Sevk: {o.ShippingName} · {o.ShippingAddress} · {o.ShippingDistrict} / {o.ShippingCity}"));
   if(o.InvoiceNumber.Length>0)detail.Children.Add(Text($"Fatura: {o.InvoiceProvider} · {o.InvoiceType} · {o.InvoiceNumber} · {o.InvoiceStatus}\nUUID: {o.InvoiceUuid}"));
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
  var saveView=Button(bar,"Görünümü kaydet");saveView.Click+=(_,_)=>{viewStore.SaveView("orders",viewName.Text,JsonSerializer.Serialize(new{search=search.Text,state=filter.SelectedIndex,marketplaces=marketplaceFilter.SelectedItems.Cast<string>().ToArray(),shops=shopFilter.SelectedItems.Cast<string>().ToArray(),stock=stockFilter.SelectedItem?.ToString()??"Tümü",dateFrom=dateFrom.SelectedDate.HasValue?dateFrom.SelectedDate.Value.ToString("O"):null,dateTo=dateTo.SelectedDate.HasValue?dateTo.SelectedDate.Value.ToString("O"):null}));viewName.Clear();ReloadViews();};var deleteView=Button(bar,"Görünümü sil");deleteView.Click+=(_,_)=>{if(savedViews.SelectedItem is not SavedUiView view)throw new InvalidOperationException("Önce görünüm seçin.");viewStore.DeleteView("orders",view.Name);ReloadViews();};
   void OpenSelectedCard(){if(grid.SelectedItem is not OrderSnapshot selected){status.Text="Önce sipariş seçin.";return;}OrderCardWindow.ShowExisting(selected,directory,Load,catalogChanged);}
   bulkPreview.Click+=(_,_)=>{var selected=grid.SelectedItems.OfType<OrderSnapshot>().ToList();if(selected.Count==0){status.Text="Önce listeden bir veya daha fazla sipariş seçin.";return;}var action=bulkAction.SelectedIndex;if(action==0){status.Text="Önce toplu işlem türünü seçin.";return;}if(action==3){var w=new Window{Title="İade yönetimi",Width=1250,Height=760,Owner=Window.GetWindow(root),WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=OrderExceptionsPanel.Create(directory)};w.ShowDialog();return;}var lines=selected.Select(order=>{var items=order.Items.Sum(x=>x.Quantity);var shipment=order.Shipments.FirstOrDefault();return action==1?$"{order.OrderId} · {items} ürün · {shipment?.Carrier??"taşıyıcı bekliyor"} · {shipment?.TrackingNumber??"takip no yok"}":$"{order.OrderId} · {order.StockDecisionLabel} · {items} ürün kalemi";});MessageBox.Show(Window.GetWindow(root),string.Join(Environment.NewLine,lines)+"\n\nBu yalnız önizlemedir; pazaryerine, kargoya veya stoka yazılmadı.","Toplu işlem önizlemesi",MessageBoxButton.OK,MessageBoxImage.Information);status.Text=$"{selected.Count} sipariş için önizleme gösterildi.";};
   openCard.Click+=(_,_)=>OpenSelectedCard();openCardMenu.Click+=(_,_)=>OpenSelectedCard();grid.MouseDoubleClick+=(_,_)=>OpenSelectedCard();add.Click+=(_,_)=>OrderCardWindow.ShowNew(directory,Load);
   excelImport.Click+=(_,_)=>{var w=new Window{Title="Excel'den sipariş al",Width=1280,Height=820,Owner=Window.GetWindow(root),WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new ExcelWorkspacePanel(catalog,directory,"orders",Load)};w.ShowDialog();};
   excelExport.Click+=(_,_)=>{try{var selected=grid.SelectedItems.OfType<OrderSnapshot>().ToList();var rows=selected.Count>0?selected:grid.Items.OfType<OrderSnapshot>().ToList();var picker=new SaveFileDialog{Filter="Excel (*.xlsx)|*.xlsx",FileName="siparisler-ayrintili.xlsx"};if(picker.ShowDialog(Window.GetWindow(root))==true){OrderExcelExport.WriteDetailed(picker.FileName,rows);status.Text=$"{rows.Count} sipariş ayrıntılı Excel dosyasına aktarıldı.";}}catch(Exception ex){status.Text=MarketplaceConnectionStore.Redact(ex.Message);}};
   returns.Click+=(_,_)=>{var w=new Window{Title="İade yönetimi",Width=1250,Height=760,Owner=Window.GetWindow(root),WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=OrderExceptionsPanel.Create(directory)};w.ShowDialog();};
   search.TextChanged+=(_,_)=>Filter();filter.SelectionChanged+=(_,_)=>Filter();marketplaceFilter.SelectionChanged+=(_,_)=>Filter();shopFilter.SelectionChanged+=(_,_)=>Filter();stockFilter.SelectionChanged+=(_,_)=>Filter();paymentFilter.SelectionChanged+=(_,_)=>Filter();sourceFilter.SelectionChanged+=(_,_)=>Filter();invoiced.Checked+=(_,_)=>Filter();invoiced.Unchecked+=(_,_)=>Filter();uninvoiced.Checked+=(_,_)=>Filter();uninvoiced.Unchecked+=(_,_)=>Filter();dateFrom.SelectedDateChanged+=(_,_)=>Filter();dateTo.SelectedDateChanged+=(_,_)=>Filter();savedViews.SelectionChanged+=(_,_)=>{if(savedViews.SelectedItem is SavedUiView view)try{LoadView(view);Filter();}catch{status.Text="Kayıtlı görünüm okunamadı.";}};
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
   async Task RefreshTrendyol()
   {
   if(running!=null)return;using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(3));running=cts;refresh.IsEnabled=refreshTrendyol.IsEnabled=add.IsEnabled=false;cancel.IsEnabled=true;detail.IsEnabled=false;status.Text="Trendyol siparişleri okunuyor…";
   try{var credentials=new TrendyolSettingsStore(directory is null?null:Path.Combine(directory,"trendyol.bin")).Load()??throw new InvalidOperationException("Önce Trendyol mağaza ayarlarında satıcı ID ve API bilgilerini kaydedin.");using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(45)};var rows=await new TrendyolOrderClient(credentials,http).ReadRecentAsync(cts.Token);cts.Token.ThrowIfCancellationRequested();await Task.Run(()=>store.SaveBatch(rows),cts.Token);Load();detail.Children.Clear();status.Text=$"Trendyol: {rows.Count} sipariş/paket alındı · {DateTime.Now:g}.";}
    catch(OperationCanceledException){status.Text="Trendyol alımı iptal edildi veya zaman aşımına uğradı; önceki kayıtlar korundu.";}catch(InvalidOperationException ex){status.Text=MarketplaceConnectionStore.Redact(ex.Message);}catch{status.Text="Trendyol siparişleri alınamadı. Mağaza API bilgilerini ve sipariş okuma yetkisini kontrol edin.";}finally{running=null;refresh.IsEnabled=refreshTrendyol.IsEnabled=add.IsEnabled=true;cancel.IsEnabled=false;detail.IsEnabled=true;}
   }
   async Task ReconcileNes(bool userInitiated)
   {
    if(running!=null)return;var settingsPath=directory is null?null:Path.Combine(directory,"nes.bin");var settings=new NesSettingsStore(settingsPath).Load();
    if(settings is null){if(userInitiated)status.Text="NES fatura numarası alınamadı: önce NES e-Fatura ekranında bağlantı ve firma ayarlarını güvenli kaydedin.";return;}
    using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(2));running=cts;refreshNes.IsEnabled=refreshTrendyol.IsEnabled=false;cancel.IsEnabled=true;status.Text="NES giden faturalar okunuyor ve sipariş numaraları eşleştiriliyor…";
    try
    {
     using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60)};var api=new NesConnection(http);
     var invoices=await api.ReadOutgoingAsync(settings,NesDocumentKind.EInvoice,cts.Token);var archives=await api.ReadOutgoingAsync(settings,NesDocumentKind.EArchive,cts.Token);var result=NesOrderReconciler.Reconcile(store,invoices.Concat(archives));Load();status.Text="NES mutabakatı tamamlandı: "+result.Label+".";
    }
    catch(OperationCanceledException){status.Text="NES fatura numarası alımı zaman aşımına uğradı; mevcut kayıtlar korundu.";}
    catch(ArgumentException ex){status.Text=MarketplaceConnectionStore.Redact(ex.Message);}
    catch(InvalidOperationException ex){status.Text=MarketplaceConnectionStore.Redact(ex.Message);}
    catch{status.Text="NES fatura numaraları alınamadı. API ortamını, anahtarı ve yetkiyi kontrol edin.";}
    finally{running=null;refreshNes.IsEnabled=refreshTrendyol.IsEnabled=true;cancel.IsEnabled=false;}
   }
   void OpenInvoice(string? requestedAction=null)
  {
   if(grid.SelectedItem is not OrderSnapshot order){status.Text="Önce faturası işlenecek tek siparişi seçin.";return;}
    OrderInvoiceDialog.Show(order,directory,requestedAction,Load);
  }
  refreshTrendyol.Click+=async(_,_)=>await RefreshTrendyol();refreshNes.Click+=async(_,_)=>await ReconcileNes(true);invoiceSelected.Click+=(_,_)=>OpenInvoice();createInvoiceMenu.Click+=(_,_)=>OpenInvoice("prepare");openInvoiceMenu.Click+=(_,_)=>OpenInvoice("open");uploadInvoiceMenu.Click+=(_,_)=>OpenInvoice("upload");
   batchNes.Click+=(_,_)=>{try{var selected=grid.SelectedItems.OfType<OrderSnapshot>().ToList();if(selected.Count==0){status.Text="Önce NES kuyruğuna eklenecek siparişleri seçin.";return;}var added=NesBatchInvoiceDialog.Show(Window.GetWindow(root),selected,directory);if(added>0)status.Text=$"{added} sipariş NES kuyruğuna eklendi; faturalar sırayla işlenecek.";}catch(Exception ex){status.Text=MarketplaceConnectionStore.Redact(ex.Message);}};
   openNesQueue.Click+=(_,_)=>{var w=new Window{Title="NES işlem kuyruğu",Width=1250,Height=760,Owner=Window.GetWindow(root),WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=NesQueuePanel.Create(directory)};w.Show();};
   ReloadViews();Load();return root;
 }
  sealed record StateChoice(string Value,string Label);
  static TextBlock Text(string text,int size=12)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(3,4,3,7),Foreground=Brushes.DarkSlateGray};
  static TextBox Field(Panel parent,string label,string value,bool readOnly=false){parent.Children.Add(Text(label));var box=new TextBox{Text=value,IsReadOnly=readOnly};parent.Children.Add(box);return box;}
  static Button Button(Panel parent,string text){var button=new Button{Content=text,Margin=new Thickness(3,5,3,5)};parent.Children.Add(button);return button;}
  static IReadOnlyList<OrderColumnDefinition> OrderColumns()=>
  [
   new("record","Kayıt ID","RecordIdLabel",85,true,true),new("order","Sipariş No","OrderId",120,true,true),new("reference","Platform Referansı","PlatformReference",130,true,true),
   new("invoice-number","Fatura No","InvoiceNumberDisplay",150),new("vat","KDV","VatTotalLabel",95),new("order-date","Sipariş Tarihi","OrderDateLabel",130),new("added-date","Sisteme Eklenme","AddedAtLabel",130),
   new("status","Durum","StatusLabel",130),new("payment-status","Ödeme Durumu","PaymentStatus",110),new("payment-type","Ödeme Tipi","PaymentType",105),new("integration","Entegrasyon","Marketplace",100),new("shop","Mağaza","ShopId",105),new("source","Kaynak","Source",120),
   new("market-status","Pazaryeri Durumu","RawStatus",130),new("market-invoice","Pazaryeri Fatura Durumu","MarketplaceInvoiceLabel",175),new("customer-code","Müşteri Kodu","CustomerCode",120,false),new("customer","Müşteri","CustomerName",150),new("shipping-name","Müşteri Sevk İsmi","ShippingName",155),new("billing-name","Müşteri Fatura İsmi","BillingName",170),
   new("product-code","Mağaza Ürün Kodu","ProductCodes",145),new("sku","Ürün Kodu","ProductSkus",130),new("barcode","Barkod","Barcodes",135),new("market-product","Mağaza Ürünü","MarketplaceProducts",260),new("invoice-product","Fatura Ürünü","InvoiceProducts",260),
   new("invoice-write-status","Fatura Yazım Durumu","InvoiceStatus",145),new("tax-id","VKN / TCKN","TaxIdentityDisplay",125),new("tax-office","Vergi Dairesi","CustomerTaxOffice",125),new("billing-city","Fatura İli","BillingCity",105),new("billing-district","Fatura İlçesi","BillingDistrict",110),new("phone","Telefon Numarası","CustomerPhone",135,false),
   new("cargo-state","Kargo Durumu","DeliveryLabel",145),new("carrier","Taşıyıcı","Carriers",120),new("tracking","Takip No","TrackingNumbers",135),new("cargo-code","Kargo Kodu","CargoCodes",130),new("cargo-payment","Kargo Ödemesi","CargoPaymentParties",120),new("package-type","Kargo Paket Tipi","CargoPackageTypes",125),new("package-count","Kargo Paket Adedi","CargoPackageCount",120),
   new("net-total","Fatura Tutarı","NetTotalLabel",105),new("discount","İndirim Tutarı","DiscountTotalLabel",105,false),new("total","Genel Toplam","TotalLabel",105),new("commission","Komisyon Tutarı","CommissionTotalLabel",115),new("line-count","Toplam Kalem Sayısı","TotalLineCount",120),new("quantity","Toplam Ürün Sayısı","TotalProductQuantity",120),new("stock","Stok Kararı","StockDecisionLabel",120)
  ];
  static void ColumnDialog(Window? owner,IReadOnlyList<OrderColumnDefinition> definitions,IReadOnlySet<string> selected,Action<HashSet<string>> apply)
  {
   var win=new Window{Title="Sipariş tablosu sütunları",Width=440,Height=680,WindowStartupLocation=owner is null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner};if(owner is not null)win.Owner=owner;
   var root=new DockPanel{Margin=new Thickness(12)};win.Content=root;
   var heading=new StackPanel();DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);heading.Children.Add(Text("Tabloda gösterilecek alanlar",18));heading.Children.Add(Text("Seçiminiz bu bilgisayarda saklanır ve programı yeniden açtığınızda korunur."));
   var boxes=new List<(OrderColumnDefinition Definition,CheckBox Box)>();var list=new StackPanel();var scroll=new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};root.Children.Add(scroll);
   foreach(var definition in definitions)
   {
    var box=new CheckBox{Content=definition.Header,IsChecked=selected.Contains(definition.Key),IsEnabled=!definition.Required,Margin=new Thickness(5,3,5,3),ToolTip=definition.Required?"Kimlik sütunu her zaman görünür.":null};list.Children.Add(box);boxes.Add((definition,box));
   }
   var buttons=new WrapPanel{Margin=new Thickness(0,8,0,0)};DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);
   var all=Button(buttons,"Tümünü göster");all.Click+=(_,_)=>{foreach(var item in boxes)item.Box.IsChecked=true;};
   var defaults=Button(buttons,"Varsayılana dön");defaults.Click+=(_,_)=>{foreach(var item in boxes)item.Box.IsChecked=item.Definition.DefaultVisible||item.Definition.Required;};
   var save=Button(buttons,"Kaydet");save.Click+=(_,_)=>{var keys=boxes.Where(x=>x.Box.IsChecked==true||x.Definition.Required).Select(x=>x.Definition.Key).ToHashSet(StringComparer.Ordinal);apply(keys);win.Close();};
   var close=Button(buttons,"Vazgeç");close.Click+=(_,_)=>win.Close();win.ShowDialog();
  }
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

