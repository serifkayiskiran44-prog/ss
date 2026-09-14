using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #940 (STOCK: source-versus-local reconciliation report). For every product and store: the latest source stock, the
// units held, the calculated available with its state, the last channel-observed stock with its age, and the verdict
// -- equal, mismatch with the difference, stale channel observation, no channel observation, no local figure -- across
// stores, from the remote stock store and the last succeeded stock dispatches; read-only: nothing is written.
[TestClass]
public sealed class StockReconciliationTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    static (CatalogStore Store, Dictionary<string, string> Ids) Seed(string root)
    {
        var store = new CatalogStore(root); var runs = new XmlRunStore(root);
        var source = new XmlSource { Id = "src", Name = "Kaynak", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" }, IntervalMinutes = 30 }; store.SaveSource(source);
        var run = runs.Start("src", "h-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromMinutes(5), source.ConfigRevision);
        store.Import(source, new[] { "SKU-1", "SKU-2" }.Select(sku => new CatalogProduct { SourceId = "src", SourceKind = "xml", Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 10, Active = true }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = source.ConfigRevision, ObservedAtUtc = Now.AddHours(-1) });
        runs.Complete(run, new ImportSummary(2, 0, 0));
        return (store, store.Products().ToDictionary(p => p.Sku, p => p.Id));
    }

    [TestMethod]
    public void EveryProductAndStoreGetsAVerdictEqualMismatchStaleMissingRemoteOrNoLocalFigureAndNothingIsWritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "reconcile-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var (store, ids) = Seed(root);
            store.SaveStockPolicy(new StockPolicy { Channel = "local", Shop = "s1", SafetyStock = 1, MaximumStock = null, Enabled = true });
            store.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "S1", SafetyStock = 0, MaximumStock = null, Enabled = true });
            store.SaveStockPolicy(new StockPolicy { Channel = "ebay", Shop = "E1", SafetyStock = 0, MaximumStock = null, Enabled = false });
            store.Reserve(ids["SKU-1"], "etsy", "S1", "o-1", 3, Now, TimeSpan.FromHours(48));
            var remote = new[]
            {
                new RemoteStockObservation("local", "s1", ids["SKU-1"], 9, Now.AddHours(-1), "okuma"),   // equal: 10 - 1 = 9
                new RemoteStockObservation("etsy", "S1", ids["SKU-1"], 5, Now.AddHours(-2), "okuma"),    // mismatch: 10 - 0 - 3 held = 7 vs 5
                new RemoteStockObservation("local", "s1", ids["SKU-2"], 9, Now.AddHours(-30), "okuma"),  // stale: a day and more
            };
            var stores = new[] { ("local", "s1"), ("etsy", "S1"), ("ebay", "E1"), ("ozon", "O1") };
            var before = new SyncStore(root).List().Count;

            var report = StockReconciliation.Build(store, store.Products(), stores, remote, Now);
            Assert.AreEqual(8, report.Rows.Count, "two products across four stores"); Assert.AreEqual((1, 1, 1, 1, 4), (report.Equal, report.Mismatch, report.Stale, report.MissingRemote, report.Blocked), report.Headline);
            StringAssert.Contains(report.Headline, "8 satır: 1 eşit, 1 uyumsuz, 1 bayat kanal gözlemi, 1 kanal gözlemi yok, 4 yerel rakam yok; salt okunur — hiçbir şey yazılmadı.");
            ChannelStockReconciliationRow Row(string sku, string channel, string shop) => report.Rows.Single(r => r.Sku == sku && r.Channel == channel && r.Shop == shop);
            var equal = Row("SKU-1", "local", "s1"); Assert.AreEqual((ChannelStockReconciliationRow.Equal, 9, 9, 0, 10, 0), (equal.Verdict, equal.Available, equal.RemoteStock, equal.Difference, equal.SourceStock, equal.Reserved)); StringAssert.Contains(equal.Words, "kaynak 10 (1 sa önce) · rezerve 0 · hesaplanan 9 (FRESH; stok 10, tampon 1, rezerve 0) · kanal 9 (1 sa önce; okuma) · eşit");
            var mismatch = Row("SKU-1", "etsy", "S1"); Assert.AreEqual((ChannelStockReconciliationRow.Mismatch, 7, 5, 2, 3), (mismatch.Verdict, mismatch.Available, mismatch.RemoteStock, mismatch.Difference, mismatch.Reserved)); StringAssert.Contains(mismatch.Words, "uyumsuz: yerel 7, kanal 5 (fark +2)");
            var stale = Row("SKU-2", "local", "s1"); Assert.AreEqual((ChannelStockReconciliationRow.Stale, ChannelStockReconciliationRow.RemoteStale, 0), (stale.Verdict, stale.RemoteState, stale.Difference)); StringAssert.Contains(stale.Words, "bayat"); StringAssert.Contains(stale.Words, "güvenilmez");
            var missing = Row("SKU-2", "etsy", "S1"); Assert.AreEqual((ChannelStockReconciliationRow.MissingRemote, ChannelStockReconciliationRow.RemoteUnknown, 10), (missing.Verdict, missing.RemoteState, missing.Available)); StringAssert.Contains(missing.Words, "kanal stoku bilinmiyor");
            var blocked = Row("SKU-1", "ebay", "E1"); Assert.AreEqual((ChannelStockReconciliationRow.LocalBlocked, ChannelStockReconciliationRow.LocalBlocked), (blocked.Verdict, blocked.LocalState)); StringAssert.Contains(blocked.LocalWords, "pasif");
            var noRule = Row("SKU-2", "ozon", "O1"); Assert.AreEqual((ChannelStockReconciliationRow.NoRule, ChannelStockReconciliationRow.NoRule), (noRule.Verdict, noRule.LocalState)); StringAssert.Contains(noRule.Words, "stok ayarı yok");
            Assert.AreEqual(ChannelStockReconciliationRow.Mismatch, report.Rows[0].Verdict, "mismatches come first"); Assert.AreEqual(2, report.ForStore("etsy", "S1").Count);

            // Read-only: no sync job, no product, no reservation changed.
            Assert.AreEqual(before, new SyncStore(root).List().Count); Assert.AreEqual(10, store.Products().Single(p => p.Sku == "SKU-1").Stock); Assert.AreEqual(1, new StockReservationStore(root).List().Count);
            Assert.AreEqual(2, StockReconciliation.Build(store, store.Products(), stores, remote, Now.AddDays(2)).Stale + StockReconciliation.Build(store, store.Products(), stores, remote, Now.AddDays(2)).Equal - 1, "two days later the fresh observations have gone stale too");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheStoreReportReadsTheRemoteStoreAndTheLastSucceededDispatchesAndTheRemoteStoreReadsBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "reconcile-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var (store, ids) = Seed(root); var sync = new SyncStore(root); var remote = new RemoteStockStore(root);
            store.SaveStockPolicy(new StockPolicy { Channel = "local", Shop = "s1", SafetyStock = 0, MaximumStock = null, Enabled = true });
            store.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "S1", SafetyStock = 0, MaximumStock = null, Enabled = true });

            // A marketplace read for SKU-1 on local; the last succeeded stock dispatch for SKU-2 on local (its payload is the figure sent); an older read never replaces a newer one.
            remote.Record(new RemoteStockObservation("local", "s1", ids["SKU-1"], 10, Now.AddMinutes(-30), "pazaryeri okuması"));
            remote.Record(new RemoteStockObservation("local", "s1", ids["SKU-1"], 3, Now.AddHours(-5), "eski okuma")); Assert.AreEqual(10, remote.List().Single().Stock, "an older observation is ignored");
            var failed = sync.EnqueueFailed(new SyncRequest("local", "stock", ids["SKU-2"], $"{ids["SKU-2"]}:1:r1:4", "s1"), "deneme");
            var job = sync.Enqueue(new SyncRequest("local", "stock", ids["SKU-2"], $"{ids["SKU-2"]}:2:r1:6", "s1")); sync.Succeed(job.Id);
            var fromSync = StockReconciliation.FromSync(sync.List()); Assert.AreEqual((1, 6, StockReconciliation.SyncSource), (fromSync.Count, fromSync[0].Stock, fromSync[0].Source));

            var jobsBefore = sync.List().Count;
            var report = store.ReconcileStock(Now);
            Assert.AreEqual(4, report.Rows.Count); Assert.AreEqual((1, 1, 2), (report.Equal, report.Mismatch, report.MissingRemote), report.Headline);
            var read = report.Rows.Single(r => r.Sku == "SKU-1" && r.Channel == "local"); Assert.AreEqual((ChannelStockReconciliationRow.Equal, 10), (read.Verdict, read.RemoteStock)); StringAssert.Contains(read.Words, "pazaryeri okuması");
            var sent = report.Rows.Single(r => r.Sku == "SKU-2" && r.Channel == "local"); Assert.AreEqual((ChannelStockReconciliationRow.Mismatch, 6, 4), (sent.Verdict, sent.RemoteStock, sent.Difference)); StringAssert.Contains(sent.Words, "kanal 6 (");
            Assert.IsTrue(report.Rows.Where(r => r.Channel == "etsy").All(r => r.Verdict == ChannelStockReconciliationRow.MissingRemote), "the other store has no observations at all");
            Assert.AreEqual(jobsBefore, sync.List().Count, "the report enqueues nothing"); Assert.AreEqual(SyncStatus.Failed, sync.List().Single(j => j.Id == failed.Id).Status, "and touches nothing");

            // Restart: the remote observation reads back.
            SqliteConnection.ClearAllPools();
            Assert.AreEqual(10, new RemoteStockStore(root).List().Single().Stock); Assert.AreEqual(ChannelStockReconciliationRow.Equal, new CatalogStore(root).ReconcileStock(Now).Rows.Single(r => r.Sku == "SKU-1" && r.Channel == "local").Verdict);
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
