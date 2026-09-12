using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record TrendyolSettings(string SupplierId, string ApiKey, string ApiSecret, string UserAgent);

public sealed class TrendyolSettingsStore(string? path = null)
{
    readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "trendyol.bin");
    public void Save(TrendyolSettings settings)
    {
        TrendyolConnection.Validate(settings); var plain = JsonSerializer.SerializeToUtf8Bytes(settings); var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { var encrypted = CredentialStore.Protect(plain); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!); File.WriteAllBytes(temporary, encrypted); File.Move(temporary, storePath, true); }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public TrendyolSettings? Load()
    {
        if (!File.Exists(storePath)) return null; var plain = CredentialStore.Unprotect(File.ReadAllBytes(storePath));
        try { var settings = JsonSerializer.Deserialize<TrendyolSettings>(plain) ?? throw new InvalidDataException("Trendyol ayarları okunamadı."); TrendyolConnection.Validate(settings); return settings; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}

/// <summary>Trendyol connector boundary. No unverified endpoint is invoked.</summary>
public sealed class TrendyolConnection
{
    public static void Validate(TrendyolSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SupplierId) || !settings.SupplierId.All(char.IsAsciiDigit) || settings.SupplierId.Length > 32) throw new ArgumentException("Trendyol satıcı ID sayısal olmalı.");
        foreach (var value in new[] { settings.ApiKey, settings.ApiSecret, settings.UserAgent }) if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Length > 512) throw new ArgumentException("Trendyol API kimlik bilgileri geçersiz.");
        // Official format: "{SellerId} - SelfIntegration" or "{SellerId} - {IntegrationCompanyName}"; requests without a matching User-Agent are rejected with HTTP 403 by Trendyol.
        // https://developers.trendyol.com/v3.0/docs/getting-started-1 (fetched 2026-09-12)
        var prefix = settings.SupplierId + " - ";
        if (!settings.UserAgent.StartsWith(prefix, StringComparison.Ordinal) || settings.UserAgent.Length == prefix.Length)
            throw new ArgumentException("Trendyol User-Agent \"{SatıcıId} - SelfIntegration\" veya \"{SatıcıId} - Entegrasyon Firması\" biçiminde olmalı.");
    }
    public static string Describe(TrendyolSettings? settings) => settings is null ? "NOT_CONFIGURED" : "Ayarlar şifreli kayıtlı; resmi API sözleşmesi ve mağaza erişimi doğrulanmalı.";
    public Task TestReadOnlyAsync(TrendyolSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        throw new InvalidOperationException("LIVE_API_BLOCKED: Trendyol resmi endpoint/scope sözleşmesi bu çalışma alanında doğrulanmadı; HTTP isteği gönderilmedi.");
    }
}
