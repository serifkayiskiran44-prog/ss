using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;
public sealed class EbaySettingsStore(string? path = null)
{
    private readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "ebay.bin");
    public void Save(EbaySavedConnection connection)
    {
        EbayConnection.Validate(connection.Settings);
        var plain = JsonSerializer.SerializeToUtf8Bytes(connection);
        var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            var encrypted = CredentialStore.Protect(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!);
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, storePath, true);
        } finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public EbaySavedConnection? Load()
    {
        if (!File.Exists(storePath)) return null;
        var plain = CredentialStore.Unprotect(File.ReadAllBytes(storePath));
        try {
            var saved = JsonSerializer.Deserialize<EbaySavedConnection>(plain) ?? throw new InvalidDataException("eBay ayarları okunamadı.");
            EbayConnection.Validate(saved.Settings);
            return saved;
        } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}
