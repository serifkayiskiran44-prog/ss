using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

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
        new MarketplaceConnectionDefinition("etsy", "Etsy", "etsy", new(new HashSet<MarketplaceOperation>{ MarketplaceOperation.ProductsRead, MarketplaceOperation.OrdersRead, MarketplaceOperation.StockWrite, MarketplaceOperation.PriceWrite, MarketplaceOperation.ProductManagement, MarketplaceOperation.ContentWrite, MarketplaceOperation.TaxonomyWrite, MarketplaceOperation.PropertiesWrite, MarketplaceOperation.ShippingWrite, MarketplaceOperation.ReadinessWrite, MarketplaceOperation.ListingCreate }), "https://developers.etsy.com/documentation/", false),
        new MarketplaceConnectionDefinition("ebay", "eBay", "ebay", new(new HashSet<MarketplaceOperation>{ MarketplaceOperation.ProductsRead, MarketplaceOperation.OrdersRead, MarketplaceOperation.StockWrite }), "https://developer.ebay.com/api-docs/", false),
        new MarketplaceConnectionDefinition("amazon", "Amazon", "amazon", MarketplaceCapabilities.LocalOnly, "https://developer-docs.amazon.com/sp-api/", true),
        new MarketplaceConnectionDefinition("trendyol", "Trendyol", "trendyol", new(new HashSet<MarketplaceOperation>{ MarketplaceOperation.ProductsRead, MarketplaceOperation.StockWrite, MarketplaceOperation.PriceWrite, MarketplaceOperation.ProductManagement, MarketplaceOperation.ContentWrite, MarketplaceOperation.CategoryWrite, MarketplaceOperation.BrandWrite, MarketplaceOperation.DeliveryWrite }), "https://developers.trendyol.com/", false),
        new MarketplaceConnectionDefinition("hepsiburada", "Hepsiburada", "hepsiburada", MarketplaceCapabilities.LocalOnly, "https://developers.hepsiburada.com/", true),
        new MarketplaceConnectionDefinition("allegro", "Allegro", "allegro", new(new HashSet<MarketplaceOperation>{ MarketplaceOperation.ProductsRead, MarketplaceOperation.OrdersRead }), "https://developer.allegro.pl/", false),
        new MarketplaceConnectionDefinition("ozon", "Ozon", "ozon", new(new HashSet<MarketplaceOperation>{ MarketplaceOperation.ProductsRead }), "https://docs.ozon.ru/api/seller/", false),
        new MarketplaceConnectionDefinition("joom", "Joom", "joom", MarketplaceCapabilities.LocalOnly, "https://merchant.joom.com/docs/api", true),
        new MarketplaceConnectionDefinition("wish", "Wish", "wish", MarketplaceCapabilities.LocalOnly, "https://merchant.wish.com/documentation/api/v3/oauth", true),
        new MarketplaceConnectionDefinition("fruugo", "Fruugo", "fruugo", MarketplaceCapabilities.LocalOnly, "https://developer.fruugo.com/", true),
        new MarketplaceConnectionDefinition("navlungo", "Navlungo", "shipping", MarketplaceCapabilities.LocalOnly, "https://navlungo.com/", true),
        new MarketplaceConnectionDefinition("bizimhesap", "BizimHesap", "bizimhesap", new(new HashSet<MarketplaceOperation>{ MarketplaceOperation.ProductsRead }), "https://bizimhesap.com/", false)
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
    string LastError,
    long Revision)
{
    public bool Active => Enabled;
}

public sealed record MarketplaceCredentialMigrationMarker(string Channel, string ConnectionId, string ShopId, DateTime CompletedUtc);

/// Outcome of applying a connection test result through the revision fence.
/// Applied is the only case that actually wrote Status/LastTestUtc/LastError.
public enum ConnectionTestApplyResult { Applied, Stale, Disabled, NotFound }

