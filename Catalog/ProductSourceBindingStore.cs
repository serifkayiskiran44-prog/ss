using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed class ProductSourceBindingStore
{
    readonly string connectionString;

    public ProductSourceBindingStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "catalog.db"),
            DefaultTimeout = 15,
            Pooling = true
        }.ToString();
        using var connection = Open();
        CatalogStore.InitializeProductSourceBindings(connection);
    }

    SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public IReadOnlyList<ProductSourceBinding> Get(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("Ürün kimliği gerekli.", nameof(productId));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ProductId,FieldGroup,SourceKind,SourceId,Enabled,Version,UpdatedUtc FROM ProductSourceBindings WHERE ProductId=$product ORDER BY FieldGroup";
        command.Parameters.AddWithValue("$product", productId.Trim());
        using var reader = command.ExecuteReader();
        var result = new List<ProductSourceBinding>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    public ProductSourceBinding Save(ProductSourceBinding binding, long expectedVersion)
    {
        if (binding is null) throw new ArgumentNullException(nameof(binding));
        if (expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        var productId = binding.ProductId.Trim();
        if (productId.Length == 0 || productId.Length > 128 || productId.Any(char.IsControl)) throw new ArgumentException("Ürün kimliği geçersiz.", nameof(binding));
        if (!Enum.IsDefined(binding.Group) || !Enum.IsDefined(binding.Kind)) throw new ArgumentException("Kaynak grubu veya türü geçersiz.", nameof(binding));
        var sourceId = binding.SourceId.Trim();
        if (binding.Kind == ProductSourceKind.Manual)
        {
            if (sourceId.Length > 0) throw new InvalidOperationException("Elle yönetilen kaynak bir XML kimliği taşıyamaz.");
        }
        else if (sourceId.Length == 0 || sourceId.Length > 128 || sourceId.Any(char.IsControl))
        {
            throw new InvalidOperationException("XML kaynak kimliği geçersiz.");
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        RequireProduct(connection, transaction, productId);
        if (binding.Kind == ProductSourceKind.Xml) RequireXmlSource(connection, transaction, sourceId);

        long currentVersion;
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT Version FROM ProductSourceBindings WHERE ProductId=$product AND FieldGroup=$group";
            current.Parameters.AddWithValue("$product", productId);
            current.Parameters.AddWithValue("$group", (int)binding.Group);
            currentVersion = current.ExecuteScalar() is long version ? version : 0;
        }
        if (currentVersion != expectedVersion) throw new InvalidOperationException("Kaynak seçimi başka bir işlemde değişti. Yenileyip tekrar deneyin.");

        var saved = binding with
        {
            ProductId = productId,
            SourceId = binding.Kind == ProductSourceKind.Manual ? "" : sourceId,
            Version = checked(currentVersion + 1),
            UpdatedUtc = DateTime.UtcNow
        };
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ProductSourceBindings(ProductId,FieldGroup,SourceKind,SourceId,Enabled,Version,UpdatedUtc)
            VALUES($product,$group,$kind,$source,$enabled,$version,$updated)
            ON CONFLICT(ProductId,FieldGroup) DO UPDATE SET
                SourceKind=excluded.SourceKind,
                SourceId=excluded.SourceId,
                Enabled=excluded.Enabled,
                Version=excluded.Version,
                UpdatedUtc=excluded.UpdatedUtc
            WHERE ProductSourceBindings.Version=$expected
            """;
        command.Parameters.AddWithValue("$product", saved.ProductId);
        command.Parameters.AddWithValue("$group", (int)saved.Group);
        command.Parameters.AddWithValue("$kind", (int)saved.Kind);
        command.Parameters.AddWithValue("$source", saved.SourceId);
        command.Parameters.AddWithValue("$enabled", saved.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$version", saved.Version);
        command.Parameters.AddWithValue("$updated", saved.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expected", expectedVersion);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Kaynak seçimi başka bir işlemde değişti. Yenileyip tekrar deneyin.");
        transaction.Commit();
        return saved;
    }

    public int MigrateFromCatalog()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var sourceIds = SourceIds(connection, transaction);
        var products = Products(connection, transaction);
        var inserted = 0;
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var product in products)
        {
            var validXmlId = sourceIds.Contains(product.SourceId) ? product.SourceId : "";
            var candidates = new[]
            {
                Migrated(product, ProductFieldGroup.Content,
                    IsXml(product.SourceKind) || IsXml(product.MediaSource), validXmlId),
                Migrated(product, ProductFieldGroup.Price, IsXml(product.PriceSource), validXmlId),
                Migrated(product, ProductFieldGroup.OnlineStock, IsXml(product.StockSource), validXmlId)
            };
            foreach (var candidate in candidates)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR IGNORE INTO ProductSourceBindings(ProductId,FieldGroup,SourceKind,SourceId,Enabled,Version,UpdatedUtc) VALUES($product,$group,$kind,$source,1,1,$updated)";
                command.Parameters.AddWithValue("$product", candidate.ProductId);
                command.Parameters.AddWithValue("$group", (int)candidate.Group);
                command.Parameters.AddWithValue("$kind", (int)candidate.Kind);
                command.Parameters.AddWithValue("$source", candidate.SourceId);
                command.Parameters.AddWithValue("$updated", now);
                inserted += command.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        return inserted;
    }

    static ProductSourceBinding Migrated(CatalogProduct product, ProductFieldGroup group, bool legacyXml, string validXmlId)
    {
        var useXml = legacyXml && validXmlId.Length > 0;
        return new(product.Id, group, useXml ? ProductSourceKind.Xml : ProductSourceKind.Manual, useXml ? validXmlId : "", true, 1, DateTime.MinValue);
    }

    static bool IsXml(string value) => string.Equals(value, "xml", StringComparison.OrdinalIgnoreCase);

    static void RequireProduct(SqliteConnection connection, SqliteTransaction transaction, string productId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM CatalogProducts WHERE Id=$id AND json_valid(Json)=1";
        command.Parameters.AddWithValue("$id", productId);
        if (command.ExecuteScalar() is null) throw new InvalidOperationException("Ürün bulunamadı veya ürün kaydı bozuk.");
    }

    static void RequireXmlSource(SqliteConnection connection, SqliteTransaction transaction, string sourceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Json FROM Sources WHERE Id=$id";
        command.Parameters.AddWithValue("$id", sourceId);
        if (command.ExecuteScalar() is not string json) throw new InvalidOperationException("XML kaynağı bulunamadı.");
        var source = JsonSerializer.Deserialize<XmlSource>(json);
        if (source is null || !string.Equals(source.Id, sourceId, StringComparison.Ordinal)) throw new InvalidOperationException("XML kaynak kaydı geçersiz.");
    }

    static HashSet<string> SourceIds(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id,Json FROM Sources";
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var id = reader.GetString(0);
            try
            {
                var source = JsonSerializer.Deserialize<XmlSource>(reader.GetString(1));
                if (source is not null && string.Equals(source.Id, id, StringComparison.Ordinal)) result.Add(id);
            }
            catch (JsonException) { }
        }
        return result;
    }

    static IReadOnlyList<CatalogProduct> Products(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id,Json FROM CatalogProducts WHERE json_valid(Json)=1";
        using var reader = command.ExecuteReader();
        var result = new List<CatalogProduct>();
        while (reader.Read())
        {
            try
            {
                var product = JsonSerializer.Deserialize<CatalogProduct>(reader.GetString(1));
                if (product is not null && string.Equals(product.Id, reader.GetString(0), StringComparison.Ordinal)) result.Add(product);
            }
            catch (JsonException) { }
        }
        return result;
    }

    static ProductSourceBinding Read(SqliteDataReader reader)
    {
        if (!Enum.IsDefined(typeof(ProductFieldGroup), reader.GetInt32(1)) || !Enum.IsDefined(typeof(ProductSourceKind), reader.GetInt32(2)))
            throw new InvalidOperationException("Ürün kaynak bağlama kaydı geçersiz.");
        if (!DateTime.TryParse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updated))
            throw new InvalidOperationException("Ürün kaynak bağlama zamanı geçersiz.");
        return new(reader.GetString(0), (ProductFieldGroup)reader.GetInt32(1), (ProductSourceKind)reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4) != 0, reader.GetInt64(5), updated);
    }
}

public partial class CatalogStore
{
    internal static void InitializeProductSourceBindings(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ProductSourceBindings(
                ProductId TEXT NOT NULL,
                FieldGroup INTEGER NOT NULL,
                SourceKind INTEGER NOT NULL,
                SourceId TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                Version INTEGER NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY(ProductId,FieldGroup))
            """;
        command.ExecuteNonQuery();
    }
}
