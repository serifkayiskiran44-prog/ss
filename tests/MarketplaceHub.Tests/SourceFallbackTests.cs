using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #897 (SOURCE FALLBACK: supplier fallback eligibility). When a product's primary source is stale or unavailable, the
// source graph says which other source could stand in — judged by its availability, how fresh its observation of this
// product is, whether its mapping covers the required fields, and what the operator's locks leave it to refresh — in
// words, with reasons, from persisted facts, and it never switches anything by itself.
[TestClass]
public sealed class SourceFallbackTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string id, int priority = 100, bool enabled = true, bool complete = true, int interval = 30) => new()
    {
        Id = id, Name = "Tedarikçi " + id.ToUpperInvariant(), Location = "https://feeds.example.com/" + id + ".xml?key=abc123", Priority = priority, Enabled = enabled, IntervalMinutes = interval, ItemPath = "/p",
        Fields = complete ? new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q" } : new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" },
    };
    static CatalogProduct Product(string sourceId = "a") => new() { SourceId = sourceId, SourceKind = "xml", Sku = "SKU-1", Name = "Kupa", Price = 10, Cost = 4, Stock = 3 };
    static Dictionary<string, DateTime> Seen(params (string Id, DateTime At)[] rows) => rows.ToDictionary(r => r.Id, r => r.At, StringComparer.Ordinal);

    [TestMethod]
    public void TheVerdictJudgesThePrimaryAndEveryCandidateWithReasonsAndNeverSwitches()
    {
        var a = Source("a"); var b = Source("b", 120); var c = Source("c", 120);
        var sources = new[] { a, b, c };

        // Healthy primary: no fallback needed, the candidates are still judged, nothing moves.
        var product = Product();
        var healthy = LinkedSourceGraph.Evaluate(product, sources, Seen(("a", Now.AddMinutes(-10)), ("b", Now.AddMinutes(-5)), ("c", Now.AddMinutes(-5))), Now);
        Assert.AreEqual(LinkedSourceGraph.PrimaryHealthy, healthy.Status); Assert.AreEqual(LinkedSourceGraph.Healthy, healthy.PrimaryState); Assert.IsNull(healthy.Recommended);
        Assert.AreEqual(2, healthy.Candidates.Count); Assert.IsTrue(healthy.Candidates.All(x => x.Eligible)); StringAssert.Contains(healthy.Headline, "yedek gerekmiyor"); Assert.AreEqual("a", product.SourceId);

        // Stale primary (seen two days ago, grace six hours): B and C tie on priority, so the operator chooses.
        var stale = LinkedSourceGraph.Evaluate(product, sources, Seen(("a", Now.AddDays(-2)), ("b", Now.AddMinutes(-5)), ("c", Now.AddMinutes(-5))), Now);
        Assert.AreEqual(LinkedSourceGraph.EqualCandidates, stale.Status); Assert.AreEqual(LinkedSourceGraph.Stale, stale.PrimaryState); Assert.IsNull(stale.Recommended);
        StringAssert.Contains(stale.Headline, "eşit öncelikli"); StringAssert.Contains(stale.Headline, "Tedarikçi B"); StringAssert.Contains(stale.Headline, "Tedarikçi C"); StringAssert.Contains(stale.Headline, "otomatik değildir");

        // A clear winner once C stands above B.
        c.Priority = 150;
        var clear = LinkedSourceGraph.Evaluate(product, sources, Seen(("a", Now.AddDays(-2)), ("b", Now.AddMinutes(-5)), ("c", Now.AddMinutes(-5))), Now);
        Assert.AreEqual(LinkedSourceGraph.FallbackAvailable, clear.Status); Assert.AreEqual("c", clear.Recommended!.SourceId); StringAssert.Contains(clear.Headline, "Tedarikçi C"); StringAssert.Contains(clear.Headline, "öncelik 150");
        Assert.AreEqual("c", clear.Candidates[0].SourceId, "eligible candidates come first, by priority");

        // Invalid fallbacks, each with its reason: disabled, never seen, stale observation, incomplete mapping, credential block, failed health check.
        var words = new Dictionary<string, string>
        {
            ["disabled"] = "kaynak devre dışı", ["unseen"] = "hiç görülmedi", ["old"] = "gözlemi bayat", ["partial"] = "eşleme eksik", ["auth"] = "kimlik bilgisi", ["down"] = "sağlık kontrolü",
        };
        var invalid = new List<XmlSource> { Source("a"), Source("disabled", 200, enabled: false), Source("unseen", 200), Source("old", 200), Source("partial", 200, complete: false), Source("auth", 200), Source("down", 200) };
        invalid.Single(s => s.Id == "auth").LastCredentialState = SourceCredentialHealth.Invalid; invalid.Single(s => s.Id == "down").LastHealthState = "TIMEOUT";
        var seen = Seen(("a", Now.AddDays(-2)), ("disabled", Now), ("old", Now.AddDays(-3)), ("partial", Now), ("auth", Now), ("down", Now));
        var none = LinkedSourceGraph.Evaluate(product, invalid, seen, Now);
        Assert.AreEqual(LinkedSourceGraph.NoFallback, none.Status); Assert.IsNull(none.Recommended); StringAssert.Contains(none.Headline, "uygun yedek yok");
        foreach (var (id, word) in words)
        {
            var candidate = none.Candidates.Single(x => x.SourceId == id);
            Assert.IsFalse(candidate.Eligible, id); Assert.IsTrue(candidate.Reasons.Any(r => r.Contains(word, StringComparison.CurrentCultureIgnoreCase)), id + ": " + string.Join(" | ", candidate.Reasons));
        }

        // Locks: a fallback refreshes only what is not locked; with price and stock both locked it is no fallback at all.
        var locked = Product(); locked.LockPrice = true;
        var partial = LinkedSourceGraph.Evaluate(locked, new[] { a, b }, Seen(("a", Now.AddDays(-2)), ("b", Now)), Now);
        Assert.AreEqual(LinkedSourceGraph.FallbackAvailable, partial.Status); CollectionAssert.DoesNotContain(partial.Recommended!.Refreshes.ToList(), "fiyat"); CollectionAssert.Contains(partial.Recommended.Refreshes.ToList(), "stok");
        Assert.IsTrue(partial.Recommended.Reasons.Any(r => r.Contains("kilitli alanlar")));
        locked.LockStock = true;
        var nothing = LinkedSourceGraph.Evaluate(locked, new[] { a, b }, Seen(("a", Now.AddDays(-2)), ("b", Now)), Now);
        Assert.AreEqual(LinkedSourceGraph.NoFallback, nothing.Status); Assert.IsTrue(nothing.Candidates.Single().Reasons.Any(r => r.Contains("hiçbir alanı tazeleyemez")));

        // An unavailable primary (disabled, gone, or the product dropped by its feed) and a product without a source.
        var off = Source("a", enabled: false);
        Assert.AreEqual(LinkedSourceGraph.Unavailable, LinkedSourceGraph.Evaluate(product, new[] { off, b }, Seen(("a", Now), ("b", Now)), Now).PrimaryState);
        Assert.AreEqual(LinkedSourceGraph.Missing, LinkedSourceGraph.Evaluate(product, new[] { b }, Seen(("b", Now)), Now).PrimaryState);
        var dropped = Product(); dropped.SourceMissing = true;
        var droppedVerdict = LinkedSourceGraph.Evaluate(dropped, new[] { a, b }, Seen(("a", Now), ("b", Now)), Now);
        Assert.AreEqual(LinkedSourceGraph.Unavailable, droppedVerdict.PrimaryState); Assert.IsTrue(droppedVerdict.PrimaryReasons.Any(r => r.Contains("beslemede artık yok")));
        Assert.AreEqual(LinkedSourceGraph.NoPrimary, LinkedSourceGraph.Evaluate(new CatalogProduct { SourceKind = "manual" }, sources, Seen(), Now).Status);

        // Never a feed address or a key in the words.
        var text = string.Join(" ", new[] { healthy, stale, clear, none, partial, nothing, droppedVerdict }.SelectMany(v => new[] { v.Headline }.Concat(v.PrimaryReasons).Concat(v.Candidates.SelectMany(x => x.Reasons.Append(x.Name)))));
        Assert.IsFalse(text.Contains("example.com") || text.Contains("abc123") || text.Contains("key="), text);
    }

    [TestMethod]
    public void SightingsAreRecordedByTheImportAndTheVerdictSurvivesARestartWithoutMovingTheProduct()
    {
        var root = Path.Combine(Path.GetTempPath(), "fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("a", 100); var b = Source("b", 80); store.SaveSource(a); store.SaveSource(b);
            void Import(XmlSource s, DateTime at)
            {
                var run = runs.Start(s.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), s.ConfigRevision);
                store.Import(s, new[] { new CatalogProduct { SourceId = s.Id, SourceKind = "xml", Sku = "SKU-1", Name = "Kupa", Price = 10, Currency = "TRY", Cost = 4, Stock = 3 } }, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = s.ConfigRevision, ObservedAtUtc = at });
                runs.Complete(run, new ImportSummary(0, 0, 0));
            }

            Import(a, Now); Import(b, Now.AddMinutes(10));
            var product = store.Products().Single();
            var seen = store.Sightings(product.Id);
            Assert.AreEqual(2, seen.Count); Assert.AreEqual(Now, seen["a"]); Assert.AreEqual(Now.AddMinutes(10), seen["b"]);

            // A fresh primary needs nothing. Two days later, with only B still carrying the product, B is the fallback -- and the product's home has not moved.
            Assert.AreEqual(LinkedSourceGraph.PrimaryHealthy, LinkedSourceGraph.Evaluate(product, store.Sources(), seen, Now.AddMinutes(20)).Status);
            Import(b, Now.AddDays(2));
            product = store.Products().Single(); seen = store.Sightings(product.Id);
            var verdict = LinkedSourceGraph.Evaluate(product, store.Sources(), seen, Now.AddDays(2).AddMinutes(5));
            Assert.AreEqual(LinkedSourceGraph.FallbackAvailable, verdict.Status); Assert.AreEqual("b", verdict.Recommended!.SourceId); Assert.AreEqual("a", product.SourceId, "no silent switch");

            // Restart: the same verdict from the same persisted facts.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root); var again = reopened.Products().Single();
            var after = LinkedSourceGraph.Evaluate(again, reopened.Sources(), reopened.Sightings(again.Id), Now.AddDays(2).AddMinutes(5));
            Assert.AreEqual(verdict.Status, after.Status); Assert.AreEqual(verdict.Headline, after.Headline); Assert.AreEqual("b", after.Recommended!.SourceId);

            // Deleting the product removes its sightings.
            reopened.DeleteProduct(again);
            Assert.AreEqual(0, reopened.Sightings(again.Id).Count);
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }
}
