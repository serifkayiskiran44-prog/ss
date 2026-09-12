using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;
public static class CredentialStore
{
    // Matches TemplateStore/CatalogStore's convention: a null directory resolves to the real per-user app-data
    // folder (unchanged behavior for the running app); a test passes its own root so it never touches this
    // machine's real stored Etsy credentials.
    private static string StorePath(string? directory) => Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"), "credentials.bin");
    public static void Save(EtsyCredentials credentials, string? directory = null)
    {
        var path = StorePath(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var encrypted = Protect(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { throw new InvalidOperationException("Bağlantı bilgileri Windows kullanıcı profilinde güvenli olarak kaydedilemedi."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static EtsyCredentials? Load(string? directory = null)
    {
        var path = StorePath(directory);
        if (!File.Exists(path)) return null;
        byte[]? plain = null;
        try { plain = Unprotect(File.ReadAllBytes(path)); return JsonSerializer.Deserialize<EtsyCredentials>(plain); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { throw new InvalidOperationException("Kayıtlı bağlantı bilgileri bu Windows kullanıcısı tarafından okunamadı. Bilgileri yeniden girip kaydedin."); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
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
