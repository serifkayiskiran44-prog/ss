using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record BizimHesapSettings(string FirmId, string Token);
public sealed record BizimHesapProduct(string Id, string Code, string Barcode, string Title, decimal Price, int Quantity);
public sealed record BizimHesapWarehouse(string Id, string Name);
public sealed record BizimHesapInventory(string ProductId, int Quantity);
public sealed record BizimHesapCreateProduct(string Sku, string Barcode, string Title, decimal NetPrice, decimal VatRate, int Quantity, string Currency);
public sealed record BizimHesapCreateResponse(int ResultCode, string ErrorText, string RemoteId);
public sealed class BizimHesapProductRejectedException(string message) : InvalidOperationException(message);

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

    public static HttpRequestMessage BuildAddProductRequest(BizimHesapSettings settings, BizimHesapCreateProduct product)
    {
        Validate(settings);
        if (string.IsNullOrWhiteSpace(product.Sku) || string.IsNullOrWhiteSpace(product.Title) || product.NetPrice < 0 || product.VatRate is < 0 or > 100 || product.Quantity < 0)
            throw new ArgumentException("Ürün kodu, adı, negatif olmayan fiyat/stok ve geçerli KDV gerekli.");
        var currency = product.Currency.Equals("TRY", StringComparison.OrdinalIgnoreCase) ? "TL" : product.Currency.ToUpperInvariant();
        if (currency is not ("TL" or "USD" or "EUR" or "GBP")) throw new ArgumentException("Desteklenmeyen para birimi.");
        var payload = JsonSerializer.Serialize(new { title = product.Title.Trim(), taxRate = product.VatRate, id = product.Sku.Trim(), price = product.NetPrice, currency, productType = 1, unit = "adet", quantity = product.Quantity, barcode = product.Barcode.Trim() });
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(BaseAddress), "addproduct"));
        request.Headers.TryAddWithoutValidation("Token", settings.Token);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return request;
    }

    public async Task<BizimHesapCreateResponse> AddProductAsync(BizimHesapSettings settings, BizimHesapCreateProduct product, CancellationToken cancellationToken = default)
    {
        using var request = BuildAddProductRequest(settings, product);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidOperationException("BizimHesap yönlendirme yanıtı verdi; kimlik bilgisi aktarılmadı.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"BizimHesap HTTP {(int)response.StatusCode}: ürün kaydedilemedi.");
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (json.Length > 65536) throw new InvalidOperationException("BizimHesap ürün kaydı yanıtı beklenenden büyük.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("BizimHesap ürün kaydı yanıtı nesne biçiminde değil.");
            var resultCode = Int(root, "resultCode", "code");
            var error = FirstText(root, "errorText", "error", "message");
            if (resultCode != 0 || error.Length > 0)
            {
                var detail = error.Length > 0 ? AuditStore.Sanitize(error) : "Sunucu hata metni döndürmedi.";
                if (detail.Length > 500) detail = detail[..500];
                throw new BizimHesapProductRejectedException($"BizimHesap ürün kaydını reddetti (resultCode: {resultCode}): {detail}");
            }
            var remoteId = FirstText(root, "id", "guid", "productId");
            if (remoteId.Length == 0 && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                remoteId = FirstText(data, "id", "guid", "productId");
            return new(resultCode, "", remoteId);
        }
        catch (JsonException) { throw new InvalidOperationException("BizimHesap ürün kaydı yanıtı beklenen JSON biçiminde değil."); }
    }

    public async Task<IReadOnlyList<BizimHesapProduct>> ReadAllProductsAsync(BizimHesapSettings settings, CancellationToken cancellationToken = default)
    {
        var all = new List<BizimHesapProduct>();
        for (var page = 1; page <= 100; page++)
        {
            var rows = await ReadProductsAsync(settings, page, 100, cancellationToken);
            all.AddRange(rows);
            if (rows.Count < 100) return all;
        }
        throw new InvalidOperationException("BizimHesap ürün sayfalaması 10.000 kayıt sınırını aştı.");
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
        {
            foreach (var name in new[] { "data", "items", "result" })
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                foreach (var name in new[] { "products", "warehouses", "inventory", "items" })
                    if (data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray();
        }
        throw new InvalidOperationException("BizimHesap yanıt listesi beklenen biçimde değil.");
    }

    static BizimHesapProduct Product(JsonElement row) => new(Text(row, "guid", "productId", "id"), Text(row, "code", "stockCode", "id"), Text(row, "barcode", "barCode"), Text(row, "title", "name"), Decimal(row, "price", "salePrice"), Integer(row, "quantity", "stock", "amount"));
    static string Text(JsonElement row, params string[] names) { foreach (var name in names) if (row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? ""; return ""; }
    static int Integer(JsonElement row, params string[] names) { foreach (var name in names) if (row.TryGetProperty(name, out var value)) { if (value.TryGetInt32(out var number)) return number; if (value.TryGetDouble(out var decimalNumber) && decimalNumber is >= int.MinValue and <= int.MaxValue) return (int)Math.Round(decimalNumber, MidpointRounding.AwayFromZero); } return 0; }
    static decimal Decimal(JsonElement row, params string[] names) { foreach (var name in names) if (row.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number)) return number; return 0; }
    static int Int(JsonElement row, params string[] names) { foreach (var name in names) if (row.TryGetProperty(name, out var value)) { if (value.TryGetInt32(out var number)) return number; if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number; } return 0; }
    static string FirstText(JsonElement row, params string[] names) { foreach (var name in names) if (row.TryGetProperty(name, out var value)) { if (value.ValueKind == JsonValueKind.String) return (value.GetString() ?? "").Trim(); if (value.ValueKind == JsonValueKind.Number) return value.GetRawText(); } return ""; }
}
