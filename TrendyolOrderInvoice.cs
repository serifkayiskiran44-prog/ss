using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed class TrendyolOrderClient
{
 const int MaxResponseBytes=16*1024*1024;
 readonly TrendyolSettings settings;
 readonly HttpClient http;
 public TrendyolOrderClient(TrendyolSettings settings,HttpClient? http=null)
 {
  TrendyolConnection.Validate(settings);this.settings=settings;
  this.http=http??new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(45)};
 }

 public async Task<IReadOnlyList<OrderSnapshot>> ReadRecentAsync(CancellationToken cancellationToken=default)
 {
  var rows=new List<OrderSnapshot>();
  for(var page=0;page<50;page++)
  {
   var url=$"https://apigw.trendyol.com/integration/order/sellers/{settings.SupplierId}/v2/orders?orderByField=PackageLastModifiedDate&orderByDirection=DESC&size=200&page={page}";
   using var request=new HttpRequestMessage(HttpMethod.Get,url);Authorize(request);
   using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellationToken);
   RejectRedirect(response);if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"Trendyol siparişleri okunamadı (HTTP {(int)response.StatusCode}).");
   using var json=await ReadJson(response,cancellationToken);
   if(!json.RootElement.TryGetProperty("content",out var content)||content.ValueKind!=JsonValueKind.Array)throw new InvalidOperationException("Trendyol sipariş yanıtı beklenen biçimde değil.");
   foreach(var item in content.EnumerateArray())rows.Add(Map(item));
   var totalPages=Int(json.RootElement,"totalPages");if(totalPages<=page+1||content.GetArrayLength()==0)break;
  }
  return rows;
 }

 OrderSnapshot Map(JsonElement row)
 {
  var supplier=Long(row,"supplierId");if(supplier>0&&supplier.ToString(CultureInfo.InvariantCulture)!=settings.SupplierId)throw new InvalidOperationException("Trendyol sipariş yanıtındaki satıcı kimliği bağlı mağazayla uyuşmuyor.");
  var packageId=Long(row,"shipmentPackageId");if(packageId<=0)throw new InvalidOperationException("Trendyol siparişinde geçerli paket kimliği bulunamadı.");
  var invoice=Object(row,"invoiceAddress");var shipping=Object(row,"shipmentAddress");var orderDate=Date(row,"orderDate");var modified=Date(row,"lastModifiedDate");
  var order=new OrderSnapshot{
   Marketplace="Trendyol",ShopId=settings.SupplierId,OrderId=Text(row,"orderNumber"),PlatformReference=Text(row,"orderNumber"),OrderDate=orderDate,AddedAt=modified,RawStatus=First(Text(row,"shipmentPackageStatus"),Text(row,"status")),PaymentStatus="Ödendi",PaymentType=Text(row,"paymentType"),Source="Trendyol API V2",
   CustomerName=(Text(row,"customerFirstName")+" "+Text(row,"customerLastName")).Trim(),MarketplaceCustomerId=Long(row,"customerId"),CustomerId=Text(row,"customerId"),BillingEmail=First(Text(row,"customerEmail"),Text(invoice,"email"),Text(shipping,"email")),CustomerEmail=First(Text(row,"customerEmail"),Text(invoice,"email"),Text(shipping,"email")),
   BillingName=First(Text(invoice,"company"),Text(invoice,"companyName"),Text(invoice,"fullName"),PersonName(invoice),(Text(row,"customerFirstName")+" "+Text(row,"customerLastName")).Trim()),BillingAddress=Text(invoice,"fullAddress"),BillingCountry=First(Text(invoice,"countryCode"),Text(invoice,"country")),BillingCity=Text(invoice,"city"),BillingDistrict=Text(invoice,"district"),TaxNumber=First(Text(invoice,"taxNumber"),Text(invoice,"taxId"),Text(invoice,"identityNumber")),CustomerTaxOffice=First(Text(invoice,"taxOffice"),Text(invoice,"taxOfficeName")),CustomerPhone=First(Text(invoice,"phone"),Text(shipping,"phone"),Text(row,"customerPhone")),
   ShippingName=First(Text(shipping,"fullName"),PersonName(shipping),(Text(row,"customerFirstName")+" "+Text(row,"customerLastName")).Trim()),ShippingAddress=Text(shipping,"fullAddress"),ShippingCountry=First(Text(shipping,"countryCode"),Text(shipping,"country")),ShippingCity=Text(shipping,"city"),ShippingDistrict=Text(shipping,"district"),ShippingPhone=First(Text(shipping,"phone"),Text(invoice,"phone"),Text(row,"customerPhone")),
   EInvoiceAvailable=Bool(invoice,"eInvoiceAvailable"),CommercialInvoice=Bool(row,"commercial")??false,MicroExport=Bool(row,"micro")??false,CrossBorderByTrendyol=Bool(row,"3pByTrendyol")??false,
   MarketplaceInvoiceStatus=Text(row,"invoiceStatus"),MarketplaceInvoiceErrors=Strings(row,"invoiceRejectedReasonKeys"),Currency=First(Text(row,"currencyCode"),"TRY"),Total=Decimal(row,"packageTotalPrice"),
   UpdatedAt=modified??orderDate??DateTimeOffset.UtcNow,SourceUpdatedAt=modified??orderDate??DateTimeOffset.UtcNow,LastSync=DateTimeOffset.UtcNow
  };
  if(row.TryGetProperty("lines",out var lines)&&lines.ValueKind==JsonValueKind.Array)foreach(var line in lines.EnumerateArray())order.Items.Add(new(){ProductId=Text(line,"productCode"),MarketplaceProductCode=First(Text(line,"merchantSku"),Text(line,"productCode")),Barcode=Text(line,"barcode"),Title=Text(line,"productName"),Sku=Text(line,"stockCode"),Quantity=Math.Max(1,Int(line,"quantity")),UnitPrice=FirstDecimal(line,"lineUnitPrice","price","amount"),VatRate=DecimalNullable(line,"vatRate")??20,DiscountAmount=DecimalNullable(line,"discount")});
  var totals=OrderTotalsCalculator.Calculate(order);order.NetTotal=totals.Net;order.VatTotal=totals.Vat;
  order.Shipments.Add(new(){Id=packageId.ToString(CultureInfo.InvariantCulture),Carrier=First(Text(row,"cargoProviderName"),Text(row,"cargoSenderNumber")),TrackingNumber=First(Text(row,"cargoTrackingNumber"),Text(row,"cargoSenderNumber")),TrackingUrl=SafeUrl(Text(row,"cargoTrackingLink")),State=ShipmentState(order.RawStatus),Source="Trendyol API V2"});
  OrdersRules.Validate(order);return order;
 }

 void Authorize(HttpRequestMessage request){request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.ApiKey+":"+settings.ApiSecret)));request.Headers.UserAgent.ParseAdd(settings.UserAgent);request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));}
 static void RejectRedirect(HttpResponseMessage response){if(response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)throw new InvalidOperationException("Trendyol yönlendirme yanıtı verdi; API bilgileri başka adrese gönderilmedi.");}
 static async Task<JsonDocument> ReadJson(HttpResponseMessage response,CancellationToken token){if(response.Content.Headers.ContentLength>MaxResponseBytes)throw new InvalidOperationException("Trendyol yanıtı boyut sınırını aşıyor.");await using var input=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();var buffer=new byte[81920];int n;while((n=await input.ReadAsync(buffer,token))>0){if(output.Length+n>MaxResponseBytes)throw new InvalidOperationException("Trendyol yanıtı boyut sınırını aşıyor.");output.Write(buffer,0,n);}try{return JsonDocument.Parse(output.ToArray());}catch(JsonException){throw new InvalidOperationException("Trendyol yanıtı geçerli JSON değil.");}}
 static JsonElement Object(JsonElement row,string name)=>row.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Object?value:default;
 static string Text(JsonElement row,string name)=>row.ValueKind==JsonValueKind.Object&&row.TryGetProperty(name,out var value)?value.ValueKind==JsonValueKind.String?value.GetString()??"":value.ValueKind is JsonValueKind.Number?value.ToString():"":"";
 static int Int(JsonElement row,string name)=>row.TryGetProperty(name,out var value)&&value.TryGetInt32(out var result)?result:0;
 static long Long(JsonElement row,string name)=>row.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out var result)?result:0;
 static decimal Decimal(JsonElement row,string name)=>DecimalNullable(row,name)??0;
 static decimal? FirstDecimal(JsonElement row,params string[] names){foreach(var name in names)if(DecimalNullable(row,name) is { } value)return value;return null;}
 static decimal? DecimalNullable(JsonElement row,string name)=>row.TryGetProperty(name,out var value)&&value.TryGetDecimal(out var result)?result:null;
 static bool? Bool(JsonElement row,string name)=>row.ValueKind==JsonValueKind.Object&&row.TryGetProperty(name,out var value)&&value.ValueKind is JsonValueKind.True or JsonValueKind.False?value.GetBoolean():null;
 static List<string> Strings(JsonElement row,string name)=>row.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Array?value.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Select(x=>x.GetString()??"").Where(x=>x.Length>0).Take(25).ToList():[];
 static DateTimeOffset? Date(JsonElement row,string name){if(!row.TryGetProperty(name,out var value))return null;if(value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out var ms))try{return DateTimeOffset.FromUnixTimeMilliseconds(ms);}catch(ArgumentOutOfRangeException){return null;}return DateTimeOffset.TryParse(value.ToString(),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var result)?result:null;}
 static string First(params string[] values)=>values.FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x))??"";
 static string PersonName(JsonElement address)=>(Text(address,"firstName")+" "+Text(address,"lastName")).Trim();
 static string SafeUrl(string value)=>OrdersRules.SafeTrackingUrl(value)?value:"";
 static string ShipmentState(string status)=>status.Trim().ToLowerInvariant() switch{"created" or "picking" or "invoiced"=>"Preparing","shipped"=>"Shipped","atcollectionpoint" or "intransit"=>"InTransit","delivered"=>"Delivered","returned" or "unpacked"=>"Returned","cancelled" or "unsupplied"=>"Exception",_=>"Unknown"};
}

