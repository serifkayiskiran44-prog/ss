using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #777 (DB: Foreign key consistency audit). This app's product/source/mapping relationships are logical
// references inside JSON blobs (CatalogProducts.$.SourceId -> Sources.Id) or plain columns
// (MarketplaceMappings.LocalId -> CatalogProducts.Id), not declared SQL foreign keys -- so PRAGMA
// foreign_key_check can never see them, and PRAGMA foreign_keys being on or off is irrelevant to them.
// The audit therefore has to resolve those references itself, and must keep working on a legacy database
// that predates a lazily-created table (MarketplaceMappings only exists once MarketplaceMappingStore has
// ever been constructed against that file).
[TestClass]
public sealed class OrphanReferenceAuditTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "orphan-audit-" + Guid.NewGuid().ToString("N"));

    static void Exec(string root, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void AValidDatabaseReportsZeroOrphansAndAnEmptyRepairPreview()
    {
        var root = NewRoot();
        try
        {
            var store = new CatalogStore(root);
            var source = new XmlSource { Id = "src-1", Name = "Supplier" };
            store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "A", Price = 10, Stock = 1 } });
            new MarketplaceMappingStore(root).Save(new("etsy", "shop-1", store.Products().Single().Id, "ext-1"));

            var result = DatabaseHealth.Inspect(root);

            Assert.AreEqual("HEALTHY", result.Status);
            Assert.AreEqual(0, result.OrphanProductCount);
            Assert.AreEqual(0, result.OrphanMappingCount);
            Assert.AreEqual("", result.RepairPreview);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ProductsWhoseSourceRowIsGoneAndMappingsWhoseProductIsGoneAreBothCounted()
    {
        var root = NewRoot();
        try
        {
            var store = new CatalogStore(root);
            var kept = new XmlSource { Id = "src-kept", Name = "Kept" };
            var doomed = new XmlSource { Id = "src-doomed", Name = "Doomed" };
            store.Import(kept, new[] { new CatalogProduct { SourceId = kept.Id, Sku = "K1", Name = "K1", Price = 10, Stock = 1 } });
            store.Import(doomed, new[]
            {
                new CatalogProduct { SourceId = doomed.Id, Sku = "D1", Name = "D1", Price = 10, Stock = 1 },
                new CatalogProduct { SourceId = doomed.Id, Sku = "D2", Name = "D2", Price = 10, Stock = 1 },
            });
            var mappings = new MarketplaceMappingStore(root);
            mappings.Save(new("etsy", "shop-1", store.Products().Single(p => p.Sku == "K1").Id, "ext-ok"));
            mappings.Save(new("etsy", "shop-1", "product-that-does-not-exist", "ext-orphan"));
            // Simulate the corruption the audit exists to catch: the source row vanishes underneath its products.
            Exec(root, "DELETE FROM Sources WHERE Id='src-doomed'");

            var result = DatabaseHealth.Inspect(root);

            Assert.AreEqual(2, result.OrphanProductCount, "Both products that referenced the deleted source must be counted.");
            Assert.AreEqual(1, result.OrphanMappingCount, "The mapping pointing at a nonexistent product must be counted; the valid one must not.");
            StringAssert.Contains(result.RepairPreview, "src-doomed");
            StringAssert.Contains(result.RepairPreview, "product-that-does-not-exist");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ALegacyDatabaseWithoutTheMappingsTableAndWithForeignKeysOffIsStillAudited()
    {
        var root = NewRoot();
        try
        {
            // CatalogStore alone never creates MarketplaceMappings; a database from before that store existed
            // looks exactly like this. PRAGMA foreign_keys is also left at SQLite's default (off) here.
            var store = new CatalogStore(root);
            var source = new XmlSource { Id = "src-1", Name = "Supplier" };
            store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "A", Price = 10, Stock = 1 } });
            Exec(root, "DELETE FROM Sources WHERE Id='src-1'");

            var result = DatabaseHealth.Inspect(root);

            Assert.AreNotEqual("ERROR", result.Status, "A missing lazily-created table must not turn the whole health check into an error.");
            Assert.AreEqual(1, result.OrphanProductCount);
            Assert.AreEqual(0, result.OrphanMappingCount);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void RepairPreviewNamesTheAffectedRowsWithoutChangingAnything()
    {
        var root = NewRoot();
        try
        {
            var store = new CatalogStore(root);
            var source = new XmlSource { Id = "src-gone", Name = "Gone" };
            store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "X", Name = "X", Price = 10, Stock = 1 } });
            Exec(root, "DELETE FROM Sources WHERE Id='src-gone'");
            var before = store.Products().Count;

            var first = DatabaseHealth.Inspect(root);
            var second = DatabaseHealth.Inspect(root);

            Assert.AreEqual(1, first.OrphanProductCount);
            Assert.AreEqual(before, store.Products().Count, "A preview must be read-only: the orphan row is reported, never deleted or altered.");
            Assert.AreEqual(first.RepairPreview, second.RepairPreview, "Running the audit twice must be idempotent.");
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root) { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
