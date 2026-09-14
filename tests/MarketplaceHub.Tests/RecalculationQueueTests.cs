using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #931 (PRICING: recalculation queue deduplication). One pending recalculation job stands per product, shop and
// operation: a new key cancels the older pending ones as superseded, the same key is the same job; a job whose rule
// revision or product version moved on is stale -- the runner cancels it before queueing anew and a dispatcher must
// not apply it; a cancelled job stays cancelled; it all reads back after a restart.
[TestClass]
public sealed class RecalculationQueueTests
{
    [TestMethod]
    public void OnePendingJobPerProductShopAndOperationAndAStaleKeyIsNeverApplied()
    {
        var root = Path.Combine(Path.GetTempPath(), "recalc-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CatalogStore(root); var sync = new SyncStore(root);
            catalog.Import(new XmlSource { Id = "src", Name = "Src" }, new[] { new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Bir", Cost = 100m, CostCurrency = "TRY", Price = 200m, Currency = "TRY", Stock = 10, Active = true } });
            var product = catalog.Products().Single();
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "s1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 0, EstimatedShippingTry = 0, TransactionCostTry = 0, VatRatePercent = 0 });
            var rule = catalog.GetPricePolicy("local", "s1")!.Version;

            // The key: product, its version, the rule revision, the payload; parsed back; an old three-part key is not judged.
            var key = new RecalculationKey(product.Id, product.UpdatedUtc.Ticks, rule, "200.00");
            Assert.AreEqual($"{product.Id}:{product.UpdatedUtc.Ticks}:r{rule}:200.00", key.Version); Assert.AreEqual(key, RecalculationKey.Parse(key.Version));
            Assert.IsNull(RecalculationKey.Parse($"{product.Id}:123:200.00")); Assert.AreEqual("a:b", RecalculationKey.Parse("p:1:r2:a:b")!.Payload);

            // Coalescing: three keys for the same product and shop leave one pending job; the same key is the same job.
            var first = RecalculationQueue.Enqueue(sync, "local", "s1", "price", product.Id, key);
            var second = RecalculationQueue.Enqueue(sync, "local", "s1", "price", product.Id, key with { Payload = "201.00" });
            var third = RecalculationQueue.Enqueue(sync, "local", "s1", "price", product.Id, key with { Payload = "202.00" });
            Assert.AreEqual((0, 1, 1), (first.Coalesced, second.Coalesced, third.Coalesced));
            Assert.AreEqual(1, sync.List().Count(j => j.Status == SyncStatus.Pending)); Assert.AreEqual(third.Job.Id, sync.List().Single(j => j.Status == SyncStatus.Pending).Id);
            Assert.AreEqual(2, sync.List().Count(j => j.Status == SyncStatus.Cancelled));
            var again = RecalculationQueue.Enqueue(sync, "local", "s1", "price", product.Id, key with { Payload = "202.00" }); Assert.AreEqual(third.Job.Id, again.Job.Id); Assert.AreEqual(0, again.Coalesced);
            RecalculationQueue.Enqueue(sync, "local", "s2", "price", product.Id, key); RecalculationQueue.Enqueue(sync, "local", "s1", "stock", product.Id, key with { RuleVersion = RecalculationQueue.RuleVersion(catalog, "stock", "local", "s1"), Payload = "10" });
            Assert.AreEqual(3, sync.List().Count(j => j.Status == SyncStatus.Pending), "another shop and another operation are their own queues");

            // The verdict: current now; stale once the rule or the product moves on, or the product is gone; a dispatcher must not apply a stale one.
            var pending = sync.List().Single(j => j.Status == SyncStatus.Pending && j.ShopId == "s1" && j.Operation == "price");
            Assert.IsTrue(RecalculationQueue.Verdict(pending, catalog).Current);
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "s1", Version = rule, Formula = "x*3", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 0, EstimatedShippingTry = 0, TransactionCostTry = 0, VatRatePercent = 0 });
            var staleRule = RecalculationQueue.Verdict(pending, catalog); Assert.IsFalse(staleRule.Current); StringAssert.Contains(staleRule.Words, "kural sürümü değişti");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => RecalculationQueue.EnsureCurrent(pending, catalog)).Message, "Bayat yeniden hesaplama sonucu uygulanmaz");
            var stockJob = sync.List().Single(j => j.Status == SyncStatus.Pending && j.Operation == "stock");
            Assert.IsTrue(RecalculationQueue.Verdict(stockJob, catalog).Current, "the stock job depends on the stock policy, not the price rule");
            product.Name = "Bir (düzenlendi)"; catalog.SaveProduct(product);
            StringAssert.Contains(RecalculationQueue.Verdict(stockJob, catalog).Words, "ürün iş kuyruğa alındıktan sonra değişti");

