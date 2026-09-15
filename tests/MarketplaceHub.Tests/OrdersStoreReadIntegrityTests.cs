using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2649: OrdersStore.ReadAll/ReadPage must isolate a corrupt or
/// identity-mismatched persisted order row instead of throwing an unhandled
/// exception, and non-manual Save/SaveBatch must fail closed rather than
/// merging into (or overwriting) a corrupt existing row.
[TestClass]
public sealed class OrdersStoreReadIntegrityTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "orders-integrity-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void InsertRawRow(string root, string marketplace, string shop, string id, string payload)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "orders.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO orders VALUES($m,$s,$i,$p)";
        cmd.Parameters.AddWithValue("$m", marketplace); cmd.Parameters.AddWithValue("$s", shop); cmd.Parameters.AddWithValue("$i", id); cmd.Parameters.AddWithValue("$p", payload);
        cmd.ExecuteNonQuery();
    }

    static OrderSnapshot Valid(string marketplace, string shop, string id) => new() { Marketplace = marketplace, ShopId = shop, OrderId = id, UpdatedAt = DateTimeOffset.UtcNow };

    [TestMethod]
    public void NormalRoundTripThroughSaveReadAllReadPageIsUnaffected() => WithRoot(root =>
    {
        var store = new OrdersStore(root);
        store.SaveBatch([Valid("etsy", "shop-a", "o1")]);
        Assert.AreEqual(1, store.ReadAll().Count);
        Assert.AreEqual(1, store.ReadPage().Total);
        Assert.AreEqual(0, store.CorruptOrders().Count);
    });

    [TestMethod]
    public void MalformedJsonRowIsIsolatedFromReadAll() => WithRoot(root =>
    {
        var store = new OrdersStore(root);
        InsertRawRow(root, "etsy", "shop-a", "o-bad", "{}");
        Assert.AreEqual(0, store.ReadAll().Count);
        Assert.AreEqual(1, store.CorruptOrders().Count);
    });

    [TestMethod]
    public void NinetyNineHealthyOrdersSurviveOneCorruptRowInReadAll() => WithRoot(root =>
    {
        var store = new OrdersStore(root);
        var orders = Enumerable.Range(0, 99).Select(i => Valid("etsy", "shop-a", "o" + i)).ToList();
        store.SaveBatch(orders);
        InsertRawRow(root, "etsy", "shop-a", "o-bad", "{}");
        Assert.AreEqual(99, store.ReadAll().Count);
        Assert.AreEqual(1, store.CorruptOrders().Count);
    });

    [TestMethod]
    public void WrongShopPayloadIdentityIsQuarantinedNotTrusted() => WithRoot(root =>
    {
        var store = new OrdersStore(root);
        var wrongShop = Valid("etsy", "shop-b", "o1");
        InsertRawRow(root, "etsy", "shop-a", "o1", JsonSerializer.Serialize(wrongShop));
        Assert.AreEqual(0, store.ReadAll().Count);
        var corrupt = store.CorruptOrders().Single();
        Assert.AreEqual("shop-a", corrupt.ShopId);
        StringAssert.Contains(corrupt.Reason, "kimlik");
    });

    [TestMethod]
    public void JsonNullLiteralIsQuarantined() => WithRoot(root =>
    {
        var store = new OrdersStore(root);
        InsertRawRow(root, "etsy", "shop-a", "o1", "null");
        Assert.AreEqual(0, store.ReadAll().Count);
        Assert.AreEqual(1, store.CorruptOrders().Count);
    });

    [TestMethod]
    public void CorruptOrderInOneShopDoesNotHideHealthyOrderInAnotherMarketplace() => WithRoot(root =>
    {
        var store = new OrdersStore(root);
        store.SaveBatch([Valid("ozon", "shop-b", "o1")]);
        InsertRawRow(root, "etsy", "shop-a", "o1", "{}");
        var healthy = store.ReadAll();
        Assert.AreEqual(1, healthy.Count);
        Assert.AreEqual("ozon", healthy[0].Marketplace);
    });

    [TestMethod]
    public void ReadPageTotalStaysDeterministicAndItemsExcludeTheCorruptRow() => WithRoot(root =>
    {
        var store = new OrdersStore(root);
        store.SaveBatch([Valid("etsy", "shop-a", "o1"), Valid("etsy", "shop-a", "o2")]);
        InsertRawRow(root, "etsy", "shop-a", "o-bad", "{}");
        var page = store.ReadPage(limit: 10);
        Assert.AreEqual(3, page.Total, "Total counts raw rows without deserializing.");
        Assert.AreEqual(2, page.Items.Count, "Corrupt row must never be materialized as a normal item.");
    });

    [TestMethod]
    public void NonManualSaveOverAnExistingCorruptRowFailsClosedAndCommitsNothing() => WithRoot(root =>
    {
        var store = new OrdersStore(root);
        InsertRawRow(root, "etsy", "shop-a", "o1", "{}");
        store.SaveBatch([Valid("etsy", "shop-a", "o2")]); // a second, independent order in the same batch
        Assert.ThrowsException<OrderRowCorruptException>(() => store.SaveBatch([
            Valid("etsy", "shop-a", "o1"), // corrupt existing row
            Valid("etsy", "shop-a", "o3")  // otherwise-valid new row in the same batch
        ]));
        // The whole batch must roll back: o3 must not have been committed.
        Assert.IsFalse(store.ReadAll().Any(o => o.OrderId == "o3"));
        Assert.AreEqual(1, store.CorruptOrders().Count);
    });

    [TestMethod]
    public void ManualSaveIsUnaffectedByThisIntegrityBoundary() => WithRoot(root =>
    {
        var store = new OrdersStore(root);
        InsertRawRow(root, "etsy", "shop-a", "o1", "{}");
        // SaveManual always overwrites (explicit user action / repair path); it must
        // not be blocked by the non-manual merge's fail-closed corruption guard.
        store.SaveManual(Valid("etsy", "shop-a", "o1"));
        Assert.AreEqual(1, store.ReadAll().Count);
        Assert.AreEqual(0, store.CorruptOrders().Count);
    });
}

