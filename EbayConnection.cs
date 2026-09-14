using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record EbaySettings(string ClientId, string ClientSecret, string RuName, string CallbackUrl, bool Sandbox);
public sealed record EbayTokens(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt);
public sealed record EbaySavedConnection(EbaySettings Settings, EbayTokens? Tokens);

public sealed class EbayAuthorization
{
    internal EbayAuthorization(EbaySettings settings, DateTimeOffset createdAt)
    { Settings = settings; CreatedAt = createdAt; State = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); }
    internal EbaySettings Settings { get; }
    internal DateTimeOffset CreatedAt { get; }
    private int consumed;
    internal bool Consume() => Interlocked.Exchange(ref consumed, 1) == 0;
    public string State { get; }
    public string AuthorizeUrl => $"https://auth{(Settings.Sandbox ? ".sandbox" : "")}.ebay.com/oauth2/authorize?client_id={Uri.EscapeDataString(Settings.ClientId)}&redirect_uri={Uri.EscapeDataString(Settings.RuName)}&response_type=code&scope={Uri.EscapeDataString(EbayConnection.Scope)}&state={State}";
}

/// <summary>OAuth token operations and read-only seller privileges. No listing writes.</summary>
public sealed class EbayConnection(HttpClient http)
{
 public const string Scope = "https://api.ebay.com/oauth/api_scope/sell.account.readonly https://api.ebay.com/oauth/api_scope/sell.inventory https://api.ebay.com/oauth/api_scope/sell.fulfillment.readonly";
    // Purely technical bounds (never a provider-format contract): eBay's own
    // App ID/Cert ID/RuName are far shorter than these in practice, but the limits
    // exist only to stop an oversized value from reaching the OAuth authorize URL,
    // the Basic-auth header, or DPAPI/JSON persistence with no upper bound at all -
    // not to encode any assumption about eBay's real field-length contract.
    const int MaxClientIdLength = 256;
    const int MaxClientSecretLength = 512;
    const int MaxRuNameLength = 256;
    const int MaxCallbackUrlLength = 2048;
    public static void Validate(EbaySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret) || string.IsNullOrWhiteSpace(settings.RuName)
            || settings.ClientId.Length > MaxClientIdLength || settings.ClientSecret.Length > MaxClientSecretLength || settings.RuName.Length > MaxRuNameLength
            || settings.ClientId.Contains(':') || settings.ClientId.Any(char.IsControl) || settings.ClientSecret.Any(char.IsControl) || settings.RuName.Any(char.IsControl)
            || string.IsNullOrEmpty(settings.CallbackUrl) || settings.CallbackUrl.Length > MaxCallbackUrlLength
            || !Uri.TryCreate(settings.CallbackUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("App ID, Cert ID, RuName ve sorgusuz HTTPS kabul adresi gerekli.");
    }
    public static EbayAuthorization Begin(EbaySettings settings, DateTimeOffset? now = null)
    { Validate(settings); return new(settings, now ?? DateTimeOffset.UtcNow); }

