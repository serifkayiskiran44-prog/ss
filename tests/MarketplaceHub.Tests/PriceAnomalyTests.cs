using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #930 (PRICING: price anomaly detector). Every written price is a snapshot; the move from the previous snapshot is
// judged against configurable percent and absolute thresholds -- a first sighting is a new product, a normal move
// passes, an extreme move up or down or a currency change is quarantined as a review item; nothing is corrected by
// itself; the real import and the manual save feed the detector; it all reads back after a restart.
[TestClass]
public sealed class PriceAnomalyTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static CatalogProduct P(string id, string sku, decimal price, string currency = "TRY") => new() { Id = id, Sku = sku, Name = "Ürün " + sku, Price = price, Currency = currency, Cost = 1, Stock = 1 };

    [TestMethod]
    public void AFirstSightingANormalMoveAnExtremeMoveAndACurrencyChangeAreEachTheirOwnOutcome()
    {
        var root = Path.Combine(Path.GetTempPath(), "price-anomaly-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new PriceAnomalyStore(root);
            Assert.AreEqual((50m, 0m), (store.GetPolicy().MaxPercentJump, store.GetPolicy().MaxAbsoluteJump), "the default: half the price, no absolute limit");

            // New product: nothing to compare.
            var first = store.Observe(P("p1", "SKU-1", 100m), FieldProvenance.FeedKind, Now);
            Assert.AreEqual(PriceObservation.NewProduct, first.Kind); Assert.IsNull(first.Anomaly); StringAssert.Contains(first.Words, "ilk fiyat gözlemi"); Assert.AreEqual(0, store.List().Count);

            // Normal jump: within the threshold, said with the percentage.
            var normal = store.Observe(P("p1", "SKU-1", 120m), FieldProvenance.FeedKind, Now.AddHours(1));
            Assert.AreEqual(PriceObservation.Normal, normal.Kind); StringAssert.Contains(normal.Words, "100 TRY → 120 TRY (%20)"); Assert.AreEqual(0, store.List().Count);

            // Extreme up: quarantined with both prices, the change and the threshold; the product is not touched.
            var product = P("p1", "SKU-1", 300m);
            var up = store.Observe(product, FieldProvenance.FeedKind, Now.AddHours(2));
            Assert.AreEqual(PriceObservation.Anomalous, up.Kind); Assert.AreEqual(PriceAnomaly.JumpUp, up.Anomaly!.Kind); Assert.AreEqual((120m, 300m, 150m, 180m), (up.Anomaly.PreviousPrice, up.Anomaly.NewPrice, up.Anomaly.PercentChange, up.Anomaly.AbsoluteChange));
            StringAssert.Contains(up.Words, "fiyat sıçradı 120 TRY → 300 TRY (%150, 180 TRY)"); StringAssert.Contains(up.Words, "otomatik düzeltme yok"); Assert.AreEqual(300m, product.Price, "nothing is corrected by itself");
            Assert.IsTrue(store.HasPending("p1")); Assert.AreEqual(1, store.List().Count);

            // Extreme down, and the absolute threshold: a small percentage above a set absolute limit is an anomaly too.
            var down = store.Observe(P("p1", "SKU-1", 30m), FieldProvenance.ManualKind, Now.AddHours(3));
            Assert.AreEqual(PriceAnomaly.JumpDown, down.Anomaly!.Kind); Assert.AreEqual(90m, down.Anomaly.PercentChange); StringAssert.Contains(down.Words, "fiyat düştü");
            store.SavePolicy(50m, 5m, Now);
            var smallButAbsolute = store.Observe(P("p1", "SKU-1", 37m), FieldProvenance.FeedKind, Now.AddHours(4));
            Assert.AreEqual(PriceObservation.Anomalous, smallButAbsolute.Kind); Assert.AreEqual(7m, smallButAbsolute.Anomaly!.AbsoluteChange); StringAssert.Contains(smallButAbsolute.Words, "/ 5 TRY");
            Assert.AreEqual(PriceObservation.Normal, store.Observe(P("p1", "SKU-1", 40m), FieldProvenance.FeedKind, Now.AddHours(5)).Kind, "3 TRY under a 5 TRY limit and 8% under 50%");

            // Currency change: its own kind whatever the numbers.
            var currency = store.Observe(P("p1", "SKU-1", 40m, "USD"), FieldProvenance.ManualKind, Now.AddHours(6));
            Assert.AreEqual(PriceAnomaly.CurrencyChange, currency.Anomaly!.Kind); StringAssert.Contains(currency.Words, "para birimi değişti 40 TRY → 40 USD");

            // The queue and the review: newest first, a note, no second review, a dismissal; the history keeps every snapshot.
            var pending = store.List(); Assert.AreEqual(4, pending.Count); Assert.AreEqual(PriceAnomaly.CurrencyChange, pending[0].Kind);
            var reviewed = store.Review(pending[1].Id, "tedarikçi teyit etti", Now.AddHours(7)); Assert.AreEqual(PriceAnomaly.Reviewed, reviewed.Status); Assert.AreEqual("tedarikçi teyit etti", reviewed.Note); Assert.IsNotNull(reviewed.ReviewedUtc);
            Assert.ThrowsException<InvalidOperationException>(() => store.Review(pending[1].Id, "yine", Now.AddHours(8)));
            Assert.AreEqual(PriceAnomaly.Dismissed, store.Review(pending[2].Id, null, Now.AddHours(8), dismiss: true).Status);
            Assert.AreEqual(2, store.List().Count); Assert.AreEqual(4, store.List(null).Count); Assert.AreEqual(7, store.History("p1").Count); Assert.AreEqual(40m, store.History("p1")[0].Price);
            Assert.ThrowsException<ArgumentException>(() => store.SavePolicy(0m, 0m, Now)); Assert.ThrowsException<ArgumentException>(() => store.SavePolicy(50m, -1m, Now));

            // Restart: the policy, the queue and the history read back.
            SqliteConnection.ClearAllPools();
            var reopened = new PriceAnomalyStore(root);
            Assert.AreEqual(5m, reopened.GetPolicy().MaxAbsoluteJump); Assert.AreEqual(2, reopened.List().Count); Assert.AreEqual(7, reopened.History("p1").Count); Assert.IsTrue(reopened.HasPending("p1"));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheRealImportAndTheManualSaveFeedTheDetectorAndNeverCorrectThePrice()
    {
        var root = Path.Combine(Path.GetTempPath(), "price-anomaly-chain-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var anomalies = new PriceAnomalyStore(root);
            var source = new XmlSource { Id = "src", Name = "Src", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" } }; store.SaveSource(source);
            CatalogProduct Row(string sku, decimal price) => new() { SourceId = "src", SourceKind = "xml", Sku = sku, Name = "Ürün " + sku, Price = price, Currency = "TRY", Cost = 10, Stock = 3 };

            // The first import: first sightings, no anomaly. Eight stable products keep the feed's average within the dropship gate's own 25% window on the second run.
            IReadOnlyList<CatalogProduct> Rows(decimal one, decimal two) => new[] { Row("SKU-1", one), Row("SKU-2", two) }.Concat(Enumerable.Range(3, 8).Select(i => Row("SKU-" + i, 100m))).ToList();
            store.Import(source, Rows(100m, 50m));
            var ids = store.Products().ToDictionary(p => p.Sku, p => p.Id);
            Assert.AreEqual(0, anomalies.List().Count); Assert.AreEqual(1, anomalies.History(ids["SKU-1"]).Count);

            // The second import: one normal move, one extreme move -- quarantined, the price still written as the feed gave it.
            store.Import(source, Rows(120m, 200m));
            var pending = anomalies.List();
            Assert.AreEqual(1, pending.Count); Assert.AreEqual("SKU-2", pending[0].Sku); Assert.AreEqual(PriceAnomaly.JumpUp, pending[0].Kind); Assert.AreEqual(300m, pending[0].PercentChange);
            Assert.AreEqual(200m, store.Products().Single(p => p.Sku == "SKU-2").Price, "the feed's price stands; the anomaly is a review item, not a correction");
            Assert.AreEqual(2, anomalies.History(ids["SKU-1"]).Count); Assert.IsTrue(anomalies.HasPending(ids["SKU-2"])); Assert.IsFalse(anomalies.HasPending(ids["SKU-1"]));

            // The manual save: an operator's extreme change is quarantined the same way, with the operator as its origin.
            var one = store.Products().Single(p => p.Sku == "SKU-1"); one.Price = 10m; store.SaveProduct(one);
            var manual = anomalies.List().Single(a => a.Sku == "SKU-1");
            Assert.AreEqual(PriceAnomaly.JumpDown, manual.Kind); Assert.AreEqual(FieldProvenance.ManualKind, anomalies.History(ids["SKU-1"])[0].Origin); Assert.AreEqual(10m, store.Products().Single(p => p.Sku == "SKU-1").Price);
            var same = store.Products().Single(p => p.Sku == "SKU-3"); same.Name = "Ürün SKU-3 (düzenlendi)"; store.SaveProduct(same);
            Assert.IsFalse(anomalies.List().Any(a => a.Sku == "SKU-3"), "a save that leaves the price alone is a normal observation");
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
