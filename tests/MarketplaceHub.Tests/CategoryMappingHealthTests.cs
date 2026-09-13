using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #914 (TAXONOMY: stale category mapping detector). A product's category text resolves to a local category by name or
// approved alias; the category must be active, the channel must map it, and the mapping must be younger than the
// category's last change. A deactivated category blocks listing readiness; an unknown text, a missing mapping or a
// label changed after the mapping warns; a removed category leaves its mapping as an orphan named by its external
// key; a renamed label under the same id keeps the old name resolving; a mapping written again is restored.
[TestClass]
public sealed class CategoryMappingHealthTests
{
    static TaxonomyEntry Category(TaxonomyStore taxonomy, string name) => taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = name, Value = "" });

    [TestMethod]
    public void VerdictsFollowTheDictionaryTheEntryAndTheMappingAndARenameKeepsTheOldNameResolving()
    {
        var root = Path.Combine(Path.GetTempPath(), "stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var taxonomy = new TaxonomyStore(root); var aliases = new TaxonomyAliasStore(root);
            var electronics = Category(taxonomy, "Elektronik"); var old = Category(taxonomy, "Eski Ürünler"); Category(taxonomy, "Bahçe");
            aliases.Save("elektronik ürünleri", electronics.Id, approved: true);
            taxonomy.Map(TaxonomyKind.Category, "e-123", electronics.Id, "etsy", "S1");
            taxonomy.Map(TaxonomyKind.Category, "e-9", old.Id, "etsy", "S1");

            // One snapshot, every verdict: no category, a known name and an approved alias mapped, an unmapped category, an unknown text.
            var snapshot = CategoryMappingHealth.Snapshot(root, "Etsy", "S1");
            Assert.AreEqual(CategoryMappingVerdict.NoCategory, snapshot.Evaluate(" ").Status); Assert.IsFalse(snapshot.Evaluate("").Blocks);
            var ok = snapshot.Evaluate("ELEKTRONİK"); Assert.AreEqual(CategoryMappingVerdict.Ok, ok.Status); StringAssert.Contains(ok.Words, "e-123");
            Assert.AreEqual(CategoryMappingVerdict.Ok, snapshot.Evaluate("Elektronik Ürünleri").Status, "an approved alias resolves to the mapped category");
            var unmapped = snapshot.Evaluate("bahçe"); Assert.AreEqual(CategoryMappingVerdict.MappingMissing, unmapped.Status); Assert.IsFalse(unmapped.Blocks); StringAssert.Contains(unmapped.Words, "etsy/S1");
            var unknown = snapshot.Evaluate("Mutfak"); Assert.AreEqual(CategoryMappingVerdict.UnknownCategory, unknown.Status); Assert.IsFalse(unknown.Blocks); StringAssert.Contains(unknown.Words, "Mutfak");
            Assert.AreEqual(CategoryMappingVerdict.Ok, snapshot.Evaluate("Eski Ürünler").Status); Assert.AreEqual(0, snapshot.Orphans.Count);

            // Deactivated: blocks.
            old.Active = false; taxonomy.Save(old);
            var blocked = CategoryMappingHealth.Snapshot(root, "etsy", "S1").Evaluate("eski ürünler"); Assert.AreEqual(CategoryMappingVerdict.EntryInactive, blocked.Status); Assert.IsTrue(blocked.Blocks); StringAssert.Contains(blocked.Words, "pasif");

            // Renamed label, same id: the old name still resolves through the rename alias; the mapping is "changed after" until it is written again; written again, it is restored.
            Thread.Sleep(5); electronics.Name = "Elektronik & Teknoloji"; taxonomy.Save(electronics);
            Assert.IsTrue(aliases.List().Any(a => a.Key == TaxonomyAliasStore.Key("Elektronik") && a.Approved && a.Source == "rename"), "the old label became an approved alias");
            var renamed = CategoryMappingHealth.Snapshot(root, "etsy", "S1");
            var stale = renamed.Evaluate("Elektronik"); Assert.AreEqual(CategoryMappingVerdict.MappingRenamed, stale.Status); Assert.IsFalse(stale.Blocks); StringAssert.Contains(stale.Words, "e-123");
            Assert.AreEqual(CategoryMappingVerdict.MappingRenamed, renamed.Evaluate("Elektronik & Teknoloji").Status);
            Thread.Sleep(5); taxonomy.Map(TaxonomyKind.Category, "e-123", electronics.Id, "etsy", "S1");
            Assert.AreEqual(CategoryMappingVerdict.Ok, CategoryMappingHealth.Snapshot(root, "etsy", "S1").Evaluate("Elektronik").Status, "the mapping written again is the restored mapping");

            // Removed: the mapping becomes an orphan named by its external key; the text resolves to nothing.
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
            { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM TaxonomyEntries WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", old.Id); cmd.ExecuteNonQuery(); }
            var removed = CategoryMappingHealth.Snapshot(root, "etsy", "S1"); Assert.AreEqual("e-9", removed.Orphans.Single().ExternalKey); Assert.AreEqual(CategoryMappingVerdict.UnknownCategory, removed.Evaluate("Eski Ürünler").Status);

            // Restart: a fresh read says the same.
            SqliteConnection.ClearAllPools();
            var reopened = CategoryMappingHealth.Snapshot(root, "etsy", "S1"); Assert.AreEqual(1, reopened.Orphans.Count); Assert.AreEqual(CategoryMappingVerdict.Ok, reopened.Evaluate("elektronik ürünleri").Status);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheMatrixAndTheQualityScanCarryTheVerdictOnTheRealPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "stale-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var taxonomy = new TaxonomyStore(root);
            var electronics = Category(taxonomy, "Elektronik"); var old = Category(taxonomy, "Eski Ürünler");
            taxonomy.Map(TaxonomyKind.Category, "e-123", electronics.Id, "etsy", "S1"); taxonomy.Map(TaxonomyKind.Category, "e-9", old.Id, "etsy", "S1");
            var source = new XmlSource { Id = "fixture", Name = "Fixture" };
            static CatalogProduct P(string sku, string category) => new() { SourceId = "fixture", Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 1, Category = category };
            store.Import(source, new[] { P("SKU-1", "Eski Ürünler"), P("SKU-2", "Elektronik"), P("SKU-3", "") });
            var products = store.Products(); string Id(string sku) => products.Single(p => p.Sku == sku).Id;
            new MarketplaceConnectionStore(root).Save("etsy", "S1", "Etsy S1", enabled: true);
            var plans = new ChannelProductsStore(root);
            foreach (var sku in new[] { "SKU-1", "SKU-2", "SKU-3" }) plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "S1", ProductId = Id(sku), Currency = "USD", PlannedPrice = 10, PlannedStock = 1 });
            old.Active = false; taxonomy.Save(old);

            // The matrix: the product in the deactivated category reads the blocking category state; its neighbours keep their plan state; the legend explains it.
            // The connection store seeds a default connection per channel, so the matrix has more columns than S1: read the S1 column.
            var rows = new ChannelListingMatrixService(root).Build().Where(r => r.Channel.Equals("etsy", StringComparison.OrdinalIgnoreCase) && r.ShopId == "S1").ToList();
            Assert.AreEqual(3, rows.Count);
            Assert.AreEqual(ChannelMatrixLegend.CategoryStale, rows.Single(r => r.ProductId == Id("SKU-1")).MappingStatus);
            Assert.AreEqual("MISSING", rows.Single(r => r.ProductId == Id("SKU-2")).MappingStatus, "a neighbour keeps its plan state"); Assert.AreEqual("MISSING", rows.Single(r => r.ProductId == Id("SKU-3")).MappingStatus);
            var legend = ChannelMatrixLegend.For(ChannelMatrixLegend.CategoryStale, "CONNECTED", false); Assert.AreEqual(ChannelMatrixLegend.CategoryStale, legend.Key); Assert.AreEqual(SeverityLevel.Blocking, legend.Level);

            // The quality scan: an Error for the blocked product, nothing for a mapped active category or no category.
            var scan = new DataQualityService(root).Scan().Where(i => i.Type == "StaleCategoryMapping").ToList();
            var blocked = scan.Single(i => i.ProductId == Id("SKU-1")); Assert.AreEqual("Error", blocked.Severity); StringAssert.Contains(blocked.Message, "pasif"); Assert.AreEqual("etsy", blocked.Marketplace); Assert.AreEqual("S1", blocked.ShopId);
            Assert.IsFalse(scan.Any(i => i.ProductId == Id("SKU-2"))); Assert.IsFalse(scan.Any(i => i.ProductId == Id("SKU-3")));

            // Restored: the category active again clears the cell and the finding.
            old.Active = true; taxonomy.Save(old);
            Assert.AreEqual("MISSING", new ChannelListingMatrixService(root).Build().Single(r => r.ProductId == Id("SKU-1") && r.ShopId == "S1").MappingStatus);
            var afterRestore = new DataQualityService(root).Scan().Where(i => i.Type == "StaleCategoryMapping" && i.ProductId == Id("SKU-1")).ToList();
            Assert.IsFalse(afterRestore.Any(i => i.Severity == "Error"), "nothing blocks any more");
            Assert.IsTrue(afterRestore.All(i => i.Severity == "Warning"), "the category record changed after the mapping was written, so the mapping is one to confirm -- a warning until it is written again");
            Thread.Sleep(5); taxonomy.Map(TaxonomyKind.Category, "e-9", old.Id, "etsy", "S1");
            Assert.IsFalse(new DataQualityService(root).Scan().Any(i => i.Type == "StaleCategoryMapping" && i.ProductId == Id("SKU-1")), "the mapping written again is restored: no finding");
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