/// Bounded diagnostics only (id/channel/shop identity, a short reason, detection
/// time) - never LastError - for a MarketplaceConnections row with an unparsable
/// LastTestUtc. See CatalogStore's CorruptProductRow for the same pattern.
public sealed record CorruptMarketplaceConnection(string Id, string Channel, string ShopId, string Reason, DateTime DetectedUtc);

/// Raised by Get(id) when the row exists but its LastTestUtc is corrupt - kept
/// distinct from returning null (which still means "no such connection"), so a
/// caller can never mistake "needs repair" for "not configured".
public sealed class MarketplaceConnectionCorruptException : Exception
{
    public string ConnectionId { get; }
    public MarketplaceConnectionCorruptException(string connectionId, string reason) : base($"Mağaza bağlantı kaydı bozuk (REVIEW_REQUIRED): {reason}") => ConnectionId = connectionId;
}

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
        EnsureColumn(connection, "Revision", "INTEGER NOT NULL DEFAULT 0");
        using var migration = connection.CreateCommand();
        migration.CommandText = """
            CREATE TABLE IF NOT EXISTS MarketplaceCredentialMigrations(
                Channel TEXT PRIMARY KEY,
                ConnectionId TEXT NOT NULL,
                ShopId TEXT NOT NULL,
                CompletedUtc TEXT NOT NULL
            )
            """;
        migration.ExecuteNonQuery();
    }

    static void EnsureColumn(SqliteConnection connection, string name, string definition)
    {
        using var check = connection.CreateCommand(); check.CommandText = "SELECT 1 FROM pragma_table_info('MarketplaceConnections') WHERE name=$name"; check.Parameters.AddWithValue("$name", name);
        if (check.ExecuteScalar() is not null) return;
        using var add = connection.CreateCommand(); add.CommandText = $"ALTER TABLE MarketplaceConnections ADD COLUMN {name} {definition}"; add.ExecuteNonQuery();
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    /// A malformed LastTestUtc must never crash the whole read - the row is
    /// excluded from the healthy result and reported only via CorruptConnections();
    /// detection re-runs from the row's own stored text every call, so it stays
    /// stable across a restart without a separate tracking table.
    public IReadOnlyList<MarketplaceConnection> List(bool includeDefaults = true)
    {
        if (includeDefaults) EnsureDefaults();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Channel,ShopId,DisplayName,Enabled,Status,LastTestUtc,LastError,Revision FROM MarketplaceConnections ORDER BY Channel,ShopId";
        using var reader = command.ExecuteReader();
        var result = new List<MarketplaceConnection>();
        while (reader.Read()) if (TryRead(reader, out var row, out _)) result.Add(row!);
        return result;
    }

    /// Bounded diagnostics for every row whose LastTestUtc failed to parse - never
    /// the raw LastError.
    public IReadOnlyList<CorruptMarketplaceConnection> CorruptConnections()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Channel,ShopId,DisplayName,Enabled,Status,LastTestUtc,LastError,Revision FROM MarketplaceConnections";
        using var reader = command.ExecuteReader();
        var result = new List<CorruptMarketplaceConnection>();
        while (reader.Read()) if (!TryRead(reader, out _, out var corrupt)) result.Add(corrupt!);
        return result;
    }

    /// A corrupt target row throws MarketplaceConnectionCorruptException rather
    /// than returning null, so "needs repair" is never confused with "not configured".
    public MarketplaceConnection? Get(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Channel,ShopId,DisplayName,Enabled,Status,LastTestUtc,LastError,Revision FROM MarketplaceConnections WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!TryRead(reader, out var row, out var corrupt)) throw new MarketplaceConnectionCorruptException(corrupt!.Id, corrupt.Reason);
        return row;
    }

    public MarketplaceConnection? Find(string channel, string shopId)
    {
        var normalizedChannel = MarketplaceConnectionCatalog.Get(channel).Id;
        shopId = ValidateLabel(shopId, "Mağaza kimliği 1–160 karakter olmalı.", nameof(shopId));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Channel,ShopId,DisplayName,Enabled,Status,LastTestUtc,LastError,Revision FROM MarketplaceConnections WHERE Channel=$channel AND ShopId=$shop";
        command.Parameters.AddWithValue("$channel", normalizedChannel);
        command.Parameters.AddWithValue("$shop", shopId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!TryRead(reader, out var row, out var corrupt)) throw new MarketplaceConnectionCorruptException(corrupt!.Id, corrupt.Reason);
        return row;
    }

    public MarketplaceConnection Save(string channel, string shopId, string displayName, bool enabled, string? id = null)
    {
        var definition = MarketplaceConnectionCatalog.Get(channel);
        shopId = ValidateLabel(shopId, "Mağaza kimliği 1–160 karakter olmalı.", nameof(shopId));
        displayName = ValidateLabel(displayName, "Görünen ad 1–160 karakter olmalı.", nameof(displayName));
        var normalizedChannel = definition.Id;
        var actualId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
        if (actualId.Length > 512 || actualId.Any(char.IsControl)) throw new ArgumentException("Bağlantı kimliği geçersiz.", nameof(id));
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Revision bumps on every metadata/enabled change (insert starts at 1) so an
        // in-flight connection test started against an older revision can be fenced
        // out by RecordTest even if Enabled itself didn't change.
        command.CommandText = """
            INSERT INTO MarketplaceConnections(Id,Channel,ShopId,DisplayName,Enabled,Status,LastTestUtc,LastError,Revision)
            VALUES($id,$channel,$shop,$name,$enabled,'NOT_CONFIGURED',NULL,'',1)
            ON CONFLICT(Channel,ShopId) DO UPDATE SET
                DisplayName=excluded.DisplayName, Enabled=excluded.Enabled, Revision=MarketplaceConnections.Revision+1
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
        command.CommandText = "UPDATE MarketplaceConnections SET Enabled=$enabled,Status=CASE WHEN $enabled=0 THEN 'DISABLED' WHEN Status='DISABLED' THEN 'NOT_CONFIGURED' ELSE Status END,Revision=Revision+1 WHERE Id=$id";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
    }

    /// <summary>Deactivates a shop without deleting its metadata or historical health result.</summary>
    public void Deactivate(string id) => SetEnabled(id, false);

    /// Compare-and-delete used only to compensate a store metadata row some
    /// caller (e.g. Migration Assistant Undo, see #2653) just created: it only
    /// removes the row while it is still at exactly the given Revision, so a row
    /// anyone has since enabled/edited/tested (which bumps Revision) is left
    /// alone rather than silently deleted out from under them.
    public bool DeleteIfUntouchedSinceCreate(string id, long expectedRevision)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM MarketplaceConnections WHERE Id=$id AND Revision=$revision";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$revision", expectedRevision);
        return command.ExecuteNonQuery() == 1;
    }

    public MarketplaceCredentialMigrationMarker? CredentialMigration(string channel)
    {
        var normalizedChannel = MarketplaceConnectionCatalog.Get(channel).Id;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Channel,ConnectionId,ShopId,CompletedUtc FROM MarketplaceCredentialMigrations WHERE Channel=$channel";
        command.Parameters.AddWithValue("$channel", normalizedChannel);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!TryParseUtc(reader.GetString(3), out var completed)) throw new InvalidDataException("Credential migration marker timestamp is corrupt.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), completed);
    }

    public void MarkCredentialMigration(string channel, string connectionId, string shopId)
    {
        var normalizedChannel = MarketplaceConnectionCatalog.Get(channel).Id;
        var connection = Get(connectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        if (!string.Equals(connection.Channel, normalizedChannel, StringComparison.Ordinal) ||
            !string.Equals(connection.ShopId, shopId, StringComparison.Ordinal))
            throw new InvalidOperationException("Mağaza bağlantı kimliği doğrulanamadı.");
        using var database = Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            INSERT INTO MarketplaceCredentialMigrations(Channel,ConnectionId,ShopId,CompletedUtc)
            VALUES($channel,$connection,$shop,$completed)
            ON CONFLICT(Channel) DO UPDATE SET ConnectionId=excluded.ConnectionId,ShopId=excluded.ShopId,CompletedUtc=excluded.CompletedUtc
            """;
        command.Parameters.AddWithValue("$channel", normalizedChannel);
        command.Parameters.AddWithValue("$connection", connectionId);
        command.Parameters.AddWithValue("$shop", shopId);
        command.Parameters.AddWithValue("$completed", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// Applies a connection-test result only if the connection is still enabled
    /// and still at the exact revision the caller observed when the test started
    /// (a revision bumps on every SetEnabled/Save). A late-arriving result from a
    /// probe started before a disable/reconfigure is rejected as Stale/Disabled
    /// instead of silently reviving or overwriting the current state - so a
    /// deactivated shop can never be flipped back to CONNECTED by a test that was
    /// already in flight when it was disabled.
    public ConnectionTestApplyResult RecordTest(string id, long expectedRevision, bool success, string? error = null)
    {
        var safeError = success ? "" : Redact(error ?? "Bağlantı testi başarısız.");
        var blocked = !success && safeError.Contains("LIVE_API_BLOCKED", StringComparison.OrdinalIgnoreCase);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE MarketplaceConnections SET Status=$status,LastTestUtc=$tested,LastError=$error WHERE Id=$id AND Enabled=1 AND Revision=$revision";
        command.Parameters.AddWithValue("$status", success ? "CONNECTED_READ_ONLY" : blocked ? "LIVE_API_BLOCKED" : "FAILED");
        command.Parameters.AddWithValue("$tested", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$error", safeError);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$revision", expectedRevision);
        if (command.ExecuteNonQuery() == 1) return ConnectionTestApplyResult.Applied;
        var current = Get(id);
        if (current is null) return ConnectionTestApplyResult.NotFound;
        return current.Enabled ? ConnectionTestApplyResult.Stale : ConnectionTestApplyResult.Disabled;
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

    static bool TryRead(SqliteDataReader reader, out MarketplaceConnection? row, out CorruptMarketplaceConnection? corrupt)
    {
        row = null; corrupt = null; var id = reader.GetString(0); var channel = reader.GetString(1); var shop = reader.GetString(2);
        DateTime? lastTest = null;
        if (!reader.IsDBNull(6)) { if (!TryParseUtc(reader.GetString(6), out var value)) { corrupt = new(id, channel, shop, "Malformed LastTestUtc timestamp", DateTime.UtcNow); return false; } lastTest = value; }
        row = new(id, channel, shop, reader.GetString(3), reader.GetInt32(4) == 1, reader.GetString(5), lastTest, reader.GetString(7), reader.GetInt64(8));
        return true;
    }

    /// Only ever a format/parse failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised by the surrounding command, not this parse.
    static bool TryParseUtc(string value, out DateTime result) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);

    static string ValidateLabel(string value, string message, string parameter)
    {
        if (value is null) throw new ArgumentNullException(parameter);
        value = value.Trim();
        if (value.Length is < 1 or > 160 || value.Any(char.IsControl)) throw new ArgumentException(message, parameter);
        return value;
    }

    internal static string Redact(string value)
    {
        var safe = Regex.Replace(value ?? "", "(?i)\\bAuthorization\\s*:\\s*(?:Bearer|Basic)\\s+[^\\s,;&]+", "Authorization: [redacted]");
        safe = Regex.Replace(safe, "(?i)([?&](?:access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|password|passwd|secret|token)=)[^&#\\s]+", "$1[redacted]");
        safe = safe.Replace("access_token", "[redacted]", StringComparison.OrdinalIgnoreCase)
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
