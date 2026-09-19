using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ManualSalePreview(
    string Id,
    string ReceiptId,
    string ProductId,
    string LocationId,
    int Quantity,
    int QuantityBefore,
    int QuantityAfter,
    long ProductGeneration,
    long LocationVersion,
    long BalanceVersion,
    DateTime CreatedUtc);

public sealed record ManualSaleReceipt(
    string PreviewId,
    string ReceiptId,
    string ProductId,
    string LocationId,
    int Quantity,
    DateTime AppliedUtc,
    bool AlreadyApplied);

/// <summary>Local-only physical sale flow. Preview and receipt rows are immutable.</summary>
public sealed class ManualSaleService
{
    readonly string directory;
    readonly string connectionString;
    readonly InventoryLocationStore inventory;

    public ManualSaleService(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        inventory = new(this.directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(this.directory, "catalog.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ManualSalePreviews(
                Id TEXT PRIMARY KEY,ReceiptId TEXT NOT NULL UNIQUE,ProductId TEXT NOT NULL,LocationId TEXT NOT NULL,
                Quantity INTEGER NOT NULL CHECK(Quantity>0),QuantityBefore INTEGER NOT NULL CHECK(QuantityBefore>=0),
                QuantityAfter INTEGER NOT NULL CHECK(QuantityAfter>=0),ProductGeneration INTEGER NOT NULL CHECK(ProductGeneration>=1),
                LocationVersion INTEGER NOT NULL CHECK(LocationVersion>=1),
                BalanceVersion INTEGER NOT NULL CHECK(BalanceVersion>=0),CreatedUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ManualSalePreviewReceipts(
                PreviewId TEXT PRIMARY KEY,ReceiptId TEXT NOT NULL UNIQUE,ProductId TEXT NOT NULL,LocationId TEXT NOT NULL,
                Quantity INTEGER NOT NULL CHECK(Quantity>0),AppliedUtc TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS ManualSalePreviews_NoUpdate BEFORE UPDATE ON ManualSalePreviews BEGIN SELECT RAISE(ABORT,'immutable manual sale preview'); END;
            CREATE TRIGGER IF NOT EXISTS ManualSalePreviews_NoDelete BEFORE DELETE ON ManualSalePreviews BEGIN SELECT RAISE(ABORT,'immutable manual sale preview'); END;
            CREATE TRIGGER IF NOT EXISTS ManualSalePreviewReceipts_NoUpdate BEFORE UPDATE ON ManualSalePreviewReceipts BEGIN SELECT RAISE(ABORT,'immutable manual sale receipt'); END;
            CREATE TRIGGER IF NOT EXISTS ManualSalePreviewReceipts_NoDelete BEFORE DELETE ON ManualSalePreviewReceipts BEGIN SELECT RAISE(ABORT,'immutable manual sale receipt'); END;
            """;
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    public ManualSalePreview Preview(string productId, string locationId, int quantity)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        var location = inventory.Locations().SingleOrDefault(item => item.Id == locationId)
            ?? throw new InvalidOperationException("Envanter konumu bulunamadı.");
        if (!location.Enabled || location.Kind != InventoryLocationKind.PhysicalStore)
            throw new InvalidOperationException("Manuel satış için etkin bir fiziksel mağaza seçin.");
        var balance = inventory.GetBalance(productId, locationId);
        if (balance.Quantity < quantity) throw new InvalidOperationException($"Fiziksel mağaza stoku yetersiz ({balance.Quantity}/{quantity}).");
        var generation = inventory.GetProductGeneration(productId);
        var preview = new ManualSalePreview(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), productId, locationId,
            quantity, balance.Quantity, checked(balance.Quantity - quantity), generation, location.Version, balance.Version, DateTime.UtcNow);
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ManualSalePreviews(Id,ReceiptId,ProductId,LocationId,Quantity,QuantityBefore,QuantityAfter,ProductGeneration,LocationVersion,BalanceVersion,CreatedUtc) VALUES($id,$receipt,$product,$location,$quantity,$before,$after,$generation,$locationVersion,$version,$at)";
        command.Parameters.AddWithValue("$id", preview.Id); command.Parameters.AddWithValue("$receipt", preview.ReceiptId);
        command.Parameters.AddWithValue("$product", preview.ProductId); command.Parameters.AddWithValue("$location", preview.LocationId);
        command.Parameters.AddWithValue("$quantity", preview.Quantity); command.Parameters.AddWithValue("$before", preview.QuantityBefore);
        command.Parameters.AddWithValue("$after", preview.QuantityAfter); command.Parameters.AddWithValue("$generation", preview.ProductGeneration);
        command.Parameters.AddWithValue("$locationVersion", preview.LocationVersion);
        command.Parameters.AddWithValue("$version", preview.BalanceVersion); command.Parameters.AddWithValue("$at", Format(preview.CreatedUtc));
        command.ExecuteNonQuery(); return preview;
    }

    public ManualSaleReceipt Apply(ManualSalePreview preview, bool approved)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!approved) throw new InvalidOperationException("Manuel mağaza satışı için açık onay gerekli.");
        ManualSaleReceipt? existingReceipt;
        using (var connection = Open())
        {
            var stored = ReadPreview(connection, preview.Id) ?? throw new InvalidOperationException("Manuel satış önizlemesi bulunamadı.");
            if (stored != preview) throw new InvalidOperationException("Manuel satış önizlemesi değiştirilemez; kayıtlı önizlemeyi yeniden yükleyin.");
            existingReceipt = ReadReceipt(connection, preview.Id);
        }
        if (existingReceipt is not null)
        {
            var duplicate = existingReceipt with { AlreadyApplied = true };
            PersistOrder(duplicate);
            return duplicate;
        }

        var applied = inventory.ApplyManualSale(preview.ReceiptId, preview.ProductId, preview.LocationId, preview.Quantity,
            preview.ProductGeneration, preview.LocationVersion, preview.BalanceVersion);
        ManualSaleReceipt receipt;
        using (var save = Open())
        using (var transaction = save.BeginTransaction(deferred: false))
        {
            var raced = ReadReceipt(save, preview.Id, transaction);
            if (raced is not null) receipt = raced with { AlreadyApplied = true };
            else
            {
                using var command = save.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO ManualSalePreviewReceipts(PreviewId,ReceiptId,ProductId,LocationId,Quantity,AppliedUtc) VALUES($preview,$receipt,$product,$location,$quantity,$at)";
                command.Parameters.AddWithValue("$preview", preview.Id); command.Parameters.AddWithValue("$receipt", preview.ReceiptId);
                command.Parameters.AddWithValue("$product", preview.ProductId); command.Parameters.AddWithValue("$location", preview.LocationId);
                command.Parameters.AddWithValue("$quantity", preview.Quantity); command.Parameters.AddWithValue("$at", Format(applied.AppliedUtc));
                command.ExecuteNonQuery();
                receipt = new(preview.Id, preview.ReceiptId, preview.ProductId, preview.LocationId, preview.Quantity, applied.AppliedUtc, applied.AlreadyApplied);
            }
            transaction.Commit();
        }
        PersistOrder(receipt);
        return receipt;
    }

    public ManualSaleReceipt? Receipt(string previewId)
    {
        using var connection = Open(); return ReadReceipt(connection, previewId);
    }

    void PersistOrder(ManualSaleReceipt receipt)
    {
        var product = new CatalogStore(directory).Products().SingleOrDefault(item => item.Id == receipt.ProductId);
        var location = inventory.Locations().SingleOrDefault(item => item.Id == receipt.LocationId);
        var at = new DateTimeOffset(DateTime.SpecifyKind(receipt.AppliedUtc, DateTimeKind.Utc));
        new OrdersStore(directory).SaveManual(new OrderSnapshot
        {
            Marketplace = "Yerel",
            ShopId = receipt.LocationId,
            ConnectionDisplayName = location?.Name ?? receipt.LocationId,
            OrderId = receipt.ReceiptId,
            RawStatus = "completed",
            PaymentStatus = "Ödendi",
            Source = "Yerel / manuel",
            IsPhysicalSale = true,
            Currency = product?.Currency ?? "",
            UpdatedAt = at,
            SourceUpdatedAt = at,
            LastSync = at,
            Items = [new OrderItem
            {
                Title = product?.Name ?? "Manuel mağaza satışı",
                Sku = product?.Sku ?? "",
                Barcode = product?.Barcode ?? "",
                ProductId = receipt.ProductId,
                Quantity = receipt.Quantity
            }]
        });
    }

    static ManualSalePreview? ReadPreview(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ReceiptId,ProductId,LocationId,Quantity,QuantityBefore,QuantityAfter,ProductGeneration,LocationVersion,BalanceVersion,CreatedUtc FROM ManualSalePreviews WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader();
        return reader.Read() ? new(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), Parse(reader.GetString(9))) : null;
    }

    static ManualSaleReceipt? ReadReceipt(SqliteConnection connection, string previewId, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT ReceiptId,ProductId,LocationId,Quantity,AppliedUtc FROM ManualSalePreviewReceipts WHERE PreviewId=$id";
        command.Parameters.AddWithValue("$id", previewId); using var reader = command.ExecuteReader();
        return reader.Read() ? new(previewId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), Parse(reader.GetString(4)), false) : null;
    }

    static string Format(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTime Parse(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
