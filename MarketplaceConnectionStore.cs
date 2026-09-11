using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record MarketplaceConnectionDefinition(
    string Id,
    string Name,
    string? RouteKey,
    MarketplaceCapabilities Capabilities,
    string DocumentationUrl,
    bool LiveApiBlocked);

public static class MarketplaceConnectionCatalog
{
    public static IReadOnlyList<MarketplaceConnectionDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        new MarketplaceConnectionDefinition("etsy", "Etsy", "etsy", new(new HashSet<MarketplaceOperation>{ MarketplaceOperation.ProductsRead, MarketplaceOperation.OrdersRead, MarketplaceOperation.StockWrite, MarketplaceOperation.PriceWrite }), "https://developers.etsy.com/documentation/", false),
        new MarketplaceConnectionDefinition("ebay", "eBay", "ebay", new(new HashSet<MarketplaceOperation>{ MarketplaceOperation.ProductsRead, MarketplaceOperation.OrdersRead, MarketplaceOperation.StockWrite }), "https://developer.ebay.com/api-docs/", false),
        new MarketplaceConnectionDefinition("amazon", "Amazon", "amazon", MarketplaceCapabilities.LocalOnly, "https://developer-docs.amazon.com/sp-api/", true),
        new MarketplaceConnectionDefinition("trendyol", "Trendyol", "trendyol", MarketplaceCapabilities.LocalOnly, "https://developers.trendyol.com/", true),
        new MarketplaceConnectionDefinition("hepsiburada", "Hepsiburada", "hepsiburada", MarketplaceCapabilities.LocalOnly, "https://developers.hepsiburada.com/", true),
        new MarketplaceConnectionDefinition("allegro", "Allegro", "allegro", new(new HashSet<MarketplaceOperation>{ MarketplaceOperation.ProductsRead, MarketplaceOperation.OrdersRead }), "https://developer.allegro.pl/", false),
        new MarketplaceConnectionDefinition("ozon", "Ozon", "ozon", new(new HashSet<MarketplaceOperation>{ MarketplaceOperation.ProductsRead }), "https://docs.ozon.ru/api/seller/", false),
        new MarketplaceConnectionDefinition("joom", "Joom", "joom", MarketplaceCapabilities.LocalOnly, "https://merchant.joom.com/docs/api", true),
        new MarketplaceConnectionDefinition("wish", "Wish", "channels", MarketplaceCapabilities.LocalOnly, "https://merchant.wish.com/documentation/api/v3/oauth", true),
        new MarketplaceConnectionDefinition("fruugo", "Fruugo", "fruugo", MarketplaceCapabilities.LocalOnly, "https://developer.fruugo.com/", true),
        new MarketplaceConnectionDefinition("navlungo", "Navlungo", "shipping", MarketplaceCapabilities.LocalOnly, "https://navlungo.com/", true)
    });

    public static MarketplaceConnectionDefinition Get(string id) => All.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Desteklenmeyen kanal.", nameof(id));
}

public sealed record MarketplaceConnection(
    string Id,
    string Channel,
    string ShopId,
    string DisplayName,
    bool Enabled,
    string Status,
    DateTime? LastTestUtc,
    string LastError);

/// <summary>Stores only non-secret shop metadata; credentials stay in the existing DPAPI stores.</summary>
public sealed class MarketplaceConnectionStore
{
    readonly string connectionString;

