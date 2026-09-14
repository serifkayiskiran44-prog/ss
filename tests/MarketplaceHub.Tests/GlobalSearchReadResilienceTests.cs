using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2629: a malformed persisted UpdatedUtc row in the derived
/// SearchIndex must not crash Search(), and healthy rows must remain
/// findable regardless of how many corrupt rows exist alongside them.
[TestClass]
public sealed class GlobalSearchReadResilienceTests
{
    static GlobalSearchIndexStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "globalsearch-resilience-" + Guid.NewGuid().ToString("N"));
        return new GlobalSearchIndexStore(root);
    }

    static void InsertRawRow(string root, string id, string title, string updatedUtc)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "search-index.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO SearchIndex(Id,Type,Route,TargetId,Title,Detail,SearchText,UpdatedUtc) VALUES($id,'Type','route','target',$title,'detail',$search,$updated)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$title", title); cmd.Parameters.AddWithValue("$search", title.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$updated", updatedUtc);
        cmd.ExecuteNonQuery();
    }

    static GlobalSearchIndexEntry Entry(string id, string title) => new(id, "Type", "route", "target-" + id, title, "detail", DateTimeOffset.UtcNow);

    [TestMethod]
    public void ValidEntriesAreSearchableNormally()
    {
        var store = NewStore(out var root);
        try
        {
            store.ReplaceAll(new[] { Entry("e1", "widget alpha") });
            var results = store.Search("widget");
            Assert.AreEqual(1, results.Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedUpdatedUtcDoesNotCrashSearch()
    {
        var store = NewStore(out var root);
        try
        {
            store.ReplaceAll(new[] { Entry("e1", "widget alpha") });
            InsertRawRow(root, "bad-1", "widget beta", "not-a-date");

            var results = store.Search("widget");
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("widget alpha", results[0].Title);

            var corrupt = store.CorruptRows();
            Assert.AreEqual(1, corrupt.Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CorruptRowDoesNotAppearInResultsAtAll()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "widget beta", "junk");
            var results = store.Search("widget");
            Assert.AreEqual(0, results.Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RebuildReplacesCorruptRowsWithFreshHealthyData()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "widget beta", "junk");
            Assert.AreEqual(1, store.CorruptRows().Count);

            store.ReplaceAll(new[] { Entry("e1", "widget gamma") });

            Assert.AreEqual(0, store.CorruptRows().Count);
            var results = store.Search("widget");
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("widget gamma", results[0].Title);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-a", "a", "1");
            InsertRawRow(root, "bad-b", "b", "2");
            InsertRawRow(root, "bad-c", "c", "3");

            Assert.AreEqual(3, store.CorruptRows().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawTitleOrDetail()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "SECRET-TITLE-VALUE", "junk");
            var corrupt = store.CorruptRows().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("SECRET-TITLE-VALUE"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesCorruptRowDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "globalsearch-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            new GlobalSearchIndexStore(root);
            InsertRawRow(root, "bad-1", "widget", "junk");

            var reopened = new GlobalSearchIndexStore(root);
            Assert.AreEqual(1, reopened.CorruptRows().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
