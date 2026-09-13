using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record WishSettings(string MerchantId, string ApiKey);

public sealed class WishSettingsStore(string? path = null)
{
    readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "wish.bin");
    public void Save(WishSettings settings)
    {
        WishConnection.Validate(settings); var plain = JsonSerializer.SerializeToUtf8Bytes(settings); var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { var encrypted = CredentialStore.Protect(plain); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!); File.WriteAllBytes(temporary, encrypted); File.Move(temporary, storePath, true); }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public WishSettings? Load()
    {
        if (!File.Exists(storePath)) return null; var plain = CredentialStore.Unprotect(File.ReadAllBytes(storePath));
        try { var settings = JsonSerializer.Deserialize<WishSettings>(plain) ?? throw new InvalidDataException("Wish ayarları okunamadı."); WishConnection.Validate(settings); return settings; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}

public sealed class WishConnection
{
    public static void Validate(WishSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.MerchantId) || settings.MerchantId.Length > 160 || settings.MerchantId.Any(char.IsControl)) throw new ArgumentException("Wish merchant ID gerekli.");
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKey.Length > 2048 || settings.ApiKey.Any(char.IsControl)) throw new ArgumentException("Wish API key geçersiz.");
    }
    public static string Describe(WishSettings? settings) => settings is null ? "NOT_CONFIGURED" : "Ayarlar şifreli kayıtlı; Wish API sözleşmesi doğrulanmalı.";
    public Task TestReadOnlyAsync(WishSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        throw new InvalidOperationException("LIVE_API_BLOCKED: Wish API kimlik ve endpoint sözleşmesi bu çalışma alanında doğrulanmadı; HTTP isteği gönderilmedi.");
    }
}
