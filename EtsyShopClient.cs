using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;
public sealed record EtsyListing(long ListingId, string Title, string State, int Quantity, decimal Price, string Currency, string Sku);
public sealed record EtsyListingPage(int Count, IReadOnlyList<EtsyListing> Listings);
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
}