public sealed class TrendyolInvoiceClient
{
 readonly TrendyolSettings settings;readonly HttpClient http;
 public TrendyolInvoiceClient(TrendyolSettings settings,HttpClient? http=null){TrendyolConnection.Validate(settings);this.settings=settings;this.http=http??new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(45)};}
 public async Task UploadPdfAsync(long shipmentPackageId,string invoiceNumber,DateTimeOffset invoiceDateTime,byte[] pdf,CancellationToken cancellationToken=default)
 {
  if(shipmentPackageId<=0)throw new ArgumentException("Geçerli Trendyol paket kimliği gerekli.");if(invoiceNumber.Length!=16||!invoiceNumber[..3].All(char.IsAsciiLetter)||!invoiceNumber[3..].All(char.IsAsciiDigit))throw new ArgumentException("Fatura numarası 3 harf ve 13 rakamdan oluşmalı.");if(pdf.Length<5||pdf.Length>10*1024*1024||!pdf.AsSpan(0,5).SequenceEqual("%PDF-"u8))throw new ArgumentException("Trendyol'a yüklenecek geçerli ve en fazla 10 MB PDF gerekli.");
  using var request=new HttpRequestMessage(HttpMethod.Post,$"https://apigw.trendyol.com/integration/sellers/{settings.SupplierId}/seller-invoice-file");request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.ApiKey+":"+settings.ApiSecret)));request.Headers.UserAgent.ParseAdd(settings.UserAgent);
  using var form=new MultipartFormDataContent();form.Add(new StringContent(shipmentPackageId.ToString(CultureInfo.InvariantCulture)),"shipmentPackageId");form.Add(new StringContent(invoiceNumber),"invoiceNumber");form.Add(new StringContent(invoiceDateTime.ToString("O",CultureInfo.InvariantCulture)),"invoiceDateTime");var file=new ByteArrayContent(pdf);file.Headers.ContentType=new MediaTypeHeaderValue("application/pdf");form.Add(file,"file","invoice.pdf");request.Content=form;
  using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellationToken);if(response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)throw new InvalidOperationException("Trendyol yönlendirme yanıtı verdi; API bilgileri başka adrese gönderilmedi.");if(response.StatusCode==HttpStatusCode.Conflict)throw new InvalidOperationException("Bu paket için Trendyol'da fatura bulunuyor. Tekrar yüklemeden önce siparişi yenileyin.");if(response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.Created)throw new InvalidOperationException($"Fatura Trendyol'a yüklenemedi (HTTP {(int)response.StatusCode}).");
 }
}
