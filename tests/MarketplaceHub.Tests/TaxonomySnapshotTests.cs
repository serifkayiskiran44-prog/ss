using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #918 (TAXONOMY: snapshot versioning). Every refresh of the taxonomy content is an immutable snapshot: a hash, a
// version that moves only when the content changed, the moment and the scope (the local dictionary; per channel and
// shop, the local content plus that channel's mappings). An unchanged refresh confirms the current version; a refresh
// that cannot read its content is FAILED with the reason and the current version stays; a channel plan says which
// snapshot it was validated with and the quality scan says when that snapshot is no longer the current one; it all
// survives a restart.
[TestClass]
public sealed class TaxonomySnapshotTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static TaxonomyEntry Entry(TaxonomyStore taxonomy, TaxonomyKind kind, string name) => taxonomy.Save(new TaxonomyEntry { Kind = kind, Name = name, Value = kind == TaxonomyKind.Brand ? name : "" });

    [TestMethod]
    public void AVersionMovesOnlyWithTheContentAFailedRefreshKeepsTheLastGoodOneAndScopesAreTheirOwn()
    {
        var root = Path.Combine(Path.GetTempPath(), "snap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var taxonomy = new TaxonomyStore(root); var aliases = new TaxonomyAliasStore(root); var snapshots = new TaxonomySnapshotStore(root);
            var electronics = Entry(taxonomy, TaxonomyKind.Category, "Elektronik");
            Assert.IsNull(snapshots.Current(TaxonomySnapshotStore.LocalScope)); Assert.AreEqual("taksonomi sürümü yok", TaxonomySnapshotStore.Describe(null, Now));

            // Refresh: the first is version 1 with a hash and a moment.
            var first = snapshots.Refresh(TaxonomySnapshotStore.LocalScope, Now);
            Assert.AreEqual(1, first.Version); Assert.AreEqual(32, first.Hash.Length); Assert.AreEqual(Now, first.RecordedUtc); Assert.IsTrue(first.IsOk);
            // Unchanged: the same content confirms the version -- the check time moves, the record time and the version do not.
            var unchanged = snapshots.Refresh(TaxonomySnapshotStore.LocalScope, Now.AddMinutes(10));
            Assert.AreEqual(1, unchanged.Version); Assert.AreEqual(first.Hash, unchanged.Hash); Assert.AreEqual(Now, unchanged.RecordedUtc); Assert.AreEqual(Now.AddMinutes(10), unchanged.CheckedUtc);
            Assert.AreEqual(1, snapshots.History(TaxonomySnapshotStore.LocalScope).Count, "an unchanged refresh is not a new record");
            // Changed: an alias added is version 2; a category renamed is version 3.
            aliases.Save("elektronik ürünleri", electronics.Id, approved: true);
            var second = snapshots.Refresh(TaxonomySnapshotStore.LocalScope, Now.AddMinutes(20)); Assert.AreEqual(2, second.Version); Assert.AreNotEqual(first.Hash, second.Hash);
            electronics.Name = "Elektronik & Teknoloji"; taxonomy.Save(electronics);
            Assert.AreEqual(3, snapshots.Refresh(TaxonomySnapshotStore.LocalScope, Now.AddMinutes(30)).Version);
            // Failed: a refresh that cannot read its content is recorded with the reason; the current version stays the last good one.
            var failed = snapshots.Refresh(TaxonomySnapshotStore.LocalScope, Now.AddMinutes(40), () => throw new IOException("disk okunamadı: token=abc"));
            Assert.AreEqual(TaxonomySnapshot.Failed, failed.Outcome); Assert.AreEqual(3, failed.Version); StringAssert.Contains(failed.Error, "disk okunamadı"); Assert.IsFalse(failed.Error.Contains("abc"), "the reason is redacted");
            Assert.AreEqual(3, snapshots.Current(TaxonomySnapshotStore.LocalScope)!.Version); Assert.IsTrue(snapshots.Current(TaxonomySnapshotStore.LocalScope)!.IsOk);
            var history = snapshots.History(TaxonomySnapshotStore.LocalScope); Assert.AreEqual(4, history.Count); Assert.AreEqual(TaxonomySnapshot.Failed, history[0].Outcome);
            StringAssert.Contains(TaxonomySnapshotStore.Describe(failed, Now.AddMinutes(41)), "başarısız"); StringAssert.Contains(TaxonomySnapshotStore.Describe(snapshots.Current(TaxonomySnapshotStore.LocalScope), Now.AddMinutes(41)), "taksonomi sürümü 3");
            Assert.AreEqual(3, snapshots.Refresh(TaxonomySnapshotStore.LocalScope, Now.AddMinutes(50)).Version, "after a failure the next good refresh of unchanged content is still version 3");

            // Scopes: a channel scope is the local content plus that channel's mappings -- a mapping change moves the channel version and not the local one.
            var channel = TaxonomySnapshotStore.ChannelScope("Etsy", "S1");
            Assert.AreEqual("channel:etsy/S1", channel);
            var channelFirst = snapshots.Refresh(channel, Now.AddHours(1)); Assert.AreEqual(1, channelFirst.Version);
            taxonomy.Map(TaxonomyKind.Category, "e-123", electronics.Id, "etsy", "S1");
            Assert.AreEqual(2, snapshots.Refresh(channel, Now.AddHours(2)).Version); Assert.AreEqual(3, snapshots.Refresh(TaxonomySnapshotStore.LocalScope, Now.AddHours(2)).Version, "the local scope did not change");
            Assert.AreEqual(1, snapshots.Refresh(TaxonomySnapshotStore.ChannelScope("etsy", "S2"), Now.AddHours(2)).Version, "another shop is its own scope");

            // Restart.
            SqliteConnection.ClearAllPools();
            var reopened = new TaxonomySnapshotStore(root);
            Assert.AreEqual(3, reopened.Current(TaxonomySnapshotStore.LocalScope)!.Version); Assert.AreEqual(2, reopened.Current(channel)!.Version); Assert.AreEqual(4, reopened.History(TaxonomySnapshotStore.LocalScope).Count);
            Assert.AreEqual(second.Hash, reopened.History(TaxonomySnapshotStore.LocalScope).Single(s => s.Version == 2).Hash);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AChannelPlanSaysWhichSnapshotValidatedItAndTheScanSaysWhenThatIsStale()
    {
        var root = Path.Combine(Path.GetTempPath(), "snap-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var taxonomy = new TaxonomyStore(root); var snapshots = new TaxonomySnapshotStore(root); var plans = new ChannelProductsStore(root);
            var electronics = Entry(taxonomy, TaxonomyKind.Category, "Elektronik");
            taxonomy.Map(TaxonomyKind.Category, "e-123", electronics.Id, "etsy", "S1");
            store.Import(new XmlSource { Id = "fixture", Name = "Fixture" }, new[] { new CatalogProduct { SourceId = "fixture", Sku = "SKU-1", Name = "Ürün", Price = 10, Currency = "TRY", Cost = 4, Stock = 1, Category = "Elektronik" } });
            var product = store.Products().Single();

            // Saving a plan validates it against the current channel snapshot and stamps the version.
            plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "S1", ProductId = product.Id, Currency = "USD", PlannedPrice = 10, PlannedStock = 1 });
            var plan = plans.Find("etsy", "S1", product.Id)!; var scope = TaxonomySnapshotStore.ChannelScope("etsy", "S1");
            Assert.AreEqual(scope, plan.TaxonomySnapshotScope); Assert.AreEqual(snapshots.Current(scope)!.Version, plan.TaxonomySnapshotVersion); Assert.IsTrue(plan.TaxonomySnapshotVersion >= 1);
            Assert.IsFalse(new DataQualityService(root).Scan().Any(i => i.Type == "MappingSnapshotStale"), "validated with the current snapshot");

            // The taxonomy changes under the plan: the scan refreshes the scope and says the plan was validated with an older snapshot.
            taxonomy.Map(TaxonomyKind.Category, "e-999", electronics.Id, "etsy", "S1");
            var stale = new DataQualityService(root).Scan().Where(i => i.Type == "MappingSnapshotStale").ToList();
            Assert.AreEqual(1, stale.Count); Assert.AreEqual(product.Id, stale[0].ProductId); Assert.AreEqual("Warning", stale[0].Severity); StringAssert.Contains(stale[0].Message, plan.TaxonomySnapshotVersion.ToString()); StringAssert.Contains(stale[0].Message, snapshots.Current(scope)!.Version.ToString());
            Assert.IsTrue(snapshots.Current(scope)!.Version > plan.TaxonomySnapshotVersion);

            // Saving the plan again validates it with the current snapshot; a restart reads the stamp back.
            plans.Save(plan);
            Assert.IsFalse(new DataQualityService(root).Scan().Any(i => i.Type == "MappingSnapshotStale"));
            SqliteConnection.ClearAllPools();
            var back = new ChannelProductsStore(root).Find("etsy", "S1", product.Id)!; Assert.AreEqual(new TaxonomySnapshotStore(root).Current(scope)!.Version, back.TaxonomySnapshotVersion);
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
