using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2623: a malformed CreatedUtc/UpdatedUtc row in QualityIssues
/// must not crash List()/Summary()/GlobalSearch's quality projection, must
/// never be silently dropped/overwritten, and healthy rows must stay fully
/// usable regardless of how many corrupt rows exist alongside them.
[TestClass]
public sealed class DataQualityReadResilienceTests
{
    static DataQualityStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "quality-resilience-" + Guid.NewGuid().ToString("N"));
        return new DataQualityStore(root);
    }

    static void InsertRawRow(string root, string id, string fingerprint, string created, string updated, string severity = "Warning", string status = "Open")
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "quality.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO QualityIssues VALUES($id,$fp,$severity,'Type','','','','','','msg','sug',$status,$created,$updated)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$fp", fingerprint); cmd.Parameters.AddWithValue("$severity", severity);
        cmd.Parameters.AddWithValue("$status", status); cmd.Parameters.AddWithValue("$created", created); cmd.Parameters.AddWithValue("$updated", updated);
        cmd.ExecuteNonQuery();
    }

    static DataQualityIssue Issue(string fingerprint) => new() { Fingerprint = fingerprint, Type = "T", Message = "m" };

    [TestMethod]
    public void ValidRowsWorkNormally()
    {
        var store = NewStore(out var root);
        try
        {
            store.Upsert(Issue("fp-1"));
            Assert.AreEqual(1, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedCreatedUtcDoesNotDropValidRows()
    {
        var store = NewStore(out var root);
        try
        {
            store.Upsert(Issue("fp-good"));
            InsertRawRow(root, "bad-1", "fp-bad", "not-a-date", DateTime.UtcNow.ToString("O"));

            var list = store.List();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("fp-good", list[0].Fingerprint);

            var corrupt = store.CorruptIssues();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedUpdatedUtcIsIsolatedTheSameWay()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "fp-bad", DateTime.UtcNow.ToString("O"), "garbage");
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptIssues().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void EmptyTimestampIsCorruptNotMinDate()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "fp-bad", "", "");
            Assert.IsFalse(store.List().Any(i => i.Id == "bad-1"));
            Assert.AreEqual(1, store.CorruptIssues().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SummaryExcludesCorruptRowsInsteadOfCrashing()
    {
        var store = NewStore(out var root);
        try
        {
            store.Upsert(new DataQualityIssue { Fingerprint = "fp-1", Type = "T", Message = "m", Severity = "Critical" });
            InsertRawRow(root, "bad-1", "fp-bad", "junk", "junk", "Critical");

            var summary = store.Summary();
            Assert.AreEqual(1, summary.Total);
            Assert.AreEqual(1, summary.Critical);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void GlobalSearchRebuildDoesNotCrashWithACorruptQualityRow()
    {
        var store = NewStore(out var root);
        try
        {
            store.Upsert(Issue("fp-good"));
            InsertRawRow(root, "bad-1", "fp-bad", "junk", "junk");

            var service = new GlobalSearchIndexService(root);
            service.Rebuild();
            var results = service.SearchAsync("m").GetAwaiter().GetResult();
            Assert.IsTrue(results is not null);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CorruptRowIsNotAutomaticallyDeletedByNormalReads()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "fp-bad", "junk", "junk");
            store.List(); store.Summary(); store.List();
            Assert.AreEqual(1, store.CorruptIssues().Count, "Reading must never silently remove a corrupt row.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesReviewRequiredState()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            new DataQualityStore(root);
            InsertRawRow(root, "bad-1", "fp-bad", "junk", "junk");

            var reopened = new DataQualityStore(root);
            Assert.AreEqual(0, reopened.List().Count);
            Assert.AreEqual(1, reopened.CorruptIssues().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-a", "fp-a", "1", DateTime.UtcNow.ToString("O"));
            InsertRawRow(root, "bad-b", "fp-b", DateTime.UtcNow.ToString("O"), "2");
            InsertRawRow(root, "bad-c", "fp-c", "3", "4");

            var corrupt = store.CorruptIssues();
            Assert.AreEqual(3, corrupt.Count);
            CollectionAssert.AreEquivalent(new[] { "bad-a", "bad-b", "bad-c" }, corrupt.Select(c => c.Id).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawMessageContent()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "fp-bad", "junk", "junk");
            var corrupt = store.CorruptIssues().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("msg|sug"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
