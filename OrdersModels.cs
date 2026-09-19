using System.Text.Json;
using System.Text.Json.Serialization;
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

 public string Marketplace {get;set;}="";
 public string ShopId {get;set;}="";
 public string ConnectionId {get;set;}="";
 public string ConnectionDisplayName {get;set;}="";
 public string OrderId {get;set;}="";
 public string RawStatus {get;set;}="";
 public string PaymentStatus {get;set;}="Bilinmiyor";
 public string Source {get;set;}="Yerel / manuel";
 public decimal? Total {get;set;}
 public string Currency {get;set;}="";
 public string TotalLabel=>Total.HasValue?$"{Total:0.00} {Currency}":"—";
 public DateTimeOffset UpdatedAt {get;set;}=DateTimeOffset.UtcNow;
 public DateTimeOffset SourceUpdatedAt {get;set;}
 public DateTimeOffset LastSync {get;set;}
 [JsonIgnore] public string StockDecisionLabel {get;set;}="Bilinmiyor";
 public bool ReviewRequired {get;set;}
 public string ReviewReason {get;set;}="";
 public bool IsPhysicalSale {get;set;}
 public List<OrderItem> Items {get;set;}=[];
 public List<OrderShipment> Shipments {get;set;}=[];
 public string DeliveryLabel=>Shipments.Count==0?"Bilinmiyor":string.Join(", ",Shipments.Select(s=>OrdersRules.Label(s.State)).Distinct());
 public string TrackingNumbers=>string.Join(", ",Shipments.Select(s=>s.TrackingNumber).Where(s=>s.Length>0));
 public string Carriers=>string.Join(", ",Shipments.Select(s=>s.Carrier).Where(s=>s.Length>0).Distinct());
 public string SyncLabel=>LastSync==default?"API senkronizasyonu yok":LastSync.ToLocalTime().ToString("g");
 public OrderSnapshot Copy()=>JsonSerializer.Deserialize<OrderSnapshot>(JsonSerializer.Serialize(this))!;
}
public sealed class OrderItem
{
 public decimal? UnitPrice {get;set;}
 public decimal VatRate {get;set;}=20;
 public string Title {get;set;}="";
 public string Sku {get;set;}="";
 public string Barcode {get;set;}="";
 public string ProductId {get;set;}="";
 public bool ReviewRequired {get;set;}
 public string ReviewReason {get;set;}="";
 public int Quantity {get;set;}=1;
}
public sealed class OrderShipment
{
 public string Id {get;set;}="";
 public string Carrier {get;set;}="";
 public string TrackingNumber {get;set;}="";
 public string TrackingUrl {get;set;}="";
 public string State {get;set;}="Unknown";
 public string Source {get;set;}="Yerel / manuel";
 public List<OrderTrackingEvent> Events {get;set;}=[];
 public string Label=>$"{Id} · {Carrier} · {TrackingNumber} · {OrdersRules.Label(State)} ({Source})";
}
public sealed record OrderTrackingEvent(DateTimeOffset At,string State,string Source);
public static class OrdersRules
{
 public static readonly string[] States=["Unknown","Preparing","Shipped","InTransit","Delivered","Exception","Returned"];
 public static string Label(string state)=>state switch {"Preparing"=>"Hazırlanıyor","Shipped"=>"Gönderildi (taşıma doğrulanmadı)","InTransit"=>"Yolda","Delivered"=>"Teslim edildi","Exception"=>"Teslimat sorunu","Returned"=>"İade",_=>"Bilinmiyor"};
 public static bool SafeTrackingUrl(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme==Uri.UriSchemeHttps&&uri.UserInfo.Length==0&&!string.IsNullOrWhiteSpace(uri.Host);
 public static bool IsRemoteOrder(OrderSnapshot order)=>!string.IsNullOrWhiteSpace(order.ConnectionId)||!string.Equals(order.Source,"Yerel / manuel",StringComparison.Ordinal);
 public static bool CanApplyOnlineStock(OrderSnapshot order)=>!order.IsPhysicalSale&&!order.ReviewRequired&&!IsRemoteOrder(order);
 public static void Validate(OrderSnapshot o)
 {
  if(string.IsNullOrWhiteSpace(o.Marketplace)||string.IsNullOrWhiteSpace(o.ShopId)||string.IsNullOrWhiteSpace(o.OrderId))throw new ArgumentException("Pazaryeri, mağaza ve sipariş numarası zorunlu.");
  if(o.Shipments.Any(s=>string.IsNullOrWhiteSpace(s.Id))||o.Shipments.Select(s=>s.Id).Distinct().Count()!=o.Shipments.Count)throw new ArgumentException("Paket kimlikleri boş veya tekrarlı olamaz.");
  if(o.Shipments.Any(s=>s.TrackingUrl.Length>0&&!SafeTrackingUrl(s.TrackingUrl)))throw new ArgumentException("Takip bağlantısı kullanıcı bilgisi içermeyen HTTPS adresi olmalı.");
  if(o.Items.Any(i=>string.IsNullOrWhiteSpace(i.Title)||i.Quantity<=0))throw new ArgumentException("Ürün adı ve pozitif adet zorunlu.");
 }
}
public sealed record OrderConnectionFilterOption(string ConnectionId,string Label)
{
 public override string ToString()=>Label;
}
/// Pure predicate extracted from OrdersPanel's in-memory filter bar so the
/// date-range (#1971) and marketplace/shop multi-select (#1972) rules are
/// directly testable without a WPF DataGrid/ListBox host.
public static class OrderFilterCriteria
{
 public static bool Matches(OrderSnapshot o,string search,string? stateFilter,IReadOnlyCollection<string> marketplaces,IReadOnlyCollection<string> shops,string stockFilter,DateTimeOffset? fromUtc,DateTimeOffset? toUtc,
  IReadOnlyCollection<string>? connections=null,IReadOnlyCollection<string>? statuses=null,IReadOnlyCollection<string>? sources=null,string reviewFilter="Tümü")
 {
  var q=(search??"").Trim();
  if(q.Length>0&&!$"{o.Marketplace} {o.ShopId} {o.ConnectionDisplayName} {o.OrderId} {o.TrackingNumbers} {string.Join(' ',o.Items.Select(i=>i.Title+" "+i.Sku+" "+i.Barcode))}".Contains(q,StringComparison.CurrentCultureIgnoreCase))return false;
  if(!string.IsNullOrEmpty(stateFilter)&&!o.Shipments.Any(s=>s.State==stateFilter))return false;
  if(marketplaces.Count>0&&!marketplaces.Contains(o.Marketplace,StringComparer.OrdinalIgnoreCase))return false;
  if(shops.Count>0&&!shops.Contains(o.ShopId,StringComparer.OrdinalIgnoreCase))return false;
  if(connections?.Count>0&&!connections.Contains(o.ConnectionId,StringComparer.Ordinal))return false;
  if(statuses?.Count>0&&!statuses.Contains(o.RawStatus,StringComparer.OrdinalIgnoreCase))return false;
  if(sources?.Count>0&&!sources.Contains(o.Source,StringComparer.CurrentCultureIgnoreCase))return false;
  if(reviewFilter=="İnceleme gerekli"&&!o.ReviewRequired)return false;
  if(reviewFilter=="Hazır"&&o.ReviewRequired)return false;
  if(stockFilter=="Stok düşüldü"&&o.StockDecisionLabel!="Stok düşüldü")return false;
  if(stockFilter=="Stok bekliyor"&&o.StockDecisionLabel=="Stok düşüldü")return false;
  if(fromUtc.HasValue&&o.UpdatedAt<fromUtc.Value)return false;
  if(toUtc.HasValue&&o.UpdatedAt>toUtc.Value)return false;
  return true;
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
  order.ConnectionId=Limit(order.ConnectionId,512);order.ConnectionDisplayName=Limit(order.ConnectionDisplayName,160);order.ReviewReason=Limit(order.ReviewReason,500);
  foreach(var item in order.Items){item.Title=Limit(item.Title,500);item.Sku=Limit(item.Sku,200);item.Barcode=Limit(item.Barcode,200);item.ProductId=Limit(item.ProductId,200);item.ReviewReason=Limit(item.ReviewReason,500);}
  foreach(var shipment in order.Shipments){shipment.Id=Limit(shipment.Id,200);shipment.Carrier=Limit(shipment.Carrier,160);shipment.TrackingNumber=Limit(shipment.TrackingNumber,200);shipment.TrackingUrl=Limit(shipment.TrackingUrl,2000);shipment.State=NormalizeState(shipment.State);}
  if(order.SourceUpdatedAt==default)order.SourceUpdatedAt=order.UpdatedAt; order.UpdatedAt=order.SourceUpdatedAt; return order;
 }
 static string NormalizeStatus(string value)=>Limit(value,80).Trim().ToLowerInvariant() switch {"paid" or "completed" or "open"=>"paid", "canceled" or "cancelled" or "refunded"=>"cancelled", "shipped"=>"shipped", _=>Limit(value,80).Trim().ToLowerInvariant()};
 static string NormalizeState(string value)=>value.Trim().ToLowerInvariant() switch {"shipped"=>"Shipped","in_transit" or "in transit"=>"InTransit","delivered"=>"Delivered","returned" or "return"=>"Returned","cancelled" or "canceled"=>"Exception", _=>"Unknown"};
 static string Limit(string value,int max){value=(value??"").Trim();return value.Length>max?value[..max]:value;}
}
