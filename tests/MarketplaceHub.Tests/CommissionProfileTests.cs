using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #923 (PRICING: commission effective-date versioning). A channel/shop's commission is a sequence of dated periods,
// each an immutable revision: start inclusive, end exclusive; overlapping periods are refused by name; closing an
// open period is a new revision that retires the old one by pointer; a date between periods is a named gap; the
// pricing chain uses the period effective at its as-of date, refuses a gap, and keeps the rule's flat rate when no
// period is defined; it all reads back after a restart.
[TestClass]
public sealed class CommissionProfileTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void PeriodsAreImmutableRevisionsWithInclusiveStartsExclusiveEndsNoOverlapsAndNamedGaps()
    {
        var root = Path.Combine(Path.GetTempPath(), "commission-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CommissionProfileStore(root);
            var t1 = Utc(2026, 1, 1); var t2 = Utc(2026, 7, 1); var t3 = Utc(2026, 10, 1);

            // Boundary: the start is inclusive, the end exclusive -- at exactly the end nothing applies until the next period begins there.
            var first = store.Save("Etsy", "S1", t1, t2, 12, 0, "yıl başı", Now);
            Assert.AreEqual(1, first.Revision); Assert.AreEqual("etsy", first.Channel); Assert.AreEqual(CommissionProfile.Active, first.Status);
            Assert.AreEqual(1, store.Resolve("etsy", "S1", t1).Profile!.Revision, "the start belongs to the period");
            Assert.AreEqual(1, store.Resolve("etsy", "S1", t2.AddTicks(-1)).Profile!.Revision);
            Assert.AreEqual(CommissionResolution.Gap, store.Resolve("etsy", "S1", t2).State, "the end does not");
            var second = store.Save("etsy", "S1", t2, null, 15, 2, "yaz", Now);
            Assert.AreEqual(2, second.Revision); Assert.AreEqual(2, store.Resolve("etsy", "S1", t2).Profile!.Revision, "a period may begin exactly where the previous ends");
            var applies = store.Resolve("etsy", "S1", Now); Assert.AreEqual(CommissionResolution.Applies, applies.State); Assert.AreEqual(2m, applies.Profile!.FixedFeeTry); StringAssert.Contains(applies.Words, "komisyon %15 + 2 TL sabit"); StringAssert.Contains(applies.Words, "dönem 2");

            // Overlap: inside a closed period, enclosing it, or starting while the open period still runs -- refused by name, nothing written.
            var inside = Assert.ThrowsException<InvalidOperationException>(() => store.Save("etsy", "S1", Utc(2026, 3, 1), Utc(2026, 4, 1), 10, 0, "", Now)); StringAssert.Contains(inside.Message, "çakışıyor"); StringAssert.Contains(inside.Message, "1 numaralı dönem");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.Save("etsy", "S1", Utc(2025, 12, 1), t3, 10, 0, "", Now)).Message, "çakışıyor");
            var open = Assert.ThrowsException<InvalidOperationException>(() => store.Save("etsy", "S1", t3, null, 20, 0, "", Now)); StringAssert.Contains(open.Message, "2 numaralı dönem"); StringAssert.Contains(open.Message, "kapatın");
            Assert.AreEqual(2, store.List("etsy", "S1").Count, "a refused period writes nothing");
            var earlier = store.Save("etsy", "S1", Utc(2025, 7, 1), t1, 9, 0, "önceki yıl", Now); Assert.AreEqual(3, earlier.Revision, "ending exactly where another starts is not an overlap");
            CollectionAssert.AreEqual(new[] { 3, 1, 2 }, store.Periods("etsy", "S1").Select(p => p.Revision).ToArray(), "periods in force, by start");

            // Closing is a new revision: the closed copy is written, the old revision is retired by pointer, its own content untouched.
            var closed = store.Close("etsy", "S1", 2, t3, Now);
            Assert.AreEqual(4, closed.Revision); Assert.AreEqual(t3, closed.EffectiveToUtc); Assert.AreEqual(15m, closed.CommissionPercent); Assert.AreEqual(2m, closed.FixedFeeTry); Assert.AreEqual("yaz", closed.Note);
            var history = store.List("etsy", "S1"); Assert.AreEqual(4, history.Count); Assert.AreEqual(4, history[0].Revision, "newest first");
            var retired = history.Single(x => x.Revision == 2); Assert.AreEqual(CommissionProfile.Retired, retired.Status); Assert.AreEqual(4, retired.RetiredByRevision); Assert.IsNull(retired.EffectiveToUtc, "the retired revision keeps its content");
            CollectionAssert.AreEqual(new[] { 3, 1, 4 }, store.Periods("etsy", "S1").Select(p => p.Revision).ToArray());
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.Close("etsy", "S1", 2, t3, Now)).Message, "artık geçerli değil");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.Close("etsy", "S1", 4, Utc(2027, 1, 1), Now)).Message, "uzatılamaz");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.Close("etsy", "S1", 9, t3, Now)).Message, "bulunamadı");

            // Gap: after the close a later period is accepted, and a date between them is a named gap.
            var later = store.Save("etsy", "S1", Utc(2026, 12, 1), null, 18, 0, "", Now); Assert.AreEqual(5, later.Revision);
            var gap = store.Resolve("etsy", "S1", Utc(2026, 11, 1));
            Assert.AreEqual(CommissionResolution.Gap, gap.State); Assert.IsNull(gap.Profile); StringAssert.Contains(gap.Words, "boşluğu"); StringAssert.Contains(gap.Words, "önceki dönem 4 2026-10-01 00:00 UTC tarihinde bitti"); StringAssert.Contains(gap.Words, "sonraki dönem 5 2026-12-01 00:00 UTC tarihinde başlıyor");

            // Numbers and dates that are not a period; nothing defined for another shop.
            Assert.ThrowsException<ArgumentException>(() => store.Save("etsy", "S2", t1, t2, 101, 0, "", Now));
            Assert.ThrowsException<ArgumentException>(() => store.Save("etsy", "S2", t2, t1, 10, 0, "", Now));
            Assert.ThrowsException<ArgumentException>(() => store.Save("etsy", "S2", t1, t2, 10, -1, "", Now));
            var none = store.Resolve("ebay", "E1", Now); Assert.AreEqual(CommissionResolution.None, none.State); StringAssert.Contains(none.Words, "tanımlı değil");

            // Restart: the history and the periods read back; the period in force today is the closed copy.
            SqliteConnection.ClearAllPools();
            var reopened = new CommissionProfileStore(root);
            Assert.AreEqual(5, reopened.List("etsy", "S1").Count); Assert.AreEqual(4, reopened.Resolve("etsy", "S1", Now).Profile!.Revision); Assert.AreEqual("yaz", reopened.Resolve("etsy", "S1", Now).Profile!.Note);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ThePricingChainUsesThePeriodEffectiveAtTheDateRefusesAGapAndKeepsTheRuleWithoutPeriods()
    {
        var root = Path.Combine(Path.GetTempPath(), "commission-chain-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CatalogStore(root);
            catalog.Import(new XmlSource { Id = "src", Name = "Src" }, new[] { new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Product", Cost = 100m, CostCurrency = "TRY", Stock = 10, Active = true } });
            var id = catalog.Products().Single().Id;
            // sale 200 TRY from x*2; the rule's flat 20% gives net 200 - 40 - 10 - 100 = 50, above the 40 TRY minimum.
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "s1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 20, EstimatedShippingTry = 10, TransactionCostTry = 0, VatRatePercent = 0, MinimumMarginTry = 40 });

            // No period defined: the rule's flat rate, as before, and the preview says so.
            var flat = catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now));
            Assert.AreEqual(200m, flat.Price); StringAssert.Contains(flat.CommissionOrigin, "kural komisyonu %20"); StringAssert.Contains(flat.CommissionOrigin, "dönem yok");

            // Periods: 10% until July, then 30% plus a 5 TRY fee -- the period of the day decides, so the same rule passes in March and is refused in September.
            var periods = new CommissionProfileStore(root); var t2 = Utc(2026, 7, 1); var t3 = Utc(2026, 10, 1);
            periods.Save("local", "s1", Utc(2026, 1, 1), t2, 10, 0, "kış", Now); periods.Save("local", "s1", t2, null, 30, 5, "yaz", Now);
            var historical = catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Utc(2026, 3, 1)));
            Assert.AreEqual(200m, historical.Price); StringAssert.Contains(historical.CommissionOrigin, "komisyon %10"); StringAssert.Contains(historical.CommissionOrigin, "dönem 1");
            var refused = Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now)));
            StringAssert.Contains(refused.Message, "asgari kâr", "30% plus the fee leaves 25 TRY, under the 40 TRY minimum: the period's rate, not the rule's, reached the guard");
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "s1", Version = catalog.GetPricePolicy("local", "s1")!.Version, Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 20, EstimatedShippingTry = 10, TransactionCostTry = 0, VatRatePercent = 0, MinimumMarginTry = 0 });
            var summer = catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now));
            StringAssert.Contains(summer.CommissionOrigin, "komisyon %30 + 5 TL sabit"); StringAssert.Contains(summer.CommissionOrigin, "dönem 2");

            // A gap between the closed summer period and nothing after it: refused by name, never the rule's rate.
            periods.Close("local", "s1", 2, t3, Now);
            var gap = Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Utc(2026, 11, 1))));
            StringAssert.Contains(gap.Message, "komisyon dönemi boşluğu"); StringAssert.Contains(gap.Message, "önceki dönem 3");
            Assert.AreEqual(200m, catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Utc(2026, 9, 1))).Price, "inside the closed copy the price is still produced");
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
