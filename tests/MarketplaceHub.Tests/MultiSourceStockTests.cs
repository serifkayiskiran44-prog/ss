using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #935 (STOCK: multi-source aggregate projection). Every import records the stock each source reported beside its
// sighting; the aggregate judges each observation (fresh, stale, missing, off), counts two sources on the same feed
// once, takes the best fresh source under the priority policy (the primary, or a fallback said so) or adds the fresh
// distinct sources under the sum policy, and says when nothing fresh or nothing at all was observed; the real import
// feeds it and it reads back after a restart.
[TestClass]
public sealed class MultiSourceStockTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string id, int priority, string location, bool enabled = true) => new() { Id = id, Name = "Kaynak " + id.ToUpperInvariant(), Priority = priority, Location = location, Enabled = enabled, IntervalMinutes = 30, ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" } };
    static SourceStockObservation Seen(string sourceId, int? stock, double hoursAgo) => new(sourceId, stock, Now.AddHours(-hoursAgo));

    [TestMethod]
    public void TwoSourcesADuplicateMappingAStaleSourceAndAFallbackAreEachJudgedAndNeverDoubleCounted()
    {
        var a = Source("a", 30, "https://a.example/feed.xml"); var b = Source("b", 20, "https://b.example/feed.xml"); var c = Source("c", 10, "https://user:pw@b.example/feed.xml?key=zzz"); // #896: the higher number is the higher priority
        var sources = new[] { a, b, c }; var product = new CatalogProduct { Id = "p1", Sku = "SKU-1", SourceId = "a", Stock = 10 };
        Assert.AreEqual(MultiSourceStock.FeedKey(b), MultiSourceStock.FeedKey(c), "the same feed behind other credentials and a query"); Assert.AreEqual("", MultiSourceStock.FeedKey(new XmlSource()));

        // Two fresh sources plus a duplicate of the second: the priority policy takes the primary; the sum policy adds the distinct ones only.
        var fresh = new[] { Seen("a", 10, 1), Seen("b", 5, 1), Seen("c", 7, 0.5) };
        var priority = MultiSourceStock.Aggregate(product, sources, fresh, Now);
        Assert.AreEqual((10, "a", AggregateStockProjection.Aggregated, AggregateStockProjection.PriorityPolicy), (priority.Stock, priority.ChosenSourceId, priority.State, priority.Policy)); StringAssert.Contains(priority.Words, "öncelikli kaynak Kaynak A (öncelik 30, 1 sa önce)");
        var sum = MultiSourceStock.Aggregate(product, sources, fresh, Now, AggregateStockProjection.SumPolicy);
        Assert.AreEqual((15, AggregateStockProjection.Aggregated), (sum.Stock, sum.State)); StringAssert.Contains(sum.Words, "birleştirilmiş stok 15 (toplam: Kaynak A 10 + Kaynak B 5)");
        Assert.AreEqual(1, sum.DuplicateGroups.Count); StringAssert.Contains(sum.DuplicateGroups[0], "Kaynak B ve Kaynak C; Kaynak B sayıldı");
        var duplicate = sum.Contributions.Single(x => x.SourceId == "c"); Assert.AreEqual((SourceStockContribution.Duplicate, false), (duplicate.State, duplicate.Counted)); StringAssert.Contains(duplicate.Words, "mükerrer eşleme");
        Assert.IsFalse(sum.Words.Contains("zzz", StringComparison.Ordinal) || sum.Words.Contains("b.example", StringComparison.Ordinal) || sum.Words.Contains("pw", StringComparison.Ordinal), "no address, no credential");

        // A stale primary: the priority policy falls back to the next fresh source and says so; the sum leaves the stale one out.
        var staleA = new[] { Seen("a", 10, 10), Seen("b", 5, 1) };
        var fallback = MultiSourceStock.Aggregate(product, sources, staleA, Now);
        Assert.AreEqual((5, "b", AggregateStockProjection.Fallback), (fallback.Stock, fallback.ChosenSourceId, fallback.State)); StringAssert.Contains(fallback.Words, "yedek kaynak Kaynak B"); StringAssert.Contains(fallback.Words, "birincil kaynak sayılmadı (Kaynak A: 10 adet, bayat (10 sa önce, sınır 6 sa))");
        Assert.AreEqual(5, MultiSourceStock.Aggregate(product, sources, staleA, Now, AggregateStockProjection.SumPolicy).Stock);

        // Missing stock, a disabled source, a deleted source: none counts; nothing fresh is STALE_ONLY; nothing observed is NO_OBSERVATION.
        var mixed = MultiSourceStock.Aggregate(product, new[] { a, Source("b", 20, "https://b.example/feed.xml", enabled: false) }, new[] { Seen("a", null, 1), Seen("b", 5, 1), Seen("zzz", 9, 1) }, Now);
        Assert.AreEqual(AggregateStockProjection.StaleOnly, mixed.State); Assert.AreEqual(0, mixed.Stock);
        CollectionAssert.AreEquivalent(new[] { SourceStockContribution.Missing, SourceStockContribution.Off, SourceStockContribution.Off }, mixed.Contributions.Select(x => x.State).ToArray()); StringAssert.Contains(mixed.Words, "birleştirilmiş stok yok"); StringAssert.Contains(mixed.Words, "kaynak devre dışı"); StringAssert.Contains(mixed.Words, "kaynak kaydı yok");
        var none = MultiSourceStock.Aggregate(product, sources, Array.Empty<SourceStockObservation>(), Now);
        Assert.AreEqual((AggregateStockProjection.NoObservation, 0), (none.State, none.Stock)); StringAssert.Contains(none.Words, "hiçbir kaynak");
        Assert.AreEqual(AggregateStockProjection.StaleOnly, MultiSourceStock.Aggregate(product, sources, new[] { Seen("a", 10, 10), Seen("b", 5, 10) }, Now).State, "two stale sources are no aggregate");
    }

