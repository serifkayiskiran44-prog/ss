using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record BizimHesapSettings(string FirmId, string Token);
public sealed record BizimHesapProduct(string Id, string Code, string Barcode, string Title, decimal Price, int Quantity);
public sealed record BizimHesapWarehouse(string Id, string Name);
public sealed record BizimHesapInventory(string ProductId, int Quantity);

/// <summary>Only the endpoints evidenced by the user-provided Postman collection live here.</summary>
public sealed class BizimHesapConnection(HttpClient http)
{
    const string BaseAddress = "https://bizimhesap.com/api/b2b/";
    const int MaxFirmIdLength = 128;
    const int MaxTokenLength = 1024;

    public static void Validate(BizimHesapSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.FirmId) || settings.FirmId.Length > MaxFirmIdLength || settings.FirmId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(settings.Token) || settings.Token.Length > MaxTokenLength || settings.Token.Any(char.IsControl))
            throw new ArgumentException("FirmID ve geçerli API Token gerekli.");
    }

    public async Task<IReadOnlyList<BizimHesapProduct>> ReadProductsAsync(BizimHesapSettings settings, int page = 1, int size = 100, CancellationToken cancellationToken = default)
    {
        if (page < 1 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page), "Sayfa 1+, boyut 1–100 olmalı.");
        using var document = await GetJsonAsync(settings, $"products?page={page}&size={size}", cancellationToken);
        return Rows(document.RootElement).Select(Product).ToArray();
    }

    public async Task<IReadOnlyList<BizimHesapWarehouse>> ReadWarehousesAsync(BizimHesapSettings settings, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(settings, "warehouses", cancellationToken);
        return Rows(document.RootElement).Select(row => new BizimHesapWarehouse(Text(row, "id", "warehouseId", "guid"), Text(row, "name", "title"))).ToArray();
    }

    public async Task<IReadOnlyList<BizimHesapInventory>> ReadInventoryAsync(BizimHesapSettings settings, string warehouseId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(warehouseId) || warehouseId.Length > 160 || warehouseId.Any(char.IsControl)) throw new ArgumentException("Geçerli depo kimliği gerekli.", nameof(warehouseId));
        using var document = await GetJsonAsync(settings, "inventory/" + Uri.EscapeDataString(warehouseId.Trim()), cancellationToken);
        return Rows(document.RootElement).Select(row => new BizimHesapInventory(Text(row, "productId", "id", "guid"), Integer(row, "quantity", "stock", "amount"))).ToArray();
    }

    async Task<JsonDocument> GetJsonAsync(BizimHesapSettings settings, string relativePath, CancellationToken cancellationToken)
    {
        Validate(settings);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(BaseAddress), relativePath));
        request.Headers.TryAddWithoutValidation("Token", settings.Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidOperationException("BizimHesap yönlendirme yanıtı verdi; kimlik bilgisi aktarılmadı.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"BizimHesap HTTP {(int)response.StatusCode}: " + ((int)response.StatusCode == 429 ? "İstek sınırına ulaşıldı." : "Token ve erişim yetkisini kontrol edin."));
        try { return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken)); }
        catch (JsonException) { throw new InvalidOperationException("BizimHesap yanıtı beklenen JSON biçiminde değil."); }
    }

    static IEnumerable<JsonElement> Rows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToArray();
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "data", "items", "result" })
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray();
        throw new InvalidOperationException("BizimHesap yanıt listesi beklenen biçimde değil.");
    }

    static BizimHesapProduct Product(JsonElement row) => new(Text(row, "guid", "productId", "id"), Text(row, "code", "stockCode", "id"), Text(row, "barcode", "barCode"), Text(row, "title", "name"), Decimal(row, "price", "salePrice"), Integer(row, "quantity", "stock", "amount"));
    static string Text(JsonElement row, params string[] names) { foreach (var name in names) if (row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? ""; return ""; }
    static int Integer(JsonElement row, params string[] names) { foreach (var name in names) if (row.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)) return number; return 0; }
    static decimal Decimal(JsonElement row, params string[] names) { foreach (var name in names) if (row.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number)) return number; return 0; }
}
