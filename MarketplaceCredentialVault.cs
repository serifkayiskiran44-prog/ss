using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// Stores one CurrentUser-DPAPI protected credential envelope per marketplace
/// connection. File names contain only a SHA-256 digest of ConnectionId; the
/// unhashed identity is authenticated by the encrypted envelope on every load.
/// </summary>
public sealed class MarketplaceCredentialVault
{
    const int MaxConnectionIdChars = 512;
    const int MaxChannelChars = 64;
    const int MaxShopIdChars = 160;
    const int MaxPlaintextBytes = 32 * 1024;
    const int MaxEncryptedFileBytes = 64 * 1024;
    const int EnvelopeVersion = 1;
    const string RecoveryMessage = "Kayıtlı mağaza bağlantı bilgileri güvenli kasadan okunamadı. Bilgileri yeniden girip kaydedin.";
    readonly string vaultDirectory;

    sealed record CredentialEnvelope<T>(int Version, string ConnectionId, string Channel, string ShopId, string PayloadType, T Payload);

    public MarketplaceCredentialVault(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Kasa dizini boş olamaz.", nameof(directory));
        vaultDirectory = Path.Combine(Path.GetFullPath(directory), "marketplace-credentials");
    }

    public void Save<T>(string connectionId, string channel, string shopId, T payload)
    {
        var identity = ValidateIdentity(connectionId, channel, shopId);
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        ValidatePayloadIdentity(identity.Channel, identity.ShopId, payload);
        var envelope = new CredentialEnvelope<T>(EnvelopeVersion, identity.ConnectionId, identity.Channel, identity.ShopId, TypeName<T>(), payload);
        byte[]? plain = null;
        var path = PathFor(identity.ConnectionId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            plain = JsonSerializer.SerializeToUtf8Bytes(envelope);
            if (plain.Length > MaxPlaintextBytes) throw new ArgumentException("Mağaza bağlantı bilgisi izin verilen boyutu aşıyor.", nameof(payload));
            var encrypted = CredentialStore.Protect(plain);
            if (encrypted.Length > MaxEncryptedFileBytes) throw new ArgumentException("Şifreli mağaza bağlantı bilgisi izin verilen boyutu aşıyor.", nameof(payload));
            Directory.CreateDirectory(vaultDirectory);
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, path, true);
        }
        catch (ArgumentException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            throw new InvalidOperationException("Mağaza bağlantı bilgileri Windows kullanıcı profilinde güvenli olarak kaydedilemedi.");
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    public T? Load<T>(string connectionId, string channel, string shopId)
    {
        var identity = ValidateIdentity(connectionId, channel, shopId);
        var path = PathFor(identity.ConnectionId);
        if (!File.Exists(path)) return default;
        byte[]? plain = null;
        try
        {
            var length = new FileInfo(path).Length;
            if (length is <= 0 or > MaxEncryptedFileBytes) throw new InvalidOperationException(RecoveryMessage);
            plain = CredentialStore.Unprotect(File.ReadAllBytes(path));
            if (plain.Length is <= 0 or > MaxPlaintextBytes) throw new InvalidOperationException(RecoveryMessage);
            var envelope = JsonSerializer.Deserialize<CredentialEnvelope<T>>(plain) ?? throw new InvalidOperationException(RecoveryMessage);
            if (envelope.Version != EnvelopeVersion ||
                !string.Equals(envelope.ConnectionId, identity.ConnectionId, StringComparison.Ordinal) ||
                !string.Equals(envelope.Channel, identity.Channel, StringComparison.Ordinal) ||
                !string.Equals(envelope.ShopId, identity.ShopId, StringComparison.Ordinal) ||
                !string.Equals(envelope.PayloadType, TypeName<T>(), StringComparison.Ordinal) || envelope.Payload is null)
                throw new InvalidOperationException(RecoveryMessage);
            ValidatePayloadIdentity(identity.Channel, identity.ShopId, envelope.Payload);
            return envelope.Payload;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException)
        {
            throw new InvalidOperationException(RecoveryMessage);
        }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public bool Remove(string connectionId)
    {
        var normalized = ValidateConnectionId(connectionId);
        var path = PathFor(normalized);
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Mağaza bağlantı bilgisi güvenli kasadan kaldırılamadı.");
        }
    }

    string PathFor(string connectionId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionId))).ToLowerInvariant();
        return Path.Combine(vaultDirectory, hash + ".bin");
    }

    static (string ConnectionId, string Channel, string ShopId) ValidateIdentity(string connectionId, string channel, string shopId)
    {
        connectionId = ValidateConnectionId(connectionId);
        channel = RequireBounded(channel, MaxChannelChars, "Kanal kimliği geçersiz.", nameof(channel)).ToLowerInvariant();
        if (!channel.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new ArgumentException("Kanal kimliği geçersiz.", nameof(channel));
        shopId = RequireBounded(shopId, MaxShopIdChars, "Mağaza kimliği geçersiz.", nameof(shopId));
        return (connectionId, channel, shopId);
    }

    static string ValidateConnectionId(string value) => RequireBounded(value, MaxConnectionIdChars, "Bağlantı kimliği geçersiz.", nameof(value));

    static string RequireBounded(string value, int maximum, string message, string parameter)
    {
        if (value is null) throw new ArgumentNullException(parameter);
        if (value.Length is < 1 || value.Length > maximum || value != value.Trim() || value.Any(char.IsControl))
            throw new ArgumentException(message, parameter);
        return value;
    }

    static string TypeName<T>() => typeof(T).FullName ?? typeof(T).Name;

    static void ValidatePayloadIdentity<T>(string channel, string shopId, T payload)
    {
        if (payload is EtsyCredentials etsy)
        {
            CredentialStore.Validate(etsy);
            if (!string.Equals(channel, "etsy", StringComparison.Ordinal) || !string.Equals(etsy.ShopId, shopId, StringComparison.Ordinal))
                throw new ArgumentException("Etsy bağlantı bilgisi hesap kimliğiyle eşleşmiyor.", nameof(payload));
        }
        else if (payload is TrendyolSettings trendyol)
        {
            TrendyolConnection.Validate(trendyol);
            if (!string.Equals(channel, "trendyol", StringComparison.Ordinal) || !string.Equals(trendyol.SupplierId, shopId, StringComparison.Ordinal))
                throw new ArgumentException("Trendyol bağlantı bilgisi hesap kimliğiyle eşleşmiyor.", nameof(payload));
        }
    }
}
