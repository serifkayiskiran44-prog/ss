using System.Text.Json;
namespace TrMarketplaceHubDesktop;
public sealed class OrderSnapshot
{
 public string Marketplace {get;set;}="";
 public string ShopId {get;set;}="";
 public string OrderId {get;set;}="";
 public string RawStatus {get;set;}="";
 public string PaymentStatus {get;set;}="Bilinmiyor";
 public string Source {get;set;}="Yerel / manuel";
 public decimal? Total {get;set;}
 public string Currency {get;set;}="";
 public string TotalLabel=>Total.HasValue?$"{Total:0.00} {Currency}":"—";
 public DateTimeOffset UpdatedAt {get;set;}=DateTimeOffset.UtcNow;
 public DateTimeOffset LastSync {get;set;}
 public List<OrderItem> Items {get;set;}=[];
 public List<OrderShipment> Shipments {get;set;}=[];
 public string DeliveryLabel=>Shipments.Count==0?"Bilinmiyor":string.Join(", ",Shipments.Select(s=>OrdersRules.Label(s.State)).Distinct());
 public string TrackingNumbers=>string.Join(", ",Shipments.Select(s=>s.TrackingNumber).Where(s=>s.Length>0));
 public string Carriers=>string.Join(", ",Shipments.Select(s=>s.Carrier).Where(s=>s.Length>0).Distinct());
 public string SyncLabel=>LastSync==default?"API senkronizasyonu yok":LastSync.ToLocalTime().ToString("g");
 public OrderSnapshot Copy()=>JsonSerializer.Deserialize<OrderSnapshot>(JsonSerializer.Serialize(this))!;
}
public sealed class OrderItem {public string Title {get;set;}="";public string Sku {get;set;}="";public int Quantity {get;set;}=1;}
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
 public static void Validate(OrderSnapshot o)
 {
  if(string.IsNullOrWhiteSpace(o.Marketplace)||string.IsNullOrWhiteSpace(o.ShopId)||string.IsNullOrWhiteSpace(o.OrderId))throw new ArgumentException("Pazaryeri, mağaza ve sipariş numarası zorunlu.");
  if(o.Shipments.Any(s=>string.IsNullOrWhiteSpace(s.Id))||o.Shipments.Select(s=>s.Id).Distinct().Count()!=o.Shipments.Count)throw new ArgumentException("Paket kimlikleri boş veya tekrarlı olamaz.");
  if(o.Shipments.Any(s=>s.TrackingUrl.Length>0&&!SafeTrackingUrl(s.TrackingUrl)))throw new ArgumentException("Takip bağlantısı kullanıcı bilgisi içermeyen HTTPS adresi olmalı.");
  if(o.Items.Any(i=>string.IsNullOrWhiteSpace(i.Title)||i.Quantity<=0))throw new ArgumentException("Ürün adı ve pozitif adet zorunlu.");
 }
}
