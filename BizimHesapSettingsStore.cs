using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed class BizimHesapSettingsStore(string? path = null)
{
    readonly string storePath = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "bizimhesap.bin");
    public void Save(BizimHesapSettings settings)
    {
        BizimHesapConnection.Validate(settings);
        var plain = JsonSerializer.SerializeToUtf8Bytes(settings); var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(storePath))!); File.WriteAllBytes(temporary, CredentialStore.Protect(plain)); File.Move(temporary, storePath, true); }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public BizimHesapSettings? Load()
    {
        if (!File.Exists(storePath)) return null;
        var plain = CredentialStore.Unprotect(File.ReadAllBytes(storePath));
        try { var settings = JsonSerializer.Deserialize<BizimHesapSettings>(plain) ?? throw new InvalidDataException("BizimHesap ayarları okunamadı."); BizimHesapConnection.Validate(settings); return settings; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete() { if (File.Exists(storePath)) File.Delete(storePath); }
}
