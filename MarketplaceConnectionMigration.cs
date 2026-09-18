using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public enum MarketplaceConnectionMigrationState { MissingLegacy, Imported, AlreadyImported }

public sealed record MarketplaceConnectionMigrationResult(
    string Channel,
    MarketplaceConnectionMigrationState State,
    string? ConnectionId,
    string? ShopId);

/// <summary>
/// Imports the two pre-vault credential files without deleting or rewriting
/// them. A durable marker is written only after the new DPAPI entry round-trips
/// with the same account identity and payload.
/// </summary>
public sealed class MarketplaceConnectionMigration
{
    readonly string directory;
    readonly MarketplaceConnectionStore connections;
    readonly MarketplaceCredentialVault vault;

    public MarketplaceConnectionMigration(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        if (string.IsNullOrWhiteSpace(this.directory)) throw new ArgumentException("Veri dizini boş olamaz.", nameof(directory));
        this.directory = Path.GetFullPath(this.directory);
        connections = new(this.directory);
        vault = new(this.directory);
    }

    public IReadOnlyList<MarketplaceConnectionMigrationResult> ImportLegacy()
    {
        var results = new List<MarketplaceConnectionMigrationResult>(2)
        {
            ImportOne("etsy", () => CredentialStore.Load(directory), value => value.ShopId, "Etsy"),
            ImportOne("trendyol", () => new TrendyolSettingsStore(Path.Combine(directory, "trendyol.bin")).Load(), value => value.SupplierId, "Trendyol")
        };
        return results;
    }

    MarketplaceConnectionMigrationResult ImportOne<T>(string channel, Func<T?> loadLegacy, Func<T, string> accountIdentity, string displayName)
        where T : class
    {
        var marker = connections.CredentialMigration(channel);
        if (marker is not null)
        {
            try
            {
                var markedConnection = connections.Get(marker.ConnectionId);
                if (markedConnection is not null &&
                    string.Equals(markedConnection.Channel, channel, StringComparison.Ordinal) &&
                    string.Equals(markedConnection.ShopId, marker.ShopId, StringComparison.Ordinal))
                {
                    var existing = vault.Load<T>(marker.ConnectionId, channel, marker.ShopId);
                    if (existing is not null && string.Equals(accountIdentity(existing), marker.ShopId, StringComparison.Ordinal))
                        return new(channel, MarketplaceConnectionMigrationState.AlreadyImported, marker.ConnectionId, marker.ShopId);
                }
            }
            catch (InvalidOperationException)
            {
                // A missing/corrupt new entry is recoverable while the untouched
                // legacy file still exists; retry the full verified import below.
            }
        }

        T? legacy;
        try { legacy = loadLegacy(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException($"Eski {channel} bağlantı bilgisi güvenli biçimde içe aktarılamadı.");
        }
        if (legacy is null) return new(channel, MarketplaceConnectionMigrationState.MissingLegacy, null, null);

        var shopId = accountIdentity(legacy);
        ValidateAccountIdentity(channel, shopId, legacy);
        var connection = connections.Find(channel, shopId) ??
            connections.Save(channel, shopId, displayName + " " + shopId, true, marker?.ConnectionId);

        vault.Save(connection.Id, channel, shopId, legacy);
        var roundTrip = vault.Load<T>(connection.Id, channel, shopId)
            ?? throw new InvalidOperationException("Yeni mağaza bağlantı bilgisi doğrulanamadı.");
        if (!string.Equals(accountIdentity(roundTrip), shopId, StringComparison.Ordinal) || !SamePayload(legacy, roundTrip))
            throw new InvalidOperationException("Yeni mağaza bağlantı bilgisi hesap kimliğiyle eşleşmiyor.");

        connections.MarkCredentialMigration(channel, connection.Id, shopId);
        return new(channel, MarketplaceConnectionMigrationState.Imported, connection.Id, shopId);
    }

    static void ValidateAccountIdentity<T>(string channel, string shopId, T payload)
    {
        if (channel == "etsy" && payload is EtsyCredentials etsy)
        {
            CredentialStore.Validate(etsy);
            if (!string.Equals(etsy.ShopId, shopId, StringComparison.Ordinal)) throw new InvalidOperationException("Eski Etsy hesabı doğrulanamadı.");
        }
        else if (channel == "trendyol" && payload is TrendyolSettings trendyol)
        {
            TrendyolConnection.Validate(trendyol);
            if (!string.Equals(trendyol.SupplierId, shopId, StringComparison.Ordinal)) throw new InvalidOperationException("Eski Trendyol hesabı doğrulanamadı.");
        }
        else throw new InvalidOperationException("Eski bağlantı türü doğrulanamadı.");
    }

    static bool SamePayload<T>(T left, T right)
    {
        var leftBytes = JsonSerializer.SerializeToUtf8Bytes(left);
        var rightBytes = JsonSerializer.SerializeToUtf8Bytes(right);
        try { return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}
