using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2635: a malformed persisted scheduler timestamp
/// (NextRunUtc/LastRunUtc/LockedUntilUtc) must not crash List()/Due(), must
/// never run via a fabricated schedule, and Get() must distinguish a corrupt
/// row from a genuinely missing one.
[TestClass]
public sealed class AutomationReadResilienceTests
{
    static AutomationStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "automation-resilience-" + Guid.NewGuid().ToString("N"));
        return new AutomationStore(root);
    }

    static void InsertRawRow(string root, string id, string nextRun, bool enabled = true, string? lastRun = null, string? lockedUntil = null)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO AutomationJobs(Id,Kind,IntervalMinutes,NextRunUtc,LastRunUtc,LockedUntilUtc,LastError,Channel,Shop,Enabled) VALUES($id,0,30,$next,$last,$locked,'','etsy','default',$enabled)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$next", nextRun);
        cmd.Parameters.AddWithValue("$last", (object?)lastRun ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$locked", (object?)lockedUntil ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    static AutomationJob Job() => new() { Kind = AutomationKind.Stock, IntervalMinutes = 15 };

    [TestMethod]
    public void ValidJobWorksNormally()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Save(Job());
            Assert.AreEqual(1, store.List().Count);
            Assert.AreEqual(job.Id, store.Get(job.Id).Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedNextRunUtcDoesNotDropOtherJobs()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(Job());
            InsertRawRow(root, "bad-1", "not-a-date");

            var list = store.List();
            Assert.AreEqual(1, list.Count);

            var corrupt = store.CorruptJobs();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedLastRunUtcIsIsolated()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", DateTime.UtcNow.ToString("O"), lastRun: "junk");
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptJobs().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedLockedUntilUtcIsIsolated()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", DateTime.UtcNow.ToString("O"), lockedUntil: "junk");
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptJobs().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void GetThrowsDistinctTypedErrorForCorruptRowVsMissing()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "junk");
            Assert.ThrowsException<AutomationJobCorruptException>(() => store.Get("bad-1"));
            Assert.ThrowsException<InvalidOperationException>(() => store.Get("does-not-exist"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DueExcludesCorruptJobEvenWhenDueByRawText()
    {
        var store = NewStore(out var root);
        try
        {
            var good = store.Save(Job());
            good.NextRunUtc = DateTime.UtcNow.AddMinutes(-5); store.Save(good);
            // "0000-01-01..." sorts before "now" lexicographically too, so the raw SQL
            // WHERE clause would treat this as due if we didn't re-validate parse.
            InsertRawRow(root, "bad-1", "0000-01-01Xgarbage");

            var due = store.Due(DateTime.UtcNow);
            Assert.IsTrue(due.All(j => j.Id != "bad-1"), "A corrupt job must never be returned as due.");
            Assert.IsTrue(due.Any(j => j.Id == good.Id));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TryClaimRefusesACorruptRow()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "junk");
            Assert.IsFalse(store.TryClaim("bad-1", DateTime.UtcNow, TimeSpan.FromMinutes(5)));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TryClaimLeaseRefusesACorruptRow()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "junk");
            var claimed = store.TryClaimLease("bad-1", DateTime.UtcNow, TimeSpan.FromMinutes(5), out var token);
            Assert.IsFalse(claimed);
            Assert.AreEqual("", token);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TryClaimStillWorksForAHealthyDueJob()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Save(Job());
            job.NextRunUtc = DateTime.UtcNow.AddMinutes(-1); store.Save(job);
            Assert.IsTrue(store.TryClaim(job.Id, DateTime.UtcNow, TimeSpan.FromMinutes(5)));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesCorruptRowDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "automation-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            new AutomationStore(root);
            InsertRawRow(root, "bad-1", "junk");

            var reopened = new AutomationStore(root);
            Assert.AreEqual(0, reopened.List().Count);
            Assert.AreEqual(1, reopened.CorruptJobs().Count);
            Assert.ThrowsException<AutomationJobCorruptException>(() => reopened.Get("bad-1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptJobsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-a", "1");
            InsertRawRow(root, "bad-b", DateTime.UtcNow.ToString("O"), lastRun: "2");
            InsertRawRow(root, "bad-c", DateTime.UtcNow.ToString("O"), lockedUntil: "3");

            var corrupt = store.CorruptJobs();
            Assert.AreEqual(3, corrupt.Count);
            CollectionAssert.AreEquivalent(new[] { "bad-a", "bad-b", "bad-c" }, corrupt.Select(c => c.Id).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawLastError()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "junk");
            var corrupt = store.CorruptJobs().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("junk"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
