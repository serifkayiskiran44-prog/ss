using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2531: AutomationJobs.IntervalMinutes must have an explicit
/// technical upper bound so Complete()'s next-run arithmetic can never throw
/// after a job's side effects have already been enqueued, and a legacy
/// out-of-bounds interval must be quarantined rather than crashing the
/// scheduler mid-run.
[TestClass]
public sealed class AutomationScheduleBoundsTests
{
    static AutomationStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "automation-schedule-bounds-" + Guid.NewGuid().ToString("N"));
        return new AutomationStore(root);
    }

    static void InsertRawInterval(string root, string id, int interval)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO AutomationJobs(Id,Kind,IntervalMinutes,NextRunUtc,LastRunUtc,LockedUntilUtc,LastError,Channel,Shop,Enabled) VALUES($id,0,$interval,$next,NULL,NULL,'','etsy','default',1)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$interval", interval); cmd.Parameters.AddWithValue("$next", DateTime.UtcNow.AddMinutes(-1).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void IntervalOneAndNormalThirtyAreAccepted()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new AutomationJob { Kind = AutomationKind.Stock, IntervalMinutes = 1 });
            store.Save(new AutomationJob { Kind = AutomationKind.Stock, IntervalMinutes = 30 });
            Assert.AreEqual(2, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ExactMaxIntervalIsAcceptedAndOneOverIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new AutomationJob { Kind = AutomationKind.Stock, IntervalMinutes = AutomationSchedule.MaxIntervalMinutes });
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(new AutomationJob { Kind = AutomationKind.Stock, IntervalMinutes = AutomationSchedule.MaxIntervalMinutes + 1 }));
            Assert.AreEqual(1, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void IntMaxValueIntervalIsRejectedAtSave()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(new AutomationJob { Kind = AutomationKind.Stock, IntervalMinutes = int.MaxValue }));
            Assert.AreEqual(0, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LegacyOversizedIntervalRowIsQuarantinedNotCrashing()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawInterval(root, "bad1", int.MaxValue);
            Assert.ThrowsException<AutomationJobCorruptException>(() => store.Get("bad1"));
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(0, store.Due(DateTime.UtcNow).Count, "An out-of-bounds-interval job must never be queued as due.");
            Assert.AreEqual(1, store.CorruptJobs().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LegacyZeroIntervalRowIsQuarantined()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawInterval(root, "bad1", 0);
            Assert.ThrowsException<AutomationJobCorruptException>(() => store.Get("bad1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OutOfBoundsIntervalJobDoesNotBlockAValidJobFromBeingDue()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawInterval(root, "bad1", int.MaxValue);
            var valid = store.Save(new AutomationJob { Kind = AutomationKind.Stock, IntervalMinutes = 15, NextRunUtc = DateTime.UtcNow.AddMinutes(-1) });
            var due = store.Due(DateTime.UtcNow);
            Assert.AreEqual(1, due.Count);
            Assert.AreEqual(valid.Id, due[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NextRunNearDateTimeMaxValueIsAControlledFailureNotACrash()
    {
        var job = new AutomationJob { Kind = AutomationKind.Stock, IntervalMinutes = AutomationSchedule.MaxIntervalMinutes, ScheduleMode = "Interval" };
        var nearMax = DateTime.MaxValue.AddDays(-1); // less headroom than MaxIntervalMinutes needs
        Assert.ThrowsException<InvalidOperationException>(() => AutomationSchedule.NextRunUtc(job, nearMax));
    }

    [TestMethod]
    public void NormalJobCompletionStillComputesADeterministicNextRun()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Save(new AutomationJob { Kind = AutomationKind.Stock, IntervalMinutes = 30, NextRunUtc = DateTime.UtcNow.AddMinutes(-1) });
            var before = DateTime.UtcNow;
            store.Complete(job.Id, before);
            var reloaded = store.Get(job.Id);
            Assert.IsTrue(reloaded.NextRunUtc >= before.AddMinutes(29) && reloaded.NextRunUtc <= before.AddMinutes(31));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartKeepsOutOfBoundsIntervalClassificationDeterministic()
    {
        var root = Path.Combine(Path.GetTempPath(), "automation-schedule-bounds-" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = new AutomationStore(root);
            InsertRawInterval(root, "bad1", int.MaxValue);
            var reopened = new AutomationStore(root);
            Assert.ThrowsException<AutomationJobCorruptException>(() => reopened.Get("bad1"));
            Assert.AreEqual(1, reopened.CorruptJobs().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
