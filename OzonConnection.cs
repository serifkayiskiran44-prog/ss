using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record OzonSettings(string ClientId, string ApiKey);
public sealed record OzonProductSummary(long ProductId, string OfferId, string Name);

/// <summary>Read-only Seller API probes. POST here queries data; it never publishes products.</summary>
public sealed class OzonConnection(HttpClient http)
{
    public static void Validate(OzonSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId) || !settings.ClientId.All(char.IsAsciiDigit)
            || string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKey.Any(char.IsControl))
            throw new ArgumentException("Sayısal Client ID ve geçerli API key gerekli.");
    }
    public async Task<long> ReadProductCountAsync(OzonSettings settings, CancellationToken cancellationToken = default)
    {
        using var json = await ReadAsync(settings, "/v3/product/list", "{\"filter\":{\"visibility\":\"ALL\"},\"last_id\":\"\",\"limit\":1}", cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
            || !result.TryGetProperty("total", out var total) || total.ValueKind != JsonValueKind.Number || !total.TryGetInt64(out var count) || count < 0)
            throw new InvalidOperationException("Ozon ürün yanıtı beklenen biçimde değil; erişim doğrulanamadı.");
        return count;
    }
    public async Task<IReadOnlyList<OzonProductSummary>> ReadProductPageAsync(OzonSettings settings, string lastId = "", int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Ozon ürün sayfası 1–100 arasında olmalı.");
        using var json = await ReadAsync(settings, "/v3/product/list", JsonSerializer.Serialize(new { filter = new { visibility = "ALL" }, last_id = lastId ?? "", limit }), cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Ozon ürün listesi yanıtı beklenen biçimde değil; okuma doğrulanamadı.");
        var rows = new List<OzonProductSummary>();
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("product_id", out var id) || !id.TryGetInt64(out var productId) || productId <= 0) throw new InvalidOperationException("Ozon ürün listesinde geçerli product_id yok.");
            var offer = item.TryGetProperty("offer_id", out var offerValue) && offerValue.ValueKind == JsonValueKind.String ? offerValue.GetString() ?? "" : "";
            var name = item.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() ?? "" : "";
            rows.Add(new(productId, offer, name));
        }
        return rows;
    }
    public async Task<int> ReadWarehouseCountAsync(OzonSettings settings, CancellationToken cancellationToken = default)
    {
        using var json = await ReadAsync(settings, "/v1/warehouse/list", "{}", cancellationToken);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Ozon depo yanıtı beklenen biçimde değil; erişim doğrulanamadı.");
        return result.GetArrayLength();
    }
    private async Task<JsonDocument> ReadAsync(OzonSettings settings, string path, string body, CancellationToken cancellationToken)
    {
        Validate(settings);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api-seller.ozon.ru" + path);
        request.Headers.Add("Client-Id", settings.ClientId);
        request.Headers.Add("Api-Key", settings.ApiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ozon HTTP {(int)response.StatusCode}: " + ((int)response.StatusCode == 429
                ? "İstek sınırına ulaşıldı; en az bir dakika sonra tekrar deneyin."
                : "Client ID, API key ve bu işlem için okuma yetkisini kontrol edin."));
        try { return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); }
        catch (JsonException) { throw new InvalidOperationException("Ozon yanıtı okunamadı; erişim doğrulanamadı."); }
    }
}
