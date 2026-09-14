using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2585: a malformed/oversized CatalogProducts row must not make
/// the rest of the catalog inaccessible, must surface as an explicit
/// review-required record with stable identity, and must never be silently
/// deleted, overwritten, or leaked (raw JSON) via routine operations.
[TestClass]
public sealed class CatalogCorruptionTests
{
    static CatalogStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "catalog-corrupt-" + Guid.NewGuid().ToString("N"));
        return new CatalogStore(root);
    }

    // CatalogStore maintains expression indexes over json_extract(Json,...), which
    // SQLite evaluates (and throws "malformed JSON" for) on every write to the row
    // being touched - so a genuinely syntax-broken row can never arrive through a
    // normal parameterized INSERT/UPDATE the way the app itself writes rows. That
    // matches production: this kind of corruption comes from something outside
    // SQLite's own write path (disk/file-level damage, an interrupted write). The
    // indexes are dropped here purely to let the test fixture plant such a row
    // directly, the way file-level corruption would produce one; they are pure
    // performance indexes, so their absence does not change read correctness.
    static void InsertRawRow(string root, string id, string json)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using (var dropIndexes = c.CreateCommand())
        {
            dropIndexes.CommandText = "DROP INDEX IF EXISTS IX_CatalogProducts_Sku;DROP INDEX IF EXISTS IX_CatalogProducts_Barcode;DROP INDEX IF EXISTS IX_CatalogProducts_Brand;DROP INDEX IF EXISTS IX_CatalogProducts_Category";
            dropIndexes.ExecuteNonQuery();
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO CatalogProducts(Id,Json) VALUES($id,$json) ON CONFLICT(Id) DO UPDATE SET Json=excluded.Json";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$json", json);
        cmd.ExecuteNonQuery();
    }

    static CatalogProduct Product(string sku) => new() { Sku = sku, Name = "Ürün " + sku, Price = 10, Stock = 1 };

    [TestMethod]
    public void TruncatedJsonRowDoesNotCrashListing()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-1"));
            InsertRawRow(root, "broken-1", "{\"Sku\":\"BROKEN\",\"Name\":");

            var products = store.Products();
            Assert.AreEqual(1, products.Count);
            Assert.AreEqual("SKU-1", products.Single().Sku);

            var corrupt = store.CorruptProducts();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("broken-1", corrupt[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WrongFieldTypeRowIsIsolatedNotCrashing()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-1"));
            // Stock is typed int in CatalogProduct; a JSON object there cannot bind.
            InsertRawRow(root, "broken-2", "{\"Id\":\"broken-2\",\"Sku\":\"BAD\",\"Name\":\"x\",\"Stock\":{\"nested\":true}}");

            Assert.AreEqual(1, store.Products().Count);
            Assert.AreEqual(1, store.CorruptProducts().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NullRequiredIdFieldIsTreatedAsCorruptNotCrashing()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-1"));
            InsertRawRow(root, "broken-3", "{\"Id\":null,\"Sku\":\"BAD\",\"Name\":\"x\"}");

            Assert.AreEqual(1, store.Products().Count);
            var corrupt = store.CorruptProducts().Single();
            Assert.AreEqual("broken-3", corrupt.Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OversizedPayloadIsRejectedWithoutFullParse()
    {
        var store = NewStore(out var root);
        try
        {
            var huge = "{\"Id\":\"broken-4\",\"Sku\":\"BAD\",\"Name\":\"" + new string('x', CatalogStore.MaxProductJsonBytes + 10) + "\"}";
            InsertRawRow(root, "broken-4", huge);

            var corrupt = store.CorruptProducts().Single();
            Assert.AreEqual("Oversized payload", corrupt.Reason);
            Assert.IsTrue(corrupt.PayloadLength > CatalogStore.MaxProductJsonBytes);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawJson()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "broken-5", "{\"Sku\":\"SECRET-SKU-VALUE\",\"Name\":");
            var corrupt = store.CorruptProducts().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("SECRET-SKU-VALUE"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void HealthyRowsRemainSearchable()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-1"));
            store.CreateManual(Product("SKU-2"));
            InsertRawRow(root, "broken-6", "not even json");

            var page = store.Search("SKU");
            Assert.AreEqual(2, page.Total);
            Assert.AreEqual(2, page.Items.Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SaveProductAgainstCorruptRowIsRejectedNotSilentlyOverwritten()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "broken-7", "{\"Id\":\"broken-7\",\"Sku\":");
            var attempted = new CatalogProduct { Id = "broken-7", Sku = "NEW", Name = "Yeni", Price = 5, Stock = 1 };
            var ex = Assert.ThrowsException<InvalidOperationException>(() => store.SaveProduct(attempted));
            StringAssert.Contains(ex.Message, "bozuk");
            Assert.AreEqual(1, store.CorruptProducts().Count, "The corrupt row must still be present, not overwritten.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeleteProductAgainstCorruptRowIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "broken-8", "{\"Id\":\"broken-8\",\"Sku\":");
            var attempted = new CatalogProduct { Id = "broken-8" };
            var ex = Assert.ThrowsException<InvalidOperationException>(() => store.DeleteProduct(attempted));
            StringAssert.Contains(ex.Message, "bozuk");
            Assert.AreEqual(1, store.CorruptProducts().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MissingRowIsDistinctFromCorruptRow()
    {
        var store = NewStore(out var root);
        try
        {
            var missing = new CatalogProduct { Id = "does-not-exist", Sku = "SKU-X", Name = "Ürün", Price = 5, Stock = 1 };
            var ex = Assert.ThrowsException<InvalidOperationException>(() => store.SaveProduct(missing));
            StringAssert.Contains(ex.Message, "bulunamadı");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeleteCorruptRowRemovesOnlyTheCorruptRecord()
    {
        var store = NewStore(out var root);
        try
        {
            var healthy = store.CreateManual(Product("SKU-1"));
            InsertRawRow(root, "broken-9", "{\"Id\":\"broken-9\",\"Sku\":");

            store.DeleteCorruptRow("broken-9");

            Assert.AreEqual(0, store.CorruptProducts().Count);
            Assert.AreEqual(1, store.Products().Count);
            Assert.AreEqual(healthy.Id, store.Products().Single().Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeleteCorruptRowRefusesAHealthyRow()
    {
        var store = NewStore(out var root);
        try
        {
            var healthy = store.CreateManual(Product("SKU-1"));
            Assert.ThrowsException<InvalidOperationException>(() => store.DeleteCorruptRow(healthy.Id));
            Assert.AreEqual(1, store.Products().Count, "A healthy row must never be removed by the corrupt-row recovery path.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeleteCorruptRowOnMissingIdThrows()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() => store.DeleteCorruptRow("nope"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesReviewRequiredState()
    {
        var root = Path.Combine(Path.GetTempPath(), "catalog-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            new CatalogStore(root).CreateManual(Product("SKU-1"));
            InsertRawRow(root, "broken-10", "{\"Id\":\"broken-10\",\"Sku\":");

            var reopened = new CatalogStore(root);
            Assert.AreEqual(1, reopened.Products().Count);
            Assert.AreEqual(1, reopened.CorruptProducts().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "broken-a", "{\"Sku\":");
            InsertRawRow(root, "broken-b", "not json");
            InsertRawRow(root, "broken-c", "{\"Id\":null}");

            var corrupt = store.CorruptProducts();
            Assert.AreEqual(3, corrupt.Count);
            CollectionAssert.AreEquivalent(new[] { "broken-a", "broken-b", "broken-c" }, corrupt.Select(c => c.Id).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DuplicateIdentityScanIgnoresCorruptRows()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(Product("SKU-DUP"));
            InsertRawRow(root, "broken-11", "{\"Sku\":\"SKU-DUP\",\"Name\":");

            // Creating a second real product with the same SKU must still be rejected -
            // the malformed row must not interfere with (or crash) identity checks.
            Assert.ThrowsException<InvalidOperationException>(() => store.CreateManual(Product("SKU-DUP")));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
