using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class InventoryLocationTests
{
    static string TempRoot() => Path.Combine(Path.GetTempPath(), "inventory-locations-" + Guid.NewGuid().ToString("N"));

    static CatalogProduct Product(string sku, int stock) => new()
    {
        Sku = sku,
        Name = "Ürün " + sku,
        Price = 10,
        Currency = "TRY",
        Stock = stock
    };

    static void WithStore(Action<CatalogStore, InventoryLocationStore, string> test)
    {
        var root = TempRoot();
        try
        {
            var catalog = new CatalogStore(root);
            test(catalog, new InventoryLocationStore(root), root);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    static void CreateLegacyCatalog(string root, CatalogProduct product)
    {
        Directory.CreateDirectory(root);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE CatalogProducts(Id TEXT PRIMARY KEY,Json TEXT NOT NULL);CREATE TABLE Sources(Id TEXT PRIMARY KEY,Json TEXT NOT NULL);INSERT INTO CatalogProducts VALUES($id,$json);";
        command.Parameters.AddWithValue("$id", product.Id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(product));
        command.ExecuteNonQuery();
    }

    [TestMethod]
    public void LegacyStockMigratesToOnlineOnceAcrossRestarts()
    {
        var root = TempRoot();
        var product = Product("SKU-1", 11);
        try
        {
            CreateLegacyCatalog(root, product);
            _ = new CatalogStore(root);
            var inventory = new InventoryLocationStore(root);
            Assert.AreEqual(11, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE CatalogProducts SET Json=json_set(Json,'$.Stock',99) WHERE Id=$id";
                command.Parameters.AddWithValue("$id", product.Id);
                command.ExecuteNonQuery();
            }

            _ = new CatalogStore(root);
            Assert.AreEqual(11, new InventoryLocationStore(root).GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity,
                "The durable migration marker must prevent a restart from importing legacy stock again.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void PhysicalBalanceIsSeparateAndTransferIsAtomic()
    {
        WithStore((catalog, inventory, _) =>
        {
            var product = catalog.CreateManual(Product("SKU-1", 10));
            inventory.CreatePhysicalStore("shop-floor", "Mağaza");

            var result = inventory.ApplyTransfer(inventory.PreviewTransfer(product.Id, InventoryLocationStore.OnlineLocationId, "shop-floor", 4));

            Assert.IsFalse(result.AlreadyApplied);
            Assert.AreEqual(6, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.AreEqual(4, inventory.GetBalance(product.Id, "shop-floor").Quantity);
            Assert.AreEqual(6, catalog.Products().Single().Stock, "Legacy Stock must mirror the online balance during the compatibility period.");
            Assert.AreEqual(2, inventory.Movements(product.Id).Count);
        });
    }

    [TestMethod]
    public void InsufficientTransferRollsBackEveryWrite()
    {
        WithStore((catalog, inventory, _) =>
        {
            var product = catalog.CreateManual(Product("SKU-1", 3));
            inventory.CreatePhysicalStore("shop-floor", "Mağaza");
            Assert.ThrowsException<InvalidOperationException>(() => inventory.PreviewTransfer(product.Id, InventoryLocationStore.OnlineLocationId, "shop-floor", 4));
            Assert.AreEqual(3, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.AreEqual(0, inventory.GetBalance(product.Id, "shop-floor").Quantity);
            Assert.AreEqual(0, inventory.Movements(product.Id).Count);
        });
    }

    [TestMethod]
    public void StaleTransferPreviewDoesNotPartiallyMoveStock()
    {
        WithStore((catalog, inventory, _) =>
        {
            var product = catalog.CreateManual(Product("SKU-1", 10));
            inventory.CreatePhysicalStore("shop-floor", "Mağaza");
            var stale = inventory.PreviewTransfer(product.Id, InventoryLocationStore.OnlineLocationId, "shop-floor", 2);
            inventory.ApplyTransfer(inventory.PreviewTransfer(product.Id, InventoryLocationStore.OnlineLocationId, "shop-floor", 1));

            Assert.ThrowsException<InvalidOperationException>(() => inventory.ApplyTransfer(stale));
            Assert.AreEqual(9, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.AreEqual(1, inventory.GetBalance(product.Id, "shop-floor").Quantity);
        });
    }

    [TestMethod]
    public void AppliedTransferReceiptRejectsAChangedPreviewPayload()
    {
        WithStore((catalog, inventory, _) =>
        {
            var product = catalog.CreateManual(Product("SKU-1", 10));
            inventory.CreatePhysicalStore("shop-floor", "Mağaza");
            var preview = inventory.PreviewTransfer(product.Id, InventoryLocationStore.OnlineLocationId, "shop-floor", 2);
            inventory.ApplyTransfer(preview);

            Assert.ThrowsException<InvalidOperationException>(() => inventory.ApplyTransfer(preview with { Quantity = 1 }));
            Assert.AreEqual(8, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.AreEqual(2, inventory.GetBalance(product.Id, "shop-floor").Quantity);
        });
    }

    [TestMethod]
    public void DuplicateOnlineOrderReceiptDeductsOnlineAndLegacyStockOnce()
    {
        WithStore((catalog, inventory, _) =>
        {
            var product = catalog.CreateManual(Product("SKU-1", 10));
            var items = new[] { new OrderItem { Sku = "SKU-1", Title = "x", Quantity = 3 } };
            var first = inventory.ApplyOrder("etsy", "shop-1", "order-1", items);
            var replay = inventory.ApplyOrder("etsy", "shop-1", "order-1", items);

            Assert.IsFalse(first.AlreadyApplied);
            Assert.IsTrue(replay.AlreadyApplied);
            Assert.AreEqual(7, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.AreEqual(7, catalog.Products().Single().Stock);
            Assert.AreEqual(1, inventory.Movements(product.Id).Count(m => m.Kind == InventoryMovementKind.OnlineOrder));
        });
    }

    [TestMethod]
    public void DuplicateManualSaleReceiptDeductsOnlyPhysicalStockOnce()
    {
        WithStore((catalog, inventory, _) =>
        {
            var product = catalog.CreateManual(Product("SKU-1", 10));
            inventory.CreatePhysicalStore("shop-floor", "Mağaza");
            inventory.ApplyTransfer(inventory.PreviewTransfer(product.Id, InventoryLocationStore.OnlineLocationId, "shop-floor", 5));

            var first = inventory.ApplyManualSale("receipt-1", product.Id, "shop-floor", 2);
            var replay = inventory.ApplyManualSale("receipt-1", product.Id, "shop-floor", 2);

            Assert.IsFalse(first.AlreadyApplied);
            Assert.IsTrue(replay.AlreadyApplied);
            Assert.AreEqual(3, inventory.GetBalance(product.Id, "shop-floor").Quantity);
            Assert.AreEqual(5, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.AreEqual(5, catalog.Products().Single().Stock);
            Assert.AreEqual(1, inventory.Movements(product.Id).Count(m => m.Kind == InventoryMovementKind.ManualSale));
        });
    }

    [TestMethod]
    public void TransferDestinationOverflowDoesNotMutateEitherBalance()
    {
        WithStore((catalog, inventory, _) =>
        {
            var product = catalog.CreateManual(Product("SKU-1", 2));
            inventory.CreatePhysicalStore("shop-floor", "Mağaza");
            inventory.SetBalance(product.Id, "shop-floor", int.MaxValue, inventory.GetBalance(product.Id, "shop-floor").Version);

            Assert.ThrowsException<OverflowException>(() => inventory.PreviewTransfer(product.Id, InventoryLocationStore.OnlineLocationId, "shop-floor", 1));

            Assert.AreEqual(2, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.AreEqual(int.MaxValue, inventory.GetBalance(product.Id, "shop-floor").Quantity);
            Assert.AreEqual(2, catalog.Products().Single().Stock);
        });
    }

    [TestMethod]
    public void SecondOrderLineFailureRollsBackFirstLineAndReceipt()
    {
        WithStore((catalog, inventory, _) =>
        {
            var first = catalog.CreateManual(Product("SKU-1", 10));
            var second = catalog.CreateManual(Product("SKU-2", 1));
            Assert.ThrowsException<InvalidOperationException>(() => inventory.ApplyOrder("etsy", "shop-1", "order-rollback", new[]
            {
                new OrderItem { Sku = "SKU-1", Title = "x", Quantity = 1 },
                new OrderItem { Sku = "SKU-2", Title = "x", Quantity = 2 }
            }));

            Assert.AreEqual(10, inventory.GetBalance(first.Id, InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.AreEqual(1, inventory.GetBalance(second.Id, InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.IsNull(catalog.GetOrderStockStatus("etsy", "shop-1", "order-rollback"));
        });
    }
}
