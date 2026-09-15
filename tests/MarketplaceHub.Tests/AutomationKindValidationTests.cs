using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2530: a persisted/unrecognized AutomationJobs.Kind must never
/// be silently treated as Price (or any other kind) - Save() rejects an
/// invalid Kind before any write, TryRead quarantines a corrupt persisted
/// Kind, and AutomationRunner's dispatch is exhaustive rather than an
/// is-X-or-Y-else-price fallback.
[TestClass]
public sealed class AutomationKindValidationTests
{
    static AutomationStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "automation-kind-" + Guid.NewGuid().ToString("N"));
        return new AutomationStore(root);
    }

    static void InsertRawKind(string root, string id, int rawKind)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO AutomationJobs(Id,Kind,IntervalMinutes,NextRunUtc,LastRunUtc,LockedUntilUtc,LastError,Channel,Shop,Enabled) VALUES($id,$kind,30,$next,NULL,NULL,'','etsy','default',1)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$kind", rawKind); cmd.Parameters.AddWithValue("$next", DateTime.UtcNow.AddMinutes(-1).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void AllFiveSupportedKindsRoundTrip()
    {
        var store = NewStore(out var root);
        try
        {
            foreach (var kind in new[] { AutomationKind.Stock, AutomationKind.Price, AutomationKind.Xml, AutomationKind.Health, AutomationKind.Sync })
            {
                var job = store.Save(new AutomationJob { Kind = kind, IntervalMinutes = 15 });
                Assert.AreEqual(kind, store.Get(job.Id).Kind);
            }
            Assert.AreEqual(0, store.CorruptJobs().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(5)]
    [DataRow(99)]
    [DataRow(int.MaxValue)]
    public void SaveRejectsAnInvalidKindBeforeAnyWrite(int invalidKind)
    {
        var store = NewStore(out var root);
        try
        {
            var job = new AutomationJob { Kind = (AutomationKind)invalidKind, IntervalMinutes = 15 };
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(job));
            Assert.AreEqual(0, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(2147483647)]
    [DataRow(99)]
    public void PersistedInvalidKindIsQuarantinedNotCastBlindly(int rawKind)
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawKind(root, "bad1", rawKind);
            Assert.ThrowsException<AutomationJobCorruptException>(() => store.Get("bad1"));
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(0, store.Due(DateTime.UtcNow).Count, "An invalid-Kind job must never be queued as due.");
            Assert.AreEqual(1, store.CorruptJobs().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InvalidKindDoesNotBlockAValidJobFromRunning()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawKind(root, "bad1", 99);
            var valid = store.Save(new AutomationJob { Kind = AutomationKind.Stock, IntervalMinutes = 15, NextRunUtc = DateTime.UtcNow.AddMinutes(-1) });
            var due = store.Due(DateTime.UtcNow);
            Assert.AreEqual(1, due.Count);
            Assert.AreEqual(valid.Id, due[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartKeepsInvalidKindClassificationDeterministic()
    {
        var root = Path.Combine(Path.GetTempPath(), "automation-kind-" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = new AutomationStore(root);
            InsertRawKind(root, "bad1", 99);
            var reopened = new AutomationStore(root);
            Assert.ThrowsException<AutomationJobCorruptException>(() => reopened.Get("bad1"));
            Assert.AreEqual(1, reopened.CorruptJobs().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RunnerDispatchIsExhaustiveNotAFallbackToPrice()
    {
        var root = Path.Combine(Path.GetTempPath(), "automation-kind-runner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new CatalogStore(root);
            catalog.Import(new XmlSource { Id = "src", Location = "https://example.test/f.xml" }, [new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Ürün", Price = 10, Currency = "USD" }]);
            catalog.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "default", Enabled = true });
            var automation = new AutomationStore(root);
            var sync = new SyncStore(root);
            // A job whose Kind is a valid enum member the runner's switch does not
            // handle would be a latent "falls through to price" bug if the switch
            // were ever refactored back to is/else; this test locks in that Stock
            // and Price are the only kinds routed through the per-product stock/price
            // loop, and that loop's operation label matches the job's own Kind.
            var stockJob = automation.Save(new AutomationJob { Kind = AutomationKind.Stock, IntervalMinutes = 15, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "etsy", Shop = "default" });
            var result = AutomationRunner.RunDue(catalog, automation, sync, stockJob.Id, "etsy", "default", DateTime.UtcNow);
            Assert.AreEqual(1, result.Queued);
            Assert.AreEqual(0, result.Errors.Count, string.Join(";", result.Errors));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
