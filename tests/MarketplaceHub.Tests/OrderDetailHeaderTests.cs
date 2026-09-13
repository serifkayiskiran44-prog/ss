using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #837 (DESIGN: Order detail header hierarchy). Three tiers from the order's own facts; a long id elided in the
// middle but complete for automation; missing payment named; a cancelled order leads with İptal and skips the SLA
// verdict; the level follows the worst fact; no item, buyer or address ever reaches the header.
[TestClass]
public sealed class OrderDetailHeaderTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 13, 16, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void TiersComeFromTheOrderAndTheLevelFollowsTheWorstFact()
    {
        var order = new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "3210987654", RawStatus = "paid", PaymentStatus = "Ödendi",
            Items = new List<OrderItem> { new() { Title = "Ayşe Yılmaz için kupa", Sku = "K1" } },
            Shipments = new List<OrderShipment> { new() { Id = "p1", State = "InTransit", Events = new List<OrderTrackingEvent> { new(Now.AddDays(-2), "Shipped", "test") } } } };
        var h = OrderDetailHeader.Compose(order, 0, Now);
        Assert.AreEqual("3210987654", h.Title); Assert.AreEqual("etsy · S1 · Ödendi", h.Secondary);
        Assert.AreEqual("Ödeme: Ödendi · Yolda (2 gün) · istisna yok", h.Tertiary);
        Assert.AreEqual(SeverityLevel.Success, h.Level); Assert.AreEqual("✔", h.Glyph);
        var text = h.Title + h.Secondary + h.Tertiary + h.AutomationText;
        Assert.IsFalse(text.Contains("Ayşe") || text.Contains("kupa") || text.Contains("K1"), "No item, no buyer: " + text);

        var late = OrderDetailHeader.Compose(order, 2, Now.AddDays(10));
        Assert.AreEqual(SeverityLevel.Blocking, late.Level); StringAssert.StartsWith(late.Sla, "Gecikti"); Assert.AreEqual("2 bekleyen istisna", late.Exceptions);
        var warned = OrderDetailHeader.Compose(order, 1, Now);
        Assert.AreEqual(SeverityLevel.Warning, warned.Level, "Pending exceptions warn when the delivery itself is fine.");
        var trouble = OrderDetailHeader.Compose(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "x", RawStatus = "paid", Shipments = new List<OrderShipment> { new() { Id = "p1", State = "Exception" } } }, 0, Now);
        Assert.AreEqual(SeverityLevel.Blocking, trouble.Level); Assert.AreEqual("Teslimat sorunu", trouble.Sla);
    }

    [TestMethod]
    public void LongIdsAreElidedMissingPaymentIsNamedAndACancelledOrderLeadsWithIptal()
    {
        var longId = new string('7', 20) + "-" + new string('9', 20);
        var h = OrderDetailHeader.Compose(new OrderSnapshot { Marketplace = "trendyol", ShopId = "Mağaza", OrderId = longId, RawStatus = "shipped" }, 0, Now);
        Assert.AreEqual(OrderDetailHeader.MaxIdChars, h.Title.Length); StringAssert.Contains(h.Title, "…");
        StringAssert.StartsWith(h.Title, "777777777777"); StringAssert.EndsWith(h.Title, "99999999999");
        Assert.AreEqual(longId, h.FullId); StringAssert.Contains(h.AutomationText, longId, "Automation gets the whole id.");
        Assert.AreEqual("Ödeme bilgisi yok", h.Payment, "The default 'Bilinmiyor' is a missing payment, not a value."); Assert.AreEqual("Paket yok", h.Sla); Assert.AreEqual("Gönderildi", h.StatusWord);

        var cancelled = OrderDetailHeader.Compose(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "c-1", RawStatus = "İptal edildi", PaymentStatus = "",
            Shipments = new List<OrderShipment> { new() { Id = "p1", State = "InTransit", Events = new List<OrderTrackingEvent> { new(Now.AddDays(-30), "Shipped", "t") } } } }, 3, Now);
        Assert.AreEqual("İptal", cancelled.StatusWord); Assert.AreEqual("SLA değerlendirilmez", cancelled.Sla, "A cancelled order is never 'late'.");
        Assert.AreEqual(SeverityLevel.Info, cancelled.Level); Assert.AreEqual("ℹ", cancelled.Glyph); Assert.AreEqual("3 bekleyen istisna", cancelled.Exceptions);

        var blank = OrderDetailHeader.Compose(new OrderSnapshot(), 0, Now);
        Assert.AreEqual("(numarasız)", blank.Title); Assert.AreEqual("kanal yok · mağaza yok · Durum yok", blank.Secondary);
        Assert.AreEqual("abcdefghijklmnopqrstuvwx", OrderDetailHeader.Elide("abcdefghijklmnopqrstuvwx"), "Exactly the limit is not elided.");
    }
}
