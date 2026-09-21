using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
namespace TrMarketplaceHubDesktop;
public sealed class OrderSnapshot
{
 public string CustomerName {get;set;}="";
 public string BillingName {get;set;}="";
 public string BillingAddress {get;set;}="";
 public string BillingCity {get;set;}="";
 public string BillingDistrict {get;set;}="";
 public string ShippingName {get;set;}="";
 public string ShippingAddress {get;set;}="";
 public string ShippingCity {get;set;}="";
 public string ShippingDistrict {get;set;}="";
 public string TaxNumber {get;set;}="";
 public string BillingEmail {get;set;}="";
 public string CustomerPhone {get;set;}="";
 public string InvoiceProvider {get;set;}="";
 public string InvoiceType {get;set;}="";
 public string InvoiceNumber {get;set;}="";
 public string InvoiceUuid {get;set;}="";
 public string InvoiceStatus {get;set;}="";
 public string InvoiceUrl {get;set;}="";
 public DateTimeOffset? InvoiceIssuedAt {get;set;}
 public string InvoiceLabel=>InvoiceNumber.Length>0?$"{InvoiceNumber} · {InvoiceStatus}":"Fatura yok";
 [JsonIgnore] public string RecordIdLabel=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Marketplace}\n{ShopId}\n{OrderId}")))[..8];
 [JsonIgnore] public string InvoiceNumberDisplay=>InvoiceNumber.Length>0?InvoiceNumber:IsMarketplaceInvoiced?"NES ile eşleştir":"Bekliyor";
 [JsonIgnore] public string VatTotalLabel=>(VatTotal??OrderTotalsCalculator.Calculate(this).Vat).ToString("N2",CultureInfo.GetCultureInfo("tr-TR"))+" "+Currency;
 [JsonIgnore] public string OrderDateLabel=>OrderDate?.ToLocalTime().ToString("dd.MM.yyyy HH:mm")??"—";
 [JsonIgnore] public string AddedAtLabel=>AddedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm")??"—";
 public bool? EInvoiceAvailable {get;set;}
 public bool CrossBorderByTrendyol {get;set;}
 public bool MicroExport {get;set;}
 public bool CommercialInvoice {get;set;}
 public long MarketplaceCustomerId {get;set;}
 public string MarketplaceInvoiceStatus {get;set;}="";
 [JsonIgnore] public bool IsMarketplaceInvoiced=>MarketplaceInvoiceStatus.Equals("Invoiced",StringComparison.OrdinalIgnoreCase)||MarketplaceInvoiceStatus.Equals("InvoiceUploaded",StringComparison.OrdinalIgnoreCase)||MarketplaceInvoiceStatus.Equals("Uploaded",StringComparison.OrdinalIgnoreCase);
 [JsonIgnore] public string MarketplaceInvoiceLabel=>MarketplaceInvoiceStatus.Length==0?"Bilinmiyor":IsMarketplaceInvoiced&&InvoiceNumber.Length==0?"Faturalandı · eşleştirilecek":MarketplaceInvoiceStatus;
 public List<string> MarketplaceInvoiceErrors {get;set;}=[];

 public string Marketplace {get;set;}="";
 public string ShopId {get;set;}="";
 public string OrderId {get;set;}="";
 public string PlatformReference {get;set;}="";
 public DateTimeOffset? OrderDate {get;set;}
 public DateTimeOffset? AddedAt {get;set;}
 public string RawStatus {get;set;}="";
 [JsonIgnore] public bool IsCancelled=>OrderStatusRules.Classify(RawStatus)==OrderLifecycleKind.Cancelled;
 [JsonIgnore] public bool IsReturn=>OrderStatusRules.Classify(RawStatus)==OrderLifecycleKind.Return;
 [JsonIgnore] public string StatusLabel=>OrderStatusRules.Label(RawStatus);
 public string PaymentStatus {get;set;}="Bilinmiyor";
 public string PaymentType {get;set;}="";
 public string CustomerId {get;set;}="";
 public string CustomerCode {get;set;}="";
 public string CustomerEmail {get;set;}="";
 public string CustomerTaxOffice {get;set;}="";
 public string CustomerIdentityNumber {get;set;}="";
 public string BillingCountry {get;set;}="";
 public string ShippingCountry {get;set;}="";
 public string ShippingPhone {get;set;}="";
 public string SellerNote {get;set;}="";
 public string Source {get;set;}="Yerel / manuel";
 public decimal? Total {get;set;}
 public decimal? NetTotal {get;set;}
 public decimal? VatTotal {get;set;}
 public decimal? DiscountTotal {get;set;}
 public decimal? CommissionTotal {get;set;}
 public decimal? UndiscountedTotal {get;set;}
 public string Currency {get;set;}="";
 public string TotalLabel=>Total.HasValue?$"{Total:0.00} {Currency}":"—";
 [JsonIgnore] public string NetTotalLabel=>Money(NetTotal??OrderTotalsCalculator.Calculate(this).Net);
 [JsonIgnore] public string DiscountTotalLabel=>Money(DiscountTotal??Items.Sum(x=>x.DiscountAmount??0));
 [JsonIgnore] public string CommissionTotalLabel=>Money(CommissionTotal??Items.Sum(x=>x.CommissionAmount??0));
 [JsonIgnore] public string TaxIdentityDisplay=>OrderInvoiceRules.DisplayIdentity(this);
 [JsonIgnore] public string ProductCodes=>Join(Items.Select(x=>x.MarketplaceProductCode.Length>0?x.MarketplaceProductCode:x.ProductId));
 [JsonIgnore] public string ProductSkus=>Join(Items.Select(x=>x.Sku));
 [JsonIgnore] public string Barcodes=>Join(Items.Select(x=>x.Barcode));
 [JsonIgnore] public string MarketplaceProducts=>Join(Items.Select(x=>x.Title));
 [JsonIgnore] public string InvoiceProducts=>Join(Items.Select(x=>x.InvoiceTitle.Length>0?x.InvoiceTitle:x.Title));
 [JsonIgnore] public int TotalLineCount=>Items.Count;
 [JsonIgnore] public int TotalProductQuantity=>Items.Sum(x=>x.Quantity);
 public DateTimeOffset UpdatedAt {get;set;}=DateTimeOffset.UtcNow;
 public DateTimeOffset SourceUpdatedAt {get;set;}
 public DateTimeOffset LastSync {get;set;}
 [JsonIgnore] public string StockDecisionLabel {get;set;}="Bilinmiyor";
 public List<OrderItem> Items {get;set;}=[];
 public List<OrderShipment> Shipments {get;set;}=[];
 public string DeliveryLabel=>Shipments.Count==0?"Bilinmiyor":string.Join(", ",Shipments.Select(s=>OrdersRules.Label(s.State)).Distinct());
 public string TrackingNumbers=>string.Join(", ",Shipments.Select(s=>s.TrackingNumber).Where(s=>s.Length>0));
 public string Carriers=>string.Join(", ",Shipments.Select(s=>s.Carrier).Where(s=>s.Length>0).Distinct());
 [JsonIgnore] public string CargoCodes=>Join(Shipments.Select(s=>s.PackageCode.Length>0?s.PackageCode:s.Id));
 [JsonIgnore] public string CargoPaymentParties=>Join(Shipments.Select(s=>s.PaymentParty));
 [JsonIgnore] public string CargoPackageTypes=>Join(Shipments.Select(s=>s.DeliveryType));
 [JsonIgnore] public int CargoPackageCount=>Shipments.Count;
 public string SyncLabel=>LastSync==default?"API senkronizasyonu yok":LastSync.ToLocalTime().ToString("g");
 public OrderSnapshot Copy()=>JsonSerializer.Deserialize<OrderSnapshot>(JsonSerializer.Serialize(this))!;
 string Money(decimal value)=>value.ToString("N2",CultureInfo.GetCultureInfo("tr-TR"))+" "+Currency;
 static string Join(IEnumerable<string> values)=>string.Join(", ",values.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase));
}
public sealed class OrderItem
{
 public long MarketplaceLineId {get;set;}
 public string ProductId {get;set;}="";
 public string MarketplaceProductCode {get;set;}="";
 public string Barcode {get;set;}="";
 public decimal? UnitPrice {get;set;}
 public decimal? NetUnitPrice {get;set;}
 public decimal VatRate {get;set;}=20;
 public decimal? DiscountAmount {get;set;}
 public decimal? CommissionAmount {get;set;}
 public decimal? PurchasePrice {get;set;}
 public string Title {get;set;}="";
 public string InvoiceTitle {get;set;}="";
 public string Sku {get;set;}="";
 public int Quantity {get;set;}=1;
}
public sealed class OrderShipment
{
 public string Id {get;set;}="";
 public string Carrier {get;set;}="";
 public string TrackingNumber {get;set;}="";
 public string TrackingUrl {get;set;}="";
 public string PackageCode {get;set;}="";
 public string PaymentParty {get;set;}="";
 public string DeliveryType {get;set;}="";
 public DateTimeOffset? ShippedAt {get;set;}
 public DateTimeOffset? DeliveredAt {get;set;}
 public string State {get;set;}="Unknown";
 public string Source {get;set;}="Yerel / manuel";
 public List<OrderTrackingEvent> Events {get;set;}=[];
 public string Label=>$"{Id} · {Carrier} · {TrackingNumber} · {OrdersRules.Label(State)} ({Source})";
}
public sealed record OrderTrackingEvent(DateTimeOffset At,string State,string Source);
public enum OrderLifecycleKind { Active, Cancelled, Return }
public static class OrderStatusRules
{
 public static OrderLifecycleKind Classify(string? rawStatus)=>Normalize(rawStatus) switch
 {
  "cancelled" or "canceled" or "unsupplied" or "iptal" or "iptaledildi"=>OrderLifecycleKind.Cancelled,
  "returned" or "return" or "unpacked" or "iade" or "iadeedildi" or "iadesurecinde"=>OrderLifecycleKind.Return,
  _=>OrderLifecycleKind.Active
 };
 public static string Label(string? rawStatus)=>Normalize(rawStatus) switch
 {
  "cancelled" or "canceled" or "iptal" or "iptaledildi"=>"İptal edildi",
  "unsupplied"=>"Tedarik edilemedi / iptal",
  "returned" or "return" or "iade" or "iadeedildi"=>"İade edildi",
  "unpacked" or "iadesurecinde"=>"İade sürecinde",
  "created"=>"Yeni sipariş","picking"=>"Hazırlanıyor","invoiced"=>"Faturalandı",
  "shipped"=>"Kargoya verildi","atcollectionpoint"=>"Teslimat noktasında","intransit"=>"Yolda","delivered"=>"Teslim edildi",
  _=>string.IsNullOrWhiteSpace(rawStatus)?"Bilinmiyor":rawStatus.Trim()
 };
 static string Normalize(string? value)=>new string((value??"").Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
public static class OrdersRules
{
 public static readonly string[] States=["Unknown","Preparing","Shipped","InTransit","Delivered","Exception","Returned"];
 public static string Label(string state)=>state switch {"Preparing"=>"Hazırlanıyor","Shipped"=>"Gönderildi (taşıma doğrulanmadı)","InTransit"=>"Yolda","Delivered"=>"Teslim edildi","Exception"=>"Teslimat sorunu","Returned"=>"İade",_=>"Bilinmiyor"};
 public static bool SafeTrackingUrl(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme==Uri.UriSchemeHttps&&uri.UserInfo.Length==0&&!string.IsNullOrWhiteSpace(uri.Host);
 public static void Validate(OrderSnapshot o)
 {
  if(string.IsNullOrWhiteSpace(o.Marketplace)||string.IsNullOrWhiteSpace(o.ShopId)||string.IsNullOrWhiteSpace(o.OrderId))throw new ArgumentException("Pazaryeri, mağaza ve sipariş numarası zorunlu.");
  if(o.Shipments.Any(s=>string.IsNullOrWhiteSpace(s.Id))||o.Shipments.Select(s=>s.Id).Distinct().Count()!=o.Shipments.Count)throw new ArgumentException("Paket kimlikleri boş veya tekrarlı olamaz.");
  if(o.Shipments.Any(s=>s.TrackingUrl.Length>0&&!SafeTrackingUrl(s.TrackingUrl)))throw new ArgumentException("Takip bağlantısı kullanıcı bilgisi içermeyen HTTPS adresi olmalı.");
  if(o.Items.Any(i=>string.IsNullOrWhiteSpace(i.Title)||i.Quantity<=0))throw new ArgumentException("Ürün adı ve pozitif adet zorunlu.");
 }
}
/// Pure predicate extracted from OrdersPanel's in-memory filter bar so the
/// date-range (#1971) and marketplace/shop multi-select (#1972) rules are
/// directly testable without a WPF DataGrid/ListBox host.
public static class OrderFilterCriteria
{
 public static bool Matches(OrderSnapshot o,string search,string? stateFilter,IReadOnlyCollection<string> marketplaces,IReadOnlyCollection<string> shops,string stockFilter,DateTimeOffset? fromUtc,DateTimeOffset? toUtc)
 {
  var q=(search??"").Trim();
  if(q.Length>0&&!$"{o.Marketplace} {o.ShopId} {o.OrderId} {o.PlatformReference} {o.CustomerName} {o.BillingName} {o.ShippingName} {o.TrackingNumbers} {string.Join(' ',o.Items.Select(i=>i.Title+" "+i.Sku+" "+i.Barcode))}".Contains(q,StringComparison.CurrentCultureIgnoreCase))return false;
  if(!string.IsNullOrEmpty(stateFilter)&&!o.Shipments.Any(s=>s.State==stateFilter))return false;
  if(marketplaces.Count>0&&!marketplaces.Contains(o.Marketplace,StringComparer.OrdinalIgnoreCase))return false;
  if(shops.Count>0&&!shops.Contains(o.ShopId,StringComparer.OrdinalIgnoreCase))return false;
  if(stockFilter=="Stok düşüldü"&&o.StockDecisionLabel!="Stok düşüldü")return false;
  if(stockFilter=="Stok bekliyor"&&o.StockDecisionLabel=="Stok düşüldü")return false;
  if(fromUtc.HasValue&&o.UpdatedAt<fromUtc.Value)return false;
  if(toUtc.HasValue&&o.UpdatedAt>toUtc.Value)return false;
  return true;
 }
}

public static class OrderAdvancedFilterCriteria
{
 public static bool Matches(OrderSnapshot order,string payment,string source,bool? invoiced,bool? uninvoiced)
 {
  if(!string.IsNullOrWhiteSpace(payment)&&payment!="Tümü"&&!order.PaymentStatus.Equals(payment,StringComparison.CurrentCultureIgnoreCase)&&!order.PaymentType.Equals(payment,StringComparison.CurrentCultureIgnoreCase))return false;
  if(!string.IsNullOrWhiteSpace(source)&&source!="Tümü"&&!order.Source.Equals(source,StringComparison.CurrentCultureIgnoreCase))return false;
  if(invoiced==true&&order.InvoiceNumber.Length==0)return false;
  if(uninvoiced==true&&order.InvoiceNumber.Length>0)return false;
  return true;
 }
}

public static class OrderInvoiceRules
{
 public static NesDocumentKind DefaultKind(OrderSnapshot order)
  => order.CrossBorderByTrendyol||order.EInvoiceAvailable==true?NesDocumentKind.EInvoice:NesDocumentKind.EArchive;

 public static string DisplayIdentity(OrderSnapshot order)
 {
  if(!string.IsNullOrWhiteSpace(order.TaxNumber))return order.TaxNumber.Trim();
  try{return NesReceiverIdentityResolver.Resolve(order,DefaultKind(order)).Value;}
  catch(InvalidOperationException){return "Eksik — Trendyol'dan VKN / TCKN alınamadı";}
 }

 public static void ValidateCanInvoice(OrderSnapshot order)
 {
  OrdersRules.Validate(order);
  var state=(order.RawStatus??"").Trim();
  if(state.Equals("Cancelled",StringComparison.OrdinalIgnoreCase)||state.Equals("Canceled",StringComparison.OrdinalIgnoreCase)||state.Equals("UnSupplied",StringComparison.OrdinalIgnoreCase)||state.Equals("UnPacked",StringComparison.OrdinalIgnoreCase)||state.Equals("Awaiting",StringComparison.OrdinalIgnoreCase))
   throw new InvalidOperationException("İptal, tedarik edilemeyen veya henüz kesinleşmemiş sipariş faturalanamaz.");
  var marketplaceInvoice=(order.MarketplaceInvoiceStatus??"").Trim();
  if(marketplaceInvoice.Equals("Invoiced",StringComparison.OrdinalIgnoreCase)||marketplaceInvoice.Equals("InvoiceUploaded",StringComparison.OrdinalIgnoreCase)||marketplaceInvoice.Equals("Uploaded",StringComparison.OrdinalIgnoreCase))
   throw new InvalidOperationException("Pazaryeri bu siparişi faturalanmış gösteriyor. İkinci fatura oluşturulmadı; mevcut NES faturası sipariş numarasıyla eşleştirilmelidir.");
  if(order.InvoiceUuid.Length>0)throw new InvalidOperationException("Bu siparişe NES faturası zaten bağlanmış.");
  if(order.Marketplace.Equals("Trendyol",StringComparison.OrdinalIgnoreCase)&&!order.Shipments.Any(x=>long.TryParse(x.Id,out var id)&&id>0))
   throw new InvalidOperationException("Trendyol paket kimliği bulunamadığı için fatura işlemi başlatılamadı.");
  if(order.Items.Count==0)throw new InvalidOperationException("Fatura oluşturmak için sipariş satırı bulunamadı.");
 }
}

/// <summary>Order workspace counters. Kept independent from WPF so queue
/// totals remain deterministic in the screen and in automated tests.</summary>
public sealed record OrderWorkspaceSummary(int Total,int Preparation,int InTransit,int Returns,int Exceptions)
{
 public static OrderWorkspaceSummary From(IEnumerable<OrderSnapshot> orders)
 {
  var rows=orders.ToList();
  bool Has(OrderSnapshot order,string state)=>order.Shipments.Any(s=>string.Equals(s.State,state,StringComparison.Ordinal));
  return new(
   rows.Count,
   rows.Count(o=>o.Shipments.Count==0||Has(o,"Preparing")||Has(o,"Unknown")),
   rows.Count(o=>Has(o,"Shipped")||Has(o,"InTransit")),
   rows.Count(o=>Has(o,"Returned")),
   rows.Count(o=>Has(o,"Exception")));
 }
 public string Label=>$"Toplam {Total:N0}   •   Hazırlık {Preparation:N0}   •   Yolda {InTransit:N0}   •   İade {Returns:N0}   •   Sorun {Exceptions:N0}";
}
public static class OrderNormalizer
{
 public static OrderSnapshot Normalize(OrderSnapshot order)
 {
  order.Marketplace=Limit(order.Marketplace,80); order.ShopId=Limit(order.ShopId,200); order.OrderId=Limit(order.OrderId,200); order.RawStatus=NormalizeStatus(order.RawStatus); order.PaymentStatus=Limit(order.PaymentStatus,80); order.Currency=order.Currency.Trim().ToUpperInvariant(); if(order.Currency.Length>3)order.Currency=order.Currency[..3];
  foreach(var item in order.Items){item.Title=Limit(item.Title,500);item.Sku=Limit(item.Sku,200);}
  foreach(var shipment in order.Shipments){shipment.Id=Limit(shipment.Id,200);shipment.Carrier=Limit(shipment.Carrier,160);shipment.TrackingNumber=Limit(shipment.TrackingNumber,200);shipment.TrackingUrl=Limit(shipment.TrackingUrl,2000);shipment.State=NormalizeState(shipment.State);}
  if(order.SourceUpdatedAt==default)order.SourceUpdatedAt=order.UpdatedAt; order.UpdatedAt=order.SourceUpdatedAt; return order;
 }
 static string NormalizeStatus(string value)=>Limit(value,80).Trim().ToLowerInvariant() switch {"paid" or "completed" or "open"=>"paid", "canceled" or "cancelled" or "refunded"=>"cancelled", "shipped"=>"shipped", _=>Limit(value,80).Trim().ToLowerInvariant()};
 static string NormalizeState(string value)=>value.Trim().ToLowerInvariant() switch {"shipped"=>"Shipped","in_transit" or "in transit"=>"InTransit","delivered"=>"Delivered","returned" or "return"=>"Returned","cancelled" or "canceled"=>"Exception", _=>"Unknown"};
 static string Limit(string value,int max){value=(value??"").Trim();return value.Length>max?value[..max]:value;}
}
