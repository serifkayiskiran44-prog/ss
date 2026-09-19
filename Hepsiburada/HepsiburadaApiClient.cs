using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Hepsiburada;

public sealed class HepsiburadaApiClient : IDisposable
{
    const int PageSize = 1000;
    const int MaxPages = 10_000;
    const int MaxRows = 1_000_000;
    const int MaxResponseBytes = 8 * 1024 * 1024;
    readonly HepsiburadaCredentials credentials;
    readonly HttpClient http;
    readonly bool ownsHttp;
    readonly string productBaseUrl;
    readonly string listingBaseUrl;
    readonly string authorization;
    readonly IHepsiburadaDelay delay;
    bool disposed;

    sealed record ProductPage(IReadOnlyList<HepsiburadaMerchantProduct> Rows, int Number, int TotalPages);

    sealed class SystemDelay : IHepsiburadaDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
    }

    public HepsiburadaApiClient(HepsiburadaCredentials credentials, HttpClient? httpClient = null, IHepsiburadaDelay? delay = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        global::TrMarketplaceHubDesktop.HepsiburadaConnection.Validate(credentials);
        if (credentials.MerchantId.Contains(':')) throw new ArgumentException("Hepsiburada merchant ID geçersiz.", nameof(credentials));
        this.credentials = credentials;
        this.delay = delay ?? new SystemDelay();
        var sit = credentials.Environment == HepsiburadaEnvironment.Sit ? "-sit" : "";
        productBaseUrl = $"https://mpop{sit}.hepsiburada.com/product";
        listingBaseUrl = $"https://listing-external{sit}.hepsiburada.com";
        authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.MerchantId + ":" + credentials.ServiceKey));
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public async Task<IReadOnlyList<HepsiburadaCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        const int categoryPageSize = 2000;
        var result = new List<HepsiburadaCategory>();
        var byId = new Dictionary<long, HepsiburadaCategory>();
        int? expectedPages = null;
        for (var page = 0; page < MaxPages; page++)
        {
            using var document = await RequestAsync($"{productBaseUrl}/api/categories/get-all-categories?page={page}&size={categoryPageSize}", cancellationToken);
            var root = document.RootElement;
            var pages = NonnegativeInt(root, "totalPages");
            var number = NonnegativeInt(root, "number", "page");
            if (number != page || pages > MaxPages || (expectedPages.HasValue && expectedPages != pages)) throw InvalidResponse();
            expectedPages = pages;
            var data = ReadArray(root, "data", "content");
            if (data.GetArrayLength() > categoryPageSize) throw InvalidResponse();
            foreach (var item in data.EnumerateArray())
            {
                var category = new HepsiburadaCategory(PositiveLong(item, "categoryId"), Text(item, "name"), CategoryPath(item),
                    Boolean(item, "leaf"), Boolean(item, "available"), IsActive(Text(item, "status")));
                if (byId.TryGetValue(category.Id, out var existing))
                {
                    if (existing != category) throw InvalidResponse();
                    continue;
                }
                if (result.Count >= MaxRows) throw InvalidResponse();
                byId.Add(category.Id, category);
                result.Add(category);
            }
            if (pages == 0 || page + 1 >= pages) return result.AsReadOnly();
        }
        throw InvalidResponse();
    }

    public async Task<IReadOnlyList<HepsiburadaAttribute>> GetCategoryAttributesAsync(long categoryId, CancellationToken cancellationToken = default)
    {
        if (categoryId <= 0) throw new ArgumentException("Hepsiburada kategori kimliği geçersiz.", nameof(categoryId));
        using var document = await RequestAsync($"{productBaseUrl}/api/categories/{categoryId}/attributes", cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var result = new List<HepsiburadaAttribute>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in new[] { "baseAttributes", "attributes", "variantAttributes" })
        {
            if (!data.TryGetProperty(group, out var rows) || rows.ValueKind == JsonValueKind.Null) continue;
            if (rows.ValueKind != JsonValueKind.Array) throw InvalidResponse();
            foreach (var item in rows.EnumerateArray())
            {
                var id = IdentifierText(item, "id");
                if (!ids.Add(id)) throw InvalidResponse();
                var values = ReadOptionalValues(item);
                result.Add(new(id, Text(item, "name"), Boolean(item, "mandatory"), OptionalBoolean(item, "multiValue") ?? false,
                    ParseKind(OptionalText(item, "type"), values.Count), values));
            }
        }
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<HepsiburadaAttributeValue>> GetAttributeValuesAsync(long categoryId, string attributeId, CancellationToken cancellationToken = default)
    {
        if (categoryId <= 0 || string.IsNullOrWhiteSpace(attributeId) || attributeId.Length > 256 || attributeId.Any(char.IsControl))
            throw new ArgumentException("Hepsiburada özellik kimliği geçersiz.");
        using var document = await RequestAsync($"{productBaseUrl}/api/categories/{categoryId}/attribute/{Uri.EscapeDataString(attributeId)}/values", cancellationToken);
        var array = ReadArray(document.RootElement, "data", "values");
        return ParseValues(array);
    }

    public async Task<IReadOnlyList<HepsiburadaBuybox>> GetBuyboxAsync(IReadOnlyCollection<string> hepsiburadaSkus, CancellationToken cancellationToken = default)
    {
        var skus = ValidateSkuList(hepsiburadaSkus);
        var query = string.Join(",", skus.Select(Uri.EscapeDataString));
        using var document = await RequestAsync($"{listingBaseUrl}/buybox-orders/merchantid/{Uri.EscapeDataString(credentials.MerchantId)}?skuList={query}", cancellationToken);
        var array = ReadRootArray(document.RootElement, "data", "items");
        var result = new List<HepsiburadaBuybox>(array.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var sku = FirstText(item, "hbSku", "hepsiburadaSku");
            if (!ids.Add(sku)) throw InvalidResponse();
            result.Add(new(sku, OptionalText(item, "merchantSku"), FirstNullableInt(item, "rank", "buyboxOrder"),
                FirstNullableDecimal(item, "winningPrice", "buyboxPrice"), OptionalNullableDecimal(item, "ownPrice"), DateTime.UtcNow));
        }
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<HepsiburadaCommission>> GetCommissionsAsync(IReadOnlyCollection<string> hepsiburadaSkus, CancellationToken cancellationToken = default)
    {
        var skus = ValidateSkuList(hepsiburadaSkus);
        var query = string.Join(",", skus.Select(Uri.EscapeDataString));
        using var document = await RequestAsync($"{listingBaseUrl}/commissions/merchantid/{Uri.EscapeDataString(credentials.MerchantId)}?skuList={query}", cancellationToken);
        var array = ReadRootArray(document.RootElement, "data", "items");
        var result = new List<HepsiburadaCommission>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
            result.Add(new(FirstText(item, "hbSku", "hepsiburadaSku"), OptionalText(item, "merchantSku"), NonnegativeDecimal(item, "rate", "commissionRate"), OptionalText(item, "currency")));
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<HepsiburadaMerchantProduct>> GetMerchantProductsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<HepsiburadaMerchantProduct>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        int? expectedPages = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var current = await GetMerchantProductsPageAsync(page, PageSize, cancellationToken);
            if (current.Number != page || current.TotalPages is < 0 or > MaxPages ||
                (expectedPages.HasValue && expectedPages.Value != current.TotalPages)) throw InvalidResponse();
            expectedPages = current.TotalPages;
            if (current.TotalPages == 0)
            {
                if (page != 0 || current.Rows.Count != 0) throw InvalidResponse();
                return Array.Empty<HepsiburadaMerchantProduct>();
            }
            if (page >= current.TotalPages || current.Rows.Count > PageSize) throw InvalidResponse();
            foreach (var row in current.Rows)
            {
                if (!identities.Add(row.HepsiburadaSku) || result.Count >= MaxRows) throw InvalidResponse();
                result.Add(row);
            }
            if (page + 1 >= current.TotalPages) return result.AsReadOnly();
        }
        throw InvalidResponse();
    }

    public async Task<IReadOnlyList<HepsiburadaListing>> GetListingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = $"{listingBaseUrl}/listings/merchantid/{Uri.EscapeDataString(credentials.MerchantId)}?offset=0&limit={PageSize}";
        using var document = await RequestAsync(path, cancellationToken);
        var rows = ReadArray(document.RootElement, "listings", "data");
        if (rows.GetArrayLength() > MaxRows) throw InvalidResponse();
        var result = new List<HepsiburadaListing>(rows.GetArrayLength());
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in rows.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var merchantId = OptionalText(item, "merchantId");
            if (merchantId.Length > 0 && !string.Equals(merchantId, credentials.MerchantId, StringComparison.Ordinal)) throw InvalidResponse();
            var merchantSku = Text(item, "merchantSku");
            var hbSku = FirstText(item, "hbSku", "hepsiburadaSku");
            if (!identities.Add(hbSku)) throw InvalidResponse();
            result.Add(new(credentials.MerchantId, merchantSku, hbSku, OptionalText(item, "barcode"),
                NonnegativeInt(item, "availableStock", "stock"), NonnegativeDecimal(item, "price"), NonnegativeInt(item, "dispatchTime")));
        }
        return result.AsReadOnly();
    }

    public async Task<HepsiburadaConnectionIdentity> TestReadOnlyAsync(CancellationToken cancellationToken = default)
    {
        var page = await GetMerchantProductsPageAsync(0, 1, cancellationToken);
        if (page.Rows.Any(row => row.MerchantId.Length > 0 && !string.Equals(row.MerchantId, credentials.MerchantId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Hepsiburada yanıtı farklı mağazaya ait.");
        return new(credentials.MerchantId, credentials.Environment, DateTime.UtcNow);
    }

    async Task<ProductPage> GetMerchantProductsPageAsync(int page, int size, CancellationToken cancellationToken)
    {
        var path = $"{productBaseUrl}/api/products/all-products-of-merchant/{Uri.EscapeDataString(credentials.MerchantId)}?page={page}&size={size}";
        using var document = await RequestAsync(path, cancellationToken);
        var root = document.RootElement;
        var array = ReadArray(root, "data", "content");
        if (array.GetArrayLength() > size) throw InvalidResponse();
        var rows = new List<HepsiburadaMerchantProduct>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var merchantId = OptionalText(item, "merchantId");
            rows.Add(new(merchantId, Text(item, "merchantSku"), OptionalText(item, "barcode"), FirstText(item, "hbSku", "hepsiburadaSku")));
        }
        return new(rows.AsReadOnly(), NonnegativeInt(root, "number", "page"), NonnegativeInt(root, "totalPages"));
    }

    async Task<JsonDocument> RequestAsync(string absoluteUrl, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        for (var attempt = 0; attempt < 3; attempt++)
        {
          using var request = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
          request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
          request.Headers.UserAgent.ParseAdd(credentials.UserAgent);
          request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
          try
          {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
            {
                await delay.DelayAsync(ReadRetryDelay(response, attempt), cancellationToken);
                continue;
            }
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new InvalidOperationException($"Hepsiburada isteği yönlendirme yanıtı verdi ({(int)response.StatusCode}); istek uygulanmadı.");
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException($"Hepsiburada yetkilendirmesi başarısız ({(int)response.StatusCode}). Merchant ID ve servis anahtarını kontrol edin.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Hepsiburada isteği başarısız ({(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength is > MaxResponseBytes) throw InvalidResponse();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken);
                if (read == 0) break;
                if (buffer.Length + read > MaxResponseBytes) throw InvalidResponse();
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 64 }, cancellationToken);
          }
          catch (JsonException) { throw InvalidResponse(); }
          catch (IOException) { throw InvalidResponse(); }
        }
        throw new InvalidOperationException("Hepsiburada hız sınırı aşıldı (429); daha sonra tekrar deneyin.");
    }

    static TimeSpan ReadRetryDelay(HttpResponseMessage response, int attempt)
    {
        var seconds = Math.Pow(2, attempt);
        if (response.Headers.RetryAfter?.Delta is { } delta) seconds = delta.TotalSeconds;
        else if (response.Headers.TryGetValues("Retry-After", out var values) && double.TryParse(values.FirstOrDefault(), out var parsed)) seconds = parsed;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0) seconds = Math.Pow(2, attempt);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.1, 30));
    }

    static IReadOnlyList<string> ValidateSkuList(IReadOnlyCollection<string> skus)
    {
        ArgumentNullException.ThrowIfNull(skus);
        if (skus.Count is < 1 or > 1000) throw new ArgumentException("Hepsiburada SKU listesi geçersiz.", nameof(skus));
        var result = new List<string>(skus.Count);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sku in skus)
        {
            if (string.IsNullOrWhiteSpace(sku) || sku != sku.Trim() || sku.Length > 256 || sku.Any(char.IsControl) || !unique.Add(sku))
                throw new ArgumentException("Hepsiburada SKU listesi geçersiz.", nameof(skus));
            result.Add(sku);
        }
        return result.AsReadOnly();
    }

    static IReadOnlyList<HepsiburadaAttributeValue> ReadOptionalValues(JsonElement item)
    {
        if (!item.TryGetProperty("values", out var values) || values.ValueKind == JsonValueKind.Null) return Array.Empty<HepsiburadaAttributeValue>();
        if (values.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        return ParseValues(values);
    }

    static IReadOnlyList<HepsiburadaAttributeValue> ParseValues(JsonElement values)
    {
        var result = new List<HepsiburadaAttributeValue>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in values.EnumerateArray())
        {
            var id = FirstText(item, "id", "valueId");
            if (!ids.Add(id)) throw InvalidResponse();
            result.Add(new(id, FirstText(item, "name", "value")));
        }
        return result.AsReadOnly();
    }

    static HepsiburadaAttributeKind ParseKind(string type, int valueCount) => type.ToLowerInvariant() switch
    {
        "enum" or "list" or "select" or "singleselect" or "multiselect" => HepsiburadaAttributeKind.List,
        "number" or "integer" or "decimal" => HepsiburadaAttributeKind.Number,
        "boolean" or "bool" => HepsiburadaAttributeKind.Boolean,
        "text" or "string" => HepsiburadaAttributeKind.Text,
        _ when valueCount > 0 => HepsiburadaAttributeKind.List,
        _ => HepsiburadaAttributeKind.Unknown
    };

    static bool IsActive(string status) => status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) || status.Equals("AKTİF", StringComparison.OrdinalIgnoreCase);

    static long PositiveLong(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) && result > 0 ? result : throw InvalidResponse();

    static bool Boolean(JsonElement item, string name) => OptionalBoolean(item, name) ?? throw InvalidResponse();

    static bool? OptionalBoolean(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw InvalidResponse();
    }

    static int? OptionalNullableInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) && result >= 0 ? result : throw InvalidResponse();
    }

    static int? FirstNullableInt(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            var value = OptionalNullableInt(item, name);
            if (value.HasValue) return value;
        }
        return null;
    }

    static decimal? OptionalNullableDecimal(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result) && result >= 0 ? result : throw InvalidResponse();
    }

    static decimal? FirstNullableDecimal(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            var value = OptionalNullableDecimal(item, name);
            if (value.HasValue) return value;
        }
        return null;
    }

    static JsonElement ReadArray(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array) return value;
        throw InvalidResponse();
    }

    static JsonElement ReadRootArray(JsonElement root, params string[] names) =>
        root.ValueKind == JsonValueKind.Array ? root : ReadArray(root, names);

    static string CategoryPath(JsonElement item)
    {
        if (item.TryGetProperty("paths", out var paths))
        {
            if (paths.ValueKind == JsonValueKind.String) return paths.GetString()?.Trim() ?? "";
            if (paths.ValueKind == JsonValueKind.Array)
            {
                var parts = paths.EnumerateArray()
                    .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : throw InvalidResponse())
                    .Where(value => value.Length > 0);
                return string.Join(" > ", parts);
            }
            throw InvalidResponse();
        }
        return OptionalText(item, "path");
    }

    static string IdentifierText(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) throw InvalidResponse();
        if (value.ValueKind == JsonValueKind.String) return Text(item, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0)
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        throw InvalidResponse();
    }

    static string FirstText(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            var value = OptionalText(item, name);
            if (value.Length > 0) return value;
        }
        throw InvalidResponse();
    }

    static string Text(JsonElement item, string name) => OptionalText(item, name) is { Length: > 0 } value ? value : throw InvalidResponse();

    static string OptionalText(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind != JsonValueKind.String) throw InvalidResponse();
        var text = value.GetString() ?? "";
        if (text.Length > 16_384 || text.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c))) throw InvalidResponse();
        return text.Trim();
    }

    static int NonnegativeInt(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) && result >= 0) return result;
        throw InvalidResponse();
    }

    static decimal NonnegativeDecimal(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var result) && result >= 0) return result;
        throw InvalidResponse();
    }

    static InvalidDataException InvalidResponse() => new("Hepsiburada yanıtı eksik, geçersiz veya güvenli sınırların dışında; sonuç uygulanmadı.");

    void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(HepsiburadaApiClient));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ownsHttp) http.Dispose();
    }
}
