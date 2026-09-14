using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #929 (PRICING: channel price comparison, read-only). For every product and every enabled shop: the local price
// the real chain would produce now (or why it would not, or that the shop has no rule), the last-known remote price
// with its currency and freshness (unknown when nobody observed one), and the verdict -- same, higher, lower, a
// currency mismatch. Remote prices come from the remote price store or the last succeeded price dispatch. Nothing
// is written to a product or a marketplace.
[TestClass]
public sealed class ChannelPriceComparisonTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static MarketplaceConnection Conn(string channel, string shop) => new(channel + ":" + shop, channel, shop, channel + " " + shop, true, "", null, "");
    static ChannelPriceCell Cell(IReadOnlyList<ChannelPriceCell> cells, string channel, string shop, string sku) => cells.Single(c => c.Channel == channel && c.Shop == shop && c.Sku == sku);

    [TestMethod]
    public void EveryProductAndShopIsComparedAcrossCurrenciesFreshnessAndMissingRulesWithoutWritingAnything()
    {
        var root = Path.Combine(Path.GetTempPath(), "price-compare-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CatalogStore(root);
            catalog.Import(new XmlSource { Id = "src", Name = "Src" }, new[] { new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Bir", Cost = 100m, CostCurrency = "TRY", Stock = 10, Active = true }, new CatalogProduct { SourceId = "src", Sku = "SKU-2", Name = "İki", Cost = 0m, CostCurrency = "TRY", Stock = 10, Active = true } });
            var products = catalog.Products(); var one = products.Single(p => p.Sku == "SKU-1"); var two = products.Single(p => p.Sku == "SKU-2");
            catalog.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "S1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 0, EstimatedShippingTry = 0, TransactionCostTry = 0, VatRatePercent = 0 });
            catalog.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "S2", Formula = "x*3", Currency = "USD", TryPerUnit = 30m, Enabled = true, CommissionPercent = 0, EstimatedShippingTry = 0, TransactionCostTry = 0, VatRatePercent = 0, FxRateObservedUtc = new DateTimeOffset(Now) });
            var remote = new[]
            {
                new RemotePriceObservation("etsy", "S1", one.Id, 200m, "TRY", Now.AddHours(-2), "read"),          // same
                new RemotePriceObservation("etsy", "S2", one.Id, 12m, "USD", Now.AddDays(-3), "read"),           // stale, ours lower (10 vs 12)
                new RemotePriceObservation("ebay", "E1", one.Id, 250m, "TRY", Now.AddHours(-1), "read"),         // no rule on this shop
                new RemotePriceObservation("etsy", "S1", two.Id, 5m, "USD", Now.AddHours(-1), "read"),           // the local price is blocked (no cost)
            };
            var snapshotBefore = SerializeProducts(catalog);
            var cells = ChannelPriceComparison.Build(catalog, products, new[] { Conn("etsy", "S1"), Conn("etsy", "S2"), Conn("ebay", "E1") }, remote, Now);

            // Multi-store: a cell per product per shop.
            Assert.AreEqual(6, cells.Count);

            // Same currency, same price: SAME with the fresh remote.
            var same = Cell(cells, "etsy", "S1", "SKU-1");
            Assert.AreEqual((ChannelPriceCell.LocalOk, 200m, "TRY", ChannelPriceCell.RemoteKnown, ChannelPriceCell.Same, 0m), (same.LocalState, same.LocalPrice, same.LocalCurrency, same.RemoteState, same.Comparison, same.DifferencePercent)); StringAssert.Contains(same.Words, "aynı");

            // A stale remote in the shop's own currency: still compared, said to be stale; ours is lower by a sixth.
            var stale = Cell(cells, "etsy", "S2", "SKU-1");
            Assert.AreEqual((10m, "USD", ChannelPriceCell.RemoteStale, ChannelPriceCell.Lower, -16.67m), (stale.LocalPrice, stale.LocalCurrency, stale.RemoteState, stale.Comparison, stale.DifferencePercent)); StringAssert.Contains(stale.Words, "bayat"); StringAssert.Contains(stale.Words, "yerel %16.67 düşük");

            // Missing rule: the shop has nothing to compare with; the remote is still shown.
            var noRule = Cell(cells, "ebay", "E1", "SKU-1");
            Assert.AreEqual((ChannelPriceCell.NoRule, null, 250m, ChannelPriceCell.Unknown), (noRule.LocalState, noRule.LocalPrice, noRule.RemotePrice, noRule.Comparison)); StringAssert.Contains(noRule.Words, "fiyat kuralı yok");

            // A blocked local price says why; a different currency on the remote is a mismatch, never a number; no remote is unknown.
            var blocked = Cell(cells, "etsy", "S1", "SKU-2");
            Assert.AreEqual((ChannelPriceCell.LocalBlocked, ChannelPriceCell.Unknown), (blocked.LocalState, blocked.Comparison)); StringAssert.Contains(blocked.LocalWords, "maliyeti girilmemiş");
            var mismatch = ChannelPriceComparison.Build(catalog, new[] { one }, new[] { Conn("etsy", "S1") }, new[] { new RemotePriceObservation("etsy", "S1", one.Id, 7m, "USD", Now, "read") }, Now).Single();
            Assert.AreEqual(ChannelPriceCell.CurrencyMismatch, mismatch.Comparison); Assert.IsNull(mismatch.DifferencePercent); StringAssert.Contains(mismatch.Words, "para birimleri farklı (TRY / USD)");
            var unknown = Cell(cells, "etsy", "S2", "SKU-2");
            Assert.AreEqual((ChannelPriceCell.RemoteUnknown, ChannelPriceCell.Unknown), (unknown.RemoteState, unknown.Comparison)); StringAssert.Contains(unknown.Words, "uzak fiyat bilinmiyor");
            var higher = ChannelPriceComparison.Build(catalog, new[] { one }, new[] { Conn("etsy", "S1") }, new[] { new RemotePriceObservation("etsy", "S1", one.Id, 160m, "TRY", Now, "read") }, Now).Single();
            Assert.AreEqual((ChannelPriceCell.Higher, 25m), (higher.Comparison, higher.DifferencePercent));

            // Read-only: the products are exactly as they were, and no sync job was queued.
            Assert.AreEqual(snapshotBefore, SerializeProducts(catalog)); Assert.AreEqual(0, new SyncStore(root).List().Count);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void RemotePricesComeFromTheStoreAndFromTheLastSucceededDispatchNewestFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "price-compare-remote-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CatalogStore(root);
            catalog.Import(new XmlSource { Id = "src", Name = "Src" }, new[] { new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Bir", Cost = 100m, CostCurrency = "TRY", Stock = 10, Active = true } });
            var one = catalog.Products().Single();
            catalog.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "S1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 0, EstimatedShippingTry = 0, TransactionCostTry = 0, VatRatePercent = 0 });

            // The last succeeded price dispatch is an observation: its payload is the price the marketplace was sent, the currency the rule's; pending and failed jobs are not, an older success loses to a newer one.
            var sync = new SyncStore(root);
            var older = sync.Enqueue(new SyncRequest("etsy", "price", one.Id, $"{one.Id}:1:190.00", "S1")); sync.Succeed(older.Id);
            Thread.Sleep(20);
            var newer = sync.Enqueue(new SyncRequest("etsy", "price", one.Id, $"{one.Id}:2:199.90", "S1")); sync.Succeed(newer.Id);
            sync.Enqueue(new SyncRequest("etsy", "price", one.Id, $"{one.Id}:3:5.00", "S1"));
            sync.EnqueueFailed(new SyncRequest("etsy", "price", one.Id, $"{one.Id}:4:error", "S2"), "hata");
            var fromSync = ChannelPriceComparison.FromSync(sync.List(), (channel, shop) => catalog.GetPricePolicy(channel, shop)?.Currency);
            var observation = fromSync.Single(); Assert.AreEqual((199.90m, "TRY", ChannelPriceComparison.SyncSource, "etsy", "S1"), (observation.Price, observation.Currency, observation.Source, observation.Channel, observation.Shop));

            // The store: a newer observation replaces, an older one is ignored; it reads back after a restart.
            var store = new RemotePriceStore(root);
            store.Record(new RemotePriceObservation("Etsy", "S1", one.Id, 210m, "try", Now, "read"));
            store.Record(new RemotePriceObservation("etsy", "S1", one.Id, 150m, "TRY", Now.AddDays(-1), "read"));
            Assert.ThrowsException<ArgumentException>(() => store.Record(new RemotePriceObservation("etsy", "S1", one.Id, -1m, "TRY", Now, "read")));
            SqliteConnection.ClearAllPools();
            var kept = new RemotePriceStore(root).List().Single(); Assert.AreEqual((210m, "TRY", "etsy"), (kept.Price, kept.Currency, kept.Channel));

            // Both sources feed the comparison; the newest observation per shop and product wins.
            var cells = ChannelPriceComparison.Build(catalog, new[] { one }, new[] { Conn("etsy", "S1") }, new RemotePriceStore(root).List().Concat(fromSync), Now);
            var cell = cells.Single(); Assert.AreEqual(210m, cell.RemotePrice); Assert.AreEqual(ChannelPriceCell.Lower, cell.Comparison, "200 local against a 210 remote");
        }
        finally { Cleanup(root); }
    }

    static string SerializeProducts(CatalogStore catalog) => string.Join("|", catalog.Products().OrderBy(p => p.Sku, StringComparer.Ordinal).Select(p => System.Text.Json.JsonSerializer.Serialize(p)));

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
