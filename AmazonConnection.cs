using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record AmazonSettings(string SellerId, string ClientId, string ClientSecret, string RefreshToken, string Region, string MarketplaceId, bool Sandbox);

public sealed class AmazonSettingsStore(string? path = null)
{
    readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "amazon.bin");
    public void Save(AmazonSettings settings)
    {
        AmazonConnection.Validate(settings);
        var plain = JsonSerializer.SerializeToUtf8Bytes(settings); var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { var encrypted = CredentialStore.Protect(plain); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!); File.WriteAllBytes(temporary, encrypted); File.Move(temporary, storePath, true); }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public AmazonSettings? Load()
    {
        if (!File.Exists(storePath)) return null; var plain = CredentialStore.Unprotect(File.ReadAllBytes(storePath));
        try { var settings = JsonSerializer.Deserialize<AmazonSettings>(plain) ?? throw new InvalidDataException("Amazon ayarları okunamadı."); AmazonConnection.Validate(settings); return settings; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}

/// <summary>Amazon SP-API boundary. No guessed endpoint is called until the official region contract is configured.</summary>
public sealed class AmazonConnection
{
    public static readonly IReadOnlySet<string> Regions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NA", "EU", "FE" };
    public static void Validate(AmazonSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SellerId) || settings.SellerId.Length > 128 || settings.SellerId.Any(char.IsControl)) throw new ArgumentException("Amazon seller ID gerekli.");
        foreach (var value in new[] { settings.ClientId, settings.ClientSecret, settings.RefreshToken, settings.MarketplaceId })
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Length > 2048) throw new ArgumentException("Amazon SP-API kimlik bilgileri geçersiz.");
        if (!Regions.Contains(settings.Region.Trim().ToUpperInvariant())) throw new ArgumentException("Amazon bölgesi NA, EU veya FE olmalı.");
    }
    public static string Describe(AmazonSettings? settings) => settings is null ? "NOT_CONFIGURED" : "Ayarlar şifreli kayıtlı; resmi SP-API read-only sözleşmesi doğrulanmalı.";
    public Task TestReadOnlyAsync(AmazonSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        throw new InvalidOperationException("LIVE_API_BLOCKED: Amazon SP-API region/credential sözleşmesi bu yapılandırmada doğrulanmadı; endpoint uydurulmadı ve HTTP isteği gönderilmedi.");
    }
}
