using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2640: a malformed StartedUtc/FinishedUtc row in XmlRuns must
/// not crash List(), a NULL FinishedUtc (still Running) must never be
/// confused with corruption, and per-source filtering must stay isolated
/// from a corrupt row belonging to a different source.
[TestClass]
public sealed class XmlRunReadResilienceTests
{
    static XmlRunStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "xmlrun-resilience-" + Guid.NewGuid().ToString("N"));
        return new XmlRunStore(root);
    }

    static void InsertRawRow(string root, string id, string sourceId, string started, string? finished = null, string status = "Succeeded")
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO XmlRuns(Id,SourceId,Status,StartedUtc,FinishedUtc,Added,Updated,Unchanged,Error) VALUES($id,$source,$status,$started,$finished,0,0,0,'')";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$source", sourceId); cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$started", started); cmd.Parameters.AddWithValue("$finished", (object?)finished ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void ValidRunsWorkNormally()
    {
        var store = NewStore(out var root);
        try
        {
            var id = store.Start("src1");
            store.Complete(id, new ImportSummary(1, 0, 0));
            var list = store.List();
            Assert.AreEqual(1, list.Count);
            Assert.IsNotNull(list[0].FinishedUtc);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedStartedUtcDoesNotDropOtherRuns()
    {
        var store = NewStore(out var root);
        try
        {
            var id = store.Start("src1"); store.Complete(id, new ImportSummary(1, 0, 0));
            InsertRawRow(root, "bad-1", "src2", "not-a-date");

            var list = store.List();
            Assert.AreEqual(1, list.Count);

            var corrupt = store.CorruptRuns();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
            Assert.AreEqual("src2", corrupt[0].SourceId);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedFinishedUtcDoesNotDropWholeHistory()
    {
        var store = NewStore(out var root);
        try
        {
            var id = store.Start("src1"); store.Complete(id, new ImportSummary(1, 0, 0));
            InsertRawRow(root, "bad-1", "src2", DateTime.UtcNow.ToString("O"), finished: "junk");

            var list = store.List();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(1, store.CorruptRuns().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NullFinishedUtcIsNotCorruptItMeansStillRunning()
    {
        var store = NewStore(out var root);
        try
        {
            store.Start("src1");
            var list = store.List();
            Assert.AreEqual(1, list.Count);
            Assert.IsNull(list[0].FinishedUtc);
            Assert.AreEqual("Running", list[0].Status);
            Assert.AreEqual(0, store.CorruptRuns().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void BothTimestampsCorruptIsStillJustOneCorruptRow()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "src1", "junk1", finished: "junk2");
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptRuns().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SourceFilterIsolatesFromAnotherSourcesCorruptRow()
    {
        var store = NewStore(out var root);
        try
        {
            var id = store.Start("src1"); store.Complete(id, new ImportSummary(1, 0, 0));
            InsertRawRow(root, "bad-1", "src2", "junk");

            Assert.AreEqual(1, store.List("src1").Count);
            Assert.AreEqual(0, store.List("src2").Count);
            Assert.AreEqual(0, store.CorruptRuns("src1").Count);
            Assert.AreEqual(1, store.CorruptRuns("src2").Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesRecoveryState()
    {
        var root = Path.Combine(Path.GetTempPath(), "xmlrun-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            new XmlRunStore(root);
            InsertRawRow(root, "bad-1", "src1", "junk");

            var reopened = new XmlRunStore(root);
            Assert.AreEqual(0, reopened.List().Count);
            Assert.AreEqual(1, reopened.CorruptRuns().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-a", "src1", "1");
            InsertRawRow(root, "bad-b", "src1", DateTime.UtcNow.ToString("O"), finished: "2");
            InsertRawRow(root, "bad-c", "src2", "3");

            var corrupt = store.CorruptRuns();
            Assert.AreEqual(3, corrupt.Count);
            CollectionAssert.AreEquivalent(new[] { "bad-a", "bad-b", "bad-c" }, corrupt.Select(c => c.Id).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawError()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "src1", "junk");
            var corrupt = store.CorruptRuns().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("SECRET"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
