using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;
public static class CredentialStore
{
    private static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
    // Purely technical bounds - a DPAPI-wrapped EtsyCredentials JSON blob is
    // normally well under 1KB even encrypted; these budgets give generous
    // headroom for future fields while making an oversized/corrupt file a
    // deterministic, bounded-memory reject rather than an unbounded
    // ReadAllBytes/deserialize. See #2644.
    private const int MaxEncryptedFileBytes = 64 * 1024;
    private const int MaxPlaintextBytes = 32 * 1024;
    public static void Save(EtsyCredentials credentials) => Save(credentials, null);
    public static void Save(EtsyCredentials credentials, string? directory)
    {
        Validate(credentials);
        var storePath = ResolveStorePath(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var encrypted = Protect(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            var temporary = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, storePath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new InvalidOperationException("Bağlantı bilgileri Windows kullanıcı profilinde güvenli olarak kaydedilemedi."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static EtsyCredentials? Load() => Load(null);
    public static EtsyCredentials? Load(string? directory)
    {
        var storePath = ResolveStorePath(directory);
        if (!File.Exists(storePath)) return null;
        const string recoveryMessage = "Kayıtlı bağlantı bilgileri bu Windows kullanıcısı tarafından okunamadı. Bilgileri yeniden girip kaydedin.";
        byte[]? plain = null;
        try
        {
            // Check the file's length before ever allocating/reading it - an
            // oversized/corrupt file is rejected deterministically without an
            // unbounded ReadAllBytes. Never truncated, deleted, or overwritten.
            var length = new FileInfo(storePath).Length;
            if (length > MaxEncryptedFileBytes) throw new InvalidOperationException(recoveryMessage);
            plain = Unprotect(File.ReadAllBytes(storePath));
            if (plain.Length > MaxPlaintextBytes) throw new InvalidOperationException(recoveryMessage);
            var credentials = JsonSerializer.Deserialize<EtsyCredentials>(plain) ?? throw new JsonException("Credential payload is empty.");
            Validate(credentials);
            return credentials;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { throw new InvalidOperationException(recoveryMessage); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    public static void Validate(EtsyCredentials credentials)
    {
        if (credentials is null) throw new ArgumentNullException(nameof(credentials));
        if (string.IsNullOrWhiteSpace(credentials.ShopId) || credentials.ShopId.Length > 160 ||
            !credentials.ShopId.All(char.IsAsciiDigit) || !long.TryParse(credentials.ShopId, out var id) || id <= 0 ||
            credentials.ShopId != id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new ArgumentException("Etsy mağaza kimliği pozitif sayı olmalı; başında sıfır olmamalı.", nameof(credentials));
        foreach (var value in new[] { credentials.Key, credentials.Secret, credentials.Token, credentials.RefreshToken, credentials.RedirectUri })
            if (value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException("Etsy bağlantı bilgileri geçersiz.", nameof(credentials));
        if (string.IsNullOrWhiteSpace(credentials.Key) || string.IsNullOrWhiteSpace(credentials.Secret))
            throw new ArgumentException("Etsy API anahtarı ve sırrı zorunludur.", nameof(credentials));
        if (credentials.GrantedScopes is { Count: > 128 } || credentials.GrantedScopes?.Any(x => x is null || x.Length > 256 || x.Any(char.IsControl)) == true)
            throw new ArgumentException("Etsy yetki kapsamları geçersiz.", nameof(credentials));
    }
    private static string ResolveStorePath(string? directory)
    {
        if (directory is null) return Path.Combine(DefaultDirectory, "credentials.bin");
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Bağlantı bilgileri dizini boş olamaz.", nameof(directory));
        return Path.Combine(Path.GetFullPath(directory), "credentials.bin");
    }
    internal static byte[] Protect(byte[] data) => Transform(data, true);
    internal static byte[] Unprotect(byte[] data) => Transform(data, false);
    private static byte[] Transform(byte[] data, bool encrypt)
    {
        var input = new DataBlob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            // CRYPTPROTECT_UI_FORBIDDEN only: omitting LOCAL_MACHINE binds data to CurrentUser.
            var ok = encrypt
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows veri koruması başarısız oldu.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
