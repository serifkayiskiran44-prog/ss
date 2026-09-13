using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed class EtsyListingTemplate
{
    public string Currency { get; set; } = "USD";
    public string WhoMade { get; set; } = "";
    public string WhenMade { get; set; } = "";
    public string TitlePrefix { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Materials { get; set; } = "";
    public bool IsSupply { get; set; }
    public long TaxonomyId { get; set; }
    public long ShippingProfileId { get; set; }
    public long ReadinessStateId { get; set; }
}

public static class TemplateStore
{
    static string FilePath(string? directory) => Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"), "etsy-template.json");
    public static EtsyListingTemplate Load(string? directory = null)
    {
        var path = FilePath(directory);
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<EtsyListingTemplate>(File.ReadAllText(path)) ?? new(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("Etsy şablonu okunamadı. Dosyayı kontrol edin."); }
    }
    // Incomplete templates may be saved; every draft is validated before any API call.
    public static void Save(EtsyListingTemplate template, string? directory = null)
    {
        var path = FilePath(directory); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed record EtsyDraftImageResult(long ListingId, long? ImageId, string? ImageError, string? ImageStatus = null);

public sealed class EtsyDrafts(HttpClient http)
{
    public async Task<EtsyDraftImageResult> CreateWithFirstImageAsync(EtsyCredentials credentials, CatalogProduct product, EtsyListingTemplate template,
        Func<Task> beforePost, Func<long, Task> afterCreated, CancellationToken cancellationToken = default)
    {
        var errors = Validate(product, template);
        if (product.EtsyCreationAttempted) errors.Add("Önceki taslak oluşturma sonucunu Etsy'den kontrol edin; otomatik tekrar yapılmaz.");
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        var first = (product.ImageUrls ?? "").Split(['|', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        PreparedMarketplaceImage? image = first is null ? null : await MarketplaceImages.PrepareAsync(await PrepareImageAsync(first, cancellationToken), ImageMarketplace.Etsy);
        var listingId = await CreateAsync(credentials, product, template, cancellationToken, beforePost);
        await afterCreated(listingId);
        if (image is null) return new(listingId, null, null);
        try { return new(listingId, await UploadImageAsync(credentials, listingId, image.Bytes, cancellationToken), null, image.Status); }
        catch (InvalidOperationException error) { return new(listingId, null, error.Message, image.Status); }
    }
    public const int ImageLimit = 20 * 1024 * 1024;
    public async Task<byte[]> PrepareImageAsync(string location, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Scheme != "https" && !uri.IsFile) || (uri.IsFile && (uri.IsUnc || !string.IsNullOrEmpty(uri.Host))))
            throw new InvalidOperationException("Görsel için kullanıcı bilgisi içermeyen HTTPS veya yerel file:/// adresi gerekli.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            if (uri.IsFile)
            {
                await using var local = File.OpenRead(uri.LocalPath);
                return await ReadImage(local, timeout.Token);
            }
            // Separate client ensures Etsy credentials/default headers never reach image hosts.
            using var images = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            using var response = await images.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Görsel indirilemedi (HTTP {(int)response.StatusCode}); doğrudan HTTPS adresini kullanın.");
            if (response.Content.Headers.ContentLength > ImageLimit) throw new InvalidOperationException("Görsel 20 MB sınırını aşıyor.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await ReadImage(stream, timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("Görsel okunamadı; dosyayı veya HTTPS adresini kontrol edin. Taslak oluşturulmadı."); }
    }
    static async Task<byte[]> ReadImage(Stream stream, CancellationToken token)
    {
        using var memory = new MemoryStream(); var buffer = new byte[81920]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + count > ImageLimit) throw new InvalidOperationException("Görsel 20 MB sınırını aşıyor.");
            memory.Write(buffer, 0, count);
        }
        // The normalization decoder validates source formats before a draft is posted.
        // Keep the transfer layer format-neutral (e.g. BMP/TIFF supported by Windows).
        return memory.ToArray();
    }
    static string ImageType(byte[] image)
    {
        if (image.Length == 0 || image.Length > ImageLimit) throw new InvalidOperationException("Görsel boş veya 20 MB sınırını aşıyor.");
        if (image.Length >= 24 && image.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return "image/png";
        if (image.Length >= 4 && image[0] == 255 && image[1] == 216 && image[2] == 255 && image[^2] == 255 && image[^1] == 217) return "image/jpeg";
        if (image.Length >= 13 && (System.Text.Encoding.ASCII.GetString(image, 0, 6) is "GIF87a" or "GIF89a")) return "image/gif";
        throw new InvalidOperationException("Görsel JPEG, PNG veya GIF dosyası olmalı.");
    }
    public async Task<long> UploadImageAsync(EtsyCredentials credentials, long listingId, byte[] image, CancellationToken cancellationToken = default)
    {
        var type = ImageType(image);
        if (listingId <= 0 || !long.TryParse(credentials.ShopId, NumberStyles.None, CultureInfo.InvariantCulture, out var shop) || shop <= 0 ||
            new[] {credentials.Key, credentials.Secret, credentials.Token}.Any(x => string.IsNullOrWhiteSpace(x) || x.Any(c => c <= 32 || c >= 127 || c == ':')))
            throw new InvalidOperationException("Etsy bağlantı bilgileri veya ilan ID geçersiz.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://openapi.etsy.com/v3/application/shops/{shop}/listings/{listingId}/images");
        request.Headers.Add("x-api-key", credentials.Key + ":" + credentials.Secret);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        var body = new MultipartFormDataContent(); request.Content = body;
        var file = new ByteArrayContent(image); file.Headers.ContentType = new MediaTypeHeaderValue(type);
        body.Add(file, "image", "product." + (type == "image/jpeg" ? "jpg" : type[6..]));
        body.Add(new StringContent("1"), "rank");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Etsy taslağı {listingId} mevcut. Görsel yüklenemedi (HTTP {(int)response.StatusCode}); Etsy'den kontrol edin.");
            using var document = await ReadJson(response, timeout.Token);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("listing_image_id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var result) || result <= 0) throw new JsonException();
            return result;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException)
        { throw new InvalidOperationException($"Etsy taslağı {listingId} mevcut; görsel yükleme sonucu belirsiz. Etsy'den kontrol edin. Otomatik tekrar yapılmadı."); }
    }
    public static readonly string[] WhoMadeValues = ["i_did", "someone_else", "collective"];
    public static readonly string[] WhenMadeValues = ["made_to_order", "2020_2026", "2010_2019", "2007_2009", "before_2007", "2000_2006", "1990s", "1980s", "1970s", "1960s", "1950s", "1940s", "1930s", "1920s", "1910s", "1900s", "1800s", "1700s", "before_1700"];
    static string Title(CatalogProduct p, EtsyListingTemplate t) => ((t.TitlePrefix ?? "").Trim() + " " + (p.Name ?? "").Trim()).Trim();
    static string[] Parts(string? value) => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static List<string> Validate(CatalogProduct p, EtsyListingTemplate t)
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.EtsyListingId)) errors.Add("Ürün zaten Etsy ilanına bağlı; yeniden oluşturulamaz.");
        if (string.IsNullOrWhiteSpace(p.Name) || Title(p, t).Length > 140) errors.Add("Ürün başlığı gerekli ve önek dahil en fazla 140 karakter olmalı.");
        if (string.IsNullOrWhiteSpace(p.Description)) errors.Add("Açıklama gerekli.");
        if (p.Price <= 0) errors.Add("Fiyat sıfırdan büyük olmalı.");
        if (!p.Active) errors.Add("Pasif ürün gönderilemez.");
        if (p.Stock <= 0) errors.Add("Stok sıfırdan büyük olmalı.");
        if (string.IsNullOrWhiteSpace(t.Currency) || !string.Equals(p.Currency, t.Currency, StringComparison.OrdinalIgnoreCase)) errors.Add("Ürün ve şablon para birimi aynı olmalı.");
        if (!WhoMadeValues.Contains(t.WhoMade)) errors.Add("Üreticiyi seçin.");
        if (!WhenMadeValues.Contains(t.WhenMade)) errors.Add("Geçerli üretim dönemini seçin.");
        if (t.TaxonomyId <= 0) errors.Add("Etsy kategori ID gerekli.");
        if (t.ShippingProfileId <= 0) errors.Add("Fiziksel ürün için kargo profili ID gerekli.");
        if (t.ReadinessStateId <= 0) errors.Add("Fiziksel ürün için hazırlık profili ID gerekli.");
        if (Parts(t.Tags).Length > 13 || Parts(t.Tags).Any(x => x.Length is < 1 or > 20)) errors.Add("En fazla 13 etiket ve etiket başına 20 karakter kullanılabilir.");
        if (Parts(t.Materials).Any(x => x.Any(c => !(char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))))) errors.Add("Malzeme alanı yalnızca harf, sayı ve boşluk içerebilir.");
        return errors;
    }
    public async Task<long> CreateAsync(EtsyCredentials credentials, CatalogProduct product, EtsyListingTemplate template, CancellationToken cancellationToken = default, Func<Task>? beforePost = null)
    {
        var errors = Validate(product, template);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        if (!long.TryParse(credentials.ShopId, NumberStyles.None, CultureInfo.InvariantCulture, out var shop) || shop <= 0 || string.IsNullOrWhiteSpace(credentials.Key) || string.IsNullOrWhiteSpace(credentials.Secret) || string.IsNullOrWhiteSpace(credentials.Token))
            throw new InvalidOperationException("Etsy bağlantı bilgileri eksik veya mağaza ID geçersiz.");
        HttpRequestMessage Request(HttpMethod method, string suffix)
        {
            var request = new HttpRequestMessage(method, $"https://openapi.etsy.com/v3/application/shops/{shop}{suffix}");
            request.Headers.Add("x-api-key", credentials.Key + ":" + credentials.Secret);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
            return request;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        try
        {
            using var shopRequest = Request(HttpMethod.Get, "");
            using var response = await http.SendAsync(shopRequest, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Etsy mağaza doğrulaması başarısız (HTTP {(int)response.StatusCode}).");
            using var document = await ReadJson(response, token);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("currency_code", out var currency) || currency.ValueKind != JsonValueKind.String || !string.Equals(currency.GetString(), template.Currency, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Etsy mağaza para birimi ürün/şablon ile eşleşmiyor; mağazayı kontrol edin.");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { throw new InvalidOperationException("Etsy mağaza doğrulaması tamamlanamadı; bağlantıyı ve mağazayı kontrol edin. Taslak isteği gönderilmedi."); }
        using var request = Request(HttpMethod.Post, "/listings?legacy=false");
        var fields = new Dictionary<string, string> {
            ["quantity"] = product.Stock.ToString(CultureInfo.InvariantCulture), ["title"] = Title(product, template), ["description"] = product.Description,
            ["price"] = product.Price.ToString("0.00##########################", CultureInfo.InvariantCulture), ["who_made"] = template.WhoMade, ["when_made"] = template.WhenMade,
            ["is_supply"] = template.IsSupply ? "true" : "false", ["taxonomy_id"] = template.TaxonomyId.ToString(CultureInfo.InvariantCulture),
            ["shipping_profile_id"] = template.ShippingProfileId.ToString(CultureInfo.InvariantCulture), ["readiness_state_id"] = template.ReadinessStateId.ToString(CultureInfo.InvariantCulture),
            ["type"] = "physical", ["should_auto_renew"] = "false"
        };
        if (Parts(template.Tags).Length > 0) fields["tags"] = string.Join(',', Parts(template.Tags));
        if (Parts(template.Materials).Length > 0) fields["materials"] = string.Join(',', Parts(template.Materials));
        // SKU belongs to updateListingInventory, not createDraftListing; no second write is hidden here.
        request.Content = new FormUrlEncodedContent(fields);
        token.ThrowIfCancellationRequested();
        if (beforePost is not null) await beforePost();
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Etsy taslak isteği HTTP {(int)response.StatusCode} döndürdü. Yeniden denemeden önce mağazanızı kontrol edin.");
            using var document = await ReadJson(response, token);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("listing_id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var result) || result <= 0) throw new JsonException();
            return result;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { throw new InvalidOperationException("Taslak oluşturma sonucu belirsiz. Yeniden denemeden önce Etsy mağazanızı kontrol edip ilan ID ile eşleştirin. Otomatik tekrar yapılmadı."); }
    }
    static async Task<JsonDocument> ReadJson(HttpResponseMessage response, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0) { if (memory.Length + count > 1024 * 1024) throw new IOException("Etsy yanıtı çok büyük."); memory.Write(buffer, 0, count); }
        try { return JsonDocument.Parse(memory.ToArray()); }
        catch (JsonException) { throw new JsonException("Etsy yanıtı okunamadı."); }
    }
}

