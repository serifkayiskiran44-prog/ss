using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #934 (STOCK: oversell risk score). A stale source stock, a stock too low once the open orders are taken off it,
// reservation pressure and a stock synchronisation a day old are each a factor with points and words; the sum is a
// level; a missing input is named and the score rests on what is known; a resolved risk is one nothing raises;
// decision support only -- nothing closes a listing. The inspect drawer shows it from the real stores.
[TestClass]
public sealed class OversellRiskTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static OversellInput Input(int stock, FreshnessState freshness = FreshnessState.Fresh, int? open = 0, DateTime? synced = null) => new("SKU-1", stock, freshness, "taze · 1 sa önce (eşik 6 sa)", open, synced ?? Now.AddHours(-1), Now);

    [TestMethod]
    public void TheScoreExplainsEveryFactorNamesMissingInputsAndResolvesWhenNothingRaisesIt()
    {
        // Low: fresh, plenty, a few open orders, synced an hour ago -- nothing raises it, the risk is resolved.
        var low = OversellRisk.Evaluate(Input(50, open: 2));
        Assert.AreEqual((OversellRiskView.Low, 0, 0, true), (low.Level, low.Score, low.Factors.Count, low.Resolved)); StringAssert.Contains(low.Words, "oversell riski düşük (0/100)"); StringAssert.Contains(low.Words, "risk çözüldü"); StringAssert.Contains(low.Words, "otomatik satış kapatma yok");

        // High: a stale source stock, open orders beyond the stock, pressure, and a synchronisation two days old.
        var high = OversellRisk.Evaluate(Input(10, FreshnessState.Stale, open: 12, synced: Now.AddHours(-48)));
        Assert.AreEqual(OversellRiskView.High, high.Level); Assert.AreEqual(100, high.Score, "35 + 40 + 10 + 15 capped at 100");
        CollectionAssert.AreEqual(new[] { OversellRisk.FreshnessFactor, OversellRisk.NetStockFactor, OversellRisk.PressureFactor, OversellRisk.SyncLagFactor }, high.Factors.Select(f => f.Key).ToArray());
        StringAssert.Contains(high.Words, "kaynak stoku bayat"); StringAssert.Contains(high.Words, "açık siparişler stoku aşıyor (stok 10, açık 12)"); StringAssert.Contains(high.Words, "rezervasyon baskısı"); StringAssert.Contains(high.Words, "stok senkronu 48 sa önce"); Assert.IsFalse(high.Resolved);

        // Medium: fresh, but the net stock after the open orders is low.
        var medium = OversellRisk.Evaluate(Input(5, open: 3));
        Assert.AreEqual((OversellRiskView.Medium, 30), (medium.Level, medium.Score)); StringAssert.Contains(medium.Words, "net stok düşük (2: stok 5, açık 3)"); StringAssert.Contains(medium.Words, "rezervasyon baskısı");
        Assert.AreEqual(OversellRiskView.Low, OversellRisk.Evaluate(Input(5, open: 1)).Level, "one open order on five leaves four: no factor");

        // Missing inputs: unknown orders and an unknown synchronisation are named; the score rests on what is known, never assumed clean.
        var missing = OversellRisk.Evaluate(new("SKU-1", 2, FreshnessState.Fresh, "taze", null, null, Now));
        CollectionAssert.AreEqual(new[] { OversellRisk.OrdersInput, OversellRisk.SyncInput }, missing.MissingInputs.ToList()); Assert.AreEqual(15, missing.Score); Assert.IsFalse(missing.Resolved); StringAssert.Contains(missing.Words, "eksik girdi: açık siparişler, stok senkronu; skor bilinen girdilerle"); StringAssert.Contains(missing.Words, "stok düşük (2); açık siparişler bilinmiyor");
        Assert.AreEqual(25, OversellRisk.Evaluate(Input(50, FreshnessState.Unknown, open: 0)).Score, "a stock without an observation time is a factor, not a missing input");
        Assert.AreEqual(35, OversellRisk.Evaluate(Input(50, FreshnessState.Frozen, open: 0)).Score);

        // Resolved: the same product after the source is fresh again, the orders shipped and the stock synced.
        var before = OversellRisk.Evaluate(Input(10, FreshnessState.Stale, open: 12, synced: Now.AddHours(-48))); Assert.AreEqual(OversellRiskView.High, before.Level);
        var after = OversellRisk.Evaluate(Input(10, FreshnessState.Fresh, open: 0, synced: Now.AddMinutes(-5))); Assert.IsTrue(after.Resolved); Assert.AreEqual(OversellRiskView.Low, after.Level);

        // Open quantity: cancelled orders and delivered or returned shipments do not count; other SKUs do not count.
        OrderSnapshot Order(string id, string sku, int qty, string status = "Yeni", string? shipment = null) { var o = new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = id, RawStatus = status, Items = { new OrderItem { Sku = sku, Quantity = qty } } }; if (shipment is not null) o.Shipments.Add(new OrderShipment { Id = "s", State = shipment }); return o; }
        var orders = new[] { Order("o1", "SKU-1", 3), Order("o2", "SKU-1", 2, shipment: "InTransit"), Order("o3", "SKU-1", 5, shipment: "Delivered"), Order("o4", "SKU-1", 4, status: "İptal"), Order("o5", "SKU-1", 1, status: "Cancelled"), Order("o6", "SKU-2", 9), Order("o7", "sku-1", 1, shipment: "Returned") };
        Assert.AreEqual(5, OversellRisk.OpenQuantity(orders, "SKU-1")); Assert.AreEqual(0, OversellRisk.OpenQuantity(orders, ""));
        Assert.IsNull(OversellRisk.LastStockSync(null)); Assert.IsNull(OversellRisk.LastStockSync(new[] { new SyncJob { Operation = "stock", Status = SyncStatus.Pending } }));
    }

    [TestMethod]
    public void TheInspectDrawerShowsTheRiskFromTheRealStoresAndItResolvesAsTheInputsImprove()
    {
        var root = Path.Combine(Path.GetTempPath(), "oversell-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root); var orders = new OrdersStore(root); var sync = new SyncStore(root);
            var source = new XmlSource { Id = "src", Name = "Kaynak", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" }, IntervalMinutes = 30 }; store.SaveSource(source);
            void ImportAt(int stock, DateTime observedUtc)
            {
                var run = runs.Start("src", "h-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromMinutes(5), source.ConfigRevision);
                store.Import(source, new[] { new CatalogProduct { SourceId = "src", SourceKind = "xml", Sku = "SKU-1", Name = "Bir", Price = 10, Currency = "TRY", Cost = 4, Stock = stock, Active = true } }, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = source.ConfigRevision, ObservedAtUtc = observedUtc });
                runs.Complete(run, new ImportSummary(1, 0, 0));
            }
            ImportAt(10, DateTime.UtcNow.AddHours(-10));
            var open = new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "Yeni", Items = { new OrderItem { Title = "Bir", Sku = "SKU-1", Quantity = 12 } } }; orders.SaveManual(open);
            var product = store.Products().Single();

            // Stale stock, orders beyond it, no stock sync ever: high, with the missing sync named.
            var view = ProductQuickInspect.Build(product, Array.Empty<SyncJob>(), DateTime.UtcNow, store.Sources(), orders.ReadAll());
            var row = view.Rows.Single(r => r.Label == "Oversell riski"); Assert.AreEqual("Stok", row.Section);
            StringAssert.Contains(row.Value, "oversell riski yüksek"); StringAssert.Contains(row.Value, "kaynak stoku bayat"); StringAssert.Contains(row.Value, "açık siparişler stoku aşıyor (stok 10, açık 12)"); StringAssert.Contains(row.Value, "eksik girdi: stok senkronu");
            var judged = OversellRisk.Judge(product, null, orders.ReadAll(), id => store.Sources().FirstOrDefault(s => s.Id == id), DateTime.UtcNow); Assert.AreEqual(OversellRiskView.High, judged.Level);

            // The order ships, the source is read again, the stock is synced: resolved.
            open.Shipments.Add(new OrderShipment { Id = "s1", State = "Delivered" }); orders.SaveManual(open);
            ImportAt(10, DateTime.UtcNow.AddMinutes(-5));
            var job = sync.Enqueue(new SyncRequest("etsy", "stock", product.Id, $"{product.Id}:1:r1:9", "S1")); sync.Succeed(job.Id);
            var resolved = ProductQuickInspect.Build(store.Products().Single(), sync.List(), DateTime.UtcNow, store.Sources(), orders.ReadAll()).Rows.Single(r => r.Label == "Oversell riski");
            StringAssert.Contains(resolved.Value, "oversell riski düşük"); StringAssert.Contains(resolved.Value, "risk çözüldü");
            Assert.IsFalse(resolved.Value.Contains("eksik girdi", StringComparison.Ordinal), resolved.Value);
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
