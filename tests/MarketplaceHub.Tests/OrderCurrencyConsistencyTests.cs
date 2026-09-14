using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #947 (ORDER MONEY: currency consistency guard). Every reported currency -- header, line, shipping, refund, the
// store's own expectation -- must be a recognizable code; an unrecognized one is its own outcome, never silently
// treated as a default. A genuine disagreement between components is a mismatch, named, raised as the shared
// CURRENCY_MISMATCH exception and resolved when the order clears.
[TestClass]
public sealed class OrderCurrencyConsistencyTests
{
    static OrderItem Line(string sku, string? currency = null) => new() { Sku = sku, Title = "Ürün " + sku, Quantity = 1, UnitPrice = 10m, Currency = currency };
    static OrderSnapshot Order(string currency, params OrderItem[] items) => new() { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Currency = currency, Items = items.ToList() };

    [TestMethod]
    public void AConsistentOrderALineMismatchARefundMismatchAndAnUnknownCurrencyAreEachJudged()
    {
        // Consistent: header, both lines (one silent, one explicit and equal), and a store expectation all agree.
        var consistent = Order("TRY", Line("A"), Line("B", "TRY")); var ok = OrderCurrencyConsistency.Evaluate(consistent, "TRY");
        Assert.AreEqual((OrderCurrencyCheck.Ok, "TRY", 0), (ok.Outcome, ok.HeaderCurrency, ok.Mismatches.Count)); StringAssert.Contains(ok.Words, "tutarlı: her bileşen TRY");

        // One line reports a different currency than the header: named by line and SKU.
        var lineMismatch = Order("TRY", Line("A"), Line("B", "USD")); var lineCheck = OrderCurrencyConsistency.Evaluate(lineMismatch);
        Assert.AreEqual(OrderCurrencyCheck.Mismatch, lineCheck.Outcome); Assert.AreEqual(1, lineCheck.Mismatches.Count); StringAssert.Contains(lineCheck.Mismatches[0], "satır 2 (SKU B) para birimi USD, başlık TRY");

        // A refund reported in a different currency than the header.
        var refundMismatch = Order("TRY", Line("A")); refundMismatch.RefundTotal = 5m; refundMismatch.RefundCurrency = "EUR";
        var refundCheck = OrderCurrencyConsistency.Evaluate(refundMismatch); Assert.AreEqual(OrderCurrencyCheck.Mismatch, refundCheck.Outcome); StringAssert.Contains(refundCheck.Mismatches.Single(), "iade para birimi EUR, başlık TRY");
        Assert.AreEqual(OrderCurrencyCheck.Ok, OrderCurrencyConsistency.Evaluate(Order("TRY", Line("A"))).Outcome, "a refund with no currency reported is not checked");

        // An unrecognized header currency is its own outcome; never a silent default. So is the store's own mismatch, and a bad shipping or line code.
        var unknown = OrderCurrencyConsistency.Evaluate(Order("TL", Line("A"))); Assert.AreEqual((OrderCurrencyCheck.UnknownCurrency, "TL"), (unknown.Outcome, unknown.HeaderCurrency)); StringAssert.Contains(unknown.Words, "tanınmıyor (TL)"); StringAssert.Contains(unknown.Words, "varsayılan bir para birimi kullanılmadı");
        Assert.AreEqual(OrderCurrencyCheck.UnknownCurrency, OrderCurrencyConsistency.Evaluate(Order("", Line("A"))).Outcome, "a missing header currency is unrecognized, not defaulted");
        var storeMismatch = OrderCurrencyConsistency.Evaluate(Order("TRY", Line("A")), "USD"); Assert.AreEqual(OrderCurrencyCheck.Mismatch, storeMismatch.Outcome); StringAssert.Contains(storeMismatch.Mismatches.Single(), "mağaza beklenen para birimi USD, sipariş başlığı TRY");
        var shipping = Order("TRY", Line("A")); shipping.ShippingCurrency = "usd"; Assert.AreEqual("kargo para birimi USD, başlık TRY", OrderCurrencyConsistency.Evaluate(shipping).Mismatches.Single());
        Assert.IsTrue(OrderCurrencyConsistency.Evaluate(Order("TRY", Line("A", "xx"))).Mismatches.Single().Contains("satır 1 para birimi tanınmıyor (xx)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SyncRaisesTheSharedCurrencyMismatchExceptionUsesTheStoresOwnCurrencyAndResolvesItWhenTheOrderClearsAndItReadsBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "currency-consistency-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var orders = new OrdersStore(root); var exceptions = new LineMappingExceptionStore(root);
            var now = DateTime.UtcNow;
            orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Currency = "TRY", Items = new List<OrderItem> { Line("A", "USD") } });
            orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-2", RawStatus = "paid", Currency = "TRY", Items = new List<OrderItem> { Line("B") } });

            Func<string, string, string?> noStoreExpectation = (_, _) => null;
            var first = OrderCurrencyConsistency.Sync(exceptions, orders.ReadAll(), noStoreExpectation, now); Assert.AreEqual((1, 0), first);
            StringAssert.Contains(exceptions.Of("etsy", "S1", "o-1").Single(x => x.Kind == LineMappingOrderException.CurrencyMismatch).Detail, "para birimi uyumsuz");
            Assert.AreEqual(0, exceptions.Of("etsy", "S1", "o-2").Count(x => x.Kind == LineMappingOrderException.CurrencyMismatch));

            // The store's own expected currency also raises a mismatch even when nothing inside the order disagrees.
            Func<string, string, string?> expectsUsd = (_, _) => "USD";
            var second = OrderCurrencyConsistency.Sync(exceptions, orders.ReadAll(), expectsUsd, now.AddMinutes(1)); Assert.AreEqual(2, second.Raised, "both orders now disagree with the store's own USD expectation");

            // The line is corrected to match the header: with no store expectation, the exception resolves.
            var fixedOrder = orders.ReadAll().Single(o => o.OrderId == "o-1"); fixedOrder.Items[0].Currency = "TRY"; fixedOrder.UpdatedAt = fixedOrder.UpdatedAt.AddMinutes(1); orders.SaveBatch(new[] { fixedOrder });
            var third = OrderCurrencyConsistency.Sync(exceptions, orders.ReadAll(), noStoreExpectation, now.AddMinutes(2)); Assert.AreEqual(2, third.Resolved, "both orders now agree with the header, no store expectation given");
            Assert.IsTrue(exceptions.Of("etsy", "S1", "o-1").Where(x => x.Kind == LineMappingOrderException.CurrencyMismatch).All(x => x.State == LineMappingOrderException.Resolved));

            // Restart: the exceptions read back.
            SqliteConnection.ClearAllPools();
            var reopened = new LineMappingExceptionStore(root);
            Assert.IsTrue(reopened.Of("etsy", "S1", "o-2").Where(x => x.Kind == LineMappingOrderException.CurrencyMismatch).All(x => x.State == LineMappingOrderException.Resolved));
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
