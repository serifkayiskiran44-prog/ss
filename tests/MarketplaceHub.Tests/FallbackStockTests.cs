using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #936 (STOCK: supplier fallback stock selection). When the record's stock is no longer fresh, the eligible fallback
// is selected from the #897 graph and the #935 observations -- the higher priority stands out, equal priorities are
// the operator's choice -- and stands in only once the operator approved that source for that product at the
// source's configuration revision: the approval lapses when the source is edited, waits when the primary recovers,
// and nothing switches by itself; the projection names the source and the revision; it all reads back after a restart.
[TestClass]
public sealed class FallbackStockTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string id, int priority, string location, int revision = 1) => new() { Id = id, Name = "Kaynak " + id.ToUpperInvariant(), Priority = priority, Location = location, Enabled = true, IntervalMinutes = 30, ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q" }, ConfigRevision = revision }; // the #897 graph needs the required mappings (Name, Cost, Stock)
    static FieldFreshness Record(FreshnessState state, string words) => new("Stock", "Stok", state, Now.AddHours(-10), "a", TimeSpan.FromHours(6), words);
    static SourceStockObservation Seen(string sourceId, int? stock, double hoursAgo) => new(sourceId, stock, Now.AddHours(-hoursAgo));

    [TestMethod]
    public void TheSelectionExplainsEqualCandidatesApprovalsRevisionsStaleFallbacksAndARecoveredPrimary()
    {
        var a = Source("a", 30, "https://a.example/feed.xml"); var b = Source("b", 20, "https://b.example/feed.xml", revision: 3); var c = Source("c", 20, "https://c.example/feed.xml", revision: 2);
        var product = new CatalogProduct { Id = "p1", Sku = "SKU-1", SourceId = "a", Stock = 10 };
        var seen = new Dictionary<string, DateTime> { ["a"] = Now.AddHours(-10), ["b"] = Now.AddHours(-1), ["c"] = Now.AddMinutes(-30) };
        var observations = new[] { Seen("a", 10, 10), Seen("b", 5, 1), Seen("c", 7, 0.5) };
        var stale = Record(FreshnessState.Stale, "bayat · 10 sa önce (eşik 6 sa)");

        // Two fallbacks of equal priority: the operator's choice; nothing stands in.
        var equal = FallbackStock.Select(product, new[] { a, b, c }, seen, observations, null, stale, Now);
        Assert.AreEqual((FallbackStockSelection.EqualCandidates, false), (equal.Status, equal.StandsIn)); Assert.IsNull(equal.Selected);
        StringAssert.Contains(equal.Words, "2 eşit öncelikli yedek (Kaynak C, Kaynak B)"); StringAssert.Contains(equal.Words, "seçim operatörün"); StringAssert.Contains(equal.Words, "otomatik stok yazımı yapılmaz");

        // An approval naming one of the tied at its current revision: that one stands in, named with the revision.
        var approvedC = new FallbackStockApproval("p1", "c", 2, Now.AddDays(-1), "");
        var applied = FallbackStock.Select(product, new[] { a, b, c }, seen, observations, approvedC, stale, Now);
        Assert.AreEqual((FallbackStockSelection.Applied, "c", 7, true), (applied.Status, applied.Selected!.SourceId, applied.Selected.Stock, applied.StandsIn)); StringAssert.Contains(applied.Words, "yedek kaynaktan: Kaynak C rev 2 (öncelik 20, stok gözlemi 30 dk önce)"); StringAssert.Contains(applied.Words, "stok 7");

        // The approved source's configuration changed since: the approval lapses; nothing stands in until renewed.
        var moved = FallbackStock.Select(product, new[] { a, b, Source("c", 20, "https://c.example/feed.xml", revision: 5) }, seen, observations, approvedC, stale, Now);
        Assert.AreEqual((FallbackStockSelection.RevisionChanged, false), (moved.Status, moved.StandsIn)); StringAssert.Contains(moved.Words, "onayı rev 2 için verildi, kaynak şimdi rev 5"); StringAssert.Contains(moved.Words, "yeniden onaylayın");

        // One fallback outranks the rest: selected, waiting for approval; an approval for another source does not count for it.
        var d = Source("d", 25, "https://d.example/feed.xml"); var seenD = new Dictionary<string, DateTime>(seen) { ["d"] = Now.AddMinutes(-15) }; var observationsD = observations.Append(Seen("d", 3, 0.25)).ToArray();
        var waiting = FallbackStock.Select(product, new[] { a, b, c, d }, seenD, observationsD, approvedC, stale, Now);
        Assert.AreEqual((FallbackStockSelection.NeedsApproval, "d", false), (waiting.Status, waiting.Selected!.SourceId, waiting.StandsIn)); StringAssert.Contains(waiting.Words, "yedek uygun: Kaynak D rev 1"); StringAssert.Contains(waiting.Words, "onay gerekiyor"); StringAssert.Contains(waiting.Words, "onay: Kaynak C rev 2"); StringAssert.Contains(waiting.Words, "başka kaynak için");

        // A stale fallback sighting, a source that never reported the stock, the primary's own feed behind other credentials: none stands in; nothing eligible is NO_FALLBACK.
        var e = Source("e", 40, "https://user:pw@a.example/feed.xml?key=zzz");
        var seenLate = new Dictionary<string, DateTime> { ["a"] = Now.AddHours(-10), ["b"] = Now.AddHours(-7), ["c"] = Now.AddMinutes(-30), ["e"] = Now.AddMinutes(-5) };
        var none = FallbackStock.Select(product, new[] { a, b, c, e }, seenLate, new[] { Seen("a", 10, 10), Seen("b", 5, 7), Seen("c", null, 0.5), Seen("e", 9, 0.1) }, approvedC, stale, Now);
        Assert.AreEqual((FallbackStockSelection.NoFallback, false), (none.Status, none.StandsIn)); StringAssert.Contains(none.Words, "uygun yedek yok"); StringAssert.Contains(none.Words, "kullanılamaz");
        StringAssert.Contains(string.Join(" ", none.Candidates.Single(x => x.SourceId == "b").Reasons), "bayat"); StringAssert.Contains(string.Join(" ", none.Candidates.Single(x => x.SourceId == "c").Reasons), "stokunu bildirmedi"); StringAssert.Contains(string.Join(" ", none.Candidates.Single(x => x.SourceId == "e").Reasons), "birincil kaynakla aynı besleme");
        Assert.IsFalse(none.Words.Contains("a.example", StringComparison.Ordinal) || none.Words.Contains("pw", StringComparison.Ordinal) || none.Words.Contains("zzz", StringComparison.Ordinal), "no address, no credential");

        // The primary recovers: its own fresh stock is used, the approval waits; a product without a primary has no selection.
        var recovered = FallbackStock.Select(product, new[] { a, b, c }, seen, observations, approvedC, Record(FreshnessState.Fresh, "taze · 30 dk önce (eşik 6 sa)"), Now);
        Assert.AreEqual((FallbackStockSelection.PrimaryOk, false), (recovered.Status, recovered.StandsIn)); StringAssert.Contains(recovered.Words, "kayıtlı stok taze"); StringAssert.Contains(recovered.Words, "beklemede");
        Assert.AreEqual(FallbackStockSelection.NoPrimary, FallbackStock.Select(new CatalogProduct { Id = "p2", Sku = "M", SourceId = "", Stock = 1 }, new[] { a, b }, seen, observations, null, stale, Now).Status);
    }

