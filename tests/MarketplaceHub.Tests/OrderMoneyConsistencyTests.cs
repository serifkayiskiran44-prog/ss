using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #946 (ORDER MONEY: order total consistency check). The total should equal the line totals minus a discount plus
// shipping and tax, within a cent-rounding tolerance, when every component is reported; a missing total, an
// unrecognized currency or a missing line price each block the check without themselves being a mismatch; only a
// real disagreement is a mismatch, raised as the shared MONEY_MISMATCH exception and resolved when it clears.
[TestClass]
public sealed class OrderMoneyConsistencyTests
{
    static OrderItem Line(string sku, decimal? price, int qty = 1) => new() { Sku = sku, Title = "Ürün " + sku, Quantity = qty, UnitPrice = price };
    static OrderSnapshot Order(decimal? total, string currency, params OrderItem[] items) => new() { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Total = total, Currency = currency, Items = items.ToList() };

    [TestMethod]
    public void AValidTotalARoundingDeltaAnUnrecognizedCurrencyAndAMissingLinePriceAreEachJudged()
    {
        // Valid: two units at 50 plus 10 shipping minus 5 discount plus 2 tax = 107.
        var valid = Order(107m, "TRY", Line("A", 50m, 2)); valid.ShippingTotal = 10m; valid.DiscountTotal = 5m; valid.TaxTotal = 2m;
        var okCheck = OrderMoneyConsistency.Evaluate(valid); Assert.AreEqual((OrderMoneyCheck.Ok, 107m, 107m, 0m), (okCheck.Outcome, okCheck.Expected, okCheck.Actual, okCheck.Difference)); StringAssert.Contains(okCheck.Words, "tutarlı: 107.00 TRY"); StringAssert.Contains(okCheck.Words, "iskonto -5.00 TRY"); StringAssert.Contains(okCheck.Words, "kargo +10.00 TRY"); StringAssert.Contains(okCheck.Words, "vergi +2.00 TRY");

        // A rounding delta of half a cent per line is within tolerance (2 lines -> 2 cent tolerance): still OK.
        var rounding = Order(100.01m, "TRY", Line("A", 33.335m, 1), Line("B", 33.335m, 2));
        var roundingCheck = OrderMoneyConsistency.Evaluate(rounding); Assert.AreEqual(OrderMoneyCheck.Ok, roundingCheck.Outcome, roundingCheck.Words);

        // An unrecognized currency blocks the check without being a mismatch.
        var badCurrency = Order(100m, "TL", Line("A", 100m)); var badCheck = OrderMoneyConsistency.Evaluate(badCurrency); Assert.AreEqual((OrderMoneyCheck.UnknownCurrency, (decimal?)null), (badCheck.Outcome, badCheck.Expected)); StringAssert.Contains(badCheck.Words, "para birimi tanınmıyor (TL)");
        Assert.AreEqual(OrderMoneyCheck.UnknownCurrency, OrderMoneyConsistency.Evaluate(Order(100m, "", Line("A", 100m))).Outcome, "an empty currency is unrecognized too");

        // A missing line price blocks the check and names the line; not itself a mismatch.
        var missingPrice = Order(150m, "TRY", Line("A", 50m), Line("B", null, 2)); var missingCheck = OrderMoneyConsistency.Evaluate(missingPrice);
        Assert.AreEqual((OrderMoneyCheck.MissingComponent, (decimal?)null), (missingCheck.Outcome, missingCheck.Expected)); CollectionAssert.AreEqual(new[] { "satır 2 (SKU B)" }, missingCheck.MissingComponents.ToArray()); StringAssert.Contains(missingCheck.Words, "satır fiyatı eksik (1 satır)");

        // No total at all: nothing to check.
        Assert.AreEqual(OrderMoneyCheck.NoTotal, OrderMoneyConsistency.Evaluate(Order(null, "TRY", Line("A", 10m))).Outcome);

        // A real mismatch: the reported total is 20 short of the lines' sum, with no components reported.
        var mismatch = Order(80m, "TRY", Line("A", 100m)); var mismatchCheck = OrderMoneyConsistency.Evaluate(mismatch);
        Assert.AreEqual((OrderMoneyCheck.Mismatch, 100m, 80m, -20m), (mismatchCheck.Outcome, mismatchCheck.Expected, mismatchCheck.Actual, mismatchCheck.Difference)); StringAssert.Contains(mismatchCheck.Words, "uyumsuz: bildirilen 80.00 TRY, hesaplanan 100.00 TRY (fark -20.00 TRY, tolerans 0.01 TRY)");
    }

    [TestMethod]
    public void SyncRaisesTheSharedMoneyMismatchExceptionAndResolvesItWhenTheOrderClearsAndItReadsBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "money-consistency-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var orders = new OrdersStore(root); var exceptions = new LineMappingExceptionStore(root);
            var now = DateTime.UtcNow;
            orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Total = 80m, Currency = "TRY", Items = new List<OrderItem> { Line("A", 100m) } });
            orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-2", RawStatus = "paid", Total = 50m, Currency = "TRY", Items = new List<OrderItem> { Line("B", 50m) } });

            var first = OrderMoneyConsistency.Sync(exceptions, orders.ReadAll(), now); Assert.AreEqual((1, 0), first);
            var reason = exceptions.StockBlockReason("etsy", "S1", "o-1"); Assert.IsNull(reason, "a money mismatch is not itself a stock block"); Assert.AreEqual(1, exceptions.Open("etsy", "S1").Count(x => x.Kind == LineMappingOrderException.MoneyMismatch));
            StringAssert.Contains(exceptions.Of("etsy", "S1", "o-1").Single(x => x.Kind == LineMappingOrderException.MoneyMismatch).Detail, "uyumsuz");

            // The order is corrected: the next sync resolves the exception.
            var fixedOrder = orders.ReadAll().Single(o => o.OrderId == "o-1"); fixedOrder.Total = 100m; fixedOrder.UpdatedAt = fixedOrder.UpdatedAt.AddMinutes(1); orders.SaveBatch(new[] { fixedOrder });
            var second = OrderMoneyConsistency.Sync(exceptions, orders.ReadAll(), now.AddMinutes(5)); Assert.AreEqual((0, 1), second);
            Assert.AreEqual(LineMappingOrderException.Resolved, exceptions.Of("etsy", "S1", "o-1").Single(x => x.Kind == LineMappingOrderException.MoneyMismatch).State);

            // Restart: the exceptions read back.
            SqliteConnection.ClearAllPools();
            var reopened = new LineMappingExceptionStore(root);
            Assert.AreEqual(LineMappingOrderException.Resolved, reopened.Of("etsy", "S1", "o-1").Single(x => x.Kind == LineMappingOrderException.MoneyMismatch).State);
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
