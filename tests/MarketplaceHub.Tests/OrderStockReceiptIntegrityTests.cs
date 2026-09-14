using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2648 (P0): a corrupt OrderStockReceipts row is the only
/// durable proof that an order's stock decrement already happened. It must
/// never be treated as "missing" or silently repaired/overwritten - doing
/// so would allow a second stock decrement for the same order.
[TestClass]
public sealed class OrderStockReceiptIntegrityTests
{
    static CatalogStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "orderstock-integrity-" + Guid.NewGuid().ToString("N"));
        return new CatalogStore(root);
    }

    static CatalogProduct Product(string sku, int stock) => new() { Sku = sku, Name = "Ürün " + sku, Price = 10, Stock = stock };

    static OrderItem Item(string sku, int qty) => new() { Sku = sku, Title = "x", Quantity = qty };

    static void InsertRawReceipt(string root, string marketplace, string shopId, string orderId, string payload, string json)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO OrderStockReceipts VALUES($m,$s,$o,$payload,$json)";
        cmd.Parameters.AddWithValue("$m", marketplace); cmd.Parameters.AddWithValue("$s", shopId); cmd.Parameters.AddWithValue("$o", orderId);
        cmd.Parameters.AddWithValue("$payload", payload); cmd.Parameters.AddWithValue("$json", json);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void FirstApplyDecrementsStockOnce()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-1", 10));
            var result = store.ApplyOrderStock("etsy", "shop1", "o1", new[] { Item("SKU-1", 3) });
            Assert.IsFalse(result.AlreadyApplied);
            Assert.AreEqual(7, store.Products().Single().Stock);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void IdempotentReplayWithSamePayloadDoesNotDecrementAgain()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-1", 10));
            store.ApplyOrderStock("etsy", "shop1", "o1", new[] { Item("SKU-1", 3) });
            var replay = store.ApplyOrderStock("etsy", "shop1", "o1", new[] { Item("SKU-1", 3) });
            Assert.IsTrue(replay.AlreadyApplied);
            Assert.AreEqual(7, store.Products().Single().Stock);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedExistingReceiptBlocksReapplyWithoutStockChange()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-1", 10));
            InsertRawReceipt(root, "etsy", "shop1", "o1", "{\"SKU-1\":3}", "{\"Marketplace\":\"etsy\",\"ShopId\":\"shop1\",\"OrderId\":");

            var ex = Assert.ThrowsException<OrderStockReceiptCorruptException>(() => store.ApplyOrderStock("etsy", "shop1", "o1", new[] { Item("SKU-1", 3) }));
            Assert.AreEqual("o1", ex.OrderId);
            Assert.AreEqual(10, store.Products().Single().Stock, "A corrupt receipt must never allow the stock mutation to proceed.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ReceiptWithWrongEmbeddedIdentityIsTreatedAsCorrupt()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-1", 10));
            var wrongIdentityJson = "{\"Marketplace\":\"etsy\",\"ShopId\":\"shop1\",\"OrderId\":\"DIFFERENT-ORDER\",\"AppliedUtc\":\"" + DateTime.UtcNow.ToString("O") + "\",\"Movements\":[{\"ProductId\":\"p1\",\"Sku\":\"SKU-1\",\"Quantity\":3,\"StockBefore\":10,\"StockAfter\":7}]}";
            InsertRawReceipt(root, "etsy", "shop1", "o1", "{\"SKU-1\":3}", wrongIdentityJson);

            Assert.ThrowsException<OrderStockReceiptCorruptException>(() => store.ApplyOrderStock("etsy", "shop1", "o1", new[] { Item("SKU-1", 3) }));
            Assert.AreEqual(10, store.Products().Single().Stock);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ReceiptWithInconsistentMovementIsTreatedAsCorrupt()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-1", 10));
            // StockAfter doesn't equal StockBefore - Quantity: internally inconsistent.
            var badMovementJson = "{\"Marketplace\":\"etsy\",\"ShopId\":\"shop1\",\"OrderId\":\"o1\",\"AppliedUtc\":\"" + DateTime.UtcNow.ToString("O") + "\",\"Movements\":[{\"ProductId\":\"p1\",\"Sku\":\"SKU-1\",\"Quantity\":3,\"StockBefore\":10,\"StockAfter\":99}]}";
            InsertRawReceipt(root, "etsy", "shop1", "o1", "{\"SKU-1\":3}", badMovementJson);

            Assert.ThrowsException<OrderStockReceiptCorruptException>(() => store.ApplyOrderStock("etsy", "shop1", "o1", new[] { Item("SKU-1", 3) }));
            Assert.AreEqual(10, store.Products().Single().Stock);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DuplicateProductIdMovementIsTreatedAsCorrupt()
    {
        var store = NewStore(out var root);
        try
        {
            var at = DateTime.UtcNow.ToString("O");
            var dupJson = $"{{\"Marketplace\":\"etsy\",\"ShopId\":\"shop1\",\"OrderId\":\"o1\",\"AppliedUtc\":\"{at}\",\"Movements\":[{{\"ProductId\":\"p1\",\"Sku\":\"SKU-1\",\"Quantity\":1,\"StockBefore\":10,\"StockAfter\":9}},{{\"ProductId\":\"p1\",\"Sku\":\"SKU-1\",\"Quantity\":1,\"StockBefore\":9,\"StockAfter\":8}}]}}";
            InsertRawReceipt(root, "etsy", "shop1", "o1", "{\"SKU-1\":2}", dupJson);

            Assert.AreEqual(1, store.CorruptOrderStockReceipts().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InvalidAppliedUtcIsTreatedAsCorrupt()
    {
        var store = NewStore(out var root);
        try
        {
            var badDateJson = "{\"Marketplace\":\"etsy\",\"ShopId\":\"shop1\",\"OrderId\":\"o1\",\"AppliedUtc\":\"0001-01-01T00:00:00\",\"Movements\":[{\"ProductId\":\"p1\",\"Sku\":\"SKU-1\",\"Quantity\":1,\"StockBefore\":10,\"StockAfter\":9}]}";
            InsertRawReceipt(root, "etsy", "shop1", "o1", "{\"SKU-1\":1}", badDateJson);

            Assert.AreEqual(1, store.CorruptOrderStockReceipts().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void GetOrderStockStatusDistinguishesMissingFromCorrupt()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.IsNull(store.GetOrderStockStatus("etsy", "shop1", "no-such-order"));

            InsertRawReceipt(root, "etsy", "shop1", "o1", "{}", "junk");
            Assert.ThrowsException<OrderStockReceiptCorruptException>(() => store.GetOrderStockStatus("etsy", "shop1", "o1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OneCorruptOrderDoesNotAffectOtherOrdersStockFlow()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-1", 10));
            store.CreateManual(Product("SKU-2", 10));
            InsertRawReceipt(root, "etsy", "shop1", "bad-order", "{}", "junk");

            var result = store.ApplyOrderStock("etsy", "shop1", "good-order", new[] { Item("SKU-2", 4) });
            Assert.IsFalse(result.AlreadyApplied);
            Assert.AreEqual(6, store.Products().Single(p => p.Sku == "SKU-2").Stock);
            Assert.AreEqual(10, store.Products().Single(p => p.Sku == "SKU-1").Stock);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesCorruptReceiptDetectionDeterministically()
    {
        var root = Path.Combine(Path.GetTempPath(), "orderstock-integrity-" + Guid.NewGuid().ToString("N"));
        try
        {
            new CatalogStore(root);
            InsertRawReceipt(root, "etsy", "shop1", "o1", "{}", "junk");

            var reopened = new CatalogStore(root);
            Assert.ThrowsException<OrderStockReceiptCorruptException>(() => reopened.GetOrderStockStatus("etsy", "shop1", "o1"));
            Assert.AreEqual(1, reopened.CorruptOrderStockReceipts().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OversizedReceiptPayloadIsRejectedWithoutFullParse()
    {
        var store = NewStore(out var root);
        try
        {
            var huge = "{\"Marketplace\":\"etsy\",\"ShopId\":\"shop1\",\"OrderId\":\"o1\",\"AppliedUtc\":\"" + DateTime.UtcNow.ToString("O") + "\",\"Movements\":[],\"pad\":\"" + new string('x', 200_100) + "\"}";
            InsertRawReceipt(root, "etsy", "shop1", "o1", "{}", huge);

            var corrupt = store.CorruptOrderStockReceipts().Single();
            Assert.AreEqual("Oversized receipt payload", corrupt.Reason);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawJsonOrProductValues()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawReceipt(root, "etsy", "shop1", "o1", "{}", "{\"secret\":\"SECRET-PRODUCT-DATA\"");
            var corrupt = store.CorruptOrderStockReceipts().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("SECRET-PRODUCT-DATA"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SameOrderIdDifferentShopIsolatedFromCorruptReceipt()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-1", 10));
            InsertRawReceipt(root, "etsy", "shop1", "o1", "{}", "junk");

            // Same OrderId under a different shop must be entirely unaffected.
            var result = store.ApplyOrderStock("etsy", "shop2", "o1", new[] { Item("SKU-1", 2) });
            Assert.IsFalse(result.AlreadyApplied);
            Assert.AreEqual(8, store.Products().Single().Stock);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