    [TestMethod]
    public void TheProjectionUsesAnApprovedFallbackStockNamedWithItsRevisionAndDropsItWhenTheSourceChangesOrThePrimaryRecovers()
    {
        var root = Path.Combine(Path.GetTempPath(), "fallback-stock-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("src-a", 30, "https://a.example/feed.xml"); var b = Source("src-b", 20, "https://b.example/feed.xml"); store.SaveSource(a); store.SaveSource(b);
            store.SaveStockPolicy(new StockPolicy { Channel = "local", Shop = "s1", SafetyStock = 1, MaximumStock = null, Enabled = true });
            void ImportAt(CatalogStore target, XmlSource source, int stock, DateTime observedUtc)
            {
                var run = runs.Start(source.Id, "h-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromMinutes(5), source.ConfigRevision);
                target.Import(source, new[] { new CatalogProduct { SourceId = source.Id, SourceKind = "xml", Sku = "SKU-1", Name = "Bir", Price = 10, Currency = "TRY", Cost = 4, Stock = stock, Active = true } }, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = source.ConfigRevision, ObservedAtUtc = observedUtc });
                runs.Complete(run, new ImportSummary(1, 0, 0));
            }
            // The fallback reads the product while the primary is still fresh: the primary keeps the field (#896), the fallback's stock is recorded beside its sighting (#935).
            ImportAt(store, a, 10, Now.AddHours(-5)); ImportAt(store, b, 5, Now.AddHours(-4));
            var product = store.Products().Single(); Assert.AreEqual(10, product.Stock, "the higher-priority primary kept the field");
            var at = Now.AddHours(1.5); // the primary's observation is 6.5 h old (stale beyond 6 h), the fallback's 5.5 h (fresh)

            // Stale primary, eligible fallback, no approval: the projection stays stale, not dispatchable, and says what waits.
            var waiting = store.ProjectStock("local", "s1", product.Id, at);
            Assert.AreEqual((StockProjection.Stale, 10, 9, false, ""), (waiting.State, waiting.Stock, waiting.Available, waiting.Dispatchable, waiting.FallbackSourceId));
            StringAssert.Contains(waiting.Words, "yedek uygun: Kaynak SRC-B rev " + b.ConfigRevision); StringAssert.Contains(waiting.Words, "onay gerekiyor");
            Assert.AreEqual(FallbackStockSelection.NeedsApproval, store.SelectFallbackStock(product.Id, at).Status);

            // Approved at the fallback's revision: its stock stands in -- the shop's arithmetic on it, named with the revision, dispatchable; audited.
            var approval = store.ApproveFallbackStock(product.Id, "src-b", "tedarikçi kesintisi", at); Assert.AreEqual(b.ConfigRevision, approval.SourceRevision);
            var standing = store.ProjectStock("local", "s1", product.Id, at);
            Assert.AreEqual((StockProjection.Fallback, 5, 4, true, "src-b", b.ConfigRevision), (standing.State, standing.Stock, standing.Available, standing.Dispatchable, standing.FallbackSourceId, standing.FallbackRevision));
            StringAssert.Contains(standing.Words, "yedek kaynaktan: Kaynak SRC-B rev " + b.ConfigRevision); Assert.AreEqual(Now.AddHours(-4), standing.ObservedUtc); Assert.AreEqual("src-b", standing.SourceId);
            Assert.IsTrue(new AuditStore(root).List(20).Any(x => x.Action == "fallback-approve" && x.Detail.Contains("rev " + b.ConfigRevision, StringComparison.Ordinal)), "the approval is on the audit trail by name and revision");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.ApproveFallbackStock(product.Id, "src-a", "", at)).Message, "kendi yedeği olamaz");

