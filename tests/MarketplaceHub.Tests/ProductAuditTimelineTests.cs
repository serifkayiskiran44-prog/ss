using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #806 (DESIGN: Product audit history readability). 500 raw rows is not history an operator can read. The
// timeline filters to the product, orders it newest-first, and collapses consecutive identical attempts into
// one entry with a count -- retries are the thing that floods this list. The detail stays collapsed and
// redacted; an audit row is where a failed call's message ends up.
[TestClass]
public sealed class ProductAuditTimelineTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    static AuditEvent Event(string action, DateTime at, string outcome = "Succeeded", string module = "sync", string product = "p1", string detail = "")
        => new() { Action = action, AtUtc = at, Outcome = outcome, Module = module, ProductId = product, Detail = detail };

    [TestMethod]
    public void AnEmptyHistorySaysSoRatherThanShowingAnEmptyList()
    {
        var timeline = ProductAuditTimeline.Build([], "p1", Now);

        Assert.AreEqual(0, timeline.Entries.Count);
        Assert.IsFalse(timeline.HasEntries);
        StringAssert.Contains(timeline.Headline, "kaydı yok");
    }

    [TestMethod]
    public void OnlyThisProductsEventsAppearAndTheNewestIsFirst()
    {
        var events = new[]
        {
            Event("price", Now.AddHours(-3)),
            Event("stock", Now.AddHours(-1)),
            Event("price", Now.AddHours(-2), product: "p2"),
        };

        var timeline = ProductAuditTimeline.Build(events, "p1", Now);

        Assert.AreEqual(2, timeline.Entries.Count, "Another product's audit row is not this product's history.");
        Assert.AreEqual("stock", timeline.Entries[0].Action);
        StringAssert.Contains(timeline.Entries[0].When, "1 saat önce");
        StringAssert.Contains(timeline.Headline, "2");
    }

    [TestMethod]
    public void ConsecutiveIdenticalAttemptsCollapseIntoOneEntryWithACountAndASpan()
    {
        var events = Enumerable.Range(0, 12).Select(i => Event("price", Now.AddMinutes(-i), "Failed", detail: "429 istek sınırı")).ToArray();

        var timeline = ProductAuditTimeline.Build(events, "p1", Now);

        Assert.AreEqual(1, timeline.Entries.Count, "Twelve identical retries are one thing that happened, not twelve.");
        var entry = timeline.Entries[0];
        Assert.AreEqual(12, entry.Count);
        StringAssert.Contains(entry.Repeat, "12 kez");
        Assert.AreEqual(Now.AddMinutes(-11), entry.FirstAtUtc);
        Assert.AreEqual(Now, entry.LastAtUtc);
        StringAssert.Contains(entry.Repeat, "11 dakika", "The span between the first and last attempt is what tells an operator whether it is still retrying.");
    }

    [TestMethod]
    public void ADifferentOutcomeOrActionBreaksTheGroupSoASuccessAfterFailuresIsVisible()
    {
        var events = new[]
        {
            Event("price", Now, "Succeeded"),
            Event("price", Now.AddMinutes(-1), "Failed"),
            Event("price", Now.AddMinutes(-2), "Failed"),
            Event("stock", Now.AddMinutes(-3), "Failed"),
        };

        var timeline = ProductAuditTimeline.Build(events, "p1", Now);

        CollectionAssert.AreEqual(new[] { 1, 2, 1 }, timeline.Entries.Select(e => e.Count).ToArray(), "The success is its own entry; the two failures group; the other action stands alone.");
        Assert.AreEqual("Succeeded", timeline.Entries[0].Outcome);
        Assert.AreEqual(2, timeline.Entries[1].Count);
        Assert.AreEqual("stock", timeline.Entries[2].Action);
    }

    [TestMethod]
    public void FiveHundredEventsStayBoundedAndOrdered()
    {
        var events = Enumerable.Range(0, 500).Select(i => Event("action-" + i, Now.AddMinutes(-i))).ToArray();

        var timeline = ProductAuditTimeline.Build(events, "p1", Now);

        Assert.AreEqual(ProductAuditTimeline.MaxEntries, timeline.Entries.Count, "A long history is capped so the panel stays scrollable rather than unbounded.");
        Assert.AreEqual("action-0", timeline.Entries[0].Action, "The cap keeps the newest, not the oldest.");
        Assert.IsTrue(timeline.Truncated);
        StringAssert.Contains(timeline.Headline, "en yeni");
        Assert.IsTrue(timeline.Entries.Zip(timeline.Entries.Skip(1)).All(p => p.First.LastAtUtc >= p.Second.LastAtUtc), "Strictly newest-first.");
    }

    [TestMethod]
    public void TheExpandableDetailIsRedactedAndCappedBecauseItIsWhereFailuresLand()
    {
        var events = new[] { Event("price", Now, "Failed", detail: "401 access_token=zzz999token Authorization: Bearer abc123secret musteri@example.com " + new string('x', 3000)) };

        var entry = ProductAuditTimeline.Build(events, "p1", Now).Entries.Single();

        Assert.IsFalse(entry.Detail.Contains("zzz999token", StringComparison.Ordinal), entry.Detail);
        Assert.IsFalse(entry.Detail.Contains("abc123secret", StringComparison.Ordinal), entry.Detail);
        Assert.IsFalse(entry.Detail.Contains("musteri@example.com", StringComparison.Ordinal), entry.Detail);
        Assert.IsTrue(entry.Detail.Length <= ProductAuditTimeline.MaxDetailLength + 1, entry.Detail.Length.ToString());
        Assert.IsTrue(entry.HasDetail, "There is something to expand...");
        Assert.IsFalse(ProductAuditTimeline.Build([Event("price", Now)], "p1", Now).Entries.Single().HasDetail, "...and nothing to expand when the row carried no detail.");
    }
}
