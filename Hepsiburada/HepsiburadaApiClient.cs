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
    bool disposed;

    sealed record ProductPage(IReadOnlyList<HepsiburadaMerchantProduct> Rows, int Number, int TotalPages);

    public HepsiburadaApiClient(HepsiburadaCredentials credentials, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        global::TrMarketplaceHubDesktop.HepsiburadaConnection.Validate(credentials);
        if (credentials.MerchantId.Contains(':')) throw new ArgumentException("Hepsiburada merchant ID geçersiz.", nameof(credentials));
        this.credentials = credentials;
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
        using var request = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
        request.Headers.UserAgent.ParseAdd(credentials.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

    static JsonElement ReadArray(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array) return value;
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
