using System.Text.Json;
using System.Text.Json.Serialization;
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
 // #946: the components a total is made of, when the marketplace reports them; null means "not reported", never zero by assumption.
 public decimal? ShippingTotal {get;set;}
 public decimal? DiscountTotal {get;set;}
 public decimal? TaxTotal {get;set;}
 public string TotalLabel=>Total.HasValue?$"{Total:0.00} {Currency}":"—";
 public DateTimeOffset UpdatedAt {get;set;}=DateTimeOffset.UtcNow;
 public DateTimeOffset SourceUpdatedAt {get;set;}
 public DateTimeOffset LastSync {get;set;}
 [JsonIgnore] public string StockDecisionLabel {get;set;}="Bilinmiyor";
 // #835: computed by the list from recorded states (never serialized -- it is time-dependent and derived).
 [JsonIgnore] public int UrgencyScore {get;set;}
 [JsonIgnore] public string UrgencyLabel {get;set;}="";
 public List<OrderItem> Items {get;set;}=[];
 public List<OrderShipment> Shipments {get;set;}=[];
 public string DeliveryLabel=>Shipments.Count==0?"Bilinmiyor":string.Join(", ",Shipments.Select(s=>OrdersRules.Label(s.State)).Distinct());
 public string TrackingNumbers=>string.Join(", ",Shipments.Select(s=>s.TrackingNumber).Where(s=>s.Length>0));
 public string Carriers=>string.Join(", ",Shipments.Select(s=>s.Carrier).Where(s=>s.Length>0).Distinct());
 public string SyncLabel=>LastSync==default?"API senkronizasyonu yok":TimeDisplay.Format(LastSync);
 // Delivery SLA of the whole order (#789): the worst package wins -- a delayed package makes the order late,
 // otherwise the longest transit shows, otherwise delivered/unshipped. Time-dependent, so never serialized.
 [JsonIgnore] public string SlaLabel=>SlaLabelAt(DateTimeOffset.UtcNow);
 public string SlaLabelAt(DateTimeOffset nowUtc)
 {
  if(Shipments.Count==0)return "—";
  var shippedFallback=SourceUpdatedAt==default?UpdatedAt:SourceUpdatedAt;
  var slas=Shipments.Select(s=>OrdersRules.EvaluateSla(s,shippedFallback,nowUtc)).ToList();
  var delayed=slas.Where(x=>x.Status=="DELAYED").ToList();if(delayed.Count>0)return $"Gecikti ({delayed.Max(x=>x.DaysInTransit)} gün)";
  var transit=slas.Where(x=>x.Status=="IN_TRANSIT").ToList();if(transit.Count>0)return $"Yolda ({transit.Max(x=>x.DaysInTransit)} gün)";
  if(slas.All(x=>x.Status=="DELIVERED"))return "Teslim edildi";
  var problem=slas.FirstOrDefault(x=>x.Status is "EXCEPTION" or "RETURNED");return problem?.Label??"Gönderilmedi";
 }
 public OrderSnapshot Copy()=>JsonSerializer.Deserialize<OrderSnapshot>(JsonSerializer.Serialize(this))!;
}
public sealed record DeliverySla(string Status,int DaysInTransit,string Label);
public sealed class OrderItem {public string Title {get;set;}="";public string Sku {get;set;}="";public int Quantity {get;set;}=1;public decimal? UnitPrice {get;set;}} // #946: the price the line was sold at; null when not reported
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
 public const int DefaultMaxTransitDays=5;
 // Delivery SLA of one package (#789). Transit is counted from the first shipped/in-transit observation; an
 // API shipment carries no observations (the Etsy client records none), so the caller passes the receipt's
 // source timestamp as the latest moment the package can have shipped. Delivered/returned/exception packages
 // are terminal and never "late"; a package with neither a shipped state nor a shipped observation is unshipped.
 public static DeliverySla EvaluateSla(OrderShipment shipment,DateTimeOffset fallbackShippedAtUtc,DateTimeOffset nowUtc,int maxTransitDays=DefaultMaxTransitDays)
 {
  ArgumentNullException.ThrowIfNull(shipment);if(maxTransitDays<1)throw new ArgumentOutOfRangeException(nameof(maxTransitDays));
  switch(shipment.State){case "Delivered":return new("DELIVERED",0,"Teslim edildi");case "Returned":return new("RETURNED",0,"İade");case "Exception":return new("EXCEPTION",0,"Teslimat sorunu");}
  var shipped=shipment.Events.Where(e=>e.State is "Shipped" or "InTransit").OrderBy(e=>e.At).FirstOrDefault();
  if(shipped is null&&shipment.State is not ("Shipped" or "InTransit"))return new("NOT_SHIPPED",0,"Gönderilmedi");
  var shippedAt=shipped?.At??fallbackShippedAtUtc;var days=Math.Max(0,(int)Math.Floor((nowUtc-shippedAt).TotalDays));
  return days>maxTransitDays?new("DELAYED",days,$"Gecikti ({days} gün)"):new("IN_TRANSIT",days,$"Yolda ({days} gün)");
 }
 public static void Validate(OrderSnapshot o)
 {
  if(string.IsNullOrWhiteSpace(o.Marketplace)||string.IsNullOrWhiteSpace(o.ShopId)||string.IsNullOrWhiteSpace(o.OrderId))throw new ArgumentException("Pazaryeri, mağaza ve sipariş numarası zorunlu.");
  if(o.Shipments.Any(s=>string.IsNullOrWhiteSpace(s.Id))||o.Shipments.Select(s=>s.Id).Distinct().Count()!=o.Shipments.Count)throw new ArgumentException("Paket kimlikleri boş veya tekrarlı olamaz.");
  if(o.Shipments.Any(s=>s.TrackingUrl.Length>0&&!SafeTrackingUrl(s.TrackingUrl)))throw new ArgumentException("Takip bağlantısı kullanıcı bilgisi içermeyen HTTPS adresi olmalı.");
  if(o.Items.Any(i=>string.IsNullOrWhiteSpace(i.Title)||i.Quantity<=0))throw new ArgumentException("Ürün adı ve pozitif adet zorunlu.");
 }
}
public static class OrderNormalizer
{
 public static OrderSnapshot Normalize(OrderSnapshot order)
 {
  order.Marketplace=Limit(order.Marketplace,80); order.ShopId=Limit(order.ShopId,200); order.OrderId=Limit(order.OrderId,200); order.RawStatus=NormalizeStatus(order.RawStatus); order.PaymentStatus=Limit(order.PaymentStatus,80); order.Currency=order.Currency.Trim().ToUpperInvariant(); if(order.Currency.Length>3)order.Currency=order.Currency[..3];
  foreach(var item in order.Items){item.Title=Limit(item.Title,500);item.Sku=Limit(item.Sku,200);}
  foreach(var shipment in order.Shipments){shipment.Id=Limit(shipment.Id,200);shipment.Carrier=NormalizeCarrier(shipment.Carrier);shipment.TrackingNumber=Limit(shipment.TrackingNumber,200);shipment.TrackingUrl=Limit(shipment.TrackingUrl,2000);shipment.State=NormalizeState(shipment.State);}
  if(order.SourceUpdatedAt==default)order.SourceUpdatedAt=order.UpdatedAt; order.UpdatedAt=order.SourceUpdatedAt; return order;
 }
 // Carrier normalization (#789): marketplaces spell the same carrier many ways ("yurtici", "YURTİÇİ KARGO",
 // "Yurtiçi Kargo A.Ş."), which used to show up as three carriers in filters and labels. Matching is on a
 // folded form (Turkish-aware lower-case, diacritics stripped, letters and digits only); an unknown carrier
 // is kept verbatim, never guessed.
 static readonly (string Key,string Name)[] KnownCarriers=[("yurtici","Yurtiçi Kargo"),("aras","Aras Kargo"),("mng","MNG Kargo"),("ptt","PTT Kargo"),("surat","Sürat Kargo"),("trendyolexpress","Trendyol Express"),("hepsijet","HepsiJet"),("kolaygelsin","Kolay Gelsin"),("sendeo","Sendeo"),("fedex","FedEx"),("usps","USPS"),("ups","UPS"),("dhl","DHL"),("tnt","TNT")];
 public static string NormalizeCarrier(string value){var trimmed=Limit(value,160);if(trimmed.Length==0)return "";var folded=Fold(trimmed);foreach(var (key,name) in KnownCarriers)if(folded.Contains(key,StringComparison.Ordinal))return name;return trimmed;}
 static string Fold(string value){var sb=new System.Text.StringBuilder();foreach(var ch in value.Replace('İ','i').Replace('I','ı').ToLowerInvariant()){var c=ch switch{'ı'=>'i','ç'=>'c','ş'=>'s','ğ'=>'g','ö'=>'o','ü'=>'u',_=>ch};if(char.IsLetterOrDigit(c))sb.Append(c);}return sb.ToString();}
 static string NormalizeStatus(string value)=>Limit(value,80).Trim().ToLowerInvariant() switch {"paid" or "completed" or "open"=>"paid", "canceled" or "cancelled" or "refunded"=>"cancelled", "shipped"=>"shipped", _=>Limit(value,80).Trim().ToLowerInvariant()};
 static string NormalizeState(string value)=>value.Trim().ToLowerInvariant() switch {"shipped"=>"Shipped","in_transit" or "in transit"=>"InTransit","delivered"=>"Delivered","returned" or "return"=>"Returned","cancelled" or "canceled"=>"Exception", _=>"Unknown"};
 static string Limit(string value,int max){value=(value??"").Trim();return value.Length>max?value[..max]:value;}
}
