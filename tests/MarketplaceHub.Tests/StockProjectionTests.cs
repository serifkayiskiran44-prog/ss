using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #932 (STOCK: source stock freshness state). The availability projection carries the source stock's freshness as
// its own state -- fresh within the source's threshold (or the operator's own entry), stale beyond it, missing without
// an observation, frozen when the source is off -- beside the number; the runner never writes a stale, missing or
// frozen source stock by itself; the previews say the state; it all reads back after a restart.
[TestClass]
public sealed class StockProjectionTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    static XmlSource Source(string id) => new() { Id = id, Name = "Kaynak " + id, ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" }, IntervalMinutes = 30 };
    static CatalogProduct Row(string sourceId, string sku, int stock) => new() { SourceId = sourceId, SourceKind = "xml", Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "TRY", Cost = 4, Stock = stock, Active = true };
    static void ImportAt(CatalogStore store, string root, XmlSource source, CatalogProduct row, DateTime observedUtc)
    {
        var runs = new XmlRunStore(root);
        var run = runs.Start(source.Id, "h-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromMinutes(5), source.ConfigRevision);
        store.Import(source, new[] { row }, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = source.ConfigRevision, ObservedAtUtc = observedUtc });
        runs.Complete(run, new ImportSummary(1, 0, 0));
    }

    [TestMethod]
    public void TheProjectionSaysFreshStaleMissingOrFrozenBesideTheNumberAndReadsBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "stock-proj-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root);
            var a = Source("src-a"); var b = Source("src-b"); var c = Source("src-c"); store.SaveSource(a); store.SaveSource(b); store.SaveSource(c);
            store.SaveStockPolicy(new StockPolicy { Channel = "local", Shop = "s1", SafetyStock = 2, MaximumStock = 100, Enabled = true });
            ImportAt(store, root, a, Row("src-a", "FRESH", 10), Now.AddHours(-1));
            ImportAt(store, root, b, Row("src-b", "STALE", 10), Now.AddHours(-10));
            ImportAt(store, root, c, Row("src-c", "FROZEN", 10), Now.AddHours(-1));
            var ids = store.Products().ToDictionary(p => p.Sku, p => p.Id);

            // Fresh: within the source's threshold (three times a 30-minute interval, at least six hours); the number is the shop's arithmetic.
            var fresh = store.ProjectStock("local", "s1", ids["FRESH"], Now);
            Assert.AreEqual((StockProjection.Fresh, 10, 8, 2, true), (fresh.State, fresh.Stock, fresh.Available, fresh.SafetyStock, fresh.Dispatchable)); Assert.AreEqual(TimeSpan.FromHours(6), fresh.Threshold); Assert.AreEqual("src-a", fresh.SourceId); StringAssert.Contains(fresh.Words, "kaynak stoku taze"); StringAssert.Contains(fresh.Words, "gösterilebilir 8");
            Assert.AreEqual(8, store.PreviewStock("local", "s1", ids["FRESH"]), "the old preview is the projection's number"); StringAssert.Contains(store.PreviewStockDetailed("local", "s1", ids["FRESH"]).Freshness, StockProjection.Fresh);

            // Stale: beyond the threshold -- the number is still computed, said untrustworthy, not dispatchable.
            var stale = store.ProjectStock("local", "s1", ids["STALE"], Now);
            Assert.AreEqual((StockProjection.Stale, 8, false), (stale.State, stale.Available, stale.Dispatchable)); Assert.AreEqual(Now.AddHours(-10), stale.ObservedUtc); StringAssert.Contains(stale.Words, "kaynak stoku bayat"); StringAssert.Contains(stale.Words, "otomatik stok yazımı yapılmaz");

