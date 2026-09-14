using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #945 (ORDER EXCEPTIONS: unmapped order queue). Orders with one or more unmapped lines are listed apart with their
// age, their impact and a deep link to the reviews; a cancelled order drops out, a resolved one drops out on the next
// sync, each store sees its own; while the exception stands the order's stock is not mutated; it reads back after a
// restart.
[TestClass]
public sealed class UnmappedOrderQueueTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static OrderItem Line(string sku, string title, int qty = 1) => new() { Sku = sku, Title = title, Quantity = qty };
    static OrderSnapshot Order(string channel, string shop, string id, double ageHours, string status = "paid", params OrderItem[] items) => new() { Marketplace = channel, ShopId = shop, OrderId = id, RawStatus = status, UpdatedAt = Now.AddHours(-ageHours), SourceUpdatedAt = Now.AddHours(-ageHours), Items = items.ToList() };
    static OrderLineReview Review(long id, string channel, string shop, string order, int index, string state = OrderLineReview.Open) => new(id, channel, shop, order, index, "SKU-X", "Bir", OrderLineJudgement.Missing, "", Array.Empty<string>(), "SKU yok", state, Now, null, "");

    [TestMethod]
    public void TheQueueListsOrdersWithUnmappedLinesByAgeAndImpactSkipsCancelledAndResolvedOnesAndKeepsStoresApart()
    {
        var orders = new[]
        {
            Order("etsy", "S1", "o-one", 30, "paid", Line("SKU-1", "A"), Line("SKU-9", "B", 3), Line("SKU-2", "C")),
            Order("etsy", "S1", "o-many", 2, "paid", Line("SKU-8", "D", 2), Line("SKU-7", "E", 4)),
            Order("etsy", "S1", "o-done", 5, "paid", Line("SKU-1", "A")),
            Order("etsy", "S1", "o-cancel", 50, "İptal edildi", Line("SKU-6", "F", 9)),
            Order("ebay", "E1", "o-one", 1, "paid", Line("SKU-5", "G")),
        };
        var reviews = new[] { Review(1, "etsy", "S1", "o-one", 1), Review(2, "etsy", "S1", "o-many", 0), Review(3, "etsy", "S1", "o-many", 1), Review(4, "etsy", "S1", "o-done", 0, OrderLineReview.Closed), Review(5, "etsy", "S1", "o-cancel", 0), Review(6, "ebay", "E1", "o-one", 0), Review(7, "etsy", "S1", "o-gone", 0) };
        var queue = UnmappedOrders.Build(orders, reviews, Now);
        Assert.AreEqual((3, 4, 10), (queue.Orders, queue.Lines, queue.Units), queue.Headline); StringAssert.Contains(queue.Headline, "3 sipariş, 4 eşlenmemiş satır (10 adet); en eskisi 1 gün önce — bu siparişlerde stok düşümü engelli");
        Assert.AreEqual("o-one", queue.Rows[0].OrderId, "the oldest first"); Assert.AreEqual("etsy", queue.Rows[0].Marketplace);
        var one = queue.For("etsy", "S1", "o-one")!; Assert.AreEqual((1, 3, 3, "orders?review=1"), (one.UnmappedLines, one.TotalLines, one.UnmappedUnits, one.DeepLink)); StringAssert.Contains(one.Words, "1/3 satır eşlenmemiş (3 adet)"); StringAssert.Contains(one.Words, "incelemeler: #1"); StringAssert.Contains(one.Words, "stok düşümü engelli");
        var many = queue.For("etsy", "S1", "o-many")!; Assert.AreEqual((2, 2, 6), (many.UnmappedLines, many.TotalLines, many.UnmappedUnits)); CollectionAssert.AreEqual(new long[] { 2, 3 }, many.ReviewIds.ToArray());
        Assert.IsNull(queue.For("etsy", "S1", "o-done"), "a closed review is no exception"); Assert.IsNull(queue.For("etsy", "S1", "o-cancel"), "a cancelled order drops out"); Assert.IsNull(queue.For("etsy", "S1", "o-gone"), "a review of an order that is gone is not listed");
        var etsyOnly = UnmappedOrders.Build(orders, reviews, Now, "etsy", "S1"); Assert.AreEqual(2, etsyOnly.Orders); Assert.IsNull(etsyOnly.For("ebay", "E1", "o-one"), "one store's queue never shows another's");
        Assert.AreEqual("Eşlenmemiş satırı olan sipariş yok.", UnmappedOrders.Build(orders, Array.Empty<OrderLineReview>(), Now).Headline);
        Assert.IsTrue(LineMappingOrderException.BlocksStock(LineMappingOrderException.UnmappedLines) && LineMappingOrderException.BlocksStock(LineMappingOrderException.DuplicateConflict) && !LineMappingOrderException.BlocksStock(LineMappingOrderException.MoneyMismatch));
    }

    [TestMethod]
    public void TheExceptionBlocksTheOrdersStockUntilTheLinesAreResolvedAndItReadsBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "unmapped-orders-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var orders = new OrdersStore(root); var reviews = new OrderLineReviewStore(root); var exceptions = new LineMappingExceptionStore(root);
            var products = new[] { new CatalogProduct { Id = "p1", Sku = "SKU-1", Name = "A", Active = true }, new CatalogProduct { Id = "p9", Sku = "SKU-9", Name = "B", Active = true } };
            orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Items = new List<OrderItem> { Line("SKU-1", "A"), Line("SKU-X", "Bilinmeyen", 2) } });
            orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-2", RawStatus = "paid", Items = new List<OrderItem> { Line("SKU-1", "A") } });
            orders.SaveManual(new OrderSnapshot { Marketplace = "ebay", ShopId = "E1", OrderId = "o-1", RawStatus = "paid", Items = new List<OrderItem> { Line("SKU-Y", "Yabancı") } });

            // The scan opens reviews; the queue lists o-1 on etsy and o-1 on ebay apart; the sync raises the exceptions; the stock apply is blocked by name.
            reviews.Scan(orders.ReadAll(), products, Now);
            var queue = UnmappedOrders.Build(orders.ReadAll(), reviews.Open(), Now.AddHours(1)); Assert.AreEqual(2, queue.Orders, queue.Headline);
            Assert.AreEqual((2, 0), UnmappedOrders.Sync(exceptions, queue, Now.AddHours(1)));
            var reason = exceptions.StockBlockReason("etsy", "S1", "o-1"); Assert.IsNotNull(reason); StringAssert.Contains(reason, "UNMAPPED_LINES"); StringAssert.Contains(reason, "1/2 satır eşlenmemiş");
            Assert.IsNull(exceptions.StockBlockReason("etsy", "S1", "o-2"), "a fully mapped order is not blocked"); Assert.IsNotNull(exceptions.StockBlockReason("ebay", "E1", "o-1"), "the same order number on another store is that store's own exception");
            Assert.AreEqual(1, exceptions.Open("etsy", "S1").Count); Assert.AreEqual(2, exceptions.Open().Count);

            // The operator resolves the line; the next scan closes the review, the queue drops the order, the sync resolves the exception, the block lifts.
            var review = reviews.Open("etsy", "S1").Single(); reviews.Resolve(review.Id, "p9", products, "", Now.AddHours(2));
            reviews.Scan(orders.ReadAll(), products, Now.AddHours(2));
            var after = UnmappedOrders.Build(orders.ReadAll(), reviews.Open(), Now.AddHours(2)); Assert.IsNull(after.For("etsy", "S1", "o-1")); Assert.AreEqual(1, after.Orders);
            Assert.AreEqual((1, 1), UnmappedOrders.Sync(exceptions, after, Now.AddHours(2)));
            Assert.IsNull(exceptions.StockBlockReason("etsy", "S1", "o-1")); Assert.AreEqual(LineMappingOrderException.Resolved, exceptions.Of("etsy", "S1", "o-1").Single().State); Assert.IsNotNull(exceptions.Of("etsy", "S1", "o-1").Single().ResolvedUtc);

            // A cancelled order with an unmapped line raises nothing; raising again reopens a resolved exception.
            orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-3", RawStatus = "İptal", Items = new List<OrderItem> { Line("SKU-Z", "Z") } });
            reviews.Scan(orders.ReadAll(), products, Now.AddHours(3)); var withCancelled = UnmappedOrders.Build(orders.ReadAll(), reviews.Open(), Now.AddHours(3)); Assert.IsNull(withCancelled.For("etsy", "S1", "o-3"));
            exceptions.Raise("etsy", "S1", "o-1", LineMappingOrderException.UnmappedLines, "tekrar", Now.AddHours(4)); Assert.AreEqual(LineMappingOrderException.Open, exceptions.Of("etsy", "S1", "o-1").Single().State); Assert.IsNull(exceptions.Of("etsy", "S1", "o-1").Single().ResolvedUtc);

            // Restart: the exceptions read back.
            SqliteConnection.ClearAllPools();
            var reopened = new LineMappingExceptionStore(root);
            Assert.AreEqual(2, reopened.Open().Count); Assert.IsNotNull(reopened.StockBlockReason("ebay", "E1", "o-1"));
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
