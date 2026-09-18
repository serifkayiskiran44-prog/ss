using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed class InventoryLocationStore
{
    readonly string directory;
    readonly string connectionString;
    public const string OnlineLocationId = InventoryLedger.OnlineLocationId;

    public InventoryLocationStore(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        _ = new CatalogStore(this.directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(this.directory, "catalog.db"), DefaultTimeout = 15, Pooling = true }.ToString();
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    static void ValidateId(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
            throw new ArgumentException("Kimlik boş, 200 karakterden uzun veya kontrol karakterli olamaz.", paramName);
    }

    public IReadOnlyList<InventoryLocation> Locations()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,Name,Kind,Enabled,Version FROM InventoryLocations ORDER BY Kind,Name,Id";
        using var reader = command.ExecuteReader(); var result = new List<InventoryLocation>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), (InventoryLocationKind)reader.GetInt32(2), reader.GetInt32(3) != 0, reader.GetInt64(4)));
        return result;
    }

    public InventoryLocation CreatePhysicalStore(string id, string name)
    {
        ValidateId(id, nameof(id));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new ArgumentException("Mağaza adı zorunlu ve en fazla 200 karakter olabilir.", nameof(name));
        id = id.Trim(); name = name.Trim();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO InventoryLocations(Id,Name,Kind,Enabled,Version) VALUES($id,$name,$kind,1,1)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$kind", (int)InventoryLocationKind.PhysicalStore);
        try { command.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("Bu envanter konumu zaten var.", ex); }
        return new(id, name, InventoryLocationKind.PhysicalStore, true, 1);
    }

    public InventoryBalance GetBalance(string productId, string locationId)
    {
        ValidateId(productId, nameof(productId)); ValidateId(locationId, nameof(locationId));
        using var connection = Open(); EnsureProductAndLocation(connection, null, productId, locationId, null);
        return InventoryLedger.ReadBalance(connection, null, productId, locationId);
    }

    public IReadOnlyList<InventoryBalance> Balances(string productId)
    {
        ValidateId(productId, nameof(productId));
        using var connection = Open(); EnsureProduct(connection, null, productId);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT LocationId,Quantity,Version FROM InventoryBalances WHERE ProductId=$product ORDER BY LocationId"; command.Parameters.AddWithValue("$product", productId);
        using var reader = command.ExecuteReader(); var result = new List<InventoryBalance>();
        while (reader.Read()) result.Add(new(productId, reader.GetString(0), checked((int)reader.GetInt64(1)), reader.GetInt64(2)));
        return result;
    }

    public IReadOnlyList<InventoryMovement> Movements(string? productId = null)
    {
        if (productId is not null) ValidateId(productId, nameof(productId));
        using var connection = Open(); return InventoryLedger.ReadMovements(connection, productId);
    }

    public InventoryBalance SetBalance(string productId, string locationId, int quantity, long expectedVersion)
    {
        ValidateId(productId, nameof(productId)); ValidateId(locationId, nameof(locationId));
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var location = EnsureProductAndLocation(connection, transaction, productId, locationId, null);
        var current = InventoryLedger.ReadBalance(connection, transaction, productId, locationId);
        if (current.Version != expectedVersion) throw new InvalidOperationException("Envanter bakiyesi değişti; yenileyip tekrar deneyin.");
        var next = InventoryLedger.SetBalance(connection, transaction, productId, locationId, quantity, expectedVersion);
        var at = DateTime.UtcNow;
        InventoryLedger.RecordMovement(connection, transaction, productId, locationId, current.Quantity, quantity, InventoryMovementKind.BalanceAdjustment, Guid.NewGuid().ToString("N"), at);
        if (location.Kind == InventoryLocationKind.Online) MirrorOnlineStock(connection, transaction, productId, quantity, at);
        transaction.Commit(); return next;
    }

    public InventoryTransferPreview PreviewTransfer(string productId, string fromLocationId, string toLocationId, int quantity)
    {
        ValidateId(productId, nameof(productId)); ValidateId(fromLocationId, nameof(fromLocationId)); ValidateId(toLocationId, nameof(toLocationId));
        if (fromLocationId == toLocationId) throw new ArgumentException("Kaynak ve hedef envanter konumu farklı olmalı.");
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: true);
        EnsureProductAndLocation(connection, transaction, productId, fromLocationId, null);
        EnsureProductAndLocation(connection, transaction, productId, toLocationId, null);
        var from = InventoryLedger.ReadBalance(connection, transaction, productId, fromLocationId);
        var to = InventoryLedger.ReadBalance(connection, transaction, productId, toLocationId);
        if (from.Quantity < quantity) throw new InvalidOperationException($"Kaynak konumda stok yetersiz ({from.Quantity}/{quantity}).");
        _ = checked(to.Quantity + quantity);
        var preview = new InventoryTransferPreview(Guid.NewGuid().ToString("N"), productId, fromLocationId, toLocationId, quantity, from.Version, to.Version, DateTime.UtcNow);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO InventoryTransferPreviews(Id,ProductId,FromLocationId,ToLocationId,Quantity,FromVersion,ToVersion,CreatedUtc) VALUES($id,$product,$from,$to,$quantity,$fromVersion,$toVersion,$at)";
        command.Parameters.AddWithValue("$id", preview.Id); command.Parameters.AddWithValue("$product", preview.ProductId); command.Parameters.AddWithValue("$from", preview.FromLocationId); command.Parameters.AddWithValue("$to", preview.ToLocationId); command.Parameters.AddWithValue("$quantity", preview.Quantity); command.Parameters.AddWithValue("$fromVersion", preview.FromVersion); command.Parameters.AddWithValue("$toVersion", preview.ToVersion); command.Parameters.AddWithValue("$at", preview.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery(); transaction.Commit(); return preview;
    }

    public InventoryApplyResult ApplyTransfer(InventoryTransferPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview); ValidateId(preview.Id, nameof(preview));
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var stored = ReadPreview(connection, transaction, preview.Id) ?? throw new InvalidOperationException("Transfer önizlemesi bulunamadı.");
        if (stored != preview) throw new InvalidOperationException("Transfer önizlemesi değiştirilemez; kayıtlı önizlemeyi yeniden yükleyin.");
        using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction; receipt.CommandText = "SELECT AppliedUtc FROM InventoryTransferReceipts WHERE PreviewId=$id"; receipt.Parameters.AddWithValue("$id", preview.Id);
            if (receipt.ExecuteScalar() is string applied)
            {
                transaction.Commit();
                var existing = InventoryLedger.ReadMovements(connection, preview.ProductId, null, preview.Id);
                return new(true, preview.Id, DateTime.Parse(applied, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), existing);
            }
        }
        var fromLocation = EnsureProductAndLocation(connection, transaction, preview.ProductId, preview.FromLocationId, null);
        var toLocation = EnsureProductAndLocation(connection, transaction, preview.ProductId, preview.ToLocationId, null);
        var from = InventoryLedger.ReadBalance(connection, transaction, preview.ProductId, preview.FromLocationId);
        var to = InventoryLedger.ReadBalance(connection, transaction, preview.ProductId, preview.ToLocationId);
        if (from.Version != preview.FromVersion || to.Version != preview.ToVersion) throw new InvalidOperationException("Transfer önizlemesinden sonra stok değişti; yeni önizleme alın.");
        if (from.Quantity < preview.Quantity) throw new InvalidOperationException("Kaynak konumda stok yetersiz.");
        var fromAfter = checked(from.Quantity - preview.Quantity); var toAfter = checked(to.Quantity + preview.Quantity);
        InventoryLedger.SetBalance(connection, transaction, preview.ProductId, preview.FromLocationId, fromAfter, from.Version);
        InventoryLedger.SetBalance(connection, transaction, preview.ProductId, preview.ToLocationId, toAfter, to.Version);
        var at = DateTime.UtcNow;
        var movements = new List<InventoryMovement>
        {
            InventoryLedger.RecordMovement(connection, transaction, preview.ProductId, preview.FromLocationId, from.Quantity, fromAfter, InventoryMovementKind.TransferOut, preview.Id, at),
            InventoryLedger.RecordMovement(connection, transaction, preview.ProductId, preview.ToLocationId, to.Quantity, toAfter, InventoryMovementKind.TransferIn, preview.Id, at)
        };
        if (fromLocation.Kind == InventoryLocationKind.Online) MirrorOnlineStock(connection, transaction, preview.ProductId, fromAfter, at);
        if (toLocation.Kind == InventoryLocationKind.Online) MirrorOnlineStock(connection, transaction, preview.ProductId, toAfter, at);
        using (var receipt = connection.CreateCommand()) { receipt.Transaction = transaction; receipt.CommandText = "INSERT INTO InventoryTransferReceipts(PreviewId,AppliedUtc) VALUES($id,$at)"; receipt.Parameters.AddWithValue("$id", preview.Id); receipt.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture)); receipt.ExecuteNonQuery(); }
        transaction.Commit(); return new(false, preview.Id, at, movements);
    }

    public InventoryApplyResult ApplyOrder(string marketplace, string shopId, string orderId, IReadOnlyList<OrderItem> items)
    {
        var result = new CatalogStore(directory).ApplyOrderStock(marketplace, shopId, orderId, items);
        var reference = InventoryLedger.OrderReference(marketplace, shopId, orderId);
        using var connection = Open();
        var movements = InventoryLedger.ReadMovements(connection, null, InventoryMovementKind.OnlineOrder, reference);
        return new(result.AlreadyApplied, reference, result.Receipt.AppliedUtc, movements);
    }

    public InventoryApplyResult ApplyManualSale(string receiptId, string productId, string locationId, int quantity)
    {
        ValidateId(receiptId, nameof(receiptId)); ValidateId(productId, nameof(productId)); ValidateId(locationId, nameof(locationId));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction; existing.CommandText = "SELECT ProductId,LocationId,Quantity,AppliedUtc FROM InventoryManualSaleReceipts WHERE ReceiptId=$id"; existing.Parameters.AddWithValue("$id", receiptId);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != productId || reader.GetString(1) != locationId || reader.GetInt64(2) != quantity) throw new InvalidOperationException("Bu manuel satış makbuzu daha önce farklı içerikle uygulandı.");
                var applied = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); reader.Close(); transaction.Commit();
                return new(true, receiptId, applied, InventoryLedger.ReadMovements(connection, productId, InventoryMovementKind.ManualSale, receiptId));
            }
        }
        var location = EnsureProductAndLocation(connection, transaction, productId, locationId, InventoryLocationKind.PhysicalStore);
        if (location.Kind != InventoryLocationKind.PhysicalStore) throw new InvalidOperationException("Manuel satış yalnız fiziksel mağaza stokundan düşülebilir.");
        var balance = InventoryLedger.ReadBalance(connection, transaction, productId, locationId);
        if (balance.Quantity < quantity) throw new InvalidOperationException($"Fiziksel mağaza stoku yetersiz ({balance.Quantity}/{quantity}).");
        var after = checked(balance.Quantity - quantity); InventoryLedger.SetBalance(connection, transaction, productId, locationId, after, balance.Version);
        var at = DateTime.UtcNow; var movement = InventoryLedger.RecordMovement(connection, transaction, productId, locationId, balance.Quantity, after, InventoryMovementKind.ManualSale, receiptId, at);
        using (var receipt = connection.CreateCommand()) { receipt.Transaction = transaction; receipt.CommandText = "INSERT INTO InventoryManualSaleReceipts(ReceiptId,ProductId,LocationId,Quantity,AppliedUtc) VALUES($id,$product,$location,$quantity,$at)"; receipt.Parameters.AddWithValue("$id", receiptId); receipt.Parameters.AddWithValue("$product", productId); receipt.Parameters.AddWithValue("$location", locationId); receipt.Parameters.AddWithValue("$quantity", quantity); receipt.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture)); receipt.ExecuteNonQuery(); }
        transaction.Commit(); return new(false, receiptId, at, new[] { movement });
    }

    static InventoryTransferPreview? ReadPreview(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT ProductId,FromLocationId,ToLocationId,Quantity,FromVersion,ToVersion,CreatedUtc FROM InventoryTransferPreviews WHERE Id=$id"; command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), checked((int)reader.GetInt64(3)), reader.GetInt64(4), reader.GetInt64(5), DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)) : null;
    }

    static void EnsureProduct(SqliteConnection connection, SqliteTransaction? transaction, string productId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT 1 FROM CatalogProducts WHERE Id=$id AND json_valid(Json)=1"; command.Parameters.AddWithValue("$id", productId);
        if (command.ExecuteScalar() is null) throw new InvalidOperationException("Merkezi ürün bulunamadı.");
    }

    static InventoryLocation EnsureProductAndLocation(SqliteConnection connection, SqliteTransaction? transaction, string productId, string locationId, InventoryLocationKind? requiredKind)
    {
        EnsureProduct(connection, transaction, productId);
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT Name,Kind,Enabled,Version FROM InventoryLocations WHERE Id=$id"; command.Parameters.AddWithValue("$id", locationId);
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw new InvalidOperationException("Envanter konumu bulunamadı.");
        var location = new InventoryLocation(locationId, reader.GetString(0), (InventoryLocationKind)reader.GetInt32(1), reader.GetInt32(2) != 0, reader.GetInt64(3));
        if (!location.Enabled) throw new InvalidOperationException("Envanter konumu pasif.");
        if (requiredKind.HasValue && location.Kind != requiredKind.Value) throw new InvalidOperationException("Envanter konumu türü bu işlem için uygun değil.");
        return location;
    }

    static void MirrorOnlineStock(SqliteConnection connection, SqliteTransaction transaction, string productId, int quantity, DateTime at)
    {
        using var find = connection.CreateCommand(); find.Transaction = transaction; find.CommandText = "SELECT Json FROM CatalogProducts WHERE Id=$id"; find.Parameters.AddWithValue("$id", productId);
        var product = find.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CatalogProduct>(json) : null;
        if (product is null) throw new InvalidOperationException("Merkezi ürün bulunamadı.");
        product.Stock = quantity; product.LockStock = true; product.UpdatedUtc = at; CatalogStore.Put(connection, "CatalogProducts", product.Id, product, transaction);
    }
}
