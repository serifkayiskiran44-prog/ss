using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2634: a malformed CreatedUtc/UpdatedUtc row in
/// OrderExceptions must not crash List(), must never resolve to a default/
/// zero date, and Save()'s exact-key upsert readback must find its own row
/// even when an unrelated row elsewhere in the table is corrupt.
[TestClass]
public sealed class OrderExceptionReadResilienceTests
{
    static OrderExceptionStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "orderexception-resilience-" + Guid.NewGuid().ToString("N"));
        return new OrderExceptionStore(root);
    }

    static void InsertRawRow(string root, string id, string marketplace, string shop, string order, string created, string updated)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "order-exceptions.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO OrderExceptions(Id,Marketplace,ShopId,OrderId,Type,EventKey,Severity,Message,Status,CreatedUtc,UpdatedUtc) VALUES($id,$m,$s,$o,'Type','key','Warning','msg','Pending',$created,$updated)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$m", marketplace); cmd.Parameters.AddWithValue("$s", shop); cmd.Parameters.AddWithValue("$o", order);
        cmd.Parameters.AddWithValue("$created", created); cmd.Parameters.AddWithValue("$updated", updated);
        cmd.ExecuteNonQuery();
    }

    static OrderExceptionRecord Record(string order) => new() { Marketplace = "etsy", ShopId = "shop1", OrderId = order, Type = "MissingSku", EventKey = "item:x", Message = "SKU eksik." };

    [TestMethod]
    public void NormalRoundTripIsUnaffected()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(Record("o1"));
            Assert.AreEqual(1, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedCreatedUtcDoesNotBlockOtherRecords()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(Record("o1"));
            InsertRawRow(root, "bad-1", "trendyol", "shop2", "o2", "not-a-date", DateTime.UtcNow.ToString("O"));

            var list = store.List();
            Assert.AreEqual(1, list.Count);

            var corrupt = store.CorruptExceptions();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedUpdatedUtcDoesNotBlockOtherRecords()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(Record("o1"));
            InsertRawRow(root, "bad-1", "trendyol", "shop2", "o2", DateTime.UtcNow.ToString("O"), "junk");

            Assert.AreEqual(1, store.List().Count);
            Assert.AreEqual(1, store.CorruptExceptions().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CorruptRowNeverResolvesToDefaultDate()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "trendyol", "shop2", "o2", "", "");
            Assert.IsFalse(store.List().Any(r => r.Id == "bad-1"));
            Assert.AreEqual(1, store.CorruptExceptions().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreIsolatedDeterministically()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-a", "etsy", "s1", "o1", "1", DateTime.UtcNow.ToString("O"));
            InsertRawRow(root, "bad-b", "etsy", "s2", "o2", DateTime.UtcNow.ToString("O"), "2");
            InsertRawRow(root, "bad-c", "etsy", "s3", "o3", "3", "4");

            var corrupt = store.CorruptExceptions();
            Assert.AreEqual(3, corrupt.Count);
            CollectionAssert.AreEquivalent(new[] { "bad-a", "bad-b", "bad-c" }, corrupt.Select(c => c.Id).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SaveReadbackFindsItsOwnRowDespiteAnUnrelatedCorruptRow()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "trendyol", "shop2", "o2", "junk", DateTime.UtcNow.ToString("O"));
            var saved = store.Save(Record("o1"));
            Assert.AreEqual("o1", saved.OrderId);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesValidRecordsAndCorruptDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "orderexception-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OrderExceptionStore(root);
            store.Save(Record("o1"));
            InsertRawRow(root, "bad-1", "trendyol", "shop2", "o2", "junk", DateTime.UtcNow.ToString("O"));

            var reopened = new OrderExceptionStore(root);
            Assert.AreEqual(1, reopened.List().Count);
            Assert.AreEqual(1, reopened.CorruptExceptions().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawMessage()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "trendyol", "shop2", "o2", "junk", DateTime.UtcNow.ToString("O"));
            var corrupt = store.CorruptExceptions().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("msg"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FindReturnsNullForACorruptRowInsteadOfCrashing()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "trendyol", "shop2", "o2", "junk", DateTime.UtcNow.ToString("O"));
            Assert.IsNull(store.Find("bad-1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
