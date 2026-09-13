using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #920 (TAXONOMY: mapping rollback proposal). A rollback returns one external key of a channel and shop to the local
// entry it named before its latest change: the proposal names both targets, previews the return's impact and binds to
// the scope's snapshot version; it is blocked without a previous revision, when the previous target is gone or
// inactive, or when other keys were edited in the scope after the mapping's latest change; applying honours the stale
// guard and writes a ROLLBACK history entry; it all holds after a restart.
[TestClass]
public sealed class MappingRollbackTests
{
    static TaxonomyEntry Category(TaxonomyStore taxonomy, string name) => taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = name, Value = "" });
    static CatalogProduct P(string sku, string category) => new() { SourceId = "fixture", Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 1, Category = category };

    [TestMethod]
    public void AProposalNamesThePreviousTargetPreviewsTheReturnAndIsBlockedWhenItCannotBeClean()
    {
        var root = Path.Combine(Path.GetTempPath(), "rollback-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var taxonomy = new TaxonomyStore(root); var plans = new ChannelProductsStore(root);
            var electronics = Category(taxonomy, "Elektronik"); var garden = Category(taxonomy, "Bahçe"); var kitchen = Category(taxonomy, "Mutfak");
            store.Import(new XmlSource { Id = "fixture", Name = "Fixture" }, new[] { P("SKU-1", "Elektronik"), P("SKU-2", "Bahçe") });
            var products = store.Products();
            plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "S1", ProductId = products.Single(p => p.Sku == "SKU-1").Id, Currency = "USD", PlannedPrice = 10, PlannedStock = 1 });
            var planList = plans.List();

            // Clean: the key was moved from Elektronik to Bahçe; the proposal returns it to Elektronik, previews the return's impact and binds to the version.
            taxonomy.Map(TaxonomyKind.Category, "e-1", electronics.Id, "etsy", "S1"); Thread.Sleep(5);
            taxonomy.Map(TaxonomyKind.Category, "e-1", garden.Id, "etsy", "S1");
            var clean = MappingRollback.Propose(root, TaxonomyKind.Category, "e-1", "Etsy", "S1", products, planList);
            Assert.IsFalse(clean.Blocked); Assert.AreEqual("Bahçe", clean.CurrentName); Assert.AreEqual("Elektronik", clean.PreviousName); Assert.AreEqual(electronics.Id, clean.PreviousLocalId); Assert.AreEqual(0, clean.UnrelatedLaterEdits);
            Assert.IsNotNull(clean.Impact); Assert.AreEqual(1, clean.Impact!.AffectedProducts); Assert.AreEqual(1, clean.Impact.AffectedPlans); StringAssert.Contains(clean.Headline, "Bahçe → Elektronik"); Assert.IsTrue(clean.SnapshotVersion >= 1); StringAssert.Contains(clean.Body, "sürüm");

            // Concurrent edit: another key edited in the scope after this mapping's latest change blocks the proposal, with the count.
            Thread.Sleep(5); taxonomy.Map(TaxonomyKind.Category, "e-9", garden.Id, "etsy", "S1");
            var blocked = MappingRollback.Propose(root, TaxonomyKind.Category, "e-1", "etsy", "S1", products, planList);
            Assert.IsTrue(blocked.Blocked); Assert.AreEqual(1, blocked.UnrelatedLaterEdits); StringAssert.Contains(blocked.BlockReason, "1 başka düzenleme"); Assert.IsNull(blocked.Impact); Assert.AreEqual("Elektronik", blocked.PreviousName, "the previous target is still named so the operator knows what a review would return to");
            Assert.ThrowsException<InvalidOperationException>(() => MappingRollback.Apply(root, blocked), "a blocked proposal applies nothing");
            Assert.AreEqual(garden.Id, taxonomy.MappingViews(TaxonomyKind.Category, "etsy", "S1").Single(m => m.ExternalKey == "e-1").LocalId);

            // No previous revision and a deleted or inactive target: blocked by name -- in another shop, its own scope, so the edits above do not count.
            taxonomy.Map(TaxonomyKind.Category, "e-2", kitchen.Id, "etsy", "S2");
            StringAssert.Contains(MappingRollback.Propose(root, TaxonomyKind.Category, "e-2", "etsy", "S2", products, planList).BlockReason, "önceki bir sürüm yok");
            StringAssert.Contains(MappingRollback.Propose(root, TaxonomyKind.Category, "e-nope", "etsy", "S2", products, planList).BlockReason, "geçmişi yok");
            Thread.Sleep(5); taxonomy.Map(TaxonomyKind.Category, "e-3", kitchen.Id, "etsy", "S2"); Thread.Sleep(5); taxonomy.Map(TaxonomyKind.Category, "e-3", garden.Id, "etsy", "S2");
            Assert.IsFalse(MappingRollback.Propose(root, TaxonomyKind.Category, "e-3", "etsy", "S2", products, planList).Blocked);
            kitchen.Active = false; taxonomy.Save(kitchen);
            var inactive = MappingRollback.Propose(root, TaxonomyKind.Category, "e-3", "etsy", "S2", products, planList); Assert.IsTrue(inactive.Blocked); StringAssert.Contains(inactive.BlockReason, "pasif"); Assert.AreEqual("Mutfak", inactive.PreviousName);
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
            { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM TaxonomyEntries WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", kitchen.Id); cmd.ExecuteNonQuery(); }
            var deleted = MappingRollback.Propose(root, TaxonomyKind.Category, "e-3", "etsy", "S2", products, planList); Assert.IsTrue(deleted.Blocked); StringAssert.Contains(deleted.BlockReason, "silinmiş");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ApplyingReturnsTheMappingAsARollbackEntryHonoursTheStaleGuardAndSurvivesARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "rollback-apply-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var taxonomy = new TaxonomyStore(root); var plans = new ChannelProductsStore(root); var snapshots = new TaxonomySnapshotStore(root);
            var electronics = Category(taxonomy, "Elektronik"); var garden = Category(taxonomy, "Bahçe");
            store.Import(new XmlSource { Id = "fixture", Name = "Fixture" }, new[] { P("SKU-1", "Elektronik") });
            var products = store.Products();
            taxonomy.Map(TaxonomyKind.Category, "e-1", electronics.Id, "etsy", "S1"); Thread.Sleep(5); taxonomy.Map(TaxonomyKind.Category, "e-1", garden.Id, "etsy", "S1");

            // Stale: the taxonomy moved after the proposal (a mapping written elsewhere) -> refused with both versions, nothing returned.
            var proposal = MappingRollback.Propose(root, TaxonomyKind.Category, "e-1", "etsy", "S1", products, plans.List());
            Thread.Sleep(5); taxonomy.Map(TaxonomyKind.Category, "e-7", garden.Id, "etsy", "S1");
            var refused = Assert.ThrowsException<InvalidOperationException>(() => MappingRollback.Apply(root, proposal));
            StringAssert.Contains(refused.Message, "yeniden alın"); Assert.AreEqual(garden.Id, taxonomy.MappingViews(TaxonomyKind.Category, "etsy", "S1").Single(m => m.ExternalKey == "e-1").LocalId);

            // Clean after the operator reviews: the unrelated edit blocks a fresh proposal until it is the newest change no more -- here the key is mapped again so its own change is the latest.
            Thread.Sleep(5); taxonomy.Map(TaxonomyKind.Category, "e-1", garden.Id, "etsy", "S1");
            var fresh = MappingRollback.Propose(root, TaxonomyKind.Category, "e-1", "etsy", "S1", products, plans.List());
            Assert.IsFalse(fresh.Blocked); Assert.AreEqual("Elektronik", fresh.PreviousName);
            var after = MappingRollback.Apply(root, fresh);
            Assert.AreEqual(electronics.Id, taxonomy.MappingViews(TaxonomyKind.Category, "etsy", "S1").Single(m => m.ExternalKey == "e-1").LocalId, "the key is back on its previous target");
            var newest = taxonomy.History(TaxonomyKind.Category, "etsy", "S1").First(); Assert.AreEqual("e-1", newest.ExternalKey); Assert.AreEqual(MappingRollback.Action, newest.Action); Assert.AreEqual(electronics.Id, newest.LocalId);
            Assert.AreEqual(fresh.SnapshotVersion + 1, after.Version); Assert.AreEqual(after.Version, snapshots.Current(fresh.Scope)!.Version);
            Assert.ThrowsException<InvalidOperationException>(() => MappingRollback.Apply(root, fresh), "the same proposal is stale once applied");

            // Restart: the history and the versions persist; a new proposal after the restart would return the key to Bahçe (its now-previous target).
            SqliteConnection.ClearAllPools();
            var again = MappingRollback.Propose(root, TaxonomyKind.Category, "e-1", "etsy", "S1", new CatalogStore(root).Products(), new ChannelProductsStore(root).List());
            Assert.IsFalse(again.Blocked); Assert.AreEqual("Elektronik", again.CurrentName); Assert.AreEqual("Bahçe", again.PreviousName); Assert.AreEqual(after.Version, again.SnapshotVersion);
            Assert.AreEqual(MappingRollback.Action, new TaxonomyStore(root).History(TaxonomyKind.Category, "etsy", "S1").First().Action);
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
