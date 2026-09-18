using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Etsy;

public sealed partial class EtsyWorkspaceService
{
    public async Task<IReadOnlyList<EtsyOperationReceipt>> SendAsync(EtsyCredentials credentials,string planId,bool approved,CancellationToken cancellationToken=default)
    {
        if(!approved)throw new InvalidOperationException("Etsy gönderimi için bu önizlemeyi açıkça onaylayın.");
        Credentials(credentials); var plan=store.Plan(planId);
        if(plan.ShopId!=credentials.ShopId)throw new InvalidOperationException("Önizleme başka Etsy mağazasına ait.");
        if(!plan.Rows.Any(r=>r.CanSend))throw new InvalidOperationException("Gönderilebilir önizleme satırı yok.");
        if(store.Receipts(plan.ShopId).Any(r=>r.PlanId==planId))throw new InvalidOperationException("Bu Etsy önizlemesi daha önce gönderildi; tekrar kullanılamaz.");
        if(DateTime.UtcNow-plan.CreatedUtc>TimeSpan.FromMinutes(30))throw new InvalidOperationException("Etsy önizlemesi 30 dakikadan eski; yenileyin.");
        store.ValidateCurrent(plan);
        var shop=await new EtsyMetadataClient(http).GetShopAsync(credentials,cancellationToken).ConfigureAwait(false);
        if(plan.AccountFingerprint!=Account(credentials,shop.UserId)||!SameCurrency(plan.ShopCurrency,shop.Currency))throw new InvalidOperationException("Etsy hesap, yetki veya para birimi önizlemeden sonra değişti.");
        // Complete every read before atomically claiming all selected rows. No partial write on a stale row.
        foreach(var row in plan.Rows.Where(r=>r.CanSend))
        {
            if(row.ListingId is not long id)continue;
            var remote=await Listing(credentials,id,cancellationToken).ConfigureAwait(false);
            if(ListingHash(remote)!=row.RemoteFingerprint)throw new InvalidOperationException("Etsy ilanı önizlemeden sonra değişti; yeni önizleme alın.");
            if(row.InventoryFingerprint.Length>0)
            {
                var inventory=await Get(credentials,$"listings/{id}/inventory",cancellationToken).ConfigureAwait(false);
                if(InventoryHash(inventory)!=row.InventoryFingerprint)throw new InvalidOperationException("Etsy envanteri önizlemeden sonra değişti; yeni önizleme alın.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); store.Claim(plan);
        var receipts=new List<EtsyOperationReceipt>();
        foreach(var row in plan.Rows.Where(r=>r.CanSend))
        {
            var receipt=new EtsyOperationReceipt { PlanId=plan.Id,ProductId=row.ProductId,Sku=row.Sku,ListingId=row.ListingId,Status="Claimed" }; receipts.Add(receipt);
            var completed=0;
            try
            {
                foreach(var step in row.Steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if(step.Image!=null)
                    {
                        if(receipt.ListingId is not long imageListing)throw new InvalidOperationException("Görsel için taslak kimliği alınamadı.");
                        await new EtsyDrafts(http).UploadImageAsync(credentials,imageListing,step.Image,cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var path=step.Path.Replace("{listing_id}",receipt.ListingId?.ToString(System.Globalization.CultureInfo.InvariantCulture)??throwIfPlaceholder(step.Path));
                        var result=await Write(credentials,step,path,cancellationToken).ConfigureAwait(false);
                        if(plan.Operation==EtsyOperation.CreateDraft&&completed==0)
                        {
                            var id=result.GetProperty("listing_id").GetInt64(); if(id<=0)throw new InvalidOperationException("Etsy taslak kimliği alınamadı.");
                            receipt.ListingId=id;
                            receipt.Detail="Taslak oluşturuldu; SKU/görsel/özellik adımları sürüyor. Yayınlanmadı.";
                            store.Complete(receipt); // Persist the created ID before any follow-up mutation.
                            _ = await Listing(credentials,id,cancellationToken).ConfigureAwait(false);
                            store.BindCreated(plan.ShopId,row.ProductId,id);
                        }
                        else if(step.Path.EndsWith("/inventory",StringComparison.Ordinal))
                        { if(!result.TryGetProperty("products",out var products)||products.ValueKind!=JsonValueKind.Array||products.GetArrayLength()!=1)throw new InvalidOperationException("Etsy envanter sonucu doğrulanamadı."); }
                        else if(step.Path.Contains("/properties/",StringComparison.Ordinal))
                        { if(!result.TryGetProperty("property_id",out var property)||property.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)!=step.Path.Split('/')[^1])throw new InvalidOperationException("Etsy özellik sonucu doğrulanamadı."); }
                        else if(!result.TryGetProperty("listing_id",out var listing)||listing.GetInt64()!=receipt.ListingId)throw new InvalidOperationException("Etsy ilan sonucu doğrulanamadı.");
                    }
                    completed++;
                }
                receipt.Status="Succeeded"; receipt.Detail=plan.Operation==EtsyOperation.CreateDraft?"Taslak ve önizlemedeki ek adımlar tamamlandı; yayınlanmadı.":"Etsy API işlemi tamamlandı.";
            }
            catch(EtsyRejectedException ex)
            { receipt.Status=completed>0?"Partial":"Failed"; receipt.Detail=$"Etsy HTTP {ex.StatusCode} ile reddetti. "+(completed>0?"Önceki adımlar uygulandı; mağazayı kontrol edin. Tekrar gönderilmez.":"Bu önizleme tekrar gönderilmez; alanları kontrol edin."); }
            catch(Exception ex) when(ex is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { receipt.Status="Unknown"; receipt.Detail="Gönderim sonucu kesinleşmedi. Etsy mağazasında ilanı kontrol edin; otomatik veya aynı ürün için yeni gönderim engellendi."; }
            store.Complete(receipt);
        }
        return receipts;
    }
    static string throwIfPlaceholder(string path)=>path.Contains("{listing_id}",StringComparison.Ordinal)?throw new InvalidOperationException("Taslak kimliği eksik."):"";
    sealed class EtsyRejectedException(int statusCode):Exception { public int StatusCode { get; }=statusCode; }
    async Task<JsonElement> Write(EtsyCredentials credentials,EtsyWriteStep step,string path,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request=new HttpRequestMessage(new HttpMethod(step.Method),"https://openapi.etsy.com/v3/application/"+path); EtsyHttp.AddHeaders(request,credentials,true);
        request.Content=step.Json.Length>0?new StringContent(step.Json,Encoding.UTF8,"application/json"):new FormUrlEncodedContent(step.Form);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)
        {
            // A server/proxy failure can occur after Etsy committed a mutation.
            if((int)response.StatusCode>=500||(int)response.StatusCode==408)throw new InvalidOperationException("Etsy işlem sonucu belirsiz.");
            throw new EtsyRejectedException((int)response.StatusCode);
        }
        await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false); using var memory=new MemoryStream();
        var bytes=new byte[8192]; int count;
        while((count=await stream.ReadAsync(bytes,timeout.Token).ConfigureAwait(false))>0) { if(memory.Length+count>8*1024*1024)throw new InvalidOperationException("Etsy sonucu boyut sınırını aşıyor."); memory.Write(bytes,0,count); }
        using var doc=JsonDocument.Parse(memory.ToArray()); return doc.RootElement.Clone();
    }
}
