using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #839 (DESIGN: Order shipping section hierarchy). No shipment, a split shipment, duplicate tracking inside the
// order and across orders, a delayed delivery -- each a headline, a row state and flags; tracking numbers and
// links are masked in the overview; nothing reads a buyer or an address.
[TestClass]
public sealed class OrderShippingSectionTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 13, 18, 0, 0, TimeSpan.Zero);
    static OrderShipment Ship(string id, string carrier, string tracking, string state, DateTimeOffset? shippedAt = null) => new()
    {
        Id = id, Carrier = carrier, TrackingNumber = tracking, State = state,
        Events = shippedAt is { } at ? new List<OrderTrackingEvent> { new(at, "Shipped", "test") } : new List<OrderTrackingEvent>(),
    };

    [TestMethod]
    public void NoShipmentSplitShipmentDelayAndDuplicatesAreNamedAndTheLevelFollowsTheWorstPackage()
    {
        var none = OrderShippingSection.Compose(new OrderSnapshot { OrderId = "o-0" }, null, Now);
        Assert.AreEqual("Paket yok", none.Headline); Assert.IsFalse(none.HasShipments); Assert.AreEqual(SeverityLevel.Info, none.Level); StringAssert.Contains(none.Notes.Single(), "Paket ekle");

        var order = new OrderSnapshot { OrderId = "o-1", SourceUpdatedAt = Now.AddDays(-1), Shipments = new List<OrderShipment>
        {
            Ship("p1", "Aras Kargo", "ARS123456789", "InTransit", Now.AddDays(-9)),
            Ship("p2", "Yurtiçi Kargo", "YK987654321", "Delivered", Now.AddDays(-3)),
            Ship("p3", "", "", "Unknown"),
        } };
        var split = OrderShippingSection.Compose(order, null, Now);
        Assert.AreEqual("3 paket · bölünmüş gönderi · 1 gecikmiş · 1 teslim · 1 işaretli", split.Headline);
        Assert.AreEqual(SeverityLevel.Blocking, split.Level, "The delayed package sets the level.");
        var p1 = split.Rows.Single(r => r.Id == "p1"); Assert.AreEqual("✖", p1.Marker); Assert.AreEqual("gecikti", p1.Word); StringAssert.StartsWith(p1.SlaLabel, "Gecikti (9 gün)"); Assert.AreEqual(SeverityLevel.Blocking, p1.Level);
        var p2 = split.Rows.Single(r => r.Id == "p2"); Assert.AreEqual("✔", p2.Marker); Assert.AreEqual(SeverityLevel.Success, p2.Level); Assert.AreEqual(0, p2.Flags.Count);
        var p3 = split.Rows.Single(r => r.Id == "p3"); Assert.AreEqual("○", p3.Marker); Assert.AreEqual("gönderilmedi", p3.Word); CollectionAssert.Contains(p3.Flags.ToList(), "takip no yok"); Assert.AreEqual("taşıyıcı yok", p3.Carrier); Assert.AreEqual("—", p3.TrackingMasked);
        Assert.AreEqual(SeverityLevel.Warning, p3.Level, "A flag lifts a quiet package to warning.");
        StringAssert.Contains(p1.Line, "AR••••••6789", "The overview masks the tracking number.");
        Assert.IsFalse(p1.Line.Contains("ARS123456789"));

        var twice = new OrderSnapshot { OrderId = "o-2", SourceUpdatedAt = Now.AddDays(-1), Shipments = new List<OrderShipment> { Ship("a", "Aras Kargo", "SAME1234567", "InTransit", Now.AddDays(-1)), Ship("b", "Aras Kargo", "same1234567", "InTransit", Now.AddDays(-1)) } };
        var dup = OrderShippingSection.Compose(twice, new Dictionary<string, IReadOnlyList<string>> { ["SAME1234567"] = new[] { "o-2", "o-9" } }, Now);
        Assert.IsTrue(dup.Rows.All(r => r.Flags.Contains("aynı takip no bu siparişte iki kez")), "Case-insensitive: the same number twice in one order flags both packages.");
        Assert.IsTrue(dup.Rows.All(r => r.Flags.Contains("takip no 1 başka siparişte de var")), "The order itself is not counted as 'elsewhere'.");
        Assert.AreEqual(SeverityLevel.Warning, dup.Level); Assert.AreEqual(2, dup.Notes.Count);
        StringAssert.Contains(dup.Headline, "2 paket · bölünmüş gönderi"); StringAssert.Contains(dup.Headline, "2 işaretli");

        var single = OrderShippingSection.Compose(new OrderSnapshot { OrderId = "o-3", Shipments = new List<OrderShipment> { Ship("p", "MNG Kargo", "MNG1", "Exception") } }, null, Now);
        Assert.AreEqual("1 paket · 1 sorunlu", single.Headline); Assert.AreEqual("teslimat sorunu", single.Rows[0].Word); Assert.AreEqual("MNG1", single.Rows[0].TrackingMasked, "Short numbers have nothing to hide.");
    }

    [TestMethod]
    public void TrackingNumbersAndLinksAreMaskedInTheOverview()
    {
        Assert.AreEqual("AB••••••WXYZ", OrderShippingSection.MaskTracking("AB1234567WXYZ"));
        Assert.AreEqual("12345678", OrderShippingSection.MaskTracking(" 12345678 "));
        Assert.AreEqual("—", OrderShippingSection.MaskTracking(null));
        Assert.AreEqual("https://track.example.com/t/ABC", OrderShippingSection.MaskTrackingUrl("https://track.example.com/t/ABC?postcode=34000&token=SECRET"));
        Assert.AreEqual("", OrderShippingSection.MaskTrackingUrl("not a url"));
        var order = new OrderSnapshot { OrderId = "o-4", Items = new List<OrderItem> { new() { Title = "Ayşe Yılmaz için kupa", Sku = "K" } }, Shipments = new List<OrderShipment> { new() { Id = "p", Carrier = "PTT Kargo", TrackingNumber = "PTT000111222", State = "Delivered", TrackingUrl = "https://t.example.com/x?name=Ayse" } } };
        var m = OrderShippingSection.Compose(order, null, Now);
        var text = m.Headline + string.Join("|", m.Rows.Select(r => r.Line)) + string.Join("|", m.Notes);
        Assert.IsFalse(text.Contains("Ayşe") || text.Contains("Ayse") || text.Contains("PTT000111222"), text);
        Assert.IsTrue(m.Rows[0].HasTrackingUrl);
    }
}
