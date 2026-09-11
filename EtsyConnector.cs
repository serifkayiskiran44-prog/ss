using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;
public record EtsyCredentials(string Key, string Secret, string Token, string ShopId, string RefreshToken = "", DateTimeOffset? ExpiresAt = null, string RedirectUri = "");
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
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://openapi.etsy.com/v3/application/shops/{shopId}");
        request.Headers.Add("x-api-key", credentials.Key + ":" + credentials.Secret);
        if (!string.IsNullOrEmpty(credentials.Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = (int)response.StatusCode switch
                {
                    401 => "Kimlik doğrulanamadı. API bilgilerini kontrol edin.",
                    403 => "Erişim reddedildi. Uygulama ve mağaza izinlerini kontrol edin.",
                    429 => "İstek sınırına ulaşıldı. Daha sonra tekrar deneyin.",
                    _ => "Etsy isteği tamamlanamadı."
                };
                throw new InvalidOperationException($"{reason} HTTP {(int)response.StatusCode}.");
            }
            await response.Content.LoadIntoBufferAsync(1024 * 1024).WaitAsync(timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("shop_name", out var name) || name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()))
                throw new InvalidOperationException("Etsy geçerli bir mağaza adı döndürmedi.");
            return name.GetString()!;
        }
        catch (JsonException) { throw new InvalidOperationException("Etsy yanıtı geçerli JSON değil."); }
        catch (OperationCanceledException) { throw new InvalidOperationException("Etsy bağlantısı zaman aşımına uğradı."); }
        catch (HttpRequestException) { throw new InvalidOperationException("Etsy bağlantısı kurulamadı. Ağ bağlantısını kontrol edin."); }
    }

    private static bool ValidHeader(string value) => !string.IsNullOrWhiteSpace(value) && value.All(c => c > 32 && c < 127 && c != ':');
}
