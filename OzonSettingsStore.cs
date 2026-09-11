using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;
public sealed class OzonSettingsStore(string? path = null)
{
    private readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "ozon.bin");
    public void Save(OzonSettings settings)
    {
        OzonConnection.Validate(settings);
        var plain = JsonSerializer.SerializeToUtf8Bytes(settings);
        var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            var encrypted = CredentialStore.Protect(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!);
            File.WriteAllBytes(temporary, encrypted); File.Move(temporary, storePath, true);
        } finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public OzonSettings? Load()
    {
        if (!File.Exists(storePath)) return null;
        var plain = CredentialStore.Unprotect(File.ReadAllBytes(storePath));
        try {
            var settings = JsonSerializer.Deserialize<OzonSettings>(plain) ?? throw new InvalidDataException("Ozon ayarları okunamadı.");
            OzonConnection.Validate(settings); return settings;
        } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}