            // Reconcile cancels the stale pending jobs of the shop, leaves the other shop's; a cancelled job stays cancelled, and its key never comes back to life.
            Assert.AreEqual(2, RecalculationQueue.Reconcile(sync, catalog, "local", "s1"));
            Assert.AreEqual(1, sync.List().Count(j => j.Status == SyncStatus.Pending)); Assert.AreEqual("s2", sync.List().Single(j => j.Status == SyncStatus.Pending).ShopId);
            var revived = RecalculationQueue.Enqueue(sync, "local", "s1", "price", product.Id, key with { Payload = "202.00" });
            Assert.AreEqual(SyncStatus.Cancelled, revived.Job.Status, "the same key returns the cancelled job, never a fresh one");

            // Restart: the queue's states persist.
            SqliteConnection.ClearAllPools();
            var reopened = new SyncStore(root).List();
            Assert.AreEqual(1, reopened.Count(j => j.Status == SyncStatus.Pending)); Assert.AreEqual(4, reopened.Count(j => j.Status == SyncStatus.Cancelled), "two coalesced and two reconciled");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheRunnerLeavesOnePendingJobAcrossABurstANewRevisionAndACancel()
    {
        var root = Path.Combine(Path.GetTempPath(), "recalc-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CatalogStore(root); var sync = new SyncStore(root); var automation = new AutomationStore(root);
            catalog.Import(new XmlSource { Id = "src", Name = "Src" }, new[] { new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Bir", Cost = 100m, CostCurrency = "TRY", Stock = 10, Active = true } });
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "s1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 0, EstimatedShippingTry = 0, TransactionCostTry = 0, VatRatePercent = 0 });
            automation.Save(new AutomationJob { Kind = AutomationKind.Price, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "local", Shop = "s1" });
            string JobId() => automation.List().Single().Id;
            void Run() { var j = automation.Get(JobId()); j.NextRunUtc = DateTime.UtcNow.AddMinutes(-1); j.LockedUntilUtc = null; j.Enabled = true; automation.Save(j); AutomationRunner.RunDue(catalog, automation, sync, JobId(), DateTime.UtcNow); }
            int Pending() => sync.List().Count(j => j.Status == SyncStatus.Pending && j.Operation == "price");

            // Burst: the product changes between runs; every run recomputes; one pending job stands, the older ones are superseded.
            Run(); Assert.AreEqual(1, Pending());
            for (var i = 0; i < 3; i++) { var p = catalog.Products().Single(); p.Cost = 100m + i + 1; catalog.SaveProduct(p); Thread.Sleep(5); Run(); }
            Assert.AreEqual(1, Pending()); Assert.IsTrue(sync.List().Count(j => j.Status == SyncStatus.Cancelled) >= 3, "the superseded jobs are cancelled, not left dispatchable");
            var standing = sync.List().Single(j => j.Status == SyncStatus.Pending && j.Operation == "price");
            StringAssert.EndsWith(standing.Version, ":206.00", "the standing job carries the latest payload (cost 103 doubled)");
            Assert.IsTrue(RecalculationQueue.Verdict(standing, catalog).Current);

            // A new rule revision: the standing job is stale; the next run cancels it before queueing the recomputed one under the new revision.
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "s1", Version = catalog.GetPricePolicy("local", "s1")!.Version, Formula = "x*3", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 0, EstimatedShippingTry = 0, TransactionCostTry = 0, VatRatePercent = 0 });
            Assert.IsFalse(RecalculationQueue.Verdict(standing, catalog).Current);
            Run();
            Assert.AreEqual(1, Pending()); var fresh = sync.List().Single(j => j.Status == SyncStatus.Pending && j.Operation == "price");
            Assert.AreNotEqual(standing.Id, fresh.Id); StringAssert.EndsWith(fresh.Version, ":309.00"); StringAssert.Contains(fresh.Version, $":r{catalog.GetPricePolicy("local", "s1")!.Version}:");
            Assert.AreEqual(SyncStatus.Cancelled, sync.Get(standing.Id).Status);

            // Cancel: the operator cancels the standing job; a run with nothing changed brings nothing back.
            Assert.IsTrue(sync.Cancel(fresh.Id)); Run();
            Assert.AreEqual(0, Pending(), "the same key returns the cancelled job; nothing new is queued until the product or the rule changes");

            // Restart: the queue reads back as it was.
            SqliteConnection.ClearAllPools();
            Assert.AreEqual(0, new SyncStore(root).List().Count(j => j.Status == SyncStatus.Pending));
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