    public async Task<EbayTokens> CompleteAsync(EbayAuthorization attempt, string callback, CancellationToken cancellationToken = default)
    {
        var expected = new Uri(attempt.Settings.CallbackUrl);
        if (DateTimeOffset.UtcNow - attempt.CreatedAt > TimeSpan.FromMinutes(10)
            || !Uri.TryCreate(callback, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || !string.Equals(uri.GetLeftPart(UriPartial.Path), expected.GetLeftPart(UriPartial.Path), StringComparison.Ordinal))
            throw new InvalidOperationException("Dönüş adresi geçersiz veya yetkilendirme süresi doldu. Yeniden başlatın.");
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            if (!query.TryAdd(key, value)) throw new InvalidOperationException("Dönüş parametreleri yinelenemez.");
        }
        if (!query.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(attempt.State)))
            throw new InvalidOperationException("Yetkilendirme state doğrulaması başarısız.");
        if (query.ContainsKey("error")) { attempt.Consume(); throw new InvalidOperationException("eBay onayı reddedildi. Yetkilendirmeyi yeniden başlatın."); }
        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code) || code.Length > 1024)
            throw new InvalidOperationException("Dönüş adresinde geçerli yetkilendirme kodu yok.");
        if (!attempt.Consume()) throw new InvalidOperationException("Bu yetkilendirme zaten kullanıldı. Yeniden başlatın.");
        return await TokenAsync(attempt.Settings, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = attempt.Settings.RuName }, null, cancellationToken);
    }

    public Task<EbayTokens> RefreshAsync(EbaySettings settings, EbayTokens tokens, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken) || tokens.RefreshExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("eBay onayı yenilenmeli. Yetkilendirmeyi yeniden başlatın.");
        return TokenAsync(settings, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken, ["scope"] = Scope }, tokens, cancellationToken);
    }
    private static string Api(EbaySettings settings) => $"https://api{(settings.Sandbox ? ".sandbox" : "")}.ebay.com";
    private async Task<EbayTokens> TokenAsync(EbaySettings settings, Dictionary<string, string> form, EbayTokens? previous, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api(settings) + "/identity/v1/oauth2/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.ClientId + ":" + settings.ClientSecret)));
        request.Content = new FormUrlEncodedContent(form);
        using var json = await SendAsync(request, cancellationToken);
        var root = json.RootElement;
        if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(access.GetString())
            || !root.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt32(out var seconds) || seconds <= 0)
            throw new InvalidOperationException("eBay geçerli erişim anahtarı döndürmedi.");
        var refresh = previous?.RefreshToken ?? "";
        var refreshExpires = previous?.RefreshExpiresAt ?? DateTimeOffset.MinValue;
        if (root.TryGetProperty("refresh_token", out var refreshValue) && refreshValue.ValueKind == JsonValueKind.String)
        {
            refresh = refreshValue.GetString() ?? "";
            if (!root.TryGetProperty("refresh_token_expires_in", out var refreshExpiry) || !refreshExpiry.TryGetInt32(out var refreshSeconds) || refreshSeconds <= 0)
                throw new InvalidOperationException("eBay yenileme anahtarının süresini döndürmedi.");
            refreshExpires = DateTimeOffset.UtcNow.AddSeconds(refreshSeconds);
        }
        if (string.IsNullOrWhiteSpace(refresh)) throw new InvalidOperationException("eBay yenileme anahtarı döndürmedi. Yeniden onay alın.");
        return new(access.GetString()!, DateTimeOffset.UtcNow.AddSeconds(seconds), refresh, refreshExpires);
    }
    public async Task<bool> VerifyAsync(EbaySettings settings, EbayTokens tokens, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        if (tokens.ExpiresAt <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(tokens.AccessToken)) throw new InvalidOperationException("Erişim anahtarını önce yenileyin.");
        using var request = new HttpRequestMessage(HttpMethod.Get, Api(settings) + "/sell/account/v1/privilege");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var json = await SendAsync(request, cancellationToken);
        if (!json.RootElement.TryGetProperty("sellerRegistrationCompleted", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("eBay yanıtında satıcı kayıt durumu yok; bağlantı doğrulanamadı.");
        return flag.GetBoolean();
    }
    public async Task<JsonDocument> GetInventoryItemAsync(EbaySettings settings,EbayTokens tokens,string sku,CancellationToken cancellationToken=default)
    {if(string.IsNullOrWhiteSpace(sku))throw new ArgumentException("eBay SKU zorunlu.");return await ApiJsonAsync(settings,tokens,HttpMethod.Get,$"/sell/inventory/v1/inventory_item/{Uri.EscapeDataString(sku)}",null,cancellationToken);}
    public async Task<JsonDocument> GetOrdersAsync(EbaySettings settings,EbayTokens tokens,string? filter=null,CancellationToken cancellationToken=default)
    {var suffix=string.IsNullOrWhiteSpace(filter)?"":"?filter="+Uri.EscapeDataString(filter);return await ApiJsonAsync(settings,tokens,HttpMethod.Get,"/sell/fulfillment/v1/order"+suffix,null,cancellationToken);}
    public async Task UpdateInventoryQuantityAsync(EbaySettings settings,EbayTokens tokens,string sku,int quantity,CancellationToken cancellationToken=default)
    {if(quantity<0)throw new ArgumentException("eBay stok negatif olamaz.");using var content=new StringContent(JsonSerializer.Serialize(new{availability=new{shipToLocationAvailability=new{quantity}}}),Encoding.UTF8,"application/json");using var json=await ApiJsonAsync(settings,tokens,HttpMethod.Put,$"/sell/inventory/v1/inventory_item/{Uri.EscapeDataString(sku)}",content,cancellationToken);}
    async Task<JsonDocument> ApiJsonAsync(EbaySettings settings,EbayTokens tokens,HttpMethod method,string path,HttpContent? content,CancellationToken cancellationToken){Validate(settings);if(tokens.ExpiresAt<=DateTimeOffset.UtcNow||string.IsNullOrWhiteSpace(tokens.AccessToken))throw new InvalidOperationException("eBay erişim anahtarı geçersiz veya süresi dolmuş.");using var request=new HttpRequestMessage(method,Api(settings)+path){Content=content};request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",tokens.AccessToken);return await SendAsync(request,cancellationToken);}
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"eBay isteği başarısız (HTTP {(int)response.StatusCode}). Anahtar, ortam ve OAuth onayını kontrol edin.");
        try { return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); }
        catch (JsonException) { throw new InvalidOperationException("eBay yanıtı okunamadı; bağlantı doğrulanamadı."); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new InvalidOperationException("eBay isteği zaman aşımına uğradı veya iptal edildi; önceki kayıtlar korundu."); }
        catch (HttpRequestException)
        { throw new InvalidOperationException("eBay ağına erişilemedi; önceki kayıtlar korundu."); }
    }
}