            // Restart: the approval and the projection read back.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root);
            Assert.AreEqual((StockProjection.Fallback, 4), (reopened.ProjectStock("local", "s1", product.Id, at).State, reopened.ProjectStock("local", "s1", product.Id, at).Available));

            // The fallback's configuration changes: the approval lapses until renewed.
            b.MarkupPercent = 5; reopened.SaveSource(b); Assert.IsTrue(b.ConfigRevision > approval.SourceRevision, "a saved configuration is a new revision");
            var lapsed = reopened.ProjectStock("local", "s1", product.Id, at);
            Assert.AreEqual((StockProjection.Stale, 10, false), (lapsed.State, lapsed.Stock, lapsed.Dispatchable)); StringAssert.Contains(lapsed.Words, "yeniden onaylayın");
            reopened.ApproveFallbackStock(product.Id, "src-b", "", at); Assert.AreEqual(StockProjection.Fallback, reopened.ProjectStock("local", "s1", product.Id, at).State);

            // The primary recovers: its own fresh stock is back, the approval waits; revoking it twice says so.
            ImportAt(reopened, a, 12, at.AddMinutes(-10));
            var recovered = reopened.ProjectStock("local", "s1", product.Id, at);
            Assert.AreEqual((StockProjection.Fresh, 12, 11, true, ""), (recovered.State, recovered.Stock, recovered.Available, recovered.Dispatchable, recovered.FallbackSourceId));
            Assert.AreEqual(FallbackStockSelection.PrimaryOk, reopened.SelectFallbackStock(product.Id, at).Status);
            Assert.IsTrue(reopened.RevokeFallbackStock(product.Id, at)); Assert.IsFalse(reopened.RevokeFallbackStock(product.Id, at));
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
