using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record FruugoSettings(string RetailerId, string Username, string Password);

public sealed class FruugoSettingsStore(string? path = null)
{
    readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "fruugo.bin");
    public void Save(FruugoSettings settings)
    {
        FruugoConnection.Validate(settings); var plain = JsonSerializer.SerializeToUtf8Bytes(settings); var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { var encrypted = CredentialStore.Protect(plain); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!); File.WriteAllBytes(temporary, encrypted); File.Move(temporary, storePath, true); }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public FruugoSettings? Load()
    {
        if (!File.Exists(storePath)) return null; var plain = CredentialStore.Unprotect(File.ReadAllBytes(storePath));
        try { var settings = JsonSerializer.Deserialize<FruugoSettings>(plain) ?? throw new InvalidDataException("Fruugo ayarları okunamadı."); FruugoConnection.Validate(settings); return settings; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}

public sealed class FruugoConnection
{
    public static void Validate(FruugoSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.RetailerId) || settings.RetailerId.Length > 128 || settings.RetailerId.Any(char.IsControl)) throw new ArgumentException("Fruugo retailer ID gerekli.");
        foreach (var value in new[] { settings.Username, settings.Password }) if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Length > 512) throw new ArgumentException("Fruugo API kimlik bilgileri geçersiz.");
    }
    public static string Describe(FruugoSettings? settings) => settings is null ? "NOT_CONFIGURED" : "Retailer ayarları şifreli kayıtlı; ürün API sözleşmesi doğrulanmalı.";
    public Task TestReadOnlyAsync(FruugoSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        throw new InvalidOperationException("LIVE_API_BLOCKED: Fruugo ürün/sipariş API sözleşmesi bu çalışma alanında doğrulanmadı; HTTP isteği gönderilmedi.");
    }
}
