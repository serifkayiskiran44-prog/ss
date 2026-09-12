using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #788 (TEST: Return and refund reconciliation suite). Real stores, real entry points: an order saved through
// OrdersStore, its stock deducted through OrderStockDecisionService (the receipt is what was fulfilled and
// what can come back), and every return/refund event reconciled through CatalogStore.PreviewOrderReturn /
// ApplyOrderReturn against the ordered quantity, the fulfilled quantity, the order total, the order currency
// and the ledger of events already applied. Fixture: K1 x3 at 100 TRY each, total 300 TRY, stock 10 -> 7.
[TestClass]
public sealed class ReturnRefundReconciliationSuiteTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "return-recon-" + Guid.NewGuid().ToString("N"));
    static void Cleanup(string root) { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    sealed record Fixture(string Root, CatalogStore Catalog, OrdersStore Orders, OrderSnapshot Order);

    static Fixture Seed(bool deductStock = true)
    {
        var root = NewRoot();
        var catalog = new CatalogStore(root);
        var source = new XmlSource { Id = "src", Name = "Src" };
        catalog.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "K1", Name = "Kupa", Price = 100m, Cost = 60m, CostCurrency = "TRY", Stock = 10, Active = true } });
        var orders = new OrdersStore(root);
        orders.SaveManual(new OrderSnapshot { Marketplace = "Yerel", ShopId = "shop-a", OrderId = "o-1", RawStatus = "Açık", Total = 300m, Currency = "TRY", Items = { new OrderItem { Title = "Kupa", Sku = "K1", Quantity = 3 } } });
        var order = orders.Find("Yerel", "shop-a", "o-1")!;
        if (deductStock) new OrderStockDecisionService(catalog).ApplyApproved(new OrderStockDecisionService(catalog).CreatePreview(order), approved: true);
        return new(root, catalog, orders, order);
    }

    static OrderReturnEvent Event(string key, int quantity, decimal refund, string currency = "TRY", string sku = "K1")
        => new("Yerel", "shop-a", "o-1", key, sku, quantity, refund, currency, DateTimeOffset.UtcNow);

    static int Stock(CatalogStore catalog) => catalog.Products().Single().Stock;

    [TestMethod]
    public void APartialReturnThenTheRemainderReconcileToFullAndRestoreExactlyWhatWasDeducted()
    {
        var f = Seed();
        try
        {
            Assert.AreEqual(7, Stock(f.Catalog));

            var first = f.Catalog.ApplyOrderReturn(f.Order, Event("ret-1", quantity: 1, refund: 100m), approved: true);
            Assert.AreEqual("OK_PARTIAL", first.Status, string.Join(",", first.Reasons));
            Assert.AreEqual(3, first.OrderedQuantity);
            Assert.AreEqual(3, first.FulfilledQuantity);
            Assert.AreEqual(1, first.ReturnedAfter);
            Assert.AreEqual(100m, first.RefundedAfter);
            Assert.AreEqual(8, Stock(f.Catalog), "One unit came back to the shared stock.");

            var second = f.Catalog.ApplyOrderReturn(f.Order, Event("ret-2", quantity: 2, refund: 200m), approved: true);
            Assert.AreEqual("OK_FULL", second.Status);
            Assert.AreEqual(3, second.ReturnedAfter);
            Assert.AreEqual(300m, second.RefundedAfter);
            Assert.AreEqual(10, Stock(f.Catalog), "After the full return the stock is exactly back where it started.");
        }
        finally { Cleanup(f.Root); }
    }

    [TestMethod]
    public void AnOverReturnIsBlockedBothAtOnceAndCumulativelyAndChangesNothing()
    {
        var f = Seed();
        try
        {
            var atOnce = f.Catalog.PreviewOrderReturn(f.Order, Event("ret-x", quantity: 4, refund: 0m));
            Assert.AreEqual("BLOCKED", atOnce.Status);
            CollectionAssert.Contains(atOnce.Reasons.ToList(), "OVER_RETURN");

            f.Catalog.ApplyOrderReturn(f.Order, Event("ret-1", quantity: 2, refund: 0m), approved: true);
            var cumulative = f.Catalog.PreviewOrderReturn(f.Order, Event("ret-2", quantity: 2, refund: 0m));
            Assert.AreEqual("BLOCKED", cumulative.Status, "2 already returned + 2 more exceeds the 3 that were ordered and fulfilled.");
            CollectionAssert.Contains(cumulative.Reasons.ToList(), "OVER_RETURN");
            Assert.ThrowsException<InvalidOperationException>(() => f.Catalog.ApplyOrderReturn(f.Order, Event("ret-2", quantity: 2, refund: 0m), approved: true));
            Assert.AreEqual(9, Stock(f.Catalog), "A blocked return restores nothing.");
            Assert.AreEqual(2, f.Catalog.PreviewOrderReturn(f.Order, Event("probe", 1, 0m)).ReturnedBefore, "A blocked event is not written to the ledger.");
        }
        finally { Cleanup(f.Root); }
    }

    [TestMethod]
    public void AnOverRefundIsBlockedAgainstTheOrderTotalIncludingRefundsAlreadyApplied()
    {
        var f = Seed();
        try
        {
            var atOnce = f.Catalog.PreviewOrderReturn(f.Order, Event("ref-x", quantity: 1, refund: 300.01m));
            Assert.AreEqual("BLOCKED", atOnce.Status);
            CollectionAssert.Contains(atOnce.Reasons.ToList(), "OVER_REFUND");

            f.Catalog.ApplyOrderReturn(f.Order, Event("ref-1", quantity: 1, refund: 200m), approved: true);
            var cumulative = f.Catalog.PreviewOrderReturn(f.Order, Event("ref-2", quantity: 1, refund: 100.01m));
            Assert.AreEqual("BLOCKED", cumulative.Status);
            CollectionAssert.Contains(cumulative.Reasons.ToList(), "OVER_REFUND");
            Assert.AreEqual("OK_PARTIAL", f.Catalog.PreviewOrderReturn(f.Order, Event("ref-2", quantity: 1, refund: 100m)).Status, "Exactly the remaining 100 TRY is allowed.");
            Assert.AreEqual("BLOCKED", f.Catalog.PreviewOrderReturn(f.Order, Event("ref-neg", quantity: 1, refund: -1m)).Status);
        }
        finally { Cleanup(f.Root); }
    }

    [TestMethod]
    public void ACurrencyMismatchIsBlockedAndNothingIsRecorded()
    {
        var f = Seed();
        try
        {
            var usd = f.Catalog.PreviewOrderReturn(f.Order, Event("cur-1", quantity: 1, refund: 3m, currency: "USD"));
            Assert.AreEqual("BLOCKED", usd.Status);
            CollectionAssert.Contains(usd.Reasons.ToList(), "CURRENCY_MISMATCH");
            Assert.ThrowsException<InvalidOperationException>(() => f.Catalog.ApplyOrderReturn(f.Order, Event("cur-1", quantity: 1, refund: 3m, currency: "USD"), approved: true));
            Assert.AreEqual(7, Stock(f.Catalog));
            Assert.AreEqual(0m, f.Catalog.PreviewOrderReturn(f.Order, Event("probe", 1, 0m)).RefundedBefore);
            Assert.AreEqual("OK_PARTIAL", f.Catalog.PreviewOrderReturn(f.Order, Event("cur-2", quantity: 1, refund: 100m, currency: "try")).Status, "Currency codes compare case-insensitively.");
        }
        finally { Cleanup(f.Root); }
    }

    [TestMethod]
    public void ADuplicateEventIsRecognisedCountedOnceAndRestoresStockOnce()
    {
        var f = Seed();
        try
        {
            var applied = f.Catalog.ApplyOrderReturn(f.Order, Event("ret-1", quantity: 1, refund: 100m), approved: true);
            var again = f.Catalog.ApplyOrderReturn(f.Order, Event("ret-1", quantity: 1, refund: 100m), approved: true);

            Assert.AreEqual("OK_PARTIAL", applied.Status);
            Assert.AreEqual("DUPLICATE", again.Status, "The same event key must not be counted or restored twice.");
            Assert.AreEqual(1, again.ReturnedAfter);
            Assert.AreEqual(100m, again.RefundedAfter);
            Assert.AreEqual(8, Stock(f.Catalog));
            Assert.AreEqual("DUPLICATE", f.Catalog.PreviewOrderReturn(f.Order, Event("ret-1", quantity: 1, refund: 100m)).Status);
        }
        finally { Cleanup(f.Root); }
    }

    [TestMethod]
    public void EventsForAnotherShopAnUnknownSkuOrAnUnfulfilledOrderCannotRestoreStock()
    {
        var f = Seed(deductStock: false);
        try
        {
            Assert.IsNull(f.Orders.Find("Yerel", "shop-b", "o-1"), "Shop B has no such order; the event has nothing to reconcile against.");
            var scope = f.Catalog.PreviewOrderReturn(f.Order, Event("scope", 1, 0m) with { ShopId = "shop-b" });
            Assert.AreEqual("BLOCKED", scope.Status);
            CollectionAssert.Contains(scope.Reasons.ToList(), "ORDER_SCOPE_MISMATCH");

            var sku = f.Catalog.PreviewOrderReturn(f.Order, Event("sku", 1, 0m, sku: "ZZ"));
            Assert.AreEqual("BLOCKED", sku.Status);
            CollectionAssert.Contains(sku.Reasons.ToList(), "SKU_NOT_IN_ORDER");

            // No stock was ever deducted for this order: the return reconciles (quantity/refund) but restores nothing.
            var unfulfilled = f.Catalog.ApplyOrderReturn(f.Order, Event("ret-1", quantity: 3, refund: 300m), approved: true);
            Assert.AreEqual("OK_FULL", unfulfilled.Status);
            Assert.AreEqual(0, unfulfilled.StockToRestore);
            Assert.AreEqual(10, Stock(f.Catalog), "Nothing was deducted, so nothing may be put back.");
        }
        finally { Cleanup(f.Root); }
    }

    [TestMethod]
    public void TheExceptionQueuesRestockPreviewFindsTheReceiptOfAMixedCaseMarketplaceOrder()
    {
        var f = Seed();
        try
        {
            // The operator marks the manual ("Yerel") order returned; the exception queue is rebuilt from the orders.
            var returned = f.Order.Copy(); returned.RawStatus = "returned"; f.Orders.SaveManual(returned);
            var exceptions = new OrderExceptionStore(f.Root);
            exceptions.Reconcile(f.Orders.ReadAll(), f.Catalog);
            var record = exceptions.List().Single(x => x.Type == "Return");
            Assert.AreEqual("PreviewReady", record.Status, "Reconcile found the receipt under the order's own casing and promised a preview.");
            Assert.AreEqual("yerel", record.Marketplace, "The exception store lower-cases the marketplace key.");

            // This is exactly what the panel's preview button does with the selected record; it used to throw
            // 'daha önce uygulanmış stok hareketi bulunamadı' for every marketplace that is not already lowercase.
            var preview = f.Catalog.CreateOrderRestockPreview(record.Marketplace, record.ShopId, record.OrderId, record.Id);
            Assert.AreEqual(3, preview.Lines.Single().Quantity);
            Assert.AreEqual("K1", preview.Lines.Single().Sku);
        }
        finally { Cleanup(f.Root); }
    }
}
