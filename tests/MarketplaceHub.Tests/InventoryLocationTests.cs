using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
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
        => CreateLegacyCatalog(root, (product.Id, JsonSerializer.Serialize(product)));

    static void CreateLegacyCatalog(string root, params (string Id, string Json)[] rows)
    {
        Directory.CreateDirectory(root);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE CatalogProducts(Id TEXT PRIMARY KEY,Json TEXT NOT NULL);CREATE TABLE Sources(Id TEXT PRIMARY KEY,Json TEXT NOT NULL);";
        command.ExecuteNonQuery();
        foreach (var row in rows)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO CatalogProducts VALUES($id,$json)";
            command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$json", row.Json);
            command.ExecuteNonQuery();
        }
    }

    static (long Balances, long Marker) InventoryMigrationCounts(string root)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM InventoryBalances),(SELECT COUNT(*) FROM InventoryMigrations WHERE Id='inventory-online-balances-v1')";
        using var reader = command.ExecuteReader(); reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    static long BalanceRowCount(string root, string productId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM InventoryBalances WHERE ProductId=$id"; command.Parameters.AddWithValue("$id", productId);
        return (long)command.ExecuteScalar()!;
    }

    static CatalogProduct Clone(CatalogProduct product) => JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(product))!;

    static (string Path, ExcelImportProfile Profile) NewProductWorkbook(string root, string sku, int stock)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".xlsx");
        using (var book = new XLWorkbook())
        {
            var sheet = book.AddWorksheet("Data");
            sheet.Cell(1, 1).Value = "SKU"; sheet.Cell(1, 2).Value = "Name"; sheet.Cell(1, 3).Value = "Stock";
            sheet.Cell(2, 1).Value = sku; sheet.Cell(2, 2).Value = "Excel product"; sheet.Cell(2, 3).Value = stock;
            book.SaveAs(path);
        }
        return (path, new ExcelImportProfile
        {
            ImportMode = ExcelImportMode.AddOnly,
            ColumnLetters = new Dictionary<string, string> { ["Sku"] = "A", ["Name"] = "B", ["Stock"] = "C" }
        });
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
    public void IdentityMismatchFailsWholeMigrationAndRestartSucceedsAfterRepair()
    {
        var root = TempRoot();
        var healthy = Product("HEALTHY", 7);
        var mismatched = Product("MISMATCH", 4); mismatched.Id = "embedded-id"; mismatched.Name = "SECRET-PAYLOAD-NAME";
        try
        {
            CreateLegacyCatalog(root,
                (healthy.Id, JsonSerializer.Serialize(healthy)),
                ("row-id", JsonSerializer.Serialize(mismatched)));

            var error = Assert.ThrowsException<InvalidOperationException>(() => _ = new CatalogStore(root));
            StringAssert.Contains(error.Message, "REVIEW_REQUIRED");
            StringAssert.Contains(error.Message, "row-id");
            StringAssert.DoesNotMatch(error.Message, new System.Text.RegularExpressions.Regex("SECRET-PAYLOAD-NAME"));
            Assert.AreEqual((0L, 0L), InventoryMigrationCounts(root));

            mismatched.Id = "row-id";
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
            {
                connection.Open(); using var repair = connection.CreateCommand();
                repair.CommandText = "UPDATE CatalogProducts SET Json=$json WHERE Id='row-id'";
                repair.Parameters.AddWithValue("$json", JsonSerializer.Serialize(mismatched)); repair.ExecuteNonQuery();
            }

            _ = new CatalogStore(root);
            var inventory = new InventoryLocationStore(root);
            Assert.AreEqual(7, inventory.GetBalance(healthy.Id, InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.AreEqual(4, inventory.GetBalance("row-id", InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.AreEqual((2L, 1L), InventoryMigrationCounts(root));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void NegativeStockRowFailsMigrationWithoutPartialBalanceOrPayloadLeak()
    {
        var root = TempRoot();
        var healthy = Product("HEALTHY", 7);
        var invalid = Product("BAD", -1); invalid.Name = "SECRET-NEGATIVE-PRODUCT";
        try
        {
            CreateLegacyCatalog(root,
                (healthy.Id, JsonSerializer.Serialize(healthy)),
                (invalid.Id, JsonSerializer.Serialize(invalid)));

            var error = Assert.ThrowsException<InvalidOperationException>(() => _ = new CatalogStore(root));
            StringAssert.Contains(error.Message, "REVIEW_REQUIRED");
            StringAssert.DoesNotMatch(error.Message, new System.Text.RegularExpressions.Regex("SECRET-NEGATIVE-PRODUCT"));
            Assert.AreEqual((0L, 0L), InventoryMigrationCounts(root));
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

    [TestMethod]
    public void DirectDeleteRejectsNonzeroOnlineOrPhysicalBalance()
    {
        WithStore((catalog, inventory, _) =>
        {
            var online = catalog.CreateManual(Product("ONLINE", 2));
            var onlineError = Assert.ThrowsException<InvalidOperationException>(() => catalog.DeleteProduct(online));
            StringAssert.Contains(onlineError.Message, "stok");

            var physical = catalog.CreateManual(Product("PHYSICAL", 3));
            inventory.CreatePhysicalStore("shop-floor", "Mağaza");
            inventory.ApplyTransfer(inventory.PreviewTransfer(physical.Id, InventoryLocationStore.OnlineLocationId, "shop-floor", 3));
            var physicalError = Assert.ThrowsException<InvalidOperationException>(() => catalog.DeleteProduct(catalog.Products().Single(p => p.Id == physical.Id)));
            StringAssert.Contains(physicalError.Message, "stok");

            Assert.AreEqual(2, catalog.Products().Count);
            Assert.AreEqual(2, inventory.GetBalance(online.Id, InventoryLocationStore.OnlineLocationId).Quantity);
            Assert.AreEqual(3, inventory.GetBalance(physical.Id, "shop-floor").Quantity);
        });
    }

    [TestMethod]
    public void ZeroBalanceDeleteRemovesBalancesButKeepsImmutableMovementHistory()
    {
        WithStore((catalog, inventory, root) =>
        {
            var product = catalog.CreateManual(Product("DELETE-ZERO", 2));
            inventory.CreatePhysicalStore("shop-floor", "Mağaza");
            inventory.ApplyTransfer(inventory.PreviewTransfer(product.Id, InventoryLocationStore.OnlineLocationId, "shop-floor", 2));
            inventory.ApplyManualSale("sale-delete-zero", product.Id, "shop-floor", 2);
            var movementCount = inventory.Movements(product.Id).Count;

            catalog.DeleteProduct(catalog.Products().Single());

            Assert.AreEqual(0, catalog.Products().Count);
            Assert.AreEqual(0L, BalanceRowCount(root, product.Id));
            Assert.AreEqual(movementCount, inventory.Movements(product.Id).Count);
        });
    }

    [TestMethod]
    public void ExcelUndoCannotRemoveCreatedProductWithNonzeroBalance()
    {
        WithStore((catalog, inventory, root) =>
        {
            var workbook = NewProductWorkbook(root, "EXCEL-STOCK", 5);
            var plan = ExcelProductImport.Preview(catalog, workbook.Path, workbook.Profile);
            var receipt = ExcelProductImport.Apply(catalog, workbook.Path, workbook.Profile, plan, new[] { 2 });

            var error = Assert.ThrowsException<InvalidOperationException>(() => catalog.UndoExcelImport(receipt.Id));
            StringAssert.Contains(error.Message, "stok");
            var product = catalog.Products().Single();
            Assert.AreEqual(5, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        });
    }

    [TestMethod]
    public void ExcelUndoCanRemoveCreatedZeroBalanceProduct()
    {
        WithStore((catalog, _, root) =>
        {
            var workbook = NewProductWorkbook(root, "EXCEL-ZERO", 0);
            var plan = ExcelProductImport.Preview(catalog, workbook.Path, workbook.Profile);
            var receipt = ExcelProductImport.Apply(catalog, workbook.Path, workbook.Profile, plan, new[] { 2 });
            var productId = catalog.Products().Single().Id;

            catalog.UndoExcelImport(receipt.Id);

            Assert.AreEqual(0, catalog.Products().Count);
            Assert.AreEqual(0L, BalanceRowCount(root, productId));
        });
    }

    [TestMethod]
    public void CatalogUndoBlocksTrueRemovalButAllowsSameIdentityRestoreWithStock()
    {
        WithStore((catalog, inventory, _) =>
        {
            var created = catalog.CreateManual(Product("UNDO-STOCK", 6));
            var removeReceipt = new CatalogUndoReceipt("remove", Array.Empty<CatalogProduct>(), new[] { Clone(created) });
            var error = Assert.ThrowsException<InvalidOperationException>(() => catalog.Undo(removeReceipt));
            StringAssert.Contains(error.Message, "stok");
            Assert.AreEqual(6, inventory.GetBalance(created.Id, InventoryLocationStore.OnlineLocationId).Quantity);

            var before = Clone(catalog.Products().Single());
            var edited = catalog.Products().Single(); edited.Name = "Changed"; catalog.SaveProduct(edited);
            var after = Clone(catalog.Products().Single());
            catalog.Undo(new CatalogUndoReceipt("restore", new[] { before }, new[] { after }));

            Assert.AreEqual("Ürün UNDO-STOCK", catalog.Products().Single().Name);
            Assert.AreEqual(6, inventory.GetBalance(created.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        });
    }
}
