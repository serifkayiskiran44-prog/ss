using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed class NesSettingsStore(string? path = null)
{
    readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "nes.bin");
    public void Save(NesSettings settings)
    {
        NesSettingsValidator.Validate(settings); var plain = JsonSerializer.SerializeToUtf8Bytes(settings); var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!); File.WriteAllBytes(temporary, CredentialStore.Protect(plain)); File.Move(temporary, storePath, true); }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public NesSettings? Load()
    {
        if (!File.Exists(storePath)) return null; var encrypted = File.ReadAllBytes(storePath); if (encrypted.Length > 64 * 1024) throw new InvalidOperationException("Kayıtlı NES ayar dosyası güvenli boyut sınırını aşıyor."); var plain = CredentialStore.Unprotect(encrypted);
        try { if (plain.Length > 32 * 1024) throw new InvalidOperationException("Kayıtlı NES ayarları güvenli boyut sınırını aşıyor."); var settings = JsonSerializer.Deserialize<NesSettings>(plain) ?? throw new InvalidOperationException("NES ayarları okunamadı."); NesSettingsValidator.Validate(settings); return settings; }
        catch (JsonException) { throw new InvalidOperationException("NES ayarları okunamadı; bilgileri yeniden kaydedin."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}
