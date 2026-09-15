using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2638: a malformed persisted UpdatedUtc/LastValidatedUtc in
/// ProductMedia must not resolve to DateTime.MinValue and pass as a normal
/// row - it must isolate to an explicit recovery state instead, without
/// dropping other healthy media rows.
[TestClass]
public sealed class MediaTimestampIntegrityTests
{
    static MediaStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "media-ts-" + Guid.NewGuid().ToString("N"));
        return new MediaStore(root);
    }

    static void InsertRawRow(string root, string id, string productId, string updatedUtc, string? lastValidatedUtc = null, int sortOrder = 0)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "media.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        var url = "https://x/" + id + ".jpg";
        cmd.CommandText = "INSERT INTO ProductMedia(Id,ProductId,Url,NormalizedUrl,Source,SortOrder,IsPrimary,ContentHash,Status,Error,LastValidatedUtc,UpdatedUtc) VALUES($id,$product,$url,$url,'manual',$order,0,'','Pending','',$validated,$updated)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$product", productId); cmd.Parameters.AddWithValue("$url", url); cmd.Parameters.AddWithValue("$order", sortOrder);
        cmd.Parameters.AddWithValue("$validated", (object?)lastValidatedUtc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", updatedUtc);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void NormalRoundTripIsUnaffected()
    {
        var store = NewStore(out var root);
        try
        {
            var row = store.Add("p1", "https://example.com/a.jpg");
            var loaded = store.List("p1").Single();
            Assert.AreEqual(row.Id, loaded.Id);
            Assert.IsNull(loaded.LastValidatedUtc);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedUpdatedUtcDoesNotDropOtherRows()
    {
        var store = NewStore(out var root);
        try
        {
            store.Add("p1", "https://example.com/good.jpg");
            InsertRawRow(root, "bad-1", "p1", "not-a-date", sortOrder: 1);

            var list = store.List("p1");
            Assert.AreEqual(1, list.Count);

            var corrupt = store.CorruptRows("p1");
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
            Assert.AreEqual("p1", corrupt[0].ProductId);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedUpdatedUtcNeverResolvesToMinValue()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "p1", "");
            Assert.IsFalse(store.List("p1").Any(m => m.Id == "bad-1"));
            Assert.AreEqual(1, store.CorruptRows("p1").Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedLastValidatedUtcIsIsolatedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "p1", DateTime.UtcNow.ToString("O"), lastValidatedUtc: "junk");
            Assert.AreEqual(0, store.List("p1").Count);
            Assert.AreEqual(1, store.CorruptRows("p1").Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NullLastValidatedUtcIsNotCorruptItMeansNeverValidated()
    {
        var store = NewStore(out var root);
        try
        {
            var row = store.Add("p1", "https://example.com/a.jpg");
            Assert.IsNull(store.List("p1").Single().LastValidatedUtc);
            Assert.AreEqual(0, store.CorruptRows("p1").Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MixedValidAndInvalidRowsAcrossProductsAreIsolatedCorrectly()
    {
        var store = NewStore(out var root);
        try
        {
            store.Add("p1", "https://example.com/a.jpg");
            store.Add("p2", "https://example.com/b.jpg");
            InsertRawRow(root, "bad-1", "p2", "junk", sortOrder: 1);

            Assert.AreEqual(1, store.List("p1").Count);
            Assert.AreEqual(1, store.List("p2").Count);
            Assert.AreEqual(0, store.CorruptRows("p1").Count);
            Assert.AreEqual(1, store.CorruptRows("p2").Count);
            Assert.AreEqual(1, store.CorruptRows().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesRecoveryState()
    {
        var root = Path.Combine(Path.GetTempPath(), "media-ts-" + Guid.NewGuid().ToString("N"));
        try
        {
            new MediaStore(root).Add("p1", "https://example.com/a.jpg");
            InsertRawRow(root, "bad-1", "p1", "junk", sortOrder: 1);

            var reopened = new MediaStore(root);
            Assert.AreEqual(1, reopened.List("p1").Count);
            Assert.AreEqual(1, reopened.CorruptRows("p1").Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawUrl()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "p1", "junk");
            var corrupt = store.CorruptRows("p1").Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("bad-1\\.jpg"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-a", "p1", "1", sortOrder: 0);
            InsertRawRow(root, "bad-b", "p1", DateTime.UtcNow.ToString("O"), lastValidatedUtc: "2", sortOrder: 1);

            var corrupt = store.CorruptRows("p1");
            Assert.AreEqual(2, corrupt.Count);
            CollectionAssert.AreEquivalent(new[] { "bad-a", "bad-b" }, corrupt.Select(c => c.Id).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
