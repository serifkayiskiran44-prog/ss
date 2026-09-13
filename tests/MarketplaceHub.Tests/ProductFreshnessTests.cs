using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #903 (PRODUCT FRESHNESS: field-level currency). Each tracked field reads its own last observation and its own
// source's threshold: fresh, stale, unknown (no observation recorded) or frozen (its source is off or gone). The same
// rule runs as SQL in the catalogue search, so the list filters by it, and a saved filter survives a restart.
[TestClass]
public sealed class ProductFreshnessTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string id, bool enabled = true, int interval = 30) => new()
    {
        Id = id, Name = "Tedarikçi " + id.ToUpperInvariant(), Location = "https://feeds.example.com/" + id + ".xml", Enabled = enabled, IntervalMinutes = interval, ItemPath = "/p",
        Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q" },
    };
    static FieldOrigin Feed(string sourceId, DateTime at) => new() { Kind = FieldProvenance.FeedKind, SourceId = sourceId, ObservedUtc = at, RunId = "r", SourceRevision = 1 };

    [TestMethod]
    public void EachFieldReadsItsOwnObservationAgainstItsOwnSourcesThreshold()
    {
        var a = Source("a"); var off = Source("b", enabled: false); var slow = Source("c", interval: 240);
        XmlSource? By(string id) => id switch { "a" => a, "b" => off, "c" => slow, _ => null };
        var product = new CatalogProduct
        {
            Id = "p1", Sku = "SKU-1", Name = "Kupa", SourceId = "a", SourceKind = "xml",
            FieldOrigins = new Dictionary<string, FieldOrigin>(StringComparer.Ordinal)
            {
                ["Price"] = Feed("a", Now.AddMinutes(-10)),
                ["Stock"] = Feed("a", Now.AddHours(-8)),
                ["Name"] = new FieldOrigin { Kind = FieldProvenance.ManualKind, ObservedUtc = Now.AddDays(-1) },
                ["ImageUrls"] = Feed("b", Now.AddMinutes(-5)),
                ["Cost"] = Feed("gone", Now.AddMinutes(-5)),
            },
        };
        var view = ProductFreshness.Evaluate(product, By, Now);
        Assert.AreEqual(5, view.Fields.Count);
        var price = view.Fields.Single(f => f.Field == "Price");
        Assert.AreEqual(FreshnessState.Fresh, price.State); StringAssert.Contains(price.Words, "taze"); StringAssert.Contains(price.Words, "10 dk önce"); StringAssert.Contains(price.Words, "eşik 6 sa"); Assert.AreEqual(TimeSpan.FromHours(6), price.Threshold);
        var stock = view.Fields.Single(f => f.Field == "Stock");
        Assert.AreEqual(FreshnessState.Stale, stock.State); StringAssert.Contains(stock.Words, "bayat"); StringAssert.Contains(stock.Words, "8 sa önce");
        var name = view.Fields.Single(f => f.Field == "Name"); Assert.AreEqual(FreshnessState.Fresh, name.State); StringAssert.Contains(name.Words, "elle");
        var description = view.Fields.Single(f => f.Field == "Description"); Assert.AreEqual(FreshnessState.Unknown, description.State); Assert.AreEqual("zaman damgası yok", description.Words); Assert.IsNull(description.ObservedUtc);
        var images = view.Fields.Single(f => f.Field == "ImageUrls"); Assert.AreEqual(FreshnessState.Frozen, images.State); StringAssert.Contains(images.Words, "kaynak devre dışı"); StringAssert.Contains(images.Words, "tazelenmez");
        Assert.AreEqual(1, view.Stale); Assert.AreEqual(1, view.Unknown); Assert.AreEqual(1, view.Frozen); Assert.IsTrue(view.AnyStale);
        Assert.AreEqual("1 alan bayat · 1 alan tazelenmez · 1 alan zaman damgasız", view.Headline);

        // A gone source freezes too; a slow source has a longer threshold; an all-fresh product says so.
        var gone = new CatalogProduct { Id = "p2", Sku = "SKU-2", Name = "x", FieldOrigins = new Dictionary<string, FieldOrigin>(StringComparer.Ordinal) { ["Price"] = Feed("gone", Now.AddMinutes(-1)) } };
        var gonePrice = ProductFreshness.Evaluate(gone, By, Now).Fields.Single(f => f.Field == "Price");
        Assert.AreEqual(FreshnessState.Frozen, gonePrice.State); StringAssert.Contains(gonePrice.Words, "kaynak silinmiş");
        Assert.AreEqual(TimeSpan.FromHours(12), ProductFreshness.Threshold(slow));
        var fresh = new CatalogProduct { Id = "p3", Sku = "SKU-3", Name = "x", FieldOrigins = ProductFreshness.Fields.ToDictionary(f => f.Field, _ => Feed("a", Now.AddMinutes(-1)), StringComparer.Ordinal) };
        var freshView = ProductFreshness.Evaluate(fresh, By, Now); Assert.AreEqual("bütün alanlar taze", freshView.Headline); Assert.IsFalse(freshView.AnyStale);

        // The SQL form names the fields and binds a cutoff per source, frozen for a disabled one.
        var parameters = new Dictionary<string, string>();
        var sql = ProductFreshness.FilterSql("stale", "Stock", new[] { a, off }, Now, parameters);
        StringAssert.Contains(sql, "$.FieldOrigins.Stock.ObservedUtc"); StringAssert.Contains(sql, "CASE"); Assert.AreEqual(5, parameters.Count);
        Assert.IsTrue(parameters.Values.Contains("2026-09-13T06:00:00"), "a six-hour cutoff for the enabled source"); Assert.IsTrue(parameters.Values.Contains(ProductFreshness.FrozenCutoff));
        StringAssert.Contains(ProductFreshness.FilterSql("stale", "Content", new[] { a }, Now, new Dictionary<string, string>()), "$.FieldOrigins.Description.ObservedUtc");
        Assert.AreEqual("1=1", ProductFreshness.FilterSql("", "any", new[] { a }, Now, new Dictionary<string, string>()));
    }

    [TestMethod]
    public void TheListFilterFindsStaleFrozenAndFreshProductsAndASavedFilterSurvivesARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "fresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var now = DateTime.UtcNow;
            var a1 = Source("a1"); var a2 = Source("a2"); var b = Source("b");
            foreach (var s in new[] { a1, a2, b }) store.SaveSource(s);
            void Import(XmlSource s, string sku, DateTime at)
            {
                var run = runs.Start(s.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), s.ConfigRevision);
                store.Import(s, new[] { new CatalogProduct { SourceId = s.Id, SourceKind = "xml", Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 3 } }, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = s.ConfigRevision, ObservedAtUtc = at });
                runs.Complete(run, new ImportSummary(1, 0, 0));
            }
            // One source per moment (the anomaly gate compares a feed with its own source's products): eight hours ago, ten minutes ago, and a source switched off afterwards.
            Import(a1, "STALE", now.AddHours(-8)); Import(a2, "FRESH", now.AddMinutes(-10)); Import(b, "FROZEN", now.AddMinutes(-10));
            b = store.Sources().Single(x => x.Id == "b"); b.Enabled = false; store.SaveSource(b);

            string[] Skus(string key) => store.Search("", 0, 200, new CatalogFilter { Freshness = key }).Items.Select(p => p.Sku).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(new[] { "FROZEN", "STALE" }, Skus("stale:any"));
            CollectionAssert.AreEqual(new[] { "FROZEN", "STALE" }, Skus("stale:Stock"));
            CollectionAssert.AreEqual(new[] { "FROZEN", "STALE" }, Skus("stale:Content"));
            CollectionAssert.AreEqual(new[] { "FRESH" }, Skus("fresh:any"));
            Assert.AreEqual(0, Skus("unknown:any").Length);
            Assert.AreEqual(3, store.Search("", 0, 200, new CatalogFilter()).Total, "no criterion keeps the whole pool");
            Assert.IsTrue(new CatalogFilter().IsDefault); Assert.IsFalse(new CatalogFilter { Freshness = "stale:any" }.IsDefault);

            // The words on the product and in the quick-inspect drawer.
            var frozen = store.Products().Single(p => p.Sku == "FROZEN");
            var view = ProductFreshness.Evaluate(frozen, id => store.Sources().FirstOrDefault(s => s.Id == id), now);
            Assert.AreEqual(5, view.Frozen); StringAssert.Contains(view.Headline, "5 alan tazelenmez");
            var inspect = ProductQuickInspect.Build(store.Products().Single(p => p.Sku == "STALE"), Array.Empty<SyncJob>(), now, store.Sources());
            var stockRow = inspect.Rows.Single(r => r.Section == "Güncellik" && r.Label == "stok"); StringAssert.Contains(stockRow.Value, "bayat");
            Assert.IsFalse(ProductQuickInspect.Build(frozen, Array.Empty<SyncJob>(), now).Rows.Any(r => r.Section == "Güncellik"), "without sources the drawer says nothing about freshness");

            // A saved filter carries the criterion across a restart and finds the same products.
            new CatalogFilterStore(root).Save("bayat", new CatalogFilter { Freshness = "stale:any" });
            SqliteConnection.ClearAllPools();
            var loaded = new CatalogFilterStore(root).List().Single(v => v.Name == "bayat").Filter;
            Assert.AreEqual("stale:any", loaded.Freshness);
            Assert.AreEqual(2, new CatalogStore(root).Search("", 0, 200, loaded).Total);
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
