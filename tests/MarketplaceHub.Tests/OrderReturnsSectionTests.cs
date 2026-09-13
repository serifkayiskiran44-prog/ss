using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #840 (DESIGN: Order returns section hierarchy). No return, partial and full returns, a rejected request, an
// over-refund warning and a currency mismatch -- each a headline, a row state, a refund line; the stock-impact
// figure comes from the reconciliation preview; messages are redacted and capped.
[TestClass]
public sealed class OrderReturnsSectionTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 13, 19, 0, 0, TimeSpan.Zero);
    static OrderSnapshot Order(decimal? total = 20m, params (string Sku, int Qty)[] items) => new()
    {
        Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Total = total, Currency = "TRY",
        Items = items.Select(i => new OrderItem { Title = "Kupa " + i.Sku, Sku = i.Sku, Quantity = i.Qty }).ToList(),
    };
    static OrderStockReceipt Receipt(params (string Sku, int Qty)[] moves) => new("etsy", "S1", "o-1", DateTime.UtcNow, moves.Select(m => new OrderStockMovement("p-" + m.Sku, m.Sku, m.Qty, 10, 10 - m.Qty)).ToList());
    static OrderReturnLedgerRow Event(string sku, int qty, decimal refund, string currency = "TRY", int restored = 0, int minutesAgo = 60) => new("ev-" + Guid.NewGuid().ToString("N")[..6], sku, qty, refund, currency, restored, Now.AddMinutes(-minutesAgo));
    static OrderExceptionRecord Request(string type, string status, string message = "İade talebi") => new() { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", Type = type, Status = status, Message = message, CreatedUtc = Now.UtcDateTime };
    static OrderReturnReconciliation Preview(string sku, int qty) => new(OrderReturnReconciliation.OkPartial, Array.Empty<string>(), 3, 3, 0, qty, 20m, 0m, 0m, "TRY", qty);

    [TestMethod]
    public void NoReturnPartialFullAndRejectedAreNamedWithStockImpactFromThePreview()
    {
        var none = OrderReturnsSection.Compose(Order(20m, ("A", 3)), Array.Empty<OrderExceptionRecord>(), Array.Empty<OrderReturnLedgerRow>(), Receipt(("A", 3)), Preview);
        Assert.AreEqual("İade yok", none.Headline); Assert.IsFalse(none.HasReturns); Assert.AreEqual(SeverityLevel.Info, none.Level);
        Assert.AreEqual("İade tutarı yok", none.RefundLine);
        var a = none.Lines.Single(); Assert.AreEqual(ReturnLineState.None, a.State); Assert.AreEqual(3, a.Returnable); Assert.AreEqual(3, a.StockToRestoreIfReturned, "The preview says what a full return would put back.");
        Assert.AreEqual("○ A · iade yok (0 / 3) · kalan iade edilebilir 3 · iade edilirse stoğa dönecek 3", a.Line);

        var partial = OrderReturnsSection.Compose(Order(20m, ("A", 3), ("B", 2)), new[] { Request("Return", "Pending"), Request("Return", "Rejected", "Müşteri Ayşe Yılmaz vazgeçti") }, new[] { Event("A", 1, 5m, restored: 1) }, Receipt(("A", 3), ("B", 2)), Preview);
        Assert.AreEqual("2 talep (1 açık) · 1 kayıtlı iade · 1 kısmi", partial.Headline); Assert.IsTrue(partial.HasReturns);
        var pa = partial.Lines.Single(l => l.Sku == "A"); Assert.AreEqual(ReturnLineState.Partial, pa.State); Assert.AreEqual("◐", pa.Marker); Assert.AreEqual(2, pa.Returnable); Assert.AreEqual(2, pa.StockToRestoreIfReturned);
        var pb = partial.Lines.Single(l => l.Sku == "B"); Assert.AreEqual(ReturnLineState.None, pb.State);
        Assert.AreEqual("İade tutarı 5" + System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator + "00 / 20" + System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator + "00 TRY", partial.RefundLine); Assert.AreEqual(SeverityLevel.Success, partial.RefundLevel);
        var rejected = partial.Requests.Single(r => r.Status == "Rejected"); Assert.AreEqual("⊘", rejected.Marker); Assert.AreEqual("reddedildi", rejected.Word); Assert.AreEqual(SeverityLevel.Info, rejected.Level);
        Assert.IsFalse(rejected.Line.Contains("Ayşe"), "A request's free-text message (it may name a customer) is not part of the overview line: " + rejected.Line);
        var pending = partial.Requests.Single(r => r.Status == "Pending"); StringAssert.StartsWith(pending.Line, "⏳ iade talebi · bekliyor · "); Assert.AreEqual(SeverityLevel.Warning, pending.Level);
        Assert.AreEqual(1, partial.Timeline.Count); StringAssert.Contains(partial.Timeline[0].Line, "A × 1"); StringAssert.Contains(partial.Timeline[0].Line, "stoğa 1");
        Assert.AreEqual(SeverityLevel.Warning, partial.Level);

        var full = OrderReturnsSection.Compose(Order(20m, ("A", 3)), new[] { Request("Return", "Resolved") }, new[] { Event("A", 2, 10m, restored: 2, minutesAgo: 120), Event("A", 1, 10m, restored: 1) }, Receipt(("A", 3)), Preview);
        Assert.AreEqual("1 talep · 2 kayıtlı iade · 1 tam", full.Headline);
        var fa = full.Lines.Single(); Assert.AreEqual(ReturnLineState.Full, fa.State); Assert.AreEqual("↩", fa.Marker); Assert.AreEqual(0, fa.Returnable); Assert.IsNull(fa.StockToRestoreIfReturned, "Nothing left to return: no preview is asked for.");
        Assert.AreEqual(SeverityLevel.Warning, full.RefundLevel, "Refunded exactly the total: worth a look, not an error.");
        Assert.AreEqual("✔", full.Requests.Single().Marker);
        Assert.IsTrue(full.Timeline[0].AtUtc < full.Timeline[1].AtUtc, "Oldest first.");

        var unshipped = OrderReturnsSection.Compose(Order(20m, ("A", 2)), Array.Empty<OrderExceptionRecord>(), Array.Empty<OrderReturnLedgerRow>(), null, Preview);
        Assert.AreEqual(2, unshipped.Lines.Single().Returnable, "Without a receipt the reconciliation caps at the ordered quantity; the section mirrors it.");
    }

    [TestMethod]
    public void OverRefundAndOverReturnAndACurrencyMismatchAreWarnedAndMessagesAreSafe()
    {
        var over = OrderReturnsSection.Compose(Order(20m, ("A", 3)), Array.Empty<OrderExceptionRecord>(), new[] { Event("A", 2, 15m), Event("A", 2, 10m) }, Receipt(("A", 3)), Preview);
        StringAssert.StartsWith(over.RefundLine, "⚠ İade tutarı"); StringAssert.Contains(over.RefundLine, "sipariş toplamını aşıyor"); Assert.AreEqual(SeverityLevel.Blocking, over.RefundLevel);
        Assert.AreEqual(ReturnLineState.Over, over.Lines.Single().State); Assert.AreEqual("▼", over.Lines.Single().Marker); Assert.AreEqual(0, over.Lines.Single().Returnable);
        StringAssert.Contains(over.Headline, "1 fazla iade"); Assert.AreEqual(SeverityLevel.Blocking, over.Level);
        Assert.IsTrue(over.Notes.Any(n => n.Contains("sipariş toplamından fazla")));

        var foreign = OrderReturnsSection.Compose(Order(20m, ("A", 3)), Array.Empty<OrderExceptionRecord>(), new[] { Event("A", 1, 3m, currency: "USD") }, Receipt(("A", 3)), Preview);
        Assert.IsTrue(foreign.Notes.Any(n => n.Contains("farklı birimde"))); Assert.AreEqual(SeverityLevel.Warning, foreign.RefundLevel);

        var noTotal = OrderReturnsSection.Compose(Order(null, ("A", 3)), Array.Empty<OrderExceptionRecord>(), new[] { Event("A", 1, 3m) }, Receipt(("A", 3)), Preview);
        StringAssert.Contains(noTotal.RefundLine, "sipariş toplamı kayıtlı değil"); Assert.AreEqual(SeverityLevel.Info, noTotal.RefundLevel);

        Assert.AreEqual("(mesaj yok)", OrderReturnsSection.SafeMessage("  "));
        Assert.AreEqual(StatusTooltip.RawPayloadHidden, OrderReturnsSection.SafeMessage("{\"reason\":\"x\",\"token\":\"SECRET\"}"));
        var longMessage = OrderReturnsSection.SafeMessage(new string('m', 300) + " token=SECRET99");
        Assert.AreEqual(OrderReturnsSection.MessageLength, longMessage.Length); Assert.IsFalse(longMessage.Contains("SECRET99"));
        Assert.IsFalse(OrderReturnsSection.SafeMessage("İade\r\nsatır iki token=SECRET99").Contains("SECRET99"));
    }
}
