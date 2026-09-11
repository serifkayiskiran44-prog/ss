using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;
public sealed record EtsyListing(long ListingId, string Title, string State, int Quantity, decimal Price, string Currency, string Sku);
public sealed record EtsyListingPage(int Count, IReadOnlyList<EtsyListing> Listings);
public sealed record EtsyListingUpdateResult(long ListingId);
public sealed record EtsyListingUpdatePreview(string ProductId,long ListingId,int Quantity,decimal Price,string Currency,DateTime ProductUpdatedUtc);
public sealed class EtsyListingSyncService(EtsyShopClient client)
{
 public EtsyListingUpdatePreview CreatePreview(Catalog.CatalogProduct product){if(!long.TryParse(product.EtsyListingId,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var listingId)||listingId<=0)throw new InvalidOperationException("Ürün geçerli bir Etsy ilanına bağlı değil.");if(!product.Active||product.Stock<=0||product.Price<=0)throw new InvalidOperationException("Pasif, stoksuz veya geçersiz fiyatlı ürün Etsy'ye gönderilemez.");return new(product.Id,listingId,product.Stock,product.Price,product.Currency,product.UpdatedUtc);}
 public async Task<EtsyListingUpdateResult> DispatchAsync(EtsyCredentials credentials,Catalog.CatalogProduct current,EtsyListingUpdatePreview preview,bool approved,CancellationToken cancellationToken=default){if(!approved)throw new InvalidOperationException("Etsy güncellemesi için önizleme onayı gerekli.");if(current.Id!=preview.ProductId||current.UpdatedUtc!=preview.ProductUpdatedUtc||current.Stock!=preview.Quantity||current.Price!=preview.Price||!string.Equals(current.Currency,preview.Currency,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Ürün önizlemeden sonra değişti; güncel önizleme alınmalı.");return await client.UpdateSimpleListingAsync(credentials,preview.ListingId,preview.Quantity,preview.Price,cancellationToken).ConfigureAwait(false);}
}
public sealed class EtsyShopClient(HttpClient client)
{
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
                var sku = item.TryGetProperty("skus", out var skus) && skus.ValueKind == JsonValueKind.Array ? string.Join(", ", skus.EnumerateArray().Select(s => s.GetString()).Where(s => !string.IsNullOrWhiteSpace(s))) : "";
                rows.Add(new(id, title, listingState, quantity, amount / divisor, currency, sku));
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
        using var request=new HttpRequestMessage(new HttpMethod("PATCH"),$"https://openapi.etsy.com/v3/application/shops/{shopId}/listings/{listingId.ToString(CultureInfo.InvariantCulture)}");EtsyHttp.AddHeaders(request,credentials,true);request.Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"quantity",quantity.ToString(CultureInfo.InvariantCulture)},{"price",price.ToString("0.00##########################",CultureInfo.InvariantCulture)}});
        using var document=await EtsyHttp.SendJsonAsync(client,request,1024*1024,cancellationToken).ConfigureAwait(false);if(document.RootElement.ValueKind!=JsonValueKind.Object||!document.RootElement.TryGetProperty("listing_id",out var id)||!id.TryGetInt64(out var result)||result<=0)throw new InvalidOperationException("Etsy güncellenmiş ilan kimliği döndürmedi.");return new(result);
    }
}
