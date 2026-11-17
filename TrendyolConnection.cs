using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record TrendyolSettings(string SupplierId, string ApiKey, string ApiSecret, string UserAgent);

/// <summary>The saved Trendyol credentials are a single legacy account; a live write for an order of another shop must be refused before any request.</summary>
public static class TrendyolOrderWriteGuard
{
    /// <summary>Credentials for the order's own shop: the per-account vault first (multi-store), the legacy single-account file only if it belongs to that same shop.</summary>
    public static TrendyolSettings Resolve(string? directory, string orderShopId)
    {
        if (string.IsNullOrWhiteSpace(orderShopId)) throw new InvalidOperationException("Siparişin Trendyol mağaza kimliği yok; işlem yapılmadı.");
        var shop = orderShopId.Trim();
        var connection = new MarketplaceConnectionStore(directory).List(false).FirstOrDefault(x => x.Enabled && x.Channel == "trendyol" && string.Equals(x.ShopId, shop, StringComparison.Ordinal));
        if (connection is not null && new MarketplaceCredentialVault(directory).Load<TrendyolSettings>(connection.Id, connection.Channel, connection.ShopId) is { } scoped) return Require(scoped, shop);
        var legacy = new TrendyolSettingsStore(directory is null ? null : System.IO.Path.Combine(directory, "trendyol.bin")).Load() ?? throw new InvalidOperationException("Bu mağaza için Trendyol bağlantı ayarları kayıtlı değil; işlem yapılmadı.");
        return Require(legacy, shop);
    }

    public static TrendyolSettings Require(TrendyolSettings settings, string orderShopId)
    {
        if (string.IsNullOrWhiteSpace(orderShopId) || !string.Equals(settings.SupplierId?.Trim(), orderShopId.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Kayıtlı Trendyol bağlantısı bu siparişin mağazasına (" + orderShopId + ") ait değil; işlem yapılmadı. Doğru mağaza bağlantısını kaydedin.");
        return settings;
    }
}

public sealed class TrendyolSettingsStore(string? path = null)
{
    const int MaxEncryptedFileBytes = 64 * 1024;
    const int MaxPlaintextBytes = 32 * 1024;
    readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "trendyol.bin");
    public void Save(TrendyolSettings settings)
    {
        TrendyolConnection.Validate(settings); var plain = JsonSerializer.SerializeToUtf8Bytes(settings); var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { var encrypted = CredentialStore.Protect(plain); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!); File.WriteAllBytes(temporary, encrypted); File.Move(temporary, storePath, true); }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public TrendyolSettings? Load()
    {
        if (!File.Exists(storePath)) return null;
        const string recoveryMessage = "Kayıtlı Trendyol bağlantı bilgileri bu Windows kullanıcısı tarafından okunamadı. Bilgileri yeniden girip kaydedin.";
        byte[]? plain = null;
        try
        {
            plain = CredentialStore.Unprotect(BoundedCredentialFile.ReadBounded(storePath, MaxEncryptedFileBytes));
            if (plain.Length is <= 0 or > MaxPlaintextBytes) throw new InvalidOperationException(recoveryMessage);
            var settings = JsonSerializer.Deserialize<TrendyolSettings>(plain) ?? throw new InvalidOperationException(recoveryMessage);
            TrendyolConnection.Validate(settings);
            return settings;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException)
        { throw new InvalidOperationException(recoveryMessage); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}

/// <summary>Trendyol Türkiye V2 connection; explicit read-only checks only.</summary>
public sealed class TrendyolConnection
{
    public static void Validate(TrendyolSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SupplierId) || !settings.SupplierId.All(char.IsAsciiDigit) || !long.TryParse(settings.SupplierId,out var sellerId) || sellerId<=0 || settings.SupplierId!=sellerId.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new ArgumentException("Trendyol satıcı ID pozitif sayı olmalı; başında sıfır olmamalı.");
        foreach (var value in new[] { settings.ApiKey, settings.ApiSecret, settings.UserAgent }) if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Length > 512) throw new ArgumentException("Trendyol API kimlik bilgileri geçersiz.");
    }
    public static string Describe(TrendyolSettings? settings) => settings is null ? "NOT_CONFIGURED" : "Ayarlar şifreli kayıtlı; Trendyol Türkiye Ürün API V2. Mağaza erişimini test edin.";
    public async Task TestReadOnlyAsync(TrendyolSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        using var client=new Trendyol.TrendyolApiClient(settings);
        await client.GetAddressesAsync(cancellationToken);
    }
}
