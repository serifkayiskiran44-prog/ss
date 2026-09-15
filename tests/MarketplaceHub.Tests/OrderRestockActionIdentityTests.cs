using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2668: restock idempotency must be bound to the exact business
/// action (ActionKey), not only to (Marketplace,ShopId,OrderId).
[TestClass]
public sealed class OrderRestockActionIdentityTests
{
    static void WithStore(Action<CatalogStore, string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "restock-identity-" + Guid.NewGuid().ToString("N"));
        try { test(new CatalogStore(root), root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // SKU-1 starts at 10; each sold order takes 3.
    static void Sell(CatalogStore store, string shop = "shop1", string order = "o1") =>
        store.ApplyOrderStock("etsy", shop, order, new[] { new OrderItem { Sku = "SKU-1", Title = "x", Quantity = 3 } });

    static void Seed(CatalogStore store) => store.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Ürün", Price = 10, Stock = 10 });

    static int Stock(CatalogStore store) => store.Products().Single(p => p.Sku == "SKU-1").Stock;

    static void InsertRawRestore(string root, string order, string actionKey, string payload, string appliedUtc)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO OrderStockRestores VALUES('etsy','shop1',$order,$action,$payload,$at)";
        cmd.Parameters.AddWithValue("$order", order); cmd.Parameters.AddWithValue("$action", actionKey); cmd.Parameters.AddWithValue("$payload", payload); cmd.Parameters.AddWithValue("$at", appliedUtc);
        cmd.ExecuteNonQuery();
    }

    static string StoredActionKey(string root, string order = "o1")
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ActionKey FROM OrderStockRestores WHERE Marketplace='etsy' AND ShopId='shop1' AND OrderId=$order"; cmd.Parameters.AddWithValue("$order", order);
        return (string)cmd.ExecuteScalar()!;
    }

    [TestMethod]
    public void FirstActionRestoresExactlyOnceAndPersistsItsKey() => WithStore((store, root) =>
    {
        Seed(store); Sell(store);
        var result = store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1"), true);
        Assert.IsFalse(result.AlreadyApplied);
        Assert.AreEqual(10, Stock(store));
        Assert.AreEqual("cancel:event-1", StoredActionKey(root));
    });

    [TestMethod]
    public void ExactReplayReturnsTheOriginalReceiptWithoutMutation() => WithStore((store, _) =>
    {
        Seed(store); Sell(store);
        var first = store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1"), true);
        var replay = store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1"), true);
        Assert.IsTrue(replay.AlreadyApplied);
        Assert.AreEqual(first.AppliedUtc, replay.AppliedUtc);
        Assert.AreEqual(first.Lines.Single().RestoredStock, replay.Lines.Single().RestoredStock);
        Assert.AreEqual(10, Stock(store));
    });

