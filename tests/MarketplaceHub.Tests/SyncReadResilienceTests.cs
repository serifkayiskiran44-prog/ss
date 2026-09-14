using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2641: a malformed persisted UpdatedUtc must not crash
/// List()/Get(), must never let Enqueue's exact-key readback silently
/// duplicate a job, and a corrupt row must never be claimed/started or
/// swept back into Pending by RecoverAbandonedRunning.
[TestClass]
public sealed class SyncReadResilienceTests
{
    static SyncStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "sync-resilience-" + Guid.NewGuid().ToString("N"));
        return new SyncStore(root);
    }

    static void InsertRawRow(string root, string id, string channel, string shop, string entity, string updatedUtc, int status = 0)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO SyncJobs(Id,Channel,ShopId,Operation,EntityId,Version,Status,FailureCount,LastError,ErrorClass,UpdatedUtc) VALUES($id,$channel,$shop,'update',$entity,'1',$status,0,'',0,$updated)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$channel", channel); cmd.Parameters.AddWithValue("$shop", shop);
        cmd.Parameters.AddWithValue("$entity", entity); cmd.Parameters.AddWithValue("$status", status); cmd.Parameters.AddWithValue("$updated", updatedUtc);
        cmd.ExecuteNonQuery();
    }

    static SyncRequest Request(string entity) => new("etsy", "update", entity, "1");

    [TestMethod]
    public void ValidJobWorksNormally()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Enqueue(Request("p1"));
            Assert.AreEqual(1, store.List().Count);
            Assert.AreEqual(job.Id, store.Get(job.Id).Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedUpdatedUtcDoesNotDropOtherJobs()
    {
        var store = NewStore(out var root);
        try
        {
            store.Enqueue(Request("p1"));
            InsertRawRow(root, "bad-1", "etsy", "default", "p2", "not-a-date");

            var list = store.List();
            Assert.AreEqual(1, list.Count);

            var corrupt = store.CorruptJobs();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void GetThrowsDistinctTypedErrorForCorruptRowVsMissing()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "etsy", "default", "p1", "junk");
            Assert.ThrowsException<SyncJobCorruptException>(() => store.Get("bad-1"));
            Assert.ThrowsException<InvalidOperationException>(() => store.Get("does-not-exist"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void EnqueueOnCorruptExistingRowFailsClosedInsteadOfDuplicating()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "etsy", "default", "p1", "junk");
            Assert.ThrowsException<SyncJobCorruptException>(() => store.Enqueue(Request("p1")));

            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
            c.Open(); using var count = c.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM SyncJobs WHERE EntityId='p1'";
            Assert.AreEqual(1L, (long)count.ExecuteScalar()!, "Enqueue must not create a duplicate job for a corrupt exact-key row.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TryStartRefusesACorruptRow()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "etsy", "default", "p1", "junk");
            Assert.IsFalse(store.TryStart("bad-1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TryStartStillWorksForAHealthyPendingJob()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Enqueue(Request("p1"));
            Assert.IsTrue(store.TryStart(job.Id));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RecoverAbandonedRunningNeverSweepsACorruptRow()
    {
        var store = NewStore(out var root);
        try
        {
            // A corrupt row that lexicographically sorts far in the past would
            // satisfy a raw text "< cutoff" comparison; it must still be excluded.
            InsertRawRow(root, "bad-1", "etsy", "default", "p1", "0000-01-01Xgarbage", status: 1);
            var recovered = store.RecoverAbandonedRunning(TimeSpan.FromMinutes(1));
            Assert.AreEqual(0, recovered);
            Assert.AreEqual(1, store.CorruptJobs().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RecoverAbandonedRunningStillRecoversAHealthyStaleRow()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "good-1", "etsy", "default", "p1", DateTime.UtcNow.AddHours(-2).ToString("O"), status: 1);
            var recovered = store.RecoverAbandonedRunning(TimeSpan.FromMinutes(1));
            Assert.AreEqual(1, recovered);
            Assert.AreEqual(SyncStatus.Pending, store.Get("good-1").Status);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesCorruptRowDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            new SyncStore(root);
            InsertRawRow(root, "bad-1", "etsy", "default", "p1", "junk");

            var reopened = new SyncStore(root);
            Assert.AreEqual(0, reopened.List().Count);
            Assert.AreEqual(1, reopened.CorruptJobs().Count);
            Assert.ThrowsException<SyncJobCorruptException>(() => reopened.Get("bad-1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-a", "etsy", "default", "p1", "1");
            InsertRawRow(root, "bad-b", "etsy", "default", "p2", "2");
            InsertRawRow(root, "bad-c", "etsy", "default", "p3", "3");

            var corrupt = store.CorruptJobs();
            Assert.AreEqual(3, corrupt.Count);
            CollectionAssert.AreEquivalent(new[] { "bad-a", "bad-b", "bad-c" }, corrupt.Select(c => c.Id).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawEntityId()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "etsy", "default", "SECRET-ENTITY-ID", "junk");
            var corrupt = store.CorruptJobs().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("SECRET-ENTITY-ID"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
