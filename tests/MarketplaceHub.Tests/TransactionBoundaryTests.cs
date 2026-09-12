using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #778 (DB: Transaction boundary audit). Every multi-step product/order/import mutation was reviewed:
// CatalogStore.ImportCore/SaveProduct/DeleteProduct/Undo/SaveSource and OrdersStore.Save each run inside one
// transaction; MarketplaceMappingStore.Save is a single upsert; every lease/claim in XmlRunStore,
// AutomationStore and SyncStore is a single conditional UPDATE (atomic by construction). The one real
// half-commit was AutomationRunner.RunClaimed's failure record: sync.Enqueue (a Pending INSERT) followed by
// sync.Fail (an UPDATE) -- two statements on two connections, with a window in which the ":error"-payload job
// is Pending and therefore dispatchable by TryStart. These tests cover the issue's four scenarios (injected
// failure, rollback, retry, restart) against the real stores, not mocks.
[TestClass]
public sealed class TransactionBoundaryTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "tx-boundary-" + Guid.NewGuid().ToString("N"));
    static void Cleanup(string root) { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    static SqliteConnection OpenRaw(string root)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open(); return c;
    }

    // A trigger-based trace is the deterministic way to observe *every* state a row passes through, at
    // machine speed and independent of timing: an Enqueue-then-Fail pair leaves a Pending entry in the
    // trace even though the row ends up Failed, while a single atomic write never does.
    static void InstallStatusTrace(string root)
    {
        using var c = OpenRaw(root); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS SyncJobsTrace(Seq INTEGER PRIMARY KEY AUTOINCREMENT, JobId TEXT NOT NULL, Version TEXT NOT NULL, Status INTEGER NOT NULL);" +
                          "CREATE TRIGGER IF NOT EXISTS trace_sync_insert AFTER INSERT ON SyncJobs BEGIN INSERT INTO SyncJobsTrace(JobId,Version,Status) VALUES(NEW.Id,NEW.Version,NEW.Status); END;" +
                          "CREATE TRIGGER IF NOT EXISTS trace_sync_update AFTER UPDATE ON SyncJobs BEGIN INSERT INTO SyncJobsTrace(JobId,Version,Status) VALUES(NEW.Id,NEW.Version,NEW.Status); END;";
        cmd.ExecuteNonQuery();
    }

    static (int Total, int Pending) TraceForErrorRecords(string root)
    {
        using var c = OpenRaw(root); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(CASE WHEN Status=$pending THEN 1 ELSE 0 END),0) FROM SyncJobsTrace WHERE Version LIKE '%:error'";
        cmd.Parameters.AddWithValue("$pending", (int)SyncStatus.Pending);
        using var r = cmd.ExecuteReader(); r.Read(); return (r.GetInt32(0), r.GetInt32(1));
    }

    [TestMethod]
    public void ADispatchFailureOnTheRealAutomationPathIsWrittenInOneStepAndIsNeverObservablyPending()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            var source = new XmlSource { Id = "src", Name = "Src" };
            catalog.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "SKU-1", Name = "P", Price = 100m, Cost = 60m, Stock = 1 } });
            // Injected failure: a policy with no expense fields is blocked by the money preflight (#285), so
            // PreviewPrice throws inside RunClaimed's per-product loop and the catch block writes the failure record.
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "default", Formula = "x*1.5", Currency = "TRY", TryPerUnit = 1, Enabled = true });
            var automation = new AutomationStore(root);
            automation.Save(new AutomationJob { Kind = AutomationKind.Price, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "local", Shop = "default" });
            var sync = new SyncStore(root);
            InstallStatusTrace(root);

            var result = AutomationRunner.RunDue(catalog, automation, sync, automation.List().Single().Id, DateTime.UtcNow);

            Assert.AreEqual(0, result.Queued);
            Assert.AreEqual(1, result.Errors.Count, string.Join(" | ", result.Errors));
            var jobs = sync.List();
            Assert.AreEqual(1, jobs.Count);
            Assert.AreEqual(SyncStatus.Failed, jobs[0].Status);
            Assert.AreEqual(1, jobs[0].FailureCount);
            var trace = TraceForErrorRecords(root);
            Assert.IsTrue(trace.Total >= 1, "The trace must have seen the failure record being written.");
            Assert.AreEqual(0, trace.Pending, "The failure record passed through a Pending state -- that is the dispatchable half-commit window this issue closes; it must be written directly in its terminal state.");
            Assert.IsFalse(sync.TryStart(jobs[0].Id), "A failure record must never be startable by a dispatcher.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void RepeatingTheSameFailureBumpsTheCountInsteadOfCreatingADuplicateOrAPendingRow()
    {
        var root = NewRoot();
        try
        {
            var sync = new SyncStore(root);
            var request = new SyncRequest("local", "price", "p1", "p1:1:error", "default");

            var first = sync.EnqueueFailed(request, "beklenmeyen hata");
            var second = sync.EnqueueFailed(request, "beklenmeyen hata");

            Assert.AreEqual(first.Id, second.Id);
            Assert.AreEqual(2, second.FailureCount);
            Assert.AreEqual(1, sync.List().Count);
            Assert.IsTrue(sync.List().All(j => j.Status == SyncStatus.Failed));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AnImportBatchThatFailsMidwayRollsBackToTheLastCommittedStateOfProductsAndSource()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            var source = new XmlSource { Id = "src", Name = "Src" };
            catalog.Import(source, new[]
            {
                new CatalogProduct { SourceId = source.Id, Sku = "K1", Name = "K1", Price = 10, Stock = 1 },
                new CatalogProduct { SourceId = source.Id, Sku = "K2", Name = "K2", Price = 10, Stock = 1 },
                new CatalogProduct { SourceId = source.Id, Sku = "K3", Name = "K3", Price = 10, Stock = 1 },
            }, System.Threading.CancellationToken.None, new XmlImportContext { CompleteFeed = true, FeedHash = "feed-1" });
            Assert.AreEqual("feed-1", catalog.Sources().Single().LastSuccessfulFeedHash);

            // Rows A and B are written inside the transaction before the duplicate on the third row throws;
            // the source-state row would have been rewritten with feed-2 at the end of the same transaction.
            var batch = new[]
            {
                new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "A", Price = 10, Stock = 1 },
                new CatalogProduct { SourceId = source.Id, Sku = "B", Name = "B", Price = 10, Stock = 1 },
                new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "A again", Price = 10, Stock = 1 },
            };
            Assert.ThrowsException<InvalidOperationException>(() => catalog.Import(source, batch, System.Threading.CancellationToken.None, new XmlImportContext { CompleteFeed = true, FeedHash = "feed-2" }));

            var skus = catalog.Products().Select(p => p.Sku).OrderBy(s => s).ToArray();
            CollectionAssert.AreEqual(new[] { "K1", "K2", "K3" }, skus, "A failure on row 3 must roll back rows 1 and 2; a partially applied batch is exactly the half-commit this audit exists to rule out.");
            var persisted = catalog.Sources().Single();
            Assert.AreEqual("feed-1", persisted.LastSuccessfulFeedHash, "The source's feed state is written in the same transaction as the products and must roll back with them.");
            Assert.AreEqual(3, persisted.LastSuccessfulFeedCount);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AFailureRecordCanStillBeRetriedExplicitlyAndAJobAbandonedByACrashIsRecoveredOnRestart()
    {
        var root = NewRoot();
        try
        {
            var sync = new SyncStore(root);
            var failed = sync.EnqueueFailed(new SyncRequest("local", "price", "p1", "p1:1:error", "default"), "beklenmeyen hata");
            sync.Retry(failed.Id);
            Assert.AreEqual(SyncStatus.Pending, sync.Get(failed.Id).Status, "An explicit operator retry must still be possible on a terminal failure record.");

            // Simulate a crash mid-dispatch: the job is left Running with nothing to finish it.
            var running = sync.Enqueue(new SyncRequest("local", "stock", "p2", "p2:1:5", "default"));
            Assert.IsTrue(sync.TryStart(running.Id));
            var restarted = new SyncStore(root); // a fresh instance is what the app constructs on the next launch
            var recovered = restarted.RecoverAbandonedRunning(TimeSpan.FromMinutes(5), DateTime.UtcNow.AddMinutes(10));

            Assert.AreEqual(1, recovered);
            Assert.AreEqual(SyncStatus.Pending, restarted.Get(running.Id).Status, "A job orphaned by a crash must come back as retryable work, not be lost or duplicated.");
            Assert.AreEqual(2, restarted.List().Count, "Recovery must not create a second copy of the job.");
        }
        finally { Cleanup(root); }
    }
}
