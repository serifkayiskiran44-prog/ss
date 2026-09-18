using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public enum InventoryLocationKind { Online, PhysicalStore }

public sealed record InventoryLocation(string Id, string Name, InventoryLocationKind Kind, bool Enabled, long Version);

public sealed record InventoryBalance(string ProductId, string LocationId, int Quantity, long Version);

public sealed record InventoryTransferPreview(string Id, string ProductId, string FromLocationId,
    string ToLocationId, int Quantity, long FromVersion, long ToVersion, DateTime CreatedUtc);

public enum InventoryMovementKind { TransferOut, TransferIn, OnlineOrder, ManualSale, OrderRestock, BalanceAdjustment }

public sealed record InventoryMovement(string Id, string ProductId, string LocationId, int QuantityBefore,
    int QuantityAfter, InventoryMovementKind Kind, string ReferenceId, DateTime CreatedUtc);

public sealed record InventoryApplyResult(bool AlreadyApplied, string ReceiptId, DateTime AppliedUtc,
    IReadOnlyList<InventoryMovement> Movements);

internal static class InventoryLedger
{
    internal const string OnlineLocationId = "online";
    const string InitialMigrationId = "inventory-online-balances-v1";

    internal static void Initialize(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS InventoryLocations(
                    Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Kind INTEGER NOT NULL,Enabled INTEGER NOT NULL,Version INTEGER NOT NULL CHECK(Version>=1));
                CREATE TABLE IF NOT EXISTS InventoryBalances(
                    ProductId TEXT NOT NULL,LocationId TEXT NOT NULL,Quantity INTEGER NOT NULL CHECK(Quantity BETWEEN 0 AND 2147483647),Version INTEGER NOT NULL CHECK(Version>=1),PRIMARY KEY(ProductId,LocationId));
                CREATE TABLE IF NOT EXISTS InventoryMovements(
                    Id TEXT PRIMARY KEY,ProductId TEXT NOT NULL,LocationId TEXT NOT NULL,QuantityBefore INTEGER NOT NULL CHECK(QuantityBefore BETWEEN 0 AND 2147483647),QuantityAfter INTEGER NOT NULL CHECK(QuantityAfter BETWEEN 0 AND 2147483647),Kind INTEGER NOT NULL,ReferenceId TEXT NOT NULL,CreatedUtc TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_InventoryMovements_Product ON InventoryMovements(ProductId,CreatedUtc,Id);
                CREATE UNIQUE INDEX IF NOT EXISTS UX_InventoryMovements_Reference ON InventoryMovements(Kind,ReferenceId,ProductId,LocationId);
                CREATE TABLE IF NOT EXISTS InventoryTransferPreviews(
                    Id TEXT PRIMARY KEY,ProductId TEXT NOT NULL,FromLocationId TEXT NOT NULL,ToLocationId TEXT NOT NULL,Quantity INTEGER NOT NULL CHECK(Quantity>0),FromVersion INTEGER NOT NULL CHECK(FromVersion>=0),ToVersion INTEGER NOT NULL CHECK(ToVersion>=0),CreatedUtc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS InventoryTransferReceipts(PreviewId TEXT PRIMARY KEY,AppliedUtc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS InventoryOrderReceipts(
                    Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,OrderId TEXT NOT NULL,Payload TEXT NOT NULL,AppliedUtc TEXT NOT NULL,PRIMARY KEY(Marketplace,ShopId,OrderId));
                CREATE TABLE IF NOT EXISTS InventoryManualSaleReceipts(
                    ReceiptId TEXT PRIMARY KEY,ProductId TEXT NOT NULL,LocationId TEXT NOT NULL,Quantity INTEGER NOT NULL CHECK(Quantity>0),AppliedUtc TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS InventoryMigrations(Id TEXT PRIMARY KEY,AppliedUtc TEXT NOT NULL);
                CREATE TRIGGER IF NOT EXISTS InventoryTransferPreviews_NoUpdate BEFORE UPDATE ON InventoryTransferPreviews BEGIN SELECT RAISE(ABORT,'immutable inventory transfer preview'); END;
                CREATE TRIGGER IF NOT EXISTS InventoryTransferPreviews_NoDelete BEFORE DELETE ON InventoryTransferPreviews BEGIN SELECT RAISE(ABORT,'immutable inventory transfer preview'); END;
                CREATE TRIGGER IF NOT EXISTS InventoryTransferReceipts_NoUpdate BEFORE UPDATE ON InventoryTransferReceipts BEGIN SELECT RAISE(ABORT,'immutable inventory transfer receipt'); END;
                CREATE TRIGGER IF NOT EXISTS InventoryTransferReceipts_NoDelete BEFORE DELETE ON InventoryTransferReceipts BEGIN SELECT RAISE(ABORT,'immutable inventory transfer receipt'); END;
                CREATE TRIGGER IF NOT EXISTS InventoryOrderReceipts_NoUpdate BEFORE UPDATE ON InventoryOrderReceipts BEGIN SELECT RAISE(ABORT,'immutable inventory order receipt'); END;
                CREATE TRIGGER IF NOT EXISTS InventoryOrderReceipts_NoDelete BEFORE DELETE ON InventoryOrderReceipts BEGIN SELECT RAISE(ABORT,'immutable inventory order receipt'); END;
                CREATE TRIGGER IF NOT EXISTS InventoryManualSaleReceipts_NoUpdate BEFORE UPDATE ON InventoryManualSaleReceipts BEGIN SELECT RAISE(ABORT,'immutable inventory manual-sale receipt'); END;
                CREATE TRIGGER IF NOT EXISTS InventoryManualSaleReceipts_NoDelete BEFORE DELETE ON InventoryManualSaleReceipts BEGIN SELECT RAISE(ABORT,'immutable inventory manual-sale receipt'); END;
                """;
            command.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction(deferred: false);
        using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = "INSERT OR IGNORE INTO InventoryLocations(Id,Name,Kind,Enabled,Version) VALUES($id,$name,$kind,1,1)";
            seed.Parameters.AddWithValue("$id", OnlineLocationId);
            seed.Parameters.AddWithValue("$name", "Online");
            seed.Parameters.AddWithValue("$kind", (int)InventoryLocationKind.Online);
            seed.ExecuteNonQuery();
        }

        using var marker = connection.CreateCommand();
        marker.Transaction = transaction;
        marker.CommandText = "SELECT 1 FROM InventoryMigrations WHERE Id=$id";
        marker.Parameters.AddWithValue("$id", InitialMigrationId);
        if (marker.ExecuteScalar() is null)
        {
            using var products = connection.CreateCommand();
            products.Transaction = transaction;
            products.CommandText = "SELECT Id,Json FROM CatalogProducts";
            using var reader = products.ExecuteReader();
            var balances = new List<(string ProductId, int Quantity)>();
            while (reader.Read())
            {
                var rowId = reader.GetString(0);
                var json = reader.GetString(1);
                if (json.Length > CatalogStore.MaxProductJsonBytes)
                    throw MigrationReviewRequired(rowId, "ürün kaydı boyut sınırını aşıyor");
                CatalogProduct? product;
                try
                {
                    product = JsonSerializer.Deserialize<CatalogProduct>(json);
                }
                catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or NotSupportedException)
                {
                    throw MigrationReviewRequired(rowId, "ürün kaydı okunamıyor");
                }
                if (product is null) throw MigrationReviewRequired(rowId, "ürün kaydı boş");
                if (string.IsNullOrWhiteSpace(rowId) || rowId.Length > 200 || rowId.Any(char.IsControl))
                    throw MigrationReviewRequired(rowId, "satır kimliği envanter için geçersiz");
                if (!string.Equals(product.Id, rowId, StringComparison.Ordinal))
                    throw MigrationReviewRequired(rowId, "satır kimliği ile ürün kimliği uyuşmuyor");
                if (product.Stock < 0) throw MigrationReviewRequired(rowId, "stok negatif");
                balances.Add((product.Id, product.Stock));
            }
            reader.Close();
            foreach (var balance in balances)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT OR IGNORE INTO InventoryBalances(ProductId,LocationId,Quantity,Version) VALUES($product,$location,$quantity,1)";
                insert.Parameters.AddWithValue("$product", balance.ProductId);
                insert.Parameters.AddWithValue("$location", OnlineLocationId);
                insert.Parameters.AddWithValue("$quantity", balance.Quantity);
                insert.ExecuteNonQuery();
            }
            using var complete = connection.CreateCommand();
            complete.Transaction = transaction;
            complete.CommandText = "INSERT INTO InventoryMigrations(Id,AppliedUtc) VALUES($id,$at)";
            complete.Parameters.AddWithValue("$id", InitialMigrationId);
            complete.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            complete.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    static InvalidOperationException MigrationReviewRequired(string rowId, string reason)
    {
        var safeId = string.Concat((rowId ?? "").Take(80).Select(character => char.IsControl(character) ? '?' : character));
        if (safeId.Length == 0) safeId = "<empty>";
        return new InvalidOperationException($"INVENTORY_MIGRATION_REVIEW_REQUIRED: katalog satırı '{safeId}' envantere taşınamadı ({reason}); katalog onarılmadan göç tamamlanmadı.");
    }

    internal static void DeleteCatalogProduct(SqliteConnection connection, SqliteTransaction transaction, string productId)
    {
        using (var stock = connection.CreateCommand())
        {
            stock.Transaction = transaction;
            stock.CommandText = "SELECT 1 FROM InventoryBalances WHERE ProductId=$product AND Quantity<>0 LIMIT 1";
            stock.Parameters.AddWithValue("$product", productId);
            if (stock.ExecuteScalar() is not null)
                throw new InvalidOperationException("Ürün silinemez: çevrimiçi veya fiziksel konumlarda stok var. Önce tüm envanter bakiyelerini sıfırlayın.");
        }
        using (var balances = connection.CreateCommand())
        {
            balances.Transaction = transaction;
            balances.CommandText = "DELETE FROM InventoryBalances WHERE ProductId=$product";
            balances.Parameters.AddWithValue("$product", productId);
            balances.ExecuteNonQuery();
        }
        using var product = connection.CreateCommand();
        product.Transaction = transaction;
        product.CommandText = "DELETE FROM CatalogProducts WHERE Id=$product";
        product.Parameters.AddWithValue("$product", productId);
        product.ExecuteNonQuery();
    }

    internal static void SyncOnlineBalance(SqliteConnection connection, SqliteTransaction? transaction, CatalogProduct product)
    {
        if (product.Stock < 0) throw new InvalidOperationException("Çevrimiçi stok negatif olamaz.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO InventoryBalances(ProductId,LocationId,Quantity,Version) VALUES($product,$location,$quantity,1)
            ON CONFLICT(ProductId,LocationId) DO UPDATE SET Quantity=excluded.Quantity,Version=InventoryBalances.Version+1
            WHERE InventoryBalances.Quantity<>excluded.Quantity
            """;
        command.Parameters.AddWithValue("$product", product.Id);
        command.Parameters.AddWithValue("$location", OnlineLocationId);
        command.Parameters.AddWithValue("$quantity", product.Stock);
        command.ExecuteNonQuery();
    }

    internal static InventoryBalance ReadBalance(SqliteConnection connection, SqliteTransaction? transaction, string productId, string locationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Quantity,Version FROM InventoryBalances WHERE ProductId=$product AND LocationId=$location";
        command.Parameters.AddWithValue("$product", productId);
        command.Parameters.AddWithValue("$location", locationId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(productId, locationId, checked((int)reader.GetInt64(0)), reader.GetInt64(1))
            : new(productId, locationId, 0, 0);
    }

    internal static InventoryBalance SetBalance(SqliteConnection connection, SqliteTransaction transaction, string productId,
        string locationId, int quantity, long expectedVersion)
    {
        if (quantity < 0 || expectedVersion < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (expectedVersion == 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO InventoryBalances(ProductId,LocationId,Quantity,Version) VALUES($product,$location,$quantity,1)";
            insert.Parameters.AddWithValue("$product", productId);
            insert.Parameters.AddWithValue("$location", locationId);
            insert.Parameters.AddWithValue("$quantity", quantity);
            try { insert.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("Envanter bakiyesi değişti; yenileyip tekrar deneyin.", ex); }
            return new(productId, locationId, quantity, 1);
        }
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE InventoryBalances SET Quantity=$quantity,Version=Version+1 WHERE ProductId=$product AND LocationId=$location AND Version=$version";
        update.Parameters.AddWithValue("$quantity", quantity);
        update.Parameters.AddWithValue("$product", productId);
        update.Parameters.AddWithValue("$location", locationId);
        update.Parameters.AddWithValue("$version", expectedVersion);
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Envanter bakiyesi değişti; yenileyip tekrar deneyin.");
        return new(productId, locationId, quantity, checked(expectedVersion + 1));
    }

    internal static InventoryMovement RecordMovement(SqliteConnection connection, SqliteTransaction transaction, string productId,
        string locationId, int before, int after, InventoryMovementKind kind, string referenceId, DateTime createdUtc)
    {
        var movement = new InventoryMovement(Guid.NewGuid().ToString("N"), productId, locationId, before, after, kind, referenceId, createdUtc);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO InventoryMovements(Id,ProductId,LocationId,QuantityBefore,QuantityAfter,Kind,ReferenceId,CreatedUtc) VALUES($id,$product,$location,$before,$after,$kind,$reference,$at)";
        command.Parameters.AddWithValue("$id", movement.Id);
        command.Parameters.AddWithValue("$product", movement.ProductId);
        command.Parameters.AddWithValue("$location", movement.LocationId);
        command.Parameters.AddWithValue("$before", movement.QuantityBefore);
        command.Parameters.AddWithValue("$after", movement.QuantityAfter);
        command.Parameters.AddWithValue("$kind", (int)movement.Kind);
        command.Parameters.AddWithValue("$reference", movement.ReferenceId);
        command.Parameters.AddWithValue("$at", movement.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return movement;
    }

    internal static IReadOnlyList<InventoryMovement> ReadMovements(SqliteConnection connection, string? productId = null,
        InventoryMovementKind? kind = null, string? referenceId = null)
    {
        using var command = connection.CreateCommand();
        var where = new List<string>();
        if (productId is not null) { where.Add("ProductId=$product"); command.Parameters.AddWithValue("$product", productId); }
        if (kind.HasValue) { where.Add("Kind=$kind"); command.Parameters.AddWithValue("$kind", (int)kind.Value); }
        if (referenceId is not null) { where.Add("ReferenceId=$reference"); command.Parameters.AddWithValue("$reference", referenceId); }
        command.CommandText = "SELECT Id,ProductId,LocationId,QuantityBefore,QuantityAfter,Kind,ReferenceId,CreatedUtc FROM InventoryMovements" +
            (where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where)) + " ORDER BY CreatedUtc,Id";
        using var reader = command.ExecuteReader();
        var result = new List<InventoryMovement>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), checked((int)reader.GetInt64(3)),
            checked((int)reader.GetInt64(4)), (InventoryMovementKind)reader.GetInt32(5), reader.GetString(6),
            DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        return result;
    }

    internal static string OrderReference(string marketplace, string shopId, string orderId) =>
        JsonSerializer.Serialize(new[] { marketplace, shopId, orderId });
}
