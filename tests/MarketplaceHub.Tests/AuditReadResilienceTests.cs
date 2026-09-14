using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2610: a malformed AuditEvents.AtUtc row must not crash
/// List()/LastFailure()/DiagnosticsService.Build(), must never be silently
/// treated as a valid (min-date) row, and a fully-corrupt "Failed" scan must
/// surface as a distinct typed error rather than a misleading null.
[TestClass]
public sealed class AuditReadResilienceTests
{
    static AuditStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "audit-resilience-" + Guid.NewGuid().ToString("N"));
        return new AuditStore(root);
    }

    static void InsertRawRow(string root, string id, string atUtc, string outcome = "Info")
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "audit.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO AuditEvents VALUES($id,$at,'system','action','','','','',$outcome,'')";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$at", atUtc); cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void ValidRowsWorkNormally()
    {
        var store = NewStore(out var root);
        try
        {
            store.Append(new AuditEvent { Module = "m", Action = "a" });
            Assert.AreEqual(1, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedRowDoesNotDropValidRows()
    {
        var store = NewStore(out var root);
        try
        {
            store.Append(new AuditEvent { Module = "m", Action = "a" });
            InsertRawRow(root, "bad-1", "not-a-date");

            var list = store.List();
            Assert.AreEqual(1, list.Count);

            var corrupt = store.CorruptEvents();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedRowNeverAppearsAsMinDateEntry()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "");
            Assert.IsFalse(store.List().Any(e => e.Id == "bad-1"));
            Assert.AreEqual(1, store.CorruptEvents().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LastFailureSkipsCorruptRowAndReturnsNewestValidFailure()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-failed", "corrupt-date", "Failed");
            InsertRawRow(root, "good-failed", DateTime.UtcNow.ToString("O"), "Failed");

            var last = store.LastFailure();
            Assert.IsNotNull(last);
            Assert.AreEqual("good-failed", last!.Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LastFailureReturnsNullWhenNoFailuresExist()
    {
        var store = NewStore(out var root);
        try
        {
            store.Append(new AuditEvent { Outcome = "Info" });
            Assert.IsNull(store.LastFailure());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LastFailureThrowsTypedErrorWhenAllScannedFailuresAreCorrupt()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "xx", "Failed");
            InsertRawRow(root, "bad-2", "yy", "Failed");
            Assert.ThrowsException<AuditStoreCorruptionException>(() => store.LastFailure());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LastFailureBehaviorIsStableAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AuditStore(root);
            InsertRawRow(root, "bad-1", "zz", "Failed");
            Assert.ThrowsException<AuditStoreCorruptionException>(() => store.LastFailure());

            var reopened = new AuditStore(root);
            Assert.ThrowsException<AuditStoreCorruptionException>(() => reopened.LastFailure());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LastFailureScanIsBounded()
    {
        var store = NewStore(out var root);
        try
        {
            for (var i = 0; i < AuditStore.MaxLastFailureScan + 5; i++) InsertRawRow(root, "bad-" + i, "corrupt", "Failed");
            // All rows within the bounded scan window are corrupt -> typed error, not
            // an unbounded exception loop across every row in the table.
            Assert.ThrowsException<AuditStoreCorruptionException>(() => store.LastFailure());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsBuildDoesNotCrashOnFullyCorruptFailedHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            new AuditStore(root);
            InsertRawRow(root, "bad-1", "corrupt", "Failed");
            var snapshot = new DiagnosticsService(root).Build();
            Assert.IsTrue(snapshot.Checks.Any(c => c.Name == "Audit geçmişi" && c.Status == "ERROR"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawDetailValues()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "junk");
            var corrupt = store.CorruptEvents().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("[0-9a-f]{32}"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesCorruptRowDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            new AuditStore(root);
            InsertRawRow(root, "bad-1", "junk");
            var reopened = new AuditStore(root);
            Assert.AreEqual(1, reopened.CorruptEvents().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-a", "1");
            InsertRawRow(root, "bad-b", "2");
            InsertRawRow(root, "bad-c", "3");
            var corrupt = store.CorruptEvents();
            Assert.AreEqual(3, corrupt.Count);
            CollectionAssert.AreEquivalent(new[] { "bad-a", "bad-b", "bad-c" }, corrupt.Select(c => c.Id).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
