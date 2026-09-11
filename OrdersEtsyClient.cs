using System.Globalization;
using System.Net.Http;
using System.Text.Json;
namespace TrMarketplaceHubDesktop;
public sealed class OrdersEtsyClient(HttpClient http)
{
 public async Task<IReadOnlyList<OrderSnapshot>> ReadAsync(EtsyCredentials credentials,CancellationToken cancellationToken=default)
 {
  if(!long.TryParse(credentials.ShopId,NumberStyles.None,CultureInfo.InvariantCulture,out var shop)||shop<=0)throw new ArgumentException("Geçerli Etsy mağaza kimliği gerekli.");
  var rows=new List<OrderSnapshot>();var ids=new HashSet<string>();int offset=0;
  for(int page=0;page<100;page++)
  {
   using var request=new HttpRequestMessage(HttpMethod.Get,$"https://openapi.etsy.com/v3/application/shops/{shop}/receipts?limit=100&offset={offset}&sort_on=updated&sort_order=desc");EtsyHttp.AddHeaders(request,credentials,true);
   using var document=await SendAsync(request,cancellationToken).ConfigureAwait(false);
   try
   {
    var root=document.RootElement;int count=root.GetProperty("count").GetInt32();var results=root.GetProperty("results");int length=results.GetArrayLength();if(count<0||length>100)throw new FormatException();
    foreach(var receipt in results.EnumerateArray())
    {
     var id=receipt.GetProperty("receipt_id").GetInt64();if(id<=0||!ids.Add(id.ToString(CultureInfo.InvariantCulture)))throw new FormatException();
     var o=new OrderSnapshot{Marketplace="Etsy",ShopId=shop.ToString(CultureInfo.InvariantCulture),OrderId=id.ToString(CultureInfo.InvariantCulture),RawStatus=Text(receipt,"status"),PaymentStatus=Flag(receipt,"is_paid")?"Ödendi":"Ödenmedi / bilinmiyor",Source="Etsy API",UpdatedAt=DateTimeOffset.FromUnixTimeSeconds(receipt.GetProperty("updated_timestamp").GetInt64()),LastSync=DateTimeOffset.UtcNow};
     if(receipt.TryGetProperty("grandtotal",out var money)&&money.ValueKind==JsonValueKind.Object){decimal divisor=money.GetProperty("divisor").GetDecimal();if(divisor<=0)throw new FormatException();o.Total=money.GetProperty("amount").GetDecimal()/divisor;o.Currency=Text(money,"currency_code");}
     if(receipt.TryGetProperty("transactions",out var items)&&items.ValueKind==JsonValueKind.Array)foreach(var item in items.EnumerateArray())o.Items.Add(new(){Title=Text(item,"title"),Sku=Text(item,"sku"),Quantity=item.TryGetProperty("quantity",out var q)?q.GetInt32():1});
     if(receipt.TryGetProperty("shipments",out var shipments)&&shipments.ValueKind==JsonValueKind.Array)foreach(var s in shipments.EnumerateArray())
     {
      var tracking=Text(s,"tracking_code");var carrier=Text(s,"carrier_name");var sid=s.TryGetProperty("receipt_shipping_id",out var si)&&si.ValueKind==JsonValueKind.Number?si.GetInt64().ToString(CultureInfo.InvariantCulture):$"unidentified:{carrier}:{tracking}";
      if(o.Shipments.Any(x=>x.Id==sid))continue;
      o.Shipments.Add(new(){Id=sid,Carrier=carrier,TrackingNumber=tracking,State="Shipped",Source="Etsy API (gönderim bildirimi)"});
     }
     if(o.Shipments.Count==0&&Flag(receipt,"is_shipped"))o.Shipments.Add(new(){Id="untracked",State="Shipped",Source="Etsy API (takip numarası yok)"});
     OrdersRules.Validate(o);rows.Add(o);
    }
    offset+=length;if(offset>=count)return rows;
    if(length==0)throw new FormatException();
   }
   catch(Exception e) when(e is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or ArgumentException or OverflowException){throw new InvalidOperationException("Etsy sipariş yanıtı eksik veya tutarsız. Önceki kayıtlar korundu.");}
  }
  throw new InvalidOperationException("Sipariş okuma sınırı (10.000 kayıt) aşıldı; eksik sonuç kaydedilmedi.");
 }
 async Task<JsonDocument> SendAsync(HttpRequestMessage request,CancellationToken token)
 {
  try{return await EtsyHttp.SendJsonAsync(http,request,8*1024*1024,token).ConfigureAwait(false);}
  catch(InvalidOperationException e) when(e.Message.Contains("403")){throw new InvalidOperationException("Etsy sipariş erişimi reddedildi (HTTP 403). Etsy API sekmesinde transactions_r sipariş okuma izniyle bağlantıyı yeniden yetkilendirin; Etsy uygulama erişimini kontrol edin. Önceki kayıtlar korundu.");}
 }
 static string Text(JsonElement item,string key)=>item.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
 static bool Flag(JsonElement item,string key)=>item.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
}