    [TestMethod]
    public void TheRealImportRecordsEachSourcesStockAndTheAggregateReadsItBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "multi-stock-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("src-a", 30, "https://a.example/feed.xml"); var b = Source("src-b", 20, "https://b.example/feed.xml"); store.SaveSource(a); store.SaveSource(b);
            void ImportAt(XmlSource source, int stock, DateTime observedUtc)
            {
                var run = runs.Start(source.Id, "h-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromMinutes(5), source.ConfigRevision);
                store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, SourceKind = "xml", Sku = "SKU-1", Name = "Bir", Price = 10, Currency = "TRY", Cost = 4, Stock = stock, Active = true } }, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = source.ConfigRevision, ObservedAtUtc = observedUtc });
                runs.Complete(run, new ImportSummary(1, 0, 0));
            }
            ImportAt(a, 10, Now.AddHours(-1)); ImportAt(b, 5, Now.AddMinutes(-30));
            var product = store.Products().Single();

            // Both sources' stocks are recorded beside their sightings; the primary counts under the priority policy, both under the sum.
            var observations = store.StockObservations(product.Id);
            CollectionAssert.AreEquivalent(new[] { ("src-a", 10), ("src-b", 5) }, observations.Select(o => (o.SourceId, o.Stock!.Value)).ToArray());
            var aggregate = store.AggregateStock(product.Id, Now); Assert.AreEqual((10, "src-a", AggregateStockProjection.Aggregated), (aggregate.Stock, aggregate.ChosenSourceId, aggregate.State));
            Assert.AreEqual(15, store.AggregateStock(product.Id, Now, AggregateStockProjection.SumPolicy).Stock);

            // The primary goes stale (its next read is old): the fallback's fresh stock is the aggregate, said so; the inspect drawer shows it.
            ImportAt(a, 12, Now.AddHours(-10));
            var fallback = store.AggregateStock(product.Id, Now); Assert.AreEqual((5, "src-b", AggregateStockProjection.Fallback), (fallback.Stock, fallback.ChosenSourceId, fallback.State));
            var drawer = ProductQuickInspect.Build(store.Products().Single(), Array.Empty<SyncJob>(), Now, store.Sources(), null, store.StockObservations(product.Id));
            StringAssert.Contains(drawer.Rows.Single(r => r.Label == "Çoklu kaynak stoku").Value, "yedek kaynak Kaynak SRC-B");

            // Restart: the observations persist with their stocks.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root);
            Assert.AreEqual(2, reopened.StockObservations(product.Id).Count); Assert.AreEqual(12, reopened.StockObservations(product.Id).Single(o => o.SourceId == "src-a").Stock);
            Assert.AreEqual(AggregateStockProjection.Fallback, reopened.AggregateStock(product.Id, Now).State);
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
