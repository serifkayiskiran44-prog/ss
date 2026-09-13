using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #838 (DESIGN: Order line reconciliation visuals). Every line's ordered / shipped / returned / cancelled from the
// order, the stock receipt and the return ledger; partial shipment, partial return and the over-return conflict
// are distinct states with a marker and a word; a cancelled order cancels every line; titles are capped and
// redacted, never a buyer.
[TestClass]
public sealed class OrderLineReconciliationTests
{
    static OrderSnapshot Order(string raw, params (string Sku, int Qty)[] items) => new()
    {
        Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = raw,
        Items = items.Select(i => new OrderItem { Title = "Kupa " + i.Sku, Sku = i.Sku, Quantity = i.Qty }).ToList(),
    };
    static OrderStockReceipt Receipt(params (string Sku, int Qty)[] moves) => new("etsy", "S1", "o-1", DateTime.UtcNow, moves.Select(m => new OrderStockMovement("p-" + m.Sku, m.Sku, m.Qty, 10, 10 - m.Qty)).ToList());

    [TestMethod]
    public void PartialShipmentPartialReturnAndOverReturnAreDistinctStatesWithMarkers()
    {
        var order = Order("paid", ("A", 3), ("B", 2), ("C", 1), ("D", 2));
        var lines = OrderLineReconciliation.Build(order, Receipt(("A", 2), ("B", 2), ("C", 1), ("D", 2)), new Dictionary<string, int> { ["B"] = 1, ["C"] = 1, ["D"] = 3 });
        Assert.AreEqual(4, lines.Count);
        var a = lines.Single(l => l.Sku == "A"); Assert.AreEqual(OrderLineState.PartiallyShipped, a.State); Assert.AreEqual("▲ eksik sevk · 1 adet bekliyor", a.StateLabel); Assert.AreEqual("1", a.Outstanding); Assert.AreEqual(SeverityLevel.Warning, a.Level);
        var b = lines.Single(l => l.Sku == "B"); Assert.AreEqual(OrderLineState.PartiallyReturned, b.State); StringAssert.StartsWith(b.StateLabel, "◐ kısmi iade · 1 / 2 geri geldi");
        var c = lines.Single(l => l.Sku == "C"); Assert.AreEqual(OrderLineState.Returned, c.State); Assert.AreEqual("↩ iade edildi", c.StateLabel);
        var d = lines.Single(l => l.Sku == "D"); Assert.AreEqual(OrderLineState.OverReturned, d.State); Assert.AreEqual(SeverityLevel.Blocking, d.Level); StringAssert.StartsWith(d.StateLabel, "▼ fazla iade · 1 adet sevk edilenden fazla");
        Assert.AreEqual(4, lines.Select(l => l.Marker).Distinct().Count(), "Every state on this order has its own marker.");
        StringAssert.Contains(OrderLineReconciliation.Summary(lines), "1 fazla iade çakışması"); StringAssert.Contains(OrderLineReconciliation.Summary(lines), "1 eksik sevk"); StringAssert.Contains(OrderLineReconciliation.Summary(lines), "2 iadeli");

        var untouched = OrderLineReconciliation.Build(Order("paid", ("A", 2)), null, null).Single();
        Assert.AreEqual(OrderLineState.Pending, untouched.State); Assert.AreEqual("○ sevk bekliyor", untouched.StateLabel); Assert.AreEqual(0, untouched.Shipped); Assert.AreEqual("2", untouched.Outstanding);
        var full = OrderLineReconciliation.Build(Order("paid", ("A", 2)), Receipt(("A", 2)), null).Single();
        Assert.AreEqual(OrderLineState.Shipped, full.State); Assert.AreEqual("＝ tam sevk", full.StateLabel); Assert.AreEqual("0", full.Outstanding);
        var over = OrderLineReconciliation.Build(Order("paid", ("A", 2)), Receipt(("A", 3)), null).Single();
        StringAssert.Contains(over.StateLabel, "1 adet fazla düşüldü", "More deducted than ordered is said, not hidden.");
    }

    [TestMethod]
    public void ACancelledOrderCancelsEveryLineSameSkuLinesMergeAndTitlesAreCappedAndRedacted()
    {
        var cancelled = OrderLineReconciliation.Build(Order("İptal edildi", ("A", 2), ("B", 1)), Receipt(("A", 2)), null);
        Assert.IsTrue(cancelled.All(l => l.State == OrderLineState.Cancelled && l.Word == "iptal"));
        Assert.AreEqual(2, cancelled.Single(l => l.Sku == "A").Cancelled); StringAssert.Contains(cancelled.Single(l => l.Sku == "A").Reason, "sevk edilmiş 2 adet");
        Assert.AreEqual("0", cancelled.Single(l => l.Sku == "A").Outstanding, "Cancelled quantity is not outstanding.");
        StringAssert.Contains(OrderLineReconciliation.Summary(cancelled), "sipariş iptal");

        var merged = OrderLineReconciliation.Build(Order("paid", ("A", 1), ("a", 2)), Receipt(("A", 3)), null);
        Assert.AreEqual(1, merged.Count, "Two lines of one SKU are one reconciliation unit."); Assert.AreEqual(3, merged[0].Ordered); Assert.AreEqual(OrderLineState.Shipped, merged[0].State);

        var order = new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Items = new List<OrderItem> { new() { Title = new string('x', 200) + " token=SECRET99", Sku = "", Quantity = 1 } } };
        var line = OrderLineReconciliation.Build(order, null, null).Single();
        Assert.AreEqual("(SKU yok)", line.Sku); Assert.AreEqual(OrderLineReconciliation.TitleLength, line.Title.Length); Assert.IsFalse(line.Title.Contains("SECRET99"));
    }
}
