using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Hepsiburada;

public sealed class HepsiburadaWorkspaceStore
{
    readonly string connectionString;

    public HepsiburadaWorkspaceStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        _ = new CatalogStore(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS HepsiburadaWorkspaceState(
                ConnectionId TEXT PRIMARY KEY,
                ShopId TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                RefreshedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS HepsiburadaRemoteProducts(
                ConnectionId TEXT NOT NULL,
                RemoteId TEXT NOT NULL,
                Json TEXT NOT NULL,
                PRIMARY KEY(ConnectionId,RemoteId)
            );
            CREATE TABLE IF NOT EXISTS HepsiburadaManualMatches(
                ConnectionId TEXT NOT NULL,
                ProductId TEXT NOT NULL,
                RemoteId TEXT NOT NULL,
                SnapshotRevision INTEGER NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY(ConnectionId,ProductId),
                UNIQUE(ConnectionId,RemoteId)
            );
            """;
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    public long ReplaceProducts(string connectionId, string shopId, IReadOnlyList<HepsiburadaMerchantProduct> products)
    {
        connectionId = Required(connectionId, nameof(connectionId));
        shopId = Required(shopId, nameof(shopId));
        ArgumentNullException.ThrowIfNull(products);
        if (products.Any(row => row.MerchantId.Length > 0 && !string.Equals(row.MerchantId, shopId, StringComparison.Ordinal)) ||
            products.Any(row => string.IsNullOrWhiteSpace(row.HepsiburadaSku)) ||
            products.GroupBy(row => row.HepsiburadaSku, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Hepsiburada ürün snapshot'ı başka mağazaya ait veya tekrar eden kimlik içeriyor.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        long revision;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT Revision FROM HepsiburadaWorkspaceState WHERE ConnectionId=$connection";
            find.Parameters.AddWithValue("$connection", connectionId);
            revision = (find.ExecuteScalar() is long current ? current : 0) + 1;
        }
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM HepsiburadaRemoteProducts WHERE ConnectionId=$connection";
            delete.Parameters.AddWithValue("$connection", connectionId);
            delete.ExecuteNonQuery();
        }
        foreach (var product in products)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO HepsiburadaRemoteProducts(ConnectionId,RemoteId,Json) VALUES($connection,$remote,$json)";
            insert.Parameters.AddWithValue("$connection", connectionId);
            insert.Parameters.AddWithValue("$remote", product.HepsiburadaSku);
            insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(product));
            insert.ExecuteNonQuery();
        }
        using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO HepsiburadaWorkspaceState(ConnectionId,ShopId,Revision,RefreshedUtc)
                VALUES($connection,$shop,$revision,$utc)
                ON CONFLICT(ConnectionId) DO UPDATE SET ShopId=excluded.ShopId,Revision=excluded.Revision,RefreshedUtc=excluded.RefreshedUtc
                """;
            state.Parameters.AddWithValue("$connection", connectionId);
            state.Parameters.AddWithValue("$shop", shopId);
            state.Parameters.AddWithValue("$revision", revision);
            state.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            state.ExecuteNonQuery();
        }
        transaction.Commit();
        return revision;
    }

    public HepsiburadaWorkspaceState Read(string connectionId)
    {
        connectionId = Required(connectionId, nameof(connectionId));
        using var connection = Open();
        string shopId = ""; long revision = 0; DateTime refreshed = default;
        using (var state = connection.CreateCommand())
        {
            state.CommandText = "SELECT ShopId,Revision,RefreshedUtc FROM HepsiburadaWorkspaceState WHERE ConnectionId=$connection";
            state.Parameters.AddWithValue("$connection", connectionId);
            using var reader = state.ExecuteReader();
            if (reader.Read())
            {
                shopId = reader.GetString(0); revision = reader.GetInt64(1);
                refreshed = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }
        }
        var products = new List<HepsiburadaMerchantProduct>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Json FROM HepsiburadaRemoteProducts WHERE ConnectionId=$connection ORDER BY RemoteId";
            command.Parameters.AddWithValue("$connection", connectionId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) products.Add(JsonSerializer.Deserialize<HepsiburadaMerchantProduct>(reader.GetString(0)) ?? throw new InvalidDataException("Hepsiburada önbelleği okunamadı."));
        }
        var matches = new List<HepsiburadaManualMatch>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ProductId,RemoteId,SnapshotRevision,UpdatedUtc FROM HepsiburadaManualMatches WHERE ConnectionId=$connection ORDER BY ProductId";
            command.Parameters.AddWithValue("$connection", connectionId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) matches.Add(new(connectionId, reader.GetString(0), reader.GetString(1), reader.GetInt64(2), DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }
        return new(connectionId, shopId, revision, refreshed, products.AsReadOnly(), matches.AsReadOnly());
    }

    public HepsiburadaManualMatch SaveManualMatch(string connectionId, string productId, string remoteId, long expectedSnapshotRevision)
    {
        connectionId = Required(connectionId, nameof(connectionId)); productId = Required(productId, nameof(productId)); remoteId = Required(remoteId, nameof(remoteId));
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var check = connection.CreateCommand(); check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM HepsiburadaRemoteProducts p JOIN HepsiburadaWorkspaceState s ON s.ConnectionId=p.ConnectionId WHERE p.ConnectionId=$connection AND p.RemoteId=$remote AND s.Revision=$revision";
        check.Parameters.AddWithValue("$connection", connectionId); check.Parameters.AddWithValue("$remote", remoteId); check.Parameters.AddWithValue("$revision", expectedSnapshotRevision);
        if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("Hepsiburada ürün listesi değişti veya uzak ürün bu mağazada yok; yeniden önizleyin.");
        var updated = DateTime.UtcNow;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO HepsiburadaManualMatches(ConnectionId,ProductId,RemoteId,SnapshotRevision,UpdatedUtc) VALUES($connection,$product,$remote,$revision,$utc) ON CONFLICT(ConnectionId,ProductId) DO UPDATE SET RemoteId=excluded.RemoteId,SnapshotRevision=excluded.SnapshotRevision,UpdatedUtc=excluded.UpdatedUtc";
        command.Parameters.AddWithValue("$connection", connectionId); command.Parameters.AddWithValue("$product", productId); command.Parameters.AddWithValue("$remote", remoteId); command.Parameters.AddWithValue("$revision", expectedSnapshotRevision); command.Parameters.AddWithValue("$utc", updated.ToString("O", CultureInfo.InvariantCulture));
        try { command.ExecuteNonQuery(); } catch (SqliteException error) when (error.SqliteErrorCode == 19) { throw new InvalidOperationException("Uzak Hepsiburada ürünü bu mağazada başka ürüne bağlı."); }
        transaction.Commit();
        return new(connectionId, productId, remoteId, expectedSnapshotRevision, updated);
    }

    static string Required(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 512 || value.Any(char.IsControl)) throw new ArgumentException("Kimlik geçersiz.", parameter);
        return value;
    }
}
