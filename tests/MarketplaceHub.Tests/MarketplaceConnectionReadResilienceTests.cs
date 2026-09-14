using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2614: a malformed persisted LastTestUtc in
/// MarketplaceConnections must not crash List(), a NULL LastTestUtc (never
/// tested) must never be confused with corruption, and Get() must
/// distinguish a corrupt row from a genuinely missing one.
[TestClass]
public sealed class MarketplaceConnectionReadResilienceTests
{
    static MarketplaceConnectionStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "connection-resilience-" + Guid.NewGuid().ToString("N"));
        return new MarketplaceConnectionStore(root);
    }

    static void InsertRawRow(string root, string id, string channel, string shopId, string? lastTestUtc)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO MarketplaceConnections(Id,Channel,ShopId,DisplayName,Enabled,Status,LastTestUtc,LastError) VALUES($id,$channel,$shop,'Test',1,'NOT_CONFIGURED',$last,'')";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$channel", channel); cmd.Parameters.AddWithValue("$shop", shopId);
        cmd.Parameters.AddWithValue("$last", (object?)lastTestUtc ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void ValidConnectionsListNormally()
    {
        var store = NewStore(out var root);
        try
        {
            var saved = store.Save("etsy", "shop1", "Etsy Mağazam", true);
            var list = store.List(false);
            Assert.IsTrue(list.Any(c => c.Id == saved.Id));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedLastTestUtcDoesNotDropOtherConnections()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save("etsy", "shop1", "Etsy Mağazam", true);
            InsertRawRow(root, "bad-1", "trendyol", "shop2", "not-a-date");

            var list = store.List(false);
            Assert.AreEqual(1, list.Count);

            var corrupt = store.CorruptConnections();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NullLastTestUtcIsNeverCorruptItMeansNeverTested()
    {
        var store = NewStore(out var root);
        try
        {
            var saved = store.Save("etsy", "shop1", "Etsy Mağazam", true);
            var loaded = store.Get(saved.Id);
            Assert.IsNotNull(loaded);
            Assert.IsNull(loaded!.LastTestUtc);
            Assert.AreEqual(0, store.CorruptConnections().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void GetThrowsDistinctTypedErrorForCorruptRowVsMissing()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "trendyol", "shop2", "junk");
            Assert.ThrowsException<MarketplaceConnectionCorruptException>(() => store.Get("bad-1"));
            Assert.IsNull(store.Get("does-not-exist"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesCorruptRowDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "connection-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            new MarketplaceConnectionStore(root);
            InsertRawRow(root, "bad-1", "trendyol", "shop2", "junk");

            var reopened = new MarketplaceConnectionStore(root);
            Assert.AreEqual(0, reopened.List(false).Count);
            Assert.AreEqual(1, reopened.CorruptConnections().Count);
            Assert.ThrowsException<MarketplaceConnectionCorruptException>(() => reopened.Get("bad-1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-a", "a", "s1", "1");
            InsertRawRow(root, "bad-b", "b", "s2", "2");
            InsertRawRow(root, "bad-c", "c", "s3", "3");

            var corrupt = store.CorruptConnections();
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
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO MarketplaceConnections(Id,Channel,ShopId,DisplayName,Enabled,Status,LastTestUtc,LastError) VALUES('bad-1','trendyol','shop2','Test',1,'FAILED','junk','SECRET-ERROR-DETAIL')";
                cmd.ExecuteNonQuery();
            }
            var corrupt = store.CorruptConnections().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("SECRET-ERROR-DETAIL"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DefaultRowsAndCustomShopCoexistWithACorruptRow()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save("etsy", "shop1", "Custom", true);
            InsertRawRow(root, "bad-1", "trendyol", "shop2", "junk");

            var list = store.List(true);
            Assert.IsTrue(list.Any(c => c.Channel == "etsy" && c.ShopId == "shop1"));
            Assert.IsTrue(list.Any(c => c.ShopId == "default"));
            Assert.IsFalse(list.Any(c => c.Id == "bad-1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
