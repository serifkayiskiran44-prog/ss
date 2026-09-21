using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record TrendyolUnsuppliedLine(long LineId,int Quantity);
public sealed record TrendyolClaim(string ClaimId,string OrderNumber,string Status,IReadOnlyList<string> ClaimLineItemIds);

public sealed class TrendyolOrderActionClient
{
    readonly TrendyolSettings settings;readonly HttpClient http;
    public TrendyolOrderActionClient(TrendyolSettings settings,HttpClient? http=null){TrendyolConnection.Validate(settings);this.settings=settings;this.http=http??new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(45)};}
    public async Task MarkUnsuppliedAsync(long packageId,IReadOnlyList<TrendyolUnsuppliedLine> lines,int reasonId,bool approved,CancellationToken token=default)
    {
        if(!approved)throw new InvalidOperationException("Canlı Trendyol iptali için açık onay gerekli.");if(packageId<=0||lines.Count==0||lines.Any(x=>x.LineId<=0||x.Quantity<=0))throw new InvalidOperationException("Paket, satır kimliği ve miktar eksiksiz olmalı.");if(reasonId is not (500 or 501 or 502 or 504 or 505 or 506))throw new ArgumentException("Trendyol tedarik edememe nedeni geçersiz.");
        await SendAsync(HttpMethod.Put,$"/integration/order/sellers/{settings.SupplierId}/shipment-packages/{packageId}/items/unsupplied",JsonSerializer.Serialize(new{lines=lines.Select(x=>new{lineId=x.LineId,quantity=x.Quantity}),reasonId}),token);
    }
    public async Task<IReadOnlyList<TrendyolClaim>> GetClaimsAsync(DateTimeOffset from,DateTimeOffset to,CancellationToken token=default)
    {
        if(to<from||to-from>TimeSpan.FromDays(30))throw new ArgumentException("İade sorgusu en fazla 30 gün olmalı.");var path=$"/integration/order/sellers/{settings.SupplierId}/claims?startDate={from.ToUnixTimeMilliseconds()}&endDate={to.ToUnixTimeMilliseconds()}&page=0&size=200";using var json=await SendAsync(HttpMethod.Get,path,null,token);var result=new List<TrendyolClaim>();if(!json.RootElement.TryGetProperty("content",out var content)||content.ValueKind!=JsonValueKind.Array)return result;foreach(var claim in content.EnumerateArray()){var id=Text(claim,"id");var number=Text(claim,"orderNumber");var status=Text(claim,"claimStatus");var ids=new List<string>();if(claim.TryGetProperty("items",out var items)&&items.ValueKind==JsonValueKind.Array)foreach(var item in items.EnumerateArray()){var lineId=Text(item,"id");if(lineId.Length>0)ids.Add(lineId);}result.Add(new(id,number,status,ids));}return result;
    }
    public async Task ApproveClaimAsync(string claimId,IReadOnlyList<string> itemIds,bool approved,CancellationToken token=default){if(!approved)throw new InvalidOperationException("Canlı iade kabulü için açık onay gerekli.");if(string.IsNullOrWhiteSpace(claimId)||itemIds.Count==0)throw new ArgumentException("İade ve kalem kimliği gerekli.");await SendAsync(HttpMethod.Put,$"/integration/order/sellers/{settings.SupplierId}/claims/{Uri.EscapeDataString(claimId)}/items/approve",JsonSerializer.Serialize(new{claimLineItemIdList=itemIds}),token);}
    public async Task RejectClaimAsync(string claimId,IReadOnlyList<string> itemIds,long reasonId,string description,bool approved,CancellationToken token=default)
    {
        if(!approved)throw new InvalidOperationException("Canlı iade reddi için açık onay gerekli.");if(string.IsNullOrWhiteSpace(claimId)||itemIds.Count==0||reasonId<=0)throw new ArgumentException("İade, kalem ve ret nedeni kimliği gerekli.");description=description.Trim();if(description.Length is < 3 or > 500)throw new ArgumentException("İade ret açıklaması 3-500 karakter olmalı.");
        using var request=new HttpRequestMessage(HttpMethod.Post,$"https://apigw.trendyol.com/integration/order/sellers/{settings.SupplierId}/claims/{Uri.EscapeDataString(claimId)}/issue");Authorize(request);using var form=new MultipartFormDataContent();form.Add(new StringContent(reasonId.ToString(CultureInfo.InvariantCulture)),"claimIssueReasonId");form.Add(new StringContent(description),"claimIssueDescription");foreach(var id in itemIds)form.Add(new StringContent(id),"claimLineItemIdList");request.Content=form;await SendRequestAsync(request,token);
    }
    async Task<JsonDocument> SendAsync(HttpMethod method,string path,string? body,CancellationToken token){using var request=new HttpRequestMessage(method,"https://apigw.trendyol.com"+path);Authorize(request);if(body is not null)request.Content=new StringContent(body,Encoding.UTF8,"application/json");return await SendRequestAsync(request,token);}
    void Authorize(HttpRequestMessage request){request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.ApiKey+":"+settings.ApiSecret)));request.Headers.UserAgent.ParseAdd(settings.UserAgent);}
    async Task<JsonDocument> SendRequestAsync(HttpRequestMessage request,CancellationToken token){using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);if(response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)throw new InvalidOperationException("Trendyol yönlendirme yanıtı verdi; kimlik bilgileri başka adrese gönderilmedi.");var bytes=await response.Content.ReadAsByteArrayAsync(token);if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"Trendyol işlemi tamamlanamadı (HTTP {(int)response.StatusCode}).");return JsonDocument.Parse(bytes.Length==0?"{}"u8.ToArray():bytes);}
    static string Text(JsonElement row,string name)=>row.TryGetProperty(name,out var v)?v.ValueKind==JsonValueKind.String?v.GetString()??"":v.ToString():"";
}
