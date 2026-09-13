using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #835 (DESIGN: Order list urgency sorting). One deterministic score from recorded states; a cancelled order is
// its own band at 0; a tie sorts by key; a missing timestamp earns nothing and says so; reasons carry no PII.
[TestClass]
public sealed class OrderUrgencyTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 13, 15, 0, 0, TimeSpan.Zero);
    static OrderUrgencyInput Input(string raw = "paid", IReadOnlyList<DeliverySla> slas = null, DateTimeOffset? sync = null, DateTimeOffset? source = null, int unmapped = 0, int pending = 0) =>
        new(raw, slas ?? Array.Empty<DeliverySla>(), sync ?? Now.AddHours(-1), source ?? Now.AddDays(-1), unmapped, pending, Now);

    [TestMethod]
    public void StatesAddUpDeterministicallyIntoBandsAndACancelledOrderIsZero()
    {
        var quiet = OrderUrgencyScorer.Score(Input(slas: new[] { new DeliverySla("DELIVERED", 0, "Teslim edildi") }));
        Assert.AreEqual(0, quiet.Score); Assert.AreEqual("düşük", quiet.Band); Assert.AreEqual(0, quiet.Reasons.Count); Assert.AreEqual("düşük · 0", quiet.Label);

        var late = OrderUrgencyScorer.Score(Input(slas: new[] { new DeliverySla("DELAYED", 9, "Gecikti (9 gün)") }, sync: default(DateTimeOffset)));
        Assert.AreEqual(30 + 4 + 10, late.Score, "Delay base, days over the SLA, and no sync."); Assert.AreEqual("yüksek", late.Band);
        CollectionAssert.AreEqual(new[] { "gecikti 9 gün", "senkron yok" }, late.Reasons.ToArray());

        var worst = OrderUrgencyScorer.Score(Input(slas: new[] { new DeliverySla("EXCEPTION", 0, "Teslimat sorunu") }, unmapped: 2, pending: 3, sync: Now.AddDays(-2)));
        Assert.AreEqual(40 + 25 + 30 + 10, worst.Score); Assert.AreEqual("acil", worst.Band);
        StringAssert.Contains(worst.Label, "3 bekleyen istisna"); StringAssert.Contains(worst.Label, "2 eşlenmemiş kalem"); StringAssert.Contains(worst.Label, "senkron eski");

        var waiting = OrderUrgencyScorer.Score(Input(source: Now.AddDays(-4)));
        Assert.AreEqual(10 + 8, waiting.Score, "A paid order unshipped for four days."); Assert.AreEqual("normal", waiting.Band); CollectionAssert.Contains(waiting.Reasons.ToList(), "4 gün bekliyor");
        var shippedToday = OrderUrgencyScorer.Score(Input(slas: new[] { new DeliverySla("IN_TRANSIT", 1, "Yolda (1 gün)") }));
        Assert.AreEqual(0, shippedToday.Score, "In transit within the SLA is not urgent.");

        var cancelled = OrderUrgencyScorer.Score(Input(raw: "cancelled", slas: new[] { new DeliverySla("EXCEPTION", 0, "x"), new DeliverySla("DELAYED", 20, "x") }, unmapped: 5, pending: 5, sync: default(DateTimeOffset)));
        Assert.AreEqual(0, cancelled.Score); Assert.AreEqual(OrderUrgencyScorer.CancelledBand, cancelled.Band); CollectionAssert.AreEqual(new[] { "sipariş iptal" }, cancelled.Reasons.ToArray());
        Assert.AreEqual(OrderUrgencyScorer.CancelledBand, OrderUrgencyScorer.Score(Input(raw: "İptal edildi")).Band);

        Assert.AreEqual("acil", OrderUrgencyScorer.Band(60)); Assert.AreEqual("yüksek", OrderUrgencyScorer.Band(59)); Assert.AreEqual("normal", OrderUrgencyScorer.Band(10)); Assert.AreEqual("düşük", OrderUrgencyScorer.Band(9));
        var again = OrderUrgencyScorer.Score(Input(slas: new[] { new DeliverySla("EXCEPTION", 0, "Teslimat sorunu") }, unmapped: 2, pending: 3, sync: Now.AddDays(-2)));
        Assert.AreEqual(worst.Score, again.Score); Assert.AreEqual(worst.Band, again.Band); CollectionAssert.AreEqual(worst.Reasons.ToArray(), again.Reasons.ToArray(), "Same input, same score, same reasons.");
    }

    [TestMethod]
    public void BandLabelsAreFixedTurkishCapitalsWhateverTheRunningCulture()
    {
        // The CI runner is en-US: capitalising "iptal" with the running culture there gives "Iptal", and a filter the page offers as "İptal" then matches nothing.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            CollectionAssert.AreEqual(new[] { "Acil", "Yüksek", "Normal", "Düşük", "İptal" }, OrderUrgencyScorer.BandOptions.Select(b => b.Label).ToArray());
            CollectionAssert.AreEqual(OrderUrgencyScorer.Bands.ToArray(), OrderUrgencyScorer.BandOptions.Select(b => b.Key).ToArray(), "One option per band, in band order.");
            Assert.AreEqual("Iptal", char.ToUpper('i', System.Globalization.CultureInfo.CurrentCulture) + "ptal", "…which is exactly why the labels are not derived at runtime.");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    public void TiesSortByKeyAndAMissingTimestampEarnsNothingButIsNamed()
    {
        var items = new[] { ("etsy/S2/e", 45), ("etsy/S1/a", 45), ("etsy/S1/z", 70), ("etsy/S2/c", 0) };
        var sorted = OrderUrgencyScorer.Sort(items, x => x.Item2, x => x.Item1).Select(x => x.Item1).ToArray();
        CollectionAssert.AreEqual(new[] { "etsy/S1/z", "etsy/S1/a", "etsy/S2/e", "etsy/S2/c" }, sorted, "Score first, then the key -- never the arrival order.");
        CollectionAssert.AreEqual(sorted, OrderUrgencyScorer.Sort(items.Reverse(), x => x.Item2, x => x.Item1).Select(x => x.Item1).ToArray(), "The same list in another order sorts the same.");

        var noStamp = OrderUrgencyScorer.Score(Input(source: default(DateTimeOffset)));
        Assert.AreEqual(0, noStamp.Score, "No timestamp: no age points are invented."); CollectionAssert.Contains(noStamp.Reasons.ToList(), "zaman damgası yok");
        var noSync = OrderUrgencyScorer.Score(Input(sync: default(DateTimeOffset), source: default(DateTimeOffset)));
        Assert.AreEqual(10, noSync.Score);
    }

    [TestMethod]
    public void TheInputComesFromTheOrdersOwnStatesAndCarriesNoPii()
    {
        var order = new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", SourceUpdatedAt = Now.AddDays(-8), LastSync = Now.AddHours(-1),
            Items = new List<OrderItem> { new() { Title = "Ayşe Yılmaz için kupa", Sku = "KUPA-1" }, new() { Title = "Hediye kartı", Sku = "" }, new() { Title = "Tabak", Sku = "YOK" } },
            Shipments = new List<OrderShipment> { new() { Id = "p1", State = "InTransit", Events = new List<OrderTrackingEvent> { new(Now.AddDays(-8), "Shipped", "test") } } } };
        var input = OrderUrgencyScorer.InputFor(order, new HashSet<string>(new[] { "KUPA-1" }, StringComparer.OrdinalIgnoreCase), pendingExceptions: 1, Now);
        Assert.AreEqual(2, input.UnmappedItems, "A blank SKU and an unknown SKU are unmapped; a known one is not.");
        Assert.AreEqual("DELAYED", input.Slas.Single().Status); Assert.AreEqual(8, input.Slas.Single().DaysInTransit);
        var urgency = OrderUrgencyScorer.Score(input);
        Assert.AreEqual(30 + 3 + 20 + 25, urgency.Score); Assert.AreEqual("acil", urgency.Band);
        var text = urgency.Label + string.Join("|", urgency.Reasons);
        Assert.IsFalse(text.Contains("Ayşe") || text.Contains("Yılmaz") || text.Contains("o-1") || text.Contains("KUPA"), "No title, name, SKU or order id in the label: " + text);
    }
}