    public MarketplaceConnectionStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MarketplaceConnections(
                Id TEXT PRIMARY KEY,
                Channel TEXT NOT NULL,
                ShopId TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                Status TEXT NOT NULL,
                LastTestUtc TEXT NULL,
                LastError TEXT NOT NULL DEFAULT '',
                UNIQUE(Channel,ShopId))
            """;
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    public IReadOnlyList<MarketplaceConnection> List(bool includeDefaults = true)
    {
        if (includeDefaults) EnsureDefaults();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Channel,ShopId,DisplayName,Enabled,Status,LastTestUtc,LastError FROM MarketplaceConnections ORDER BY Channel,ShopId";
        using var reader = command.ExecuteReader();
        var result = new List<MarketplaceConnection>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    public MarketplaceConnection? Get(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Channel,ShopId,DisplayName,Enabled,Status,LastTestUtc,LastError FROM MarketplaceConnections WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public MarketplaceConnection Save(string channel, string shopId, string displayName, bool enabled, string? id = null)
    {
        var definition = MarketplaceConnectionCatalog.Get(channel);
        shopId = shopId.Trim();
        displayName = displayName.Trim();
        if (shopId.Length is < 1 or > 160 || shopId.Any(char.IsControl)) throw new ArgumentException("Mağaza kimliği 1–160 karakter olmalı.", nameof(shopId));
        if (displayName.Length is < 1 or > 160 || displayName.Any(char.IsControl)) throw new ArgumentException("Görünen ad 1–160 karakter olmalı.", nameof(displayName));
        var normalizedChannel = definition.Id;
        var actualId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MarketplaceConnections(Id,Channel,ShopId,DisplayName,Enabled,Status,LastTestUtc,LastError)
            VALUES($id,$channel,$shop,$name,$enabled,'NOT_CONFIGURED',NULL,'')
            ON CONFLICT(Channel,ShopId) DO UPDATE SET
                DisplayName=excluded.DisplayName, Enabled=excluded.Enabled
            """;
        command.Parameters.AddWithValue("$id", actualId);
        command.Parameters.AddWithValue("$channel", normalizedChannel);
        command.Parameters.AddWithValue("$shop", shopId);
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.ExecuteNonQuery();
        return List(false).Single(x => x.Channel == normalizedChannel && x.ShopId == shopId);
    }

    public void SetEnabled(string id, bool enabled)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MarketplaceConnections SET Enabled=$enabled WHERE Id=$id";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
    }

    public void RecordTest(string id, bool success, string? error = null)
    {
        var safeError = success ? "" : Redact(error ?? "Bağlantı testi başarısız.");
        var blocked = !success && safeError.Contains("LIVE_API_BLOCKED", StringComparison.OrdinalIgnoreCase);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MarketplaceConnections SET Status=$status,LastTestUtc=$tested,LastError=$error WHERE Id=$id";
        command.Parameters.AddWithValue("$status", success ? "CONNECTED_READ_ONLY" : blocked ? "LIVE_API_BLOCKED" : "FAILED");
        command.Parameters.AddWithValue("$tested", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$error", safeError);
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
    }

    void EnsureDefaults()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var definition in MarketplaceConnectionCatalog.All)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO MarketplaceConnections(Id,Channel,ShopId,DisplayName,Enabled,Status,LastError) VALUES($id,$channel,'default',$name,1,'NOT_CONFIGURED','')";
            command.Parameters.AddWithValue("$id", definition.Id + ":default");
            command.Parameters.AddWithValue("$channel", definition.Id);
            command.Parameters.AddWithValue("$name", definition.Name + " (varsayılan)");
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    static MarketplaceConnection Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4) == 1,
        reader.GetString(5), reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.GetString(7));

    internal static string Redact(string value)
    {
        var safe = value.Replace("access_token", "[redacted]", StringComparison.OrdinalIgnoreCase)
            .Replace("refresh_token", "[redacted]", StringComparison.OrdinalIgnoreCase)
            .Replace("api-key", "[redacted]", StringComparison.OrdinalIgnoreCase)
            .Replace("api_key", "[redacted]", StringComparison.OrdinalIgnoreCase)
            .Replace("client_secret", "[redacted]", StringComparison.OrdinalIgnoreCase)
            .Replace("secret", "[redacted]", StringComparison.OrdinalIgnoreCase)
            .Replace("token", "[redacted]", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return safe.Length > 500 ? safe[..500] : safe;
    }
}
