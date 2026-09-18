using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Trendyol;

public sealed class TrendyolApiClient : IDisposable
{
    const string BaseUrl = "https://apigw.trendyol.com";
    const int MaxResponseBytes = 8 * 1024 * 1024;
    const int MaxRows = 1_000_000;
    const int MaxPages = 10_000;
    readonly HttpClient http;
    readonly bool ownsHttp;
    readonly long sellerId;
    readonly string authorization;
    readonly string seller;
    bool disposed;
    public string AccountFingerprint { get; }

    // An injected HttpClient is a trusted transport (primarily for offline tests).
    // Production uses this client's own handler with redirection disabled.
    public TrendyolApiClient(TrendyolSettings settings, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TrendyolConnection.Validate(settings);
        if (!long.TryParse(settings.SupplierId, NumberStyles.None, CultureInfo.InvariantCulture, out sellerId) || sellerId <= 0 || settings.ApiKey.Contains(':'))
            throw new ArgumentException("Trendyol bağlantı bilgileri geçersiz.");
        seller = sellerId.ToString(CultureInfo.InvariantCulture);
        AccountFingerprint = TrendyolWorkspaceStore.AccountFingerprint(settings);
        authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.ApiKey + ":" + settings.ApiSecret));
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<IReadOnlyList<TrendyolCategory>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        using var document = await RequestAsync(HttpMethod.Get, "/integration/product/product-categories", null, cancellationToken);
        var result = new List<TrendyolCategory>();
        var ids = new HashSet<long>();
        void Visit(JsonElement array, string parent, int depth)
        {
            if (depth > 40 || array.ValueKind != JsonValueKind.Array) throw InvalidResponse();
            foreach (var item in array.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = PositiveId(item, "id");
                if (!ids.Add(id) || result.Count >= MaxRows) throw InvalidResponse();
                var name = DisplayText(item, "name");
                var children = Array(item, "subCategories");
                var path = parent.Length == 0 ? name : parent + " > " + name;
                result.Add(new(id, name, path, children.GetArrayLength() == 0));
                Visit(children, path, depth + 1);
            }
        }
        Visit(Array(document.RootElement, "categories"), "", 0);
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<TrendyolBrand>> GetBrandsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TrendyolBrand>();
        var ids = new HashSet<long>();
        for (var page = 0; page < MaxPages; page++)
        {
            using var document = await RequestAsync(HttpMethod.Get, $"/integration/product/brands?page={page}&size=1000", null, cancellationToken);
            var array = Array(document.RootElement, "brands");
            if (array.GetArrayLength() > 1000) throw InvalidResponse();
            foreach (var item in array.EnumerateArray())
            {
                var id = PositiveId(item, "id");
                if (!ids.Add(id) || result.Count >= MaxRows) throw InvalidResponse();
                result.Add(new(id, DisplayText(item, "name")));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (array.GetArrayLength() < 1000) return result.AsReadOnly();
        }
        throw InvalidResponse();
    }

    public async Task<IReadOnlyList<TrendyolBrand>> GetBrandsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!ValidIdentifier(name)) throw new ArgumentException("Trendyol marka arama metni geçersiz.");
        using var document = await RequestAsync(HttpMethod.Get, "/integration/product/brands/by-name?name=" + Uri.EscapeDataString(name), null, cancellationToken);
        var array = document.RootElement;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaxRows) throw InvalidResponse();
        var result = new List<TrendyolBrand>();
        var namesById = new Dictionary<long, string>();
        foreach (var item in array.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = PositiveId(item, "id");
            var displayName = DisplayText(item, "name");
            if (namesById.TryGetValue(id, out var previousName))
            {
                if (!string.Equals(previousName, displayName, StringComparison.Ordinal)) throw InvalidResponse();
                continue;
            }
            namesById.Add(id, displayName);
            result.Add(new(id, displayName));
        }
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<TrendyolAttribute>> GetAttributesAsync(long categoryId, CancellationToken cancellationToken = default)
    {
        if (categoryId <= 0) throw new ArgumentException("Trendyol kategori ID geçersiz.");
        using var document = await RequestAsync(HttpMethod.Get, $"/integration/product/categories/{categoryId}/attributes", null, cancellationToken);
        var result = new List<TrendyolAttribute>();
        var ids = new HashSet<long>();
        foreach (var item in Array(document.RootElement, "categoryAttributes").EnumerateArray())
        {
            var attribute = Property(item, "attribute");
            var id = PositiveId(attribute, "id");
            if (!ids.Add(id) || ids.Count > 1000) throw InvalidResponse();
            var name = DisplayText(attribute, "name");
            var required = Boolean(item, "required");
            var custom = Boolean(item, "allowCustom");
            var multiple = Boolean(item, "allowMultipleAttributeValues");
            var values = await GetAttributeValuesAsync(categoryId, id, cancellationToken);
            result.Add(new(id, name, required, custom, multiple, values));
        }
        return result.AsReadOnly();
    }

    async Task<IReadOnlyList<TrendyolAttributeValue>> GetAttributeValuesAsync(long categoryId, long attributeId, CancellationToken cancellationToken)
    {
        var result = new List<TrendyolAttributeValue>();
        var ids = new HashSet<long>();
        int? totalPages = null;
        for (var page = 0; page < MaxPages; page++)
        {
            using var document = await RequestAsync(HttpMethod.Get, $"/integration/product/categories/{categoryId}/attributes/{attributeId}/values?page={page}&size=1000", null, cancellationToken);
            var root = document.RootElement;
            var pages = NonnegativeInt(root, "totalPages");
            if (pages > MaxPages || (totalPages.HasValue && totalPages != pages) || NonnegativeInt(root, "page") != page) throw InvalidResponse();
            totalPages = pages;
            var array = Array(root, "content");
            var count = array.GetArrayLength();
            if (count > 1000 || (pages == 0 && count != 0) || (page + 1 < pages && count != 1000) || (pages > 0 && count == 0)) throw InvalidResponse();
            foreach (var item in array.EnumerateArray())
            {
                var id = PositiveId(item, "attributeValueId");
                if (!ids.Add(id) || result.Count >= MaxRows) throw InvalidResponse();
                result.Add(new(id, DisplayText(item, "attributeValue")));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (page + 1 >= pages) return result.AsReadOnly();
        }
        throw InvalidResponse();
    }

    public async Task<IReadOnlyList<TrendyolRemoteProduct>> GetProductsAsync(bool approved, CancellationToken cancellationToken = default)
    {
        var result = new List<TrendyolRemoteProduct>();
        var barcodes = new HashSet<string>(StringComparer.Ordinal);
        var contents = new HashSet<long>();
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var readContents = 0;
        int? total = null;
        string? nextToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var cursor = page >= 100;
            if (cursor && (string.IsNullOrEmpty(nextToken) || !tokens.Add(nextToken))) throw InvalidResponse();
            var suffix = cursor ? "&nextPageToken=" + Uri.EscapeDataString(nextToken!) : "&page=" + page;
            using var document = await RequestAsync(HttpMethod.Get, $"/integration/product/sellers/{seller}/products/{(approved ? "approved" : "unapproved")}?size=100" + suffix, null, cancellationToken);
            var root = document.RootElement;
            var currentTotal = NonnegativeInt(root, "totalElements");
            var pages = NonnegativeInt(root, "totalPages");
            if (currentTotal > MaxRows || pages > MaxPages || (total.HasValue && total != currentTotal) || pages != (currentTotal + 99) / 100) throw InvalidResponse();
            if (!cursor && NonnegativeInt(root, "page") != page) throw InvalidResponse();
            total = currentTotal;
            var array = Array(root, "content");
            if (array.GetArrayLength() != Math.Min(100, currentTotal - readContents)) throw InvalidResponse();
            foreach (var item in array.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var title = DisplayText(item, "title");
                if (approved)
                {
                    var contentId = PositiveId(item, "contentId");
                    if (!contents.Add(contentId)) throw InvalidResponse();
                    var offers = Array(item, "variants");
                    if (offers.GetArrayLength() == 0) throw InvalidResponse();
                    foreach (var offer in offers.EnumerateArray()) AddProduct(offer, title, contentId, true);
                }
                else AddProduct(item, title, 0, false);
            }
            readContents += array.GetArrayLength();
            if (readContents == currentTotal) return result.AsReadOnly();
            nextToken = OptionalText(root, "nextPageToken");
        }
        throw InvalidResponse();

        void AddProduct(JsonElement item, string title, long contentId, bool isApproved)
        {
            if (PositiveId(item, "supplierId") != sellerId) throw InvalidResponse();
            var barcode = Text(item, "barcode");
            if (!barcodes.Add(barcode) || result.Count >= MaxRows) throw InvalidResponse();
            var stock = isApproved ? OptionalObject(item, "stock") : item;
            var price = isApproved ? OptionalObject(item, "price") : item;
            var status = ReadProductStatus(item, isApproved);
            result.Add(new(barcode, ReadRemoteStockCode(item), title, contentId,
                OptionalInt(stock, "quantity"), OptionalDecimal(price, "salePrice"), OptionalDecimal(price, "listPrice"), isApproved)
            {
                Status = status.Status,
                StatusDetail = status.Detail
            });
        }
    }

    static (string Status, string Detail) ReadProductStatus(JsonElement item, bool approved)
    {
        var documentNeeded = OptionalBoolean(item, "docNeeded");
        var details = new List<string>();
        string status;
        if (approved)
        {
            // Read every supplied flag before choosing precedence so a malformed lower
            // priority flag cannot be silently accepted behind a higher priority one.
            var blacklisted = OptionalBoolean(item, "blacklisted");
            var archived = OptionalBoolean(item, "archived");
            var locked = OptionalBoolean(item, "locked");
            var onSale = OptionalBoolean(item, "onSale");
            var lockReason = OptionalDisplayText(item, "lockReason");
            status = blacklisted == true ? "blacklisted" : archived == true ? "archived" : locked == true ? "locked"
                : documentNeeded == true ? "documentRequired" : onSale == true ? "onSale" : onSale == false ? "notOnSale" : "approved";
            if (locked == true && lockReason.Length > 0) details.Add(lockReason);
        }
        else
        {
            var remoteStatus = OptionalText(item, "status");
            status = documentNeeded == true ? "documentRequired" : string.IsNullOrWhiteSpace(remoteStatus) ? "unapproved" : remoteStatus;
            if (item.TryGetProperty("rejectReasonDetails", out var rejections) && rejections.ValueKind != JsonValueKind.Null)
            {
                if (rejections.ValueKind != JsonValueKind.Array) throw InvalidResponse();
                foreach (var rejection in rejections.EnumerateArray())
                {
                    var reason = OptionalDisplayText(rejection, "rejectReason");
                    var detail = OptionalDisplayText(rejection, "rejectReasonDetail");
                    var combined = reason.Length > 0 && detail.Length > 0 ? reason + ": " + detail : reason + detail;
                    if (combined.Length > 0) details.Add(combined);
                }
            }
        }
        if (documentNeeded == true) details.Add("Trendyol satıcı panelinden gerekli belgeyi yükleyin.");
        var statusDetail = string.Join(" | ", details);
        if (statusDetail.Length > 16_384) throw InvalidResponse();
        return (status, statusDetail);
    }

    public async Task<IReadOnlyList<TrendyolAddress>> GetAddressesAsync(CancellationToken cancellationToken = default)
    {
        using var document = await RequestAsync(HttpMethod.Get, $"/integration/sellers/{seller}/addresses", null, cancellationToken);
        var result = new List<TrendyolAddress>();
        var ids = new HashSet<long>();
        foreach (var item in Array(document.RootElement, "supplierAddresses").EnumerateArray())
        {
            var id = PositiveId(item, "id");
            if (!ids.Add(id)) throw InvalidResponse();
            result.Add(new(id, DisplayText(item, "fullAddress"), Boolean(item, "isShipmentAddress"), Boolean(item, "isReturningAddress")));
        }
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<TrendyolCarrier>> GetCarriersAsync(CancellationToken cancellationToken = default)
    {
        using var document = await RequestAsync(HttpMethod.Get, "/integration/product/lookup/cargo-providers", null, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        var result = new List<TrendyolCarrier>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var code = Text(item, "code");
            if (!codes.Add(code)) throw InvalidResponse();
            result.Add(new(code, DisplayText(item, "name")));
        }
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<TrendyolBuybox>> GetBuyboxAsync(IEnumerable<string> barcodes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(barcodes);
        var requested = barcodes.Take(MaxRows + 1).ToArray();
        if (requested.Length > MaxRows || requested.Any(x => !ValidIdentifier(x))) throw new ArgumentException("Trendyol barkod listesi geçersiz.");
        var result = new List<TrendyolBuybox>();
        foreach (var chunk in requested.Distinct(StringComparer.Ordinal).Chunk(10))
        {
            using var document = await RequestAsync(HttpMethod.Post, $"/integration/product/sellers/{seller}/products/buybox-information", JsonSerializer.Serialize(new { barcodes = chunk }), cancellationToken);
            var remaining = chunk.ToHashSet(StringComparer.Ordinal);
            foreach (var item in Array(document.RootElement, "buyboxInfo").EnumerateArray())
            {
                var barcode = Text(item, "barcode");
                if (!remaining.Remove(barcode)) throw InvalidResponse();
                result.Add(new(barcode, OptionalInt(item, "buyboxOrder"), OptionalDecimal(item, "buyboxPrice"),
                    OptionalDecimal(item, "secondBuyboxPrice"), OptionalDecimal(item, "thirdBuyboxPrice"), Boolean(item, "hasMultipleSeller")));
            }
            // Missing barcodes mean the service supplied no observation, not an invented rank/price.
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result.AsReadOnly();
    }

    public async Task<JsonElement> GetBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        if (!ValidIdentifier(batchId) || batchId is "." or "..") throw new ArgumentException("Trendyol işlem kimliği geçersiz.");
        using var document = await RequestAsync(HttpMethod.Get, $"/integration/product/sellers/{seller}/products/batch-requests/{Uri.EscapeDataString(batchId)}", null, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        return document.RootElement.Clone();
    }

    // Only the preview/approval dispatcher in this assembly may send mutation payloads.
    // A timeout or network failure must remain unknown; this client never retries writes.
    internal async Task<string> SendBatchAsync(string operation, string payloadJson, CancellationToken cancellationToken = default)
    {
        var path = operation switch
        {
            "create" => $"/integration/product/sellers/{seller}/v2/products",
            "inventory" => $"/integration/inventory/sellers/{seller}/products/price-and-inventory",
            "delivery" => $"/integration/product/sellers/{seller}/products/delivery-info-bulk-update",
            "details" => $"/integration/product/sellers/{seller}/products/variant-bulk-update",
            "unapproved" => $"/integration/product/sellers/{seller}/products/unapproved-bulk-update",
            "content" => $"/integration/product/sellers/{seller}/products/content-bulk-update",
            _ => throw new ArgumentException("Desteklenmeyen Trendyol işlemi.")
        };
        if (string.IsNullOrWhiteSpace(payloadJson) || Encoding.UTF8.GetByteCount(payloadJson) > MaxResponseBytes) throw new ArgumentException("Trendyol gönderim içeriği geçersiz.");
        try
        {
            using var payload = JsonDocument.Parse(payloadJson, new JsonDocumentOptions { MaxDepth = 48 });
            var items = Array(payload.RootElement, "items");
            if (items.GetArrayLength() is < 1 or > 1000 || items.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Object)) throw InvalidResponse();
            if (operation == "details" && items.EnumerateArray().Any(item => item.EnumerateObject().Any(property =>
                property.Name is not ("barcode" or "cargoProviders" or "shipmentAddressId" or "returningAddressId")))) throw InvalidResponse();
            if (operation == "unapproved" && items.EnumerateArray().Any(item => item.EnumerateObject().Any(property =>
                property.Name is not ("barcode" or "title" or "description" or "productMainId" or "brandId" or "categoryId"
                    or "stockCode" or "origin" or "dimensionalWeight" or "vatRate" or "deliveryOption" or "images" or "attributes"
                    or "cargoProviders" or "shipmentAddressId" or "returningAddressId")))) throw InvalidResponse();
            if (operation == "content")
            {
                foreach (var item in items.EnumerateArray())
                {
                    _ = PositiveId(item, "contentId");
                    if (item.EnumerateObject().Any(property => property.Name is not ("contentId" or "title" or "description" or "images"))
                        || !(item.TryGetProperty("title", out _) || item.TryGetProperty("description", out _) || item.TryGetProperty("images", out _))) throw InvalidResponse();
                }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new ArgumentException("Trendyol gönderim içeriği geçersiz.");
        }
        using var document = await RequestAsync(HttpMethod.Post, path, payloadJson, cancellationToken);
        return Text(document.RootElement, "batchRequestId");
    }

    async Task<JsonDocument> RequestAsync(HttpMethod method, string path, string? json, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = http.Timeout;
        deadline.CancelAfter(timeout > TimeSpan.Zero && timeout < TimeSpan.FromSeconds(60) ? timeout : TimeSpan.FromSeconds(60));
        var requestCancellation = deadline.Token;
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization);
        request.Headers.TryAddWithoutValidation("User-Agent", seller + " - Self Integration");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Trendyol isteği başarısız (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
            if (response.RequestMessage?.RequestUri is { } returnedUri && returnedUri != request.RequestUri)
                throw new HttpRequestException("Trendyol yönlendirmesi reddedildi.");
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw InvalidResponse();
            await using var stream = await response.Content.ReadAsStreamAsync(requestCancellation);
            using var buffer = new MemoryStream();
            var bytes = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(bytes, requestCancellation)) != 0)
            {
                if (buffer.Length + read > MaxResponseBytes) throw InvalidResponse();
                buffer.Write(bytes, 0, read);
            }
            requestCancellation.ThrowIfCancellationRequested();
            return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 48 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Trendyol isteği iptal edildi.", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new HttpRequestException("Trendyol isteği zaman aşımına uğradı; gönderim sonucu belirsiz olabilir.");
        }
        catch (HttpRequestException error)
        {
            throw new HttpRequestException(error.StatusCode.HasValue ? $"Trendyol isteği başarısız (HTTP {(int)error.StatusCode.Value})." : "Trendyol bağlantısı tamamlanamadı; gönderim sonucu belirsiz olabilir.", null, error.StatusCode);
        }
        catch (JsonException) { throw InvalidResponse(); }
        catch (IOException) { throw InvalidResponse(); }
    }

    static InvalidDataException InvalidResponse() => new("Trendyol yanıtı eksik, geçersiz veya güvenli sınırların dışında; sonuç uygulanmadı.");
    static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl);
    static JsonElement Property(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : throw InvalidResponse();
    static JsonElement Array(JsonElement item, string name)
    {
        var value = Property(item, name);
        return value.ValueKind == JsonValueKind.Array ? value : throw InvalidResponse();
    }
    static string Text(JsonElement item, string name) => OptionalText(item, name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value : throw InvalidResponse();
    static string DisplayText(JsonElement item, string name)
    {
        var value = Property(item, name);
        if (value.ValueKind != JsonValueKind.String) throw InvalidResponse();
        var text = value.GetString()!;
        if (text.Length > 16_384 || text.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character))) throw InvalidResponse();
        var normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length > 0 ? normalized : throw InvalidResponse();
    }
    static string OptionalDisplayText(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind != JsonValueKind.String) throw InvalidResponse();
        var text = value.GetString()!;
        if (text.Length > 16_384 || text.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character))) throw InvalidResponse();
        return string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
    static string? OptionalText(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw InvalidResponse();
        var text = value.GetString()!;
        return text.Length <= 16_384 && !text.Any(char.IsControl) ? text : throw InvalidResponse();
    }
    static string ReadRemoteStockCode(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        if (!item.TryGetProperty("stockCode", out var value) || value.ValueKind == JsonValueKind.Null) return "";
        if (value.ValueKind != JsonValueKind.String) throw InvalidResponse();
        var text = value.GetString()!;
        // Remote metadata may contain whitespace controls. Preserve every character:
        // trimming or display normalization would silently change the matching identity.
        return text.Length <= 16_384 && !text.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character))
            ? text : throw InvalidResponse();
    }
    static long PositiveId(JsonElement item, string name)
    {
        var value = Property(item, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) && id > 0 ? id : throw InvalidResponse();
    }
    static int NonnegativeInt(JsonElement item, string name) => OptionalInt(item, name) is { } number ? number : throw InvalidResponse();
    static int? OptionalInt(JsonElement item, string name)
    {
        if (item.ValueKind == JsonValueKind.Undefined) return null;
        if (item.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number : throw InvalidResponse();
    }
    static decimal? OptionalDecimal(JsonElement item, string name)
    {
        if (item.ValueKind == JsonValueKind.Undefined) return null;
        if (item.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number >= 0 ? number : throw InvalidResponse();
    }
    static bool Boolean(JsonElement item, string name)
    {
        var value = Property(item, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw InvalidResponse();
    }
    static bool? OptionalBoolean(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw InvalidResponse();
    }
    static JsonElement OptionalObject(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return default;
        return value.ValueKind == JsonValueKind.Object ? value : throw InvalidResponse();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ownsHttp) http.Dispose();
    }
}
