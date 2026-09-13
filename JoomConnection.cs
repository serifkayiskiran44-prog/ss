using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record JoomSettings(string MerchantId, string ApiKey);

public sealed class JoomSettingsStore(string? path = null)
{
    readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "joom.bin");
    public void Save(JoomSettings settings)
    {
        JoomConnection.Validate(settings); var plain = JsonSerializer.SerializeToUtf8Bytes(settings); var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { var encrypted = CredentialStore.Protect(plain); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!); File.WriteAllBytes(temporary, encrypted); File.Move(temporary, storePath, true); }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public JoomSettings? Load()
    {
        if (!File.Exists(storePath)) return null; var plain = CredentialStore.Unprotect(File.ReadAllBytes(storePath));
        try { var settings = JsonSerializer.Deserialize<JoomSettings>(plain) ?? throw new InvalidDataException("Joom ayarları okunamadı."); JoomConnection.Validate(settings); return settings; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}

public sealed class JoomConnection
{
    public static void Validate(JoomSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.MerchantId) || settings.MerchantId.Length > 160 || settings.MerchantId.Any(char.IsControl)) throw new ArgumentException("Joom merchant ID gerekli.");
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKey.Length > 2048 || settings.ApiKey.Any(char.IsControl)) throw new ArgumentException("Joom API key geçersiz.");
    }
    public static string Describe(JoomSettings? settings) => settings is null ? "NOT_CONFIGURED" : "Ayarlar şifreli kayıtlı; Joom API kimlik/sözleşme doğrulaması bekleniyor.";
    public Task TestReadOnlyAsync(JoomSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        throw new InvalidOperationException("LIVE_API_BLOCKED: Joom API kimlik ve endpoint sözleşmesi bu çalışma alanında doğrulanmadı; HTTP isteği gönderilmedi.");
    }
}
