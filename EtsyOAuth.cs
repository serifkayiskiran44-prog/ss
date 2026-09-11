using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed class OAuthAttempt
{
    public string AuthorizeUrl { get; }
    internal string Key { get; }
    internal string RedirectUri { get; }
    internal string State { get; }
    internal string Verifier { get; }
    internal DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    private int consumed;
    internal OAuthAttempt(string url, string key, string redirect, string state, string verifier)
        => (AuthorizeUrl, Key, RedirectUri, State, Verifier) = (url, key, redirect, state, verifier);
    internal void Consume()
    {
        if (DateTimeOffset.UtcNow - CreatedAt > TimeSpan.FromMinutes(10) || Interlocked.Exchange(ref consumed, 1) != 0)
            throw new InvalidOperationException("Yetkilendirme isteği sona erdi veya kullanıldı. Yeniden başlatın.");
    }
}

public sealed class EtsyOAuth(HttpClient client)
{
    public OAuthAttempt Begin(string key, string redirectUri)
    {
        EtsyHttp.ValidateValue(key);
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) || !redirectUri.StartsWith("https://", StringComparison.Ordinal) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || redirectUri.Contains('?') || redirectUri.Contains('#') || redirectUri.Any(char.IsWhiteSpace))
            throw new ArgumentException("Etsy uygulamasında kayıtlı, sorgu ve parça içermeyen tam HTTPS dönüş adresini girin.");
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string,string> {
            ["response_type"]="code", ["client_id"]=key, ["redirect_uri"]=redirectUri,
            ["scope"]="shops_r listings_r listings_w transactions_r", ["state"]=state,
            ["code_challenge"]=Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"]="S256"
        };
        return new("https://www.etsy.com/oauth/connect?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value))), key, redirectUri, state, verifier);
    }

    public Task<EtsyCredentials> ExchangeAsync(EtsyCredentials credentials, OAuthAttempt attempt, string callbackUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        attempt.Consume();
        if (credentials.Key != attempt.Key || callbackUrl.Length > 16384 ||
            !Uri.TryCreate(callbackUrl, UriKind.Absolute, out var callback) || callback.Fragment.Length != 0 ||
            callbackUrl.Split('?')[0] != attempt.RedirectUri || !callbackUrl.Contains('?'))
            throw new InvalidOperationException("Dönüş adresi bu yetkilendirme isteğiyle eşleşmiyor.");
        var values = new Dictionary<string,string>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in callback.Query.TrimStart('?').Split('&'))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2 || !values.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1].Replace("+", " "))))
                    throw new InvalidOperationException("Dönüş adresi geçersiz parametreler içeriyor.");
            }
        }
        catch (UriFormatException) { throw new InvalidOperationException("Dönüş adresi geçersiz."); }
        if (!values.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(attempt.State)))
            throw new InvalidOperationException("Yetkilendirme güvenlik kodu eşleşmiyor. Yeniden başlatın.");
        if (values.ContainsKey("error")) throw new InvalidOperationException("Etsy yetkilendirmesi onaylanmadı. Yeniden başlatın.");
        if (!values.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Dönüş adresinde yetkilendirme kodu bulunamadı.");
        return TokenAsync(credentials with { RedirectUri = attempt.RedirectUri }, new Dictionary<string,string> {
            ["grant_type"]="authorization_code", ["client_id"]=credentials.Key, ["redirect_uri"]=attempt.RedirectUri, ["code"]=code, ["code_verifier"]=attempt.Verifier
        }, cancellationToken);
    }

    public Task<EtsyCredentials> RefreshAsync(EtsyCredentials credentials, CancellationToken cancellationToken = default)
    {
        EtsyHttp.ValidateValue(credentials.RefreshToken);
        return TokenAsync(credentials, new Dictionary<string,string> { ["grant_type"]="refresh_token", ["client_id"]=credentials.Key, ["refresh_token"]=credentials.RefreshToken }, cancellationToken);
    }

    private async Task<EtsyCredentials> TokenAsync(EtsyCredentials credentials, Dictionary<string,string> form, CancellationToken cancellationToken)
    {
        // Etsy's current OAuth 2.0 token endpoint is served from api.etsy.com.
        // Keep the API key header and PKCE exchange unchanged; no fallback endpoint is guessed.
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.etsy.com/v3/public/oauth/token") { Content = new FormUrlEncodedContent(form) };
        EtsyHttp.AddHeaders(request, credentials, false);
        using var document = await EtsyHttp.SendJsonAsync(client, request, 64 * 1024, cancellationToken).ConfigureAwait(false);
        try
        {
            var root = document.RootElement;
            var access = root.GetProperty("access_token").GetString()!;
            var refresh = root.GetProperty("refresh_token").GetString()!;
            var expires = root.GetProperty("expires_in").GetInt32();
            if (!string.Equals(root.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) || expires <= 0 || expires > 604800)
                throw new InvalidOperationException();
            EtsyHttp.ValidateValue(access); EtsyHttp.ValidateValue(refresh);
            return credentials with { Token = access, RefreshToken = refresh, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires) };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { throw new InvalidOperationException("Etsy geçerli bir erişim bilgisi yanıtı döndürmedi."); }
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
}

internal static class EtsyHttp
{
    internal static void ValidateValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(c => c <= 32 || c >= 127 || c == ':'))
            throw new ArgumentException("Etsy erişim bilgileri eksik veya geçersiz.");
    }
    internal static void AddHeaders(HttpRequestMessage request, EtsyCredentials credentials, bool bearer)
    {
        ValidateValue(credentials.Key); ValidateValue(credentials.Secret);
        request.Headers.Add("x-api-key", credentials.Key+":"+credentials.Secret);
        if (bearer) { ValidateValue(credentials.Token); request.Headers.Authorization = new("Bearer", credentials.Token); }
    }
    internal static async Task<JsonDocument> SendJsonAsync(HttpClient client, HttpRequestMessage request, int maxBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Etsy isteği tamamlanamadı. HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(bytes.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > maxBytes) throw new InvalidOperationException("Etsy yanıtı boyut sınırını aşıyor.");
                buffer.Write(bytes, 0, count);
            }
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("Etsy bağlantısı zaman aşımına uğradı."); }
        catch (HttpRequestException) { throw new InvalidOperationException("Etsy bağlantısı kurulamadı."); }
        catch (IOException) { throw new InvalidOperationException("Etsy yanıtı okunamadı."); }
        catch (JsonException) { throw new InvalidOperationException("Etsy yanıtı geçerli JSON değil."); }
    }
}
