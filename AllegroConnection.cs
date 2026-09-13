using System.Globalization;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record AllegroSettings(string ClientId, string ClientSecret, string RedirectUri, string AccessToken, string RefreshToken, bool Sandbox);
public sealed record AllegroOfferSummary(string Id, string Name, string Status);
public sealed record AllegroOrderSummary(string Id, string Status, DateTimeOffset? UpdatedAt);

public sealed class AllegroSettingsStore(string? path = null)
{
    readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "allegro.bin");
    public void Save(AllegroSettings settings)
    {
        AllegroConnection.Validate(settings); var plain = JsonSerializer.SerializeToUtf8Bytes(settings); var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { var encrypted = CredentialStore.Protect(plain); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!); File.WriteAllBytes(temporary, encrypted); File.Move(temporary, storePath, true); }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public AllegroSettings? Load()
    {
        if (!File.Exists(storePath)) return null; var plain = CredentialStore.Unprotect(File.ReadAllBytes(storePath));
        try { var settings = JsonSerializer.Deserialize<AllegroSettings>(plain) ?? throw new InvalidDataException("Allegro ayarları okunamadı."); AllegroConnection.Validate(settings); return settings; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}

/// <summary>Official Allegro public read resources. Write/fulfillment operations are intentionally absent.</summary>
public sealed class AllegroConnection(HttpClient http)
{
    const string ApiBase = "https://api.allegro.pl";
    const string MediaType = "application/vnd.allegro.public.v1+json";
    public static void Validate(AllegroSettings settings)
    {
        foreach (var value in new[] { settings.ClientId, settings.ClientSecret, settings.AccessToken }) if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Length > 4096) throw new ArgumentException("Allegro OAuth bilgileri geçersiz.");
        if (string.IsNullOrWhiteSpace(settings.RefreshToken) || settings.RefreshToken.Any(char.IsControl)) throw new ArgumentException("Allegro refresh token gerekli.");
        if (!Uri.TryCreate(settings.RedirectUri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new ArgumentException("Allegro dönüş adresi sorgusuz HTTPS olmalı.");
        if (settings.Sandbox) throw new ArgumentException("Allegro sandbox sözleşmesi bu connector'da yapılandırılmadı; canlı olmayan endpoint uydurulmadı.");
    }
    public async Task<IReadOnlyList<AllegroOfferSummary>> GetOffersAsync(AllegroSettings settings, int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000 || offset < 0) throw new ArgumentOutOfRangeException(nameof(limit), "Allegro sayfalama sınırı geçersiz.");
        using var json = await GetAsync(settings, $"/sale/offers?limit={limit.ToString(CultureInfo.InvariantCulture)}&offset={offset.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
        if (!json.RootElement.TryGetProperty("offers", out var offers) || offers.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Allegro offers yanıtı beklenen biçimde değil.");
        return offers.EnumerateArray().Select(item => new AllegroOfferSummary(
            RequiredText(item, "id"), OptionalText(item, "name"), item.TryGetProperty("publication", out var publication) && publication.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString() ?? "" : "")).ToArray();
    }
    public async Task<IReadOnlyList<AllegroOrderSummary>> GetOrdersAsync(AllegroSettings settings, int limit = 100, int offset = 0, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100 || offset < 0) throw new ArgumentOutOfRangeException(nameof(limit), "Allegro sipariş sayfalama sınırı geçersiz.");
        using var json = await GetAsync(settings, $"/order/checkout-forms?limit={limit.ToString(CultureInfo.InvariantCulture)}&offset={offset.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
        if (!json.RootElement.TryGetProperty("checkoutForms", out var orders) || orders.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Allegro checkoutForms yanıtı beklenen biçimde değil.");
        return orders.EnumerateArray().Select(item => new AllegroOrderSummary(RequiredText(item, "id"), OptionalText(item, "status"), item.TryGetProperty("updatedAt", out var updated) && updated.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(updated.GetString(), out var date) ? date : null)).ToArray();
    }
    public async Task<string> TestReadOnlyAsync(AllegroSettings settings, CancellationToken cancellationToken = default)
    {
        var offers = await GetOffersAsync(settings, 1, 0, cancellationToken); return $"Allegro read-only erişimi doğrulandı ({offers.Count} teklif örneği).";
    }
    async Task<JsonDocument> GetAsync(AllegroSettings settings, string path, CancellationToken cancellationToken)
    {
        Validate(settings); using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaType));
        try { using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Allegro HTTP {(int)response.StatusCode}: erişim veya OAuth kapsamını kontrol edin."); return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("Allegro isteği zaman aşımına uğradı."); }
        catch (JsonException) { throw new InvalidOperationException("Allegro JSON yanıtı okunamadı."); }
    }
    static string RequiredText(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidOperationException($"Allegro yanıtında {property} eksik.");
    static string OptionalText(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
