using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TrMarketplaceHubDesktop.Hepsiburada;

namespace TrMarketplaceHubDesktop;

public sealed record HepsiburadaSettings(string MerchantId, string Username, string Password, string UserAgent);

public sealed class HepsiburadaSettingsStore(string? path = null)
{
    readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "hepsiburada.bin");
    public void Save(HepsiburadaSettings settings)
    {
        HepsiburadaConnection.Validate(settings); var plain = JsonSerializer.SerializeToUtf8Bytes(settings); var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { var encrypted = CredentialStore.Protect(plain); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!); File.WriteAllBytes(temporary, encrypted); File.Move(temporary, storePath, true); }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public HepsiburadaSettings? Load()
    {
        if (!File.Exists(storePath)) return null; var plain = CredentialStore.Unprotect(File.ReadAllBytes(storePath));
        try { var settings = JsonSerializer.Deserialize<HepsiburadaSettings>(plain) ?? throw new InvalidDataException("Hepsiburada ayarları okunamadı."); HepsiburadaConnection.Validate(settings); return settings; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}

public sealed class HepsiburadaConnection
{
    const int MaxMerchantIdChars = 128;
    const int MaxCredentialChars = 512;

    public static void Validate(HepsiburadaSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.MerchantId) || settings.MerchantId.Length > 128 || settings.MerchantId.Any(char.IsControl)) throw new ArgumentException("Hepsiburada merchant ID gerekli.");
        foreach (var value in new[] { settings.Username, settings.Password, settings.UserAgent }) if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Length > 512) throw new ArgumentException("Hepsiburada API kimlik bilgileri geçersiz.");
    }

    public static void Validate(HepsiburadaCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        RequireCredential(credentials.MerchantId, MaxMerchantIdChars, "Hepsiburada merchant ID gerekli.");
        RequireCredential(credentials.ServiceKey, MaxCredentialChars, "Hepsiburada servis anahtarı geçersiz.");
        RequireCredential(credentials.UserAgent, MaxCredentialChars, "Hepsiburada User-Agent bilgisi geçersiz.");
        if (!Enum.IsDefined(credentials.Environment))
            throw new ArgumentException("Hepsiburada ortamı geçersiz.", nameof(credentials));
    }

    static void RequireCredential(string value, int maximum, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException(message);
    }
    public static string Describe(HepsiburadaSettings? settings) => settings is null ? "NOT_CONFIGURED" : "Ayarlar şifreli kayıtlı; resmi API sözleşmesi ve merchant yetkisi doğrulanmalı.";
    public Task TestReadOnlyAsync(HepsiburadaSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        throw new InvalidOperationException("LIVE_API_BLOCKED: Hepsiburada resmi endpoint/scope sözleşmesi bu çalışma alanında doğrulanmadı; HTTP isteği gönderilmedi.");
    }
}
