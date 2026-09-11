using System.Security.Cryptography;
using System.Text;
namespace TrMarketplaceHubDesktop;
public sealed record NavlungoSettings(string ClientId, string CallbackUri, bool Sandbox);
public sealed class NavlungoAuthorizationPreparation
{
    public string AuthorizeUrl { get; }
    internal string Verifier { get; }
    internal string State { get; }
    internal NavlungoAuthorizationPreparation(string url, string verifier, string state)
        => (AuthorizeUrl, Verifier, State) = (url, verifier, state);
}
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
}