    [TestMethod]
    public void DifferentActionForTheSameOrderIsAConflictNotAReplay() => WithStore((store, _) =>
    {
        Seed(store); Sell(store);
        store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1"), true);
        var ex = Assert.ThrowsException<OrderRestockReviewRequiredException>(() => store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "return:event-2"), true));
        Assert.AreEqual(OrderRestockReviewReason.ActionConflict, ex.Reason);
        Assert.AreNotEqual(ex.IncomingActionKeyHash, ex.StoredActionKeyHash);
        Assert.IsFalse(ex.Message.Contains("event-"), "Diagnostics must not echo raw action keys.");
        Assert.AreEqual(10, Stock(store));
    });

    [TestMethod]
    public void SurroundingWhitespaceCanonicalizesToTheSameAction() => WithStore((store, _) =>
    {
        Seed(store); Sell(store);
        var preview = store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1");
        store.ApplyOrderRestock(preview, true);
        var replay = store.ApplyOrderRestock(preview with { ActionKey = "  cancel:event-1  " }, true);
        Assert.IsTrue(replay.AlreadyApplied);
        Assert.AreEqual(10, Stock(store));
    });

    [TestMethod]
    public void CasingDifferencesStayDistinctActions() => WithStore((store, _) =>
    {
        Seed(store); Sell(store);
        store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1"), true);
        var ex = Assert.ThrowsException<OrderRestockReviewRequiredException>(() => store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:Event-1"), true));
        Assert.AreEqual(OrderRestockReviewReason.ActionConflict, ex.Reason);
    });

    [TestMethod]
    public void ControlCharactersAndOverLengthKeysRejectBeforeAnyMutation() => WithStore((store, root) =>
    {
        Seed(store); Sell(store);
        var preview = store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1");
        Assert.ThrowsException<ArgumentException>(() => store.ApplyOrderRestock(preview with { ActionKey = "cancel:event-1" }, true));
        Assert.ThrowsException<ArgumentException>(() => store.ApplyOrderRestock(preview with { ActionKey = new string('k', 201) }, true));
        Assert.ThrowsException<ArgumentException>(() => store.CreateOrderRestockPreview("etsy", "shop1", "o1", new string('k', 201)));
        Assert.AreEqual(7, Stock(store));
        Assert.AreEqual(OrderRestockReviewReason.ActionConflict, Assert.ThrowsException<OrderRestockReviewRequiredException>(() =>
        {
            store.ApplyOrderRestock(preview, true);
            store.ApplyOrderRestock(preview with { ActionKey = "other" }, true);
        }).Reason, "After rejected keys, the first valid apply must still be the one that restores.");
    });

    [TestMethod]
    public void LegacyBlankActionKeyIsReviewRequiredNeverReplaySuccess() => WithStore((store, root) =>
    {
        Seed(store); Sell(store);
        var productId = store.Products().Single().Id;
        var payload = JsonSerializer.Serialize(new[] { new OrderRestockPreviewLine(productId, "SKU-1", 3, 7, 10, DateTime.UtcNow) });
        InsertRawRestore(root, "o1", "   ", payload, DateTime.UtcNow.ToString("O"));

        var ex = Assert.ThrowsException<OrderRestockReviewRequiredException>(() => store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1"), true));
        Assert.AreEqual(OrderRestockReviewReason.LegacyActionKey, ex.Reason);
        Assert.AreEqual(7, Stock(store));
    });

    [TestMethod]
    public void MalformedReceiptWithMatchingKeyIsReceiptCorruptNotSuccess() => WithStore((store, root) =>
    {
        Seed(store); Sell(store);
        InsertRawRestore(root, "o1", "cancel:event-1", "{broken", DateTime.UtcNow.ToString("O"));

        var same = Assert.ThrowsException<OrderRestockReviewRequiredException>(() => store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1"), true));
        var other = Assert.ThrowsException<OrderRestockReviewRequiredException>(() => store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "return:event-2"), true));
        Assert.AreEqual(OrderRestockReviewReason.ReceiptCorrupt, same.Reason);
        Assert.AreEqual(OrderRestockReviewReason.ReceiptCorrupt, other.Reason);
        Assert.AreEqual(7, Stock(store));
        Assert.AreEqual("cancel:event-1", StoredActionKey(root), "A corrupt receipt must not be deleted or overwritten.");
    });

    [TestMethod]
    public void InvalidAppliedUtcIsReceiptCorrupt() => WithStore((store, root) =>
    {
        Seed(store); Sell(store);
        var productId = store.Products().Single().Id;
        var payload = JsonSerializer.Serialize(new[] { new OrderRestockPreviewLine(productId, "SKU-1", 3, 7, 10, DateTime.UtcNow) });
        InsertRawRestore(root, "o1", "cancel:event-1", payload, "not-a-date");

        var ex = Assert.ThrowsException<OrderRestockReviewRequiredException>(() => store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1"), true));
        Assert.AreEqual(OrderRestockReviewReason.ReceiptCorrupt, ex.Reason);
        Assert.AreEqual(7, Stock(store));
    });

    [TestMethod]
    public void ConcurrentSameActionProducesOneMutationAndIdempotentReplays() => WithStore((store, root) =>
    {
        Seed(store); Sell(store);
        var preview = store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1");
        using var gate = new Barrier(8);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => { var s = new CatalogStore(root); gate.SignalAndWait(); return s.ApplyOrderRestock(preview, true); })).ToArray();
        Task.WaitAll(tasks);
        Assert.AreEqual(1, tasks.Count(t => !t.Result.AlreadyApplied));
        Assert.AreEqual(7, tasks.Count(t => t.Result.AlreadyApplied));
        Assert.AreEqual(10, Stock(store));
    });

    [TestMethod]
    public void ConcurrentDifferentActionsCommitAtMostOneRestock() => WithStore((store, root) =>
    {
        Seed(store); Sell(store);
        var preview = store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-0");
        using var gate = new Barrier(8);
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var s = new CatalogStore(root); gate.SignalAndWait();
            try { return s.ApplyOrderRestock(preview with { ActionKey = "cancel:event-" + i }, true).AlreadyApplied ? "replay" : "applied"; }
            catch (OrderRestockReviewRequiredException ex) when (ex.Reason == OrderRestockReviewReason.ActionConflict) { return "conflict"; }
        })).ToArray();
        Task.WaitAll(tasks);
        Assert.AreEqual(1, tasks.Count(t => t.Result == "applied"));
        Assert.AreEqual(7, tasks.Count(t => t.Result == "conflict"));
        Assert.AreEqual(10, Stock(store));
    });

    [TestMethod]
    public void RestartPreservesActionIdentity() => WithStore((store, root) =>
    {
        Seed(store); Sell(store);
        store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1"), true);

        var reopened = new CatalogStore(root);
        Assert.IsTrue(reopened.ApplyOrderRestock(reopened.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1"), true).AlreadyApplied);
        Assert.ThrowsException<OrderRestockReviewRequiredException>(() => reopened.ApplyOrderRestock(reopened.CreateOrderRestockPreview("etsy", "shop1", "o1", "return:event-2"), true));
        Assert.AreEqual(10, Stock(reopened));
    });

    [TestMethod]
    public void SameOrderIdInAnotherShopStaysIsolated() => WithStore((store, _) =>
    {
        Seed(store); Sell(store, "shop1", "o1"); Sell(store, "shop2", "o1");
        Assert.AreEqual(4, Stock(store));
        store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1"), true);
        var other = store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop2", "o1", "return:event-2"), true);
        Assert.IsFalse(other.AlreadyApplied);
        Assert.AreEqual(10, Stock(store));
    });

    [TestMethod]
    public void SameActionKeyOnADifferentOrderIsNotConflated() => WithStore((store, _) =>
    {
        Seed(store); Sell(store, "shop1", "o1"); Sell(store, "shop1", "o2");
        Assert.IsFalse(store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o1", "shared-key"), true).AlreadyApplied);
        Assert.IsFalse(store.ApplyOrderRestock(store.CreateOrderRestockPreview("etsy", "shop1", "o2", "shared-key"), true).AlreadyApplied);
        Assert.AreEqual(10, Stock(store));
    });

    [TestMethod]
    public void DatabaseBusyIsNotMisclassifiedAsConflictOrCorruption() => WithStore((store, root) =>
    {
        Seed(store); Sell(store);
        var preview = store.CreateOrderRestockPreview("etsy", "shop1", "o1", "cancel:event-1");
        using var locker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db"), DefaultTimeout = 1 }.ToString());
        locker.Open();
        using var hold = locker.BeginTransaction(deferred: false);
        var ex = Assert.ThrowsException<SqliteException>(() => store.ApplyOrderRestock(preview, true));
        Assert.AreEqual(5, ex.SqliteErrorCode, "Expected SQLITE_BUSY, surfaced as-is.");
        hold.Rollback();
        Assert.AreEqual(7, Stock(store));
    });
}
