using System.Security.Cryptography;
using System.Text;
namespace TrMarketplaceHubDesktop;
public sealed record NavlungoSettings(string ClientId, string CallbackUri, bool Sandbox);
public sealed class NavlungoAuthorizationPreparation
{
    public string AuthorizeUrl { get; }
    internal string Verifier { get; }
    internal string State { get; }
    internal bool Consumed { get; private set; }
    internal NavlungoAuthorizationPreparation(string url, string verifier, string state)
        => (AuthorizeUrl, Verifier, State) = (url, verifier, state);
    internal void MarkConsumed()
    {
        if (Consumed) throw new InvalidOperationException("Navlungo yetkilendirme isteği zaten kullanıldı; callback replay reddedildi.");
        Consumed = true;
    }
}

// Navlungo's authorize response echoes back every querystring parameter the client originally sent
// (github.com/Navlungo/public-api-docs, README.md §2.1, fetched 2026-09-12), so client_id and state
// added to the authorize request are returned as-is and can be checked here.
public sealed record NavlungoTokenRequest(string ClientId, string Code, string CodeVerifier, string Scope);
public static class NavlungoConnection
{
    public static bool IsConnected => false;
    public static string Describe(NavlungoSettings? settings) => settings is null
        ? "Bağlı değil • Navlungo Express API başvurusu gerekli."
        : $"{(settings.Sandbox ? "QA" : "Canlı")} ayarları kayıtlı • Hesap henüz yetkilendirilmedi.";
    public static void Validate(NavlungoSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId) || settings.ClientId.Length > 256 || settings.ClientId.Any(char.IsWhiteSpace))
            throw new ArgumentException("Navlungo tarafından verilen client_id bilgisini girin.");
        if (!Uri.TryCreate(settings.CallbackUri, UriKind.Absolute, out var callback) || callback.Scheme != "https" ||
            callback.UserInfo.Length != 0 || callback.Query.Length != 0 || callback.Fragment.Length != 0 || settings.CallbackUri.Any(char.IsWhiteSpace))
            throw new ArgumentException("Navlungo başvurusunda kayıtlı tam HTTPS dönüş adresini girin; sorgu ve parça eklemeyin.");
    }
    public static NavlungoAuthorizationPreparation Begin(NavlungoSettings settings)
    {
        Validate(settings);
        var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // Navlungo explicitly documents standard Base64 SHA256, not Base64Url.
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        var host = settings.Sandbox ? "https://qa.navlungo.com" : "https://navlungo.com";
        return new($"{host}/authorize?client_id={Uri.EscapeDataString(settings.ClientId)}&code_challenge={Uri.EscapeDataString(challenge)}&state={state}", verifier, state);
    }

    // Validates a callback against the pending authorization it belongs to and prepares the Token API
    // request. Rejects: state mismatch (CSRF), a callback echoing a different client_id than the one
    // that started this flow (wrong account), a missing/blank code, and replay of an already-consumed
    // pending authorization. Scope matches token.md's documented minimum for authorization_code: "openid offline_access".
    public static NavlungoTokenRequest HandleCallback(NavlungoSettings settings, NavlungoAuthorizationPreparation pending, IReadOnlyDictionary<string, string> callbackQuery)
    {
        Validate(settings);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(callbackQuery);
        if (pending.Consumed) throw new InvalidOperationException("Navlungo yetkilendirme isteği zaten kullanıldı; callback replay reddedildi.");
        if (!callbackQuery.TryGetValue("state", out var state) || state != pending.State)
            throw new InvalidOperationException("Navlungo callback state değeri eşleşmiyor; CSRF şüphesiyle reddedildi.");
        if (!callbackQuery.TryGetValue("client_id", out var clientId) || clientId != settings.ClientId)
            throw new InvalidOperationException("Navlungo callback farklı bir hesaba ait; client_id eşleşmiyor.");
        if (!callbackQuery.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Navlungo callback authorization code içermiyor.");
        pending.MarkConsumed();
        return new(settings.ClientId, code, pending.Verifier, "openid offline_access");
    }
}