            // Frozen: the source is switched off; the operator's own entry is fresh; a product without an observation is missing.
            c.Enabled = false; store.SaveSource(c);
            var frozen = store.ProjectStock("local", "s1", ids["FROZEN"], Now); Assert.AreEqual(StockProjection.Frozen, frozen.State); StringAssert.Contains(frozen.Words, "tazelenmez");
            var edited = store.Products().Single(p => p.Sku == "STALE"); edited.Stock = 30; store.SaveProduct(edited);
            var manual = store.ProjectStock("local", "s1", ids["STALE"], Now); Assert.AreEqual((StockProjection.Fresh, 28), (manual.State, manual.Available)); StringAssert.Contains(manual.Words, "elle");
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
            { conn.Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "UPDATE CatalogProducts SET Json=json_remove(Json,'$.FieldOrigins') WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", ids["FRESH"]); Assert.AreEqual(1, cmd.ExecuteNonQuery()); }
            var missing = store.ProjectStock("local", "s1", ids["FRESH"], Now); Assert.AreEqual((StockProjection.Missing, 8, false), (missing.State, missing.Available, missing.Dispatchable)); StringAssert.Contains(missing.Words, "gözlem zamanı yok");

            // The shop's arithmetic: the maximum caps, an inactive product is nothing; no policy or a disabled one refuses by name.
            store.SaveStockPolicy(new StockPolicy { Channel = "local", Shop = "s1", Version = store.GetStockPolicy("local", "s1")!.Version, SafetyStock = 2, MaximumStock = 5, Enabled = true });
            Assert.AreEqual(5, store.ProjectStock("local", "s1", ids["STALE"], Now).Available);
            var off = store.Products().Single(p => p.Sku == "STALE"); off.Active = false; store.SaveProduct(off); Assert.AreEqual(0, store.ProjectStock("local", "s1", ids["STALE"], Now).Available);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.ProjectStock("local", "s9", ids["STALE"], Now)).Message, "stok ayarını kaydedin");

            // Restart: the states read back as they were.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root);
            Assert.AreEqual(StockProjection.Frozen, reopened.ProjectStock("local", "s1", ids["FROZEN"], Now).State); Assert.AreEqual(StockProjection.Missing, reopened.ProjectStock("local", "s1", ids["FRESH"], Now).State);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheRunnerWritesOnlyAFreshSourceStockAndRefusesTheRestByName()
    {
        var root = Path.Combine(Path.GetTempPath(), "stock-proj-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var sync = new SyncStore(root); var automation = new AutomationStore(root);
            var a = Source("src-a"); var b = Source("src-b"); store.SaveSource(a); store.SaveSource(b);
            store.SaveStockPolicy(new StockPolicy { Channel = "local", Shop = "s1", SafetyStock = 1, MaximumStock = null, Enabled = true });
            ImportAt(store, root, a, Row("src-a", "FRESH", 10), DateTime.UtcNow.AddMinutes(-30));
            ImportAt(store, root, b, Row("src-b", "STALE", 10), DateTime.UtcNow.AddHours(-10));
            automation.Save(new AutomationJob { Kind = AutomationKind.Stock, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "local", Shop = "s1" });

            var run = AutomationRunner.RunDue(store, automation, sync, automation.List().Single().Id, DateTime.UtcNow);
            Assert.AreEqual(1, run.Queued, string.Join(" | ", run.Errors)); Assert.AreEqual(1, run.Errors.Count);
            StringAssert.Contains(run.Errors[0], "STALE:"); StringAssert.Contains(run.Errors[0], "Stok gönderimi engellendi"); StringAssert.Contains(run.Errors[0], "bayat");
            var jobs = sync.List().Where(j => j.Operation == "stock").ToList();
            var pending = jobs.Single(j => j.Status == SyncStatus.Pending); StringAssert.EndsWith(pending.Version, ":9", "the fresh product's available stock (10 minus a safety stock of 1) is the payload");
            Assert.AreEqual(1, jobs.Count(j => j.Status == SyncStatus.Failed), "the stale product's stock is a failed record, never dispatchable");
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
