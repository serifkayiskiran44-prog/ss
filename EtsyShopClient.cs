using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;
public sealed record EtsyListing(long ListingId, string Title, string State, int Quantity, decimal Price, string Currency, string Sku)
{
    // Preserve the actual response array: the display string alone is not a match key.
    public IReadOnlyList<string> Skus { get; init; } = [];
}
public sealed record EtsyListingPage(int Count, IReadOnlyList<EtsyListing> Listings);
public sealed record EtsyListingUpdateResult(long ListingId);
public sealed record EtsyListingDetail(long ListingId,string Title,string Description,string State,int Quantity,decimal Price,string Currency,IReadOnlyList<string> Skus);
public sealed record EtsyListingUpdatePreview(string ProductId,long ListingId,int Quantity,decimal Price,string Currency,DateTime ProductUpdatedUtc)
{
 /// Zero saleable stock never rounds up to 1 or force-sends a stock-out PATCH;
 /// it deactivates the listing instead (a distinct, explicitly approved plan).
 public bool Deactivate => Quantity<=0;
}
public sealed class EtsyListingSyncService(EtsyShopClient client)
{
 public EtsyListingUpdatePreview CreatePreview(Catalog.CatalogProduct product){if(!long.TryParse(product.EtsyListingId,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var listingId)||listingId<=0)throw new InvalidOperationException("Ürün geçerli bir Etsy ilanına bağlı değil.");if(!product.Active||product.Stock<0||product.Price<=0)throw new InvalidOperationException("Pasif, negatif stoklu veya geçersiz fiyatlı ürün Etsy'ye gönderilemez.");return new(product.Id,listingId,product.Stock,product.Price,product.Currency,product.UpdatedUtc);}
 public async Task<EtsyListingUpdateResult> DispatchAsync(EtsyCredentials credentials,Catalog.CatalogProduct current,EtsyListingUpdatePreview preview,bool approved,CancellationToken cancellationToken=default){if(!approved)throw new InvalidOperationException("Etsy güncellemesi için önizleme onayı gerekli.");if(current.Id!=preview.ProductId||current.UpdatedUtc!=preview.ProductUpdatedUtc||current.Stock!=preview.Quantity||current.Price!=preview.Price||!string.Equals(current.Currency,preview.Currency,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Ürün önizlemeden sonra değişti; güncel önizleme alınmalı.");return await DispatchCoreAsync(credentials,preview,cancellationToken).ConfigureAwait(false);}
 public async Task<EtsyListingUpdateResult> DispatchAsync(EtsyCredentials credentials,Catalog.CatalogProduct current,EtsyListingUpdatePreview preview,bool approved,Catalog.SyncStore sync,string syncJobId,CancellationToken cancellationToken=default){if(!approved)throw new InvalidOperationException("Etsy güncellemesi için önizleme onayı gerekli.");if(current.Id!=preview.ProductId||current.UpdatedUtc!=preview.ProductUpdatedUtc||current.Stock!=preview.Quantity||current.Price!=preview.Price||!string.Equals(current.Currency,preview.Currency,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Ürün önizlemeden sonra değişti; güncel önizleme alınmalı.");ValidateSyncJob(sync,syncJobId,current.Id,credentials.ShopId);if(!sync.TryStart(syncJobId,out var generation)){var existing=sync.Get(syncJobId);if(existing.Status==Catalog.SyncStatus.Succeeded)throw new InvalidOperationException("Bu Etsy sync işi daha önce başarıyla gönderildi.");throw new InvalidOperationException("Etsy sync işi başka bir işlem tarafından yürütülüyor veya tekrar gönderilemez.");}try{var result=await DispatchCoreAsync(credentials,preview,cancellationToken).ConfigureAwait(false);sync.Succeed(syncJobId,generation);return result;}catch(Exception ex){sync.Fail(syncJobId,generation,ex.Message);throw;}}
 /// Fetches the remote listing first so currency/state come from Etsy, not a copy of
 /// the local preview; a currency mismatch or wrong shop/listing makes zero writes.
 /// Zero stock takes the deactivation path instead of a stock-out PATCH; an already
 /// inactive listing is treated as already applied instead of writing again.
 async Task<EtsyListingUpdateResult> DispatchCoreAsync(EtsyCredentials credentials,EtsyListingUpdatePreview preview,CancellationToken cancellationToken)
 {
  var remote=await client.GetListingAsync(credentials,preview.ListingId,cancellationToken).ConfigureAwait(false);
  if(preview.Deactivate)
  {
   if(string.Equals(remote.State,"inactive",StringComparison.OrdinalIgnoreCase))return new(remote.ListingId);
   return await client.DeactivateListingAsync(credentials,preview.ListingId,cancellationToken).ConfigureAwait(false);
  }
  if(!string.Equals(remote.Currency,preview.Currency,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"Etsy ilanının mağaza para birimi ({remote.Currency}) önizlemeyle ({preview.Currency}) uyuşmuyor; gönderim yapılmadı.");
  return await client.UpdateSimpleListingAsync(credentials,preview.ListingId,preview.Quantity,preview.Price,cancellationToken).ConfigureAwait(false);
 }
 public async Task<EtsyListingUpdateResult> PublishAsync(EtsyCredentials credentials,Catalog.CatalogProduct current,EtsyListingUpdatePreview preview,bool approved,Catalog.SyncStore sync,string syncJobId,CancellationToken cancellationToken=default){if(!approved)throw new InvalidOperationException("Etsy yayınlama için önizleme onayı gerekli.");if(current.Id!=preview.ProductId||current.UpdatedUtc!=preview.ProductUpdatedUtc||current.Stock!=preview.Quantity||current.Price!=preview.Price||!string.Equals(current.Currency,preview.Currency,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Ürün yayın önizlemesi güncel değil; güncel önizleme alınmalı.");ValidateSyncJob(sync,syncJobId,current.Id,credentials.ShopId);if(!sync.TryStart(syncJobId,out var generation)){var existing=sync.Get(syncJobId);if(existing.Status==Catalog.SyncStatus.Succeeded)throw new InvalidOperationException("Bu Etsy yayın işi daha önce başarıyla gönderildi.");throw new InvalidOperationException("Etsy yayın işi başka bir işlem tarafından yürütülüyor veya tekrar gönderilemez.");}try{var result=await client.PublishListingAsync(credentials,preview.ListingId,cancellationToken).ConfigureAwait(false);sync.Succeed(syncJobId,generation);return result;}catch(Exception ex){sync.Fail(syncJobId,generation,ex.Message);throw;}}
 static void ValidateSyncJob(Catalog.SyncStore sync,string id,string productId,string shopId){var job=sync.Get(id);if(!job.Channel.Equals("etsy",StringComparison.OrdinalIgnoreCase)||job.ShopId!=shopId.Trim()||job.EntityId!=productId)throw new InvalidOperationException("Etsy sync işi ürün veya mağaza bağlamıyla eşleşmiyor; canlı işlem engellendi.");}
}
public sealed class EtsyShopClient(HttpClient client)
{
    public async Task<EtsyListingDetail> GetListingAsync(EtsyCredentials credentials,long listingId,CancellationToken cancellationToken=default)
    {
        if(listingId<=0)throw new ArgumentException("Etsy ilan kimliği pozitif olmalı.");
        if(!long.TryParse(credentials.ShopId,NumberStyles.None,CultureInfo.InvariantCulture,out var shopId)||shopId<=0)throw new ArgumentException("Mağaza kimliği pozitif bir sayı olmalıdır.");
        using var request=new HttpRequestMessage(HttpMethod.Get,$"https://openapi.etsy.com/v3/application/listings/{listingId.ToString(CultureInfo.InvariantCulture)}");EtsyHttp.AddHeaders(request,credentials,true);
        using var document=await EtsyHttp.SendJsonAsync(client,request,4*1024*1024,cancellationToken).ConfigureAwait(false);
        try{var root=document.RootElement;if(root.GetProperty("shop_id").GetInt64()!=shopId)throw new InvalidOperationException("Etsy ilanı bağlı mağazaya ait değil.");var money=root.GetProperty("price");var divisor=money.GetProperty("divisor").GetDecimal();if(divisor<=0)throw new FormatException();var skus=root.TryGetProperty("skus",out var skuArray)&&skuArray.ValueKind==JsonValueKind.Array?skuArray.EnumerateArray().Select(x=>x.GetString()??"").Where(x=>x.Length>0).ToArray():Array.Empty<string>();var result=new EtsyListingDetail(root.GetProperty("listing_id").GetInt64(),root.GetProperty("title").GetString()??"",root.GetProperty("description").GetString()??"",root.GetProperty("state").GetString()??"",root.GetProperty("quantity").GetInt32(),money.GetProperty("amount").GetDecimal()/divisor,money.GetProperty("currency_code").GetString()??"",skus);if(result.ListingId!=listingId||result.Title.Length==0||result.Quantity<0||divisor<=0)throw new FormatException();return result;}catch(Exception e) when(e is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or OverflowException){throw new InvalidOperationException("Etsy ilan detay yanıtı eksik veya tutarsız.");}
    }
    public async Task<EtsyListingPage> GetListingsAsync(EtsyCredentials credentials, string state = "active", int offset = 0, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(credentials.ShopId, NumberStyles.None, CultureInfo.InvariantCulture, out var shopId) || shopId <= 0)
            throw new ArgumentException("Mağaza kimliği pozitif bir sayı olmalıdır.");
        if (state is not ("active" or "inactive" or "sold_out" or "draft" or "expired") || offset < 0)
            throw new ArgumentException("İlan durumu veya sayfa başlangıcı geçersiz.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://openapi.etsy.com/v3/application/shops/{shopId}/listings?state={state}&limit=100&offset={offset.ToString(CultureInfo.InvariantCulture)}");
        EtsyHttp.AddHeaders(request, credentials, true);
        using var document = await EtsyHttp.SendJsonAsync(client, request, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        try
        {
            var root = document.RootElement;
            var count = root.GetProperty("count").GetInt32();
            var results = root.GetProperty("results");
            if (count < 0 || results.GetArrayLength() > 100) throw new InvalidOperationException();
            var rows = new List<EtsyListing>();
            foreach (var item in results.EnumerateArray())
            {
                var id = item.GetProperty("listing_id").GetInt64();
                var title = item.GetProperty("title").GetString();
                var listingState = item.GetProperty("state").GetString();
                var quantity = item.GetProperty("quantity").GetInt32();
                var price = item.GetProperty("price");
                var amount = price.GetProperty("amount").GetDecimal();
                var divisor = price.GetProperty("divisor").GetDecimal();
                var currency = price.GetProperty("currency_code").GetString();
                if (id <= 0 || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(listingState) || quantity < 0 || amount < 0 || divisor <= 0 || string.IsNullOrWhiteSpace(currency)) throw new InvalidOperationException();
                var exactSkus = item.TryGetProperty("skus", out var skus) && skus.ValueKind == JsonValueKind.Array ? skus.EnumerateArray().Select(s => s.GetString()??"").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() : [];
                rows.Add(new(id, title, listingState, quantity, amount / divisor, currency, string.Join(", ",exactSkus)) { Skus=exactSkus });
            }
            return new(count, rows.AsReadOnly());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidOperationException("Etsy geçerli bir ilan listesi döndürmedi."); }
    }
    public async Task<EtsyListingUpdateResult> UpdateSimpleListingAsync(EtsyCredentials credentials,long listingId,int quantity,decimal price,CancellationToken cancellationToken=default)
    {
        if(listingId<=0||quantity<=0||price<=0)throw new ArgumentException("Etsy ilan ID, stok ve fiyat geçerli olmalı.");
        if(!long.TryParse(credentials.ShopId,NumberStyles.None,CultureInfo.InvariantCulture,out var shopId)||shopId<=0)throw new ArgumentException("Mağaza kimliği pozitif bir sayı olmalıdır.");
        var listing=await GetListingAsync(credentials,listingId,cancellationToken).ConfigureAwait(false);
        using var read=new HttpRequestMessage(HttpMethod.Get,$"https://openapi.etsy.com/v3/application/listings/{listingId}/inventory"); EtsyHttp.AddHeaders(read,credentials,true);
        using var inventory=await EtsyHttp.SendJsonAsync(client,read,4*1024*1024,cancellationToken).ConfigureAwait(false);
        var body=Etsy.EtsyWorkspaceService.SimpleInventory(inventory.RootElement);
        var currency=inventory.RootElement.GetProperty("products")[0].GetProperty("offerings")[0].GetProperty("price").GetProperty("currency_code").GetString();
        if(!string.Equals(currency,listing.Currency,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Etsy envanter ve ilan para birimi uyuşmuyor.");
        body["products"]![0]!["offerings"]![0]!["price"]=price; body["products"]![0]!["offerings"]![0]!["quantity"]=quantity;
        using var request=new HttpRequestMessage(HttpMethod.Put,$"https://openapi.etsy.com/v3/application/listings/{listingId}/inventory"); EtsyHttp.AddHeaders(request,credentials,true);
        request.Content=new StringContent(body.ToJsonString(),System.Text.Encoding.UTF8,"application/json");
        using var document=await EtsyHttp.SendJsonAsync(client,request,4*1024*1024,cancellationToken).ConfigureAwait(false);
        if(!document.RootElement.TryGetProperty("products",out var products)||products.ValueKind!=JsonValueKind.Array||products.GetArrayLength()!=1)throw new InvalidOperationException("Etsy envanter güncelleme sonucu doğrulanamadı."); return new(listingId);
    }
    public async Task<EtsyListingUpdateResult> PublishListingAsync(EtsyCredentials credentials,long listingId,CancellationToken cancellationToken=default)
    {
        if(listingId<=0)throw new ArgumentException("Etsy ilan ID geçerli olmalı.");
        if(!long.TryParse(credentials.ShopId,NumberStyles.None,CultureInfo.InvariantCulture,out var shopId)||shopId<=0)throw new ArgumentException("Mağaza kimliği pozitif bir sayı olmalıdır.");
        var listing=await GetListingAsync(credentials,listingId,cancellationToken).ConfigureAwait(false);
        if(listing.State=="sold_out"||listing.Quantity<=0)throw new InvalidOperationException("Tükenmiş ilan yayınlanamaz; önce ayrı stok önizlemesini tamamlayın.");
        using var request=new HttpRequestMessage(new HttpMethod("PATCH"),$"https://openapi.etsy.com/v3/application/shops/{shopId}/listings/{listingId.ToString(CultureInfo.InvariantCulture)}"); EtsyHttp.AddHeaders(request,credentials,true); request.Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"state","active"}});
        using var document=await EtsyHttp.SendJsonAsync(client,request,1024*1024,cancellationToken).ConfigureAwait(false);
        if(document.RootElement.ValueKind!=JsonValueKind.Object||!document.RootElement.TryGetProperty("listing_id",out var id)||!id.TryGetInt64(out var result)||result!=listingId)throw new InvalidOperationException("Etsy yayın yanıtı ilan kimliği döndürmedi."); return new(result);
    }
    /// Explicit, distinct plan for zero saleable stock: takes the listing off sale
    /// instead of PATCHing a stock-out quantity or rounding up to 1.
    public async Task<EtsyListingUpdateResult> DeactivateListingAsync(EtsyCredentials credentials,long listingId,CancellationToken cancellationToken=default)
    {
        if(listingId<=0)throw new ArgumentException("Etsy ilan ID geçerli olmalı.");
        if(!long.TryParse(credentials.ShopId,NumberStyles.None,CultureInfo.InvariantCulture,out var shopId)||shopId<=0)throw new ArgumentException("Mağaza kimliği pozitif bir sayı olmalıdır.");
        _=await GetListingAsync(credentials,listingId,cancellationToken).ConfigureAwait(false);
        using var request=new HttpRequestMessage(new HttpMethod("PATCH"),$"https://openapi.etsy.com/v3/application/shops/{shopId}/listings/{listingId.ToString(CultureInfo.InvariantCulture)}"); EtsyHttp.AddHeaders(request,credentials,true); request.Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"state","inactive"}});
        using var document=await EtsyHttp.SendJsonAsync(client,request,1024*1024,cancellationToken).ConfigureAwait(false);
        if(document.RootElement.ValueKind!=JsonValueKind.Object||!document.RootElement.TryGetProperty("listing_id",out var id)||!id.TryGetInt64(out var result)||result!=listingId)throw new InvalidOperationException("Etsy pasife alma yanıtı ilan kimliği döndürmedi."); return new(result);
    }
}
