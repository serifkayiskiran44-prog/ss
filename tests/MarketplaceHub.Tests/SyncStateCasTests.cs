using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2607: Succeed/Fail must be compare-and-swapped on state (and,
/// for a Running episode claimed via TryStart, on its generation) so a
/// late/duplicate completion callback can never overwrite Cancelled or apply
/// to the wrong Running episode after an abandoned-run recovery reclaim.
[TestClass]
public sealed class SyncStateCasTests
{
    static SyncStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "sync-cas-" + Guid.NewGuid().ToString("N"));
        return new SyncStore(root);
    }

    static SyncRequest Request(string entity = "p1") => new("etsy", "update", entity, "1");

    [TestMethod]
    public void ValidRunningSucceedTransitionsExactlyOnce()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Enqueue(Request());
            Assert.IsTrue(store.TryStart(job.Id, out var generation));
            Assert.IsTrue(store.Succeed(job.Id, generation));
            Assert.AreEqual(SyncStatus.Succeeded, store.Get(job.Id).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CancelThenLateSucceedLeavesFinalStateCancelled()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Enqueue(Request());
            Assert.IsTrue(store.TryStart(job.Id, out var generation));
            Assert.IsTrue(store.Cancel(job.Id));

            var applied = store.Succeed(job.Id, generation);
            Assert.IsFalse(applied);
            Assert.AreEqual(SyncStatus.Cancelled, store.Get(job.Id).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CancelThenLateFailLeavesFinalStateCancelledAndDoesNotIncrementFailureCount()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Enqueue(Request());
            Assert.IsTrue(store.TryStart(job.Id, out var generation));
            Assert.IsTrue(store.Cancel(job.Id));

            var applied = store.Fail(job.Id, generation, "late failure");
            Assert.IsFalse(applied);
            var current = store.Get(job.Id);
            Assert.AreEqual(SyncStatus.Cancelled, current.Status);
            Assert.AreEqual(0, current.FailureCount);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AbandonedRunReclaimedThenStaleOriginalCompletionDoesNotAffectNewRun()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Enqueue(Request());
            Assert.IsTrue(store.TryStart(job.Id, out var generationA));

            // Simulate abandonment: the row goes back to Pending (as
            // RecoverAbandonedRunning would do after the lease expires), then a
            // second worker claims it, getting a new generation.
            Assert.AreEqual(1, store.RecoverAbandonedRunning(TimeSpan.FromSeconds(1), DateTime.UtcNow.AddMinutes(1)));
            Assert.IsTrue(store.TryStart(job.Id, out var generationB));
            Assert.AreNotEqual(generationA, generationB);

            // A's late completion must not affect B's run.
            var staleApplied = store.Succeed(job.Id, generationA);
            Assert.IsFalse(staleApplied);
            Assert.AreEqual(SyncStatus.Running, store.Get(job.Id).Status);

            // B's own completion still works.
            Assert.IsTrue(store.Succeed(job.Id, generationB));
            Assert.AreEqual(SyncStatus.Succeeded, store.Get(job.Id).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OnlyOneOfTwoConcurrentTerminalCallbacksWins()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Enqueue(Request());
            Assert.IsTrue(store.TryStart(job.Id, out var generation));

            var first = store.Succeed(job.Id, generation);
            var second = store.Fail(job.Id, generation, "too late");

            Assert.IsTrue(first);
            Assert.IsFalse(second);
            Assert.AreEqual(SyncStatus.Succeeded, store.Get(job.Id).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DirectFailWithoutStartStillWorksFromPending()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Enqueue(Request());
            Assert.IsTrue(store.Fail(job.Id, "immediate failure"));
            Assert.AreEqual(SyncStatus.Failed, store.Get(job.Id).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NoGenerationSucceedStillRequiresRunningState()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Enqueue(Request());
            // Never started (still Pending) - Succeed without generation must reject.
            Assert.IsFalse(store.Succeed(job.Id));
            Assert.AreEqual(SyncStatus.Pending, store.Get(job.Id).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MissingJobStillThrowsForSucceedAndFail()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() => store.Succeed("does-not-exist"));
            Assert.ThrowsException<InvalidOperationException>(() => store.Fail("does-not-exist", "x"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RetryAndFailureCountSemanticsUnaffectedByCas()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Enqueue(Request());
            Assert.IsTrue(store.TryStart(job.Id, out var generation));
            Assert.IsTrue(store.Fail(job.Id, generation, "network timeout"));
            var afterFail = store.Get(job.Id);
            Assert.AreEqual(1, afterFail.FailureCount);
            Assert.AreEqual(SyncErrorClass.Network, afterFail.ErrorClass);

            store.Retry(job.Id);
            Assert.AreEqual(SyncStatus.Pending, store.Get(job.Id).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
