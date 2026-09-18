using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;
public record EtsyCredentials(string Key, string Secret, string Token, string ShopId, string RefreshToken = "", DateTimeOffset? ExpiresAt = null, string RedirectUri = "", IReadOnlyList<string>? GrantedScopes = null)
{
    public bool IsAccessTokenUsable(DateTimeOffset? now = null) => !string.IsNullOrWhiteSpace(Token) && (!ExpiresAt.HasValue || ExpiresAt.Value > (now ?? DateTimeOffset.UtcNow).AddMinutes(1));
}
public class EtsyConnector(System.Net.Http.HttpClient client)
{
    public async Task<string> TestAsync(EtsyCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.ShopId) || credentials.ShopId.Any(c => c < '0' || c > '9') ||
            !long.TryParse(credentials.ShopId, NumberStyles.None, CultureInfo.InvariantCulture, out var shopId) || shopId <= 0)
            throw new ArgumentException("Mağaza kimliği pozitif bir sayı olmalıdır.");
        if (!ValidHeader(credentials.Key) || !ValidHeader(credentials.Secret) ||
            (!string.IsNullOrEmpty(credentials.Token) && !ValidHeader(credentials.Token)))
            throw new ArgumentException("API anahtarı ve paylaşılan sır zorunludur; bilgiler boşluk veya kontrol karakteri içeremez.");
        return (await new EtsyMetadataClient(client).GetShopAsync(credentials).ConfigureAwait(false)).Name;
    }

    private static bool ValidHeader(string value) => !string.IsNullOrWhiteSpace(value) && value.All(c => c > 32 && c < 127 && c != ':');
}
