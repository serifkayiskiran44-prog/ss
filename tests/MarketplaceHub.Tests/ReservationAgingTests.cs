using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #937 (STOCK: reservation aging report). A reservation holds units of a product for an order on a store until an
// expiry or a release; the projection takes the active holds off the available figure and says so; the aging report
// judges every reservation -- state, the order's fate, age bucket, time to expiry, share of the stock -- and calls
// stale what deserves a look; it all reads back after a restart.
[TestClass]
public sealed class ReservationAgingTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static StockReservation Res(long id, string productId, string sku, string channel, string shop, string orderId, int qty, double ageHours, double? ttlHours, string state = StockReservation.Active)
        => new(id, productId, sku, channel, shop, orderId, qty, Now.AddHours(-ageHours), ttlHours is { } t ? Now.AddHours(-ageHours + t) : null, state, state == StockReservation.Released ? Now.AddHours(-1) : null, state == StockReservation.Released ? "sevk edildi" : "", "");
    static OrderSnapshot Order(string channel, string shop, string id, string status = "Yeni", string? shipment = null) { var o = new OrderSnapshot { Marketplace = channel, ShopId = shop, OrderId = id, RawStatus = status, Items = { new OrderItem { Title = "Bir", Sku = "SKU-1", Quantity = 1 } } }; if (shipment is not null) o.Shipments.Add(new OrderShipment { Id = "s", State = shipment }); return o; }

    [TestMethod]
    public void TheReportJudgesActiveExpiredReleasedOrphanAndCancelledHoldsAcrossStores()
    {
        var products = new[] { new CatalogProduct { Id = "p1", Sku = "SKU-1", Stock = 10 }, new CatalogProduct { Id = "p2", Sku = "SKU-2", Stock = 0 } };
        var orders = new[] { Order("etsy", "S1", "o-open"), Order("etsy", "S1", "o-cancel", status: "İptal edildi"), Order("ebay", "E1", "o-done", shipment: "Delivered") };
        var reservations = new[]
        {
            Res(1, "p1", "SKU-1", "etsy", "S1", "o-open", 3, ageHours: 2, ttlHours: 48),                       // active, open order, fresh
            Res(2, "p1", "SKU-1", "etsy", "S1", "o-gone", 2, ageHours: 30, ttlHours: 24),                      // expired in effect, and its order is gone
            Res(3, "p1", "SKU-1", "etsy", "S1", "o-cancel", 1, ageHours: 5, ttlHours: null),                   // active for a cancelled order
            Res(4, "p1", "SKU-1", "ebay", "E1", "o-done", 4, ageHours: 200, ttlHours: null, state: StockReservation.Released), // released eight days ago: never stale
            Res(5, "p2", "SKU-2", "ebay", "E1", "", 5, ageHours: 96, ttlHours: null),                          // manual, open-ended, older than three days, on a product with no stock
            Res(6, "p1", "SKU-1", "ebay", "E1", "o-done", 2, ageHours: 1, ttlHours: 6),                        // active for a completed order on the other store
        };
        var report = ReservationAging.Build(reservations, orders, products, Now);
        Assert.AreEqual((4, 1, 1, 4, 11), (report.Active, report.Expired, report.Released, report.Stale, report.ReservedUnits), report.Headline);
        StringAssert.Contains(report.Headline, "4 aktif rezervasyon (11 adet), 1 süresi dolmuş, 1 serbest; 4 bayat — inceleyin");
        var byId = report.Rows.ToDictionary(r => r.Id);
        Assert.AreEqual((StockReservation.Active, ReservationAging.OrderOpen, false, "<1 gün", 30), (byId[1].State, byId[1].OrderState, byId[1].Stale, byId[1].AgeBucket, byId[1].StockImpactPercent)); StringAssert.Contains(byId[1].Words, "bitişe 1 gün"); StringAssert.Contains(byId[1].Words, "stok etkisi %30 (stok 10)");
        Assert.AreEqual((StockReservation.Expired, ReservationAging.OrderMissing, true), (byId[2].State, byId[2].OrderState, byId[2].Stale)); CollectionAssert.AreEquivalent(new[] { "süresi dolmuş, hâlâ kayıtlı", "siparişi bulunamadı (öksüz)" }, byId[2].Reasons.ToArray()); StringAssert.Contains(byId[2].Words, "bitişi 6 sa önce");
        Assert.AreEqual((true, ReservationAging.OrderCancelled), (byId[3].Stale, byId[3].OrderState)); StringAssert.Contains(byId[3].Reasons.Single(), "sipariş iptal, rezervasyon açık");
        Assert.AreEqual((StockReservation.Released, false, ">7 gün"), (byId[4].State, byId[4].Stale, byId[4].AgeBucket));
        Assert.AreEqual((true, ReservationAging.OrderNone, 100, "3-7 gün"), (byId[5].Stale, byId[5].OrderState, byId[5].StockImpactPercent, byId[5].AgeBucket)); StringAssert.Contains(byId[5].Reasons.Single(), "süresiz ve 4 günden eski");
        Assert.AreEqual((true, ReservationAging.OrderCompleted), (byId[6].Stale, byId[6].OrderState)); StringAssert.Contains(byId[6].Reasons.Single(), "sipariş tamamlandı, rezervasyon açık");
        CollectionAssert.AreEqual(new long[] { 2, 3, 5, 6 }, report.StaleRows.Select(r => r.Id).OrderBy(x => x).ToArray(), "the stale drill-down");
        Assert.IsTrue(report.Rows.Take(4).All(r => r.Stale), "stale rows come first");
        CollectionAssert.AreEquivalent(new long[] { 4, 5, 6 }, report.ForStore("ebay", "E1").Select(r => r.Id).ToArray(), "one store's rows");
        Assert.AreEqual("Rezervasyon yok.", ReservationAging.Build(Array.Empty<StockReservation>(), orders, products, Now).Headline);
    }

    [TestMethod]
    public void ActiveHoldsComeOffTheProjectionPerStoreAndTheReportAndTheHoldsReadBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "reservation-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root); var orders = new OrdersStore(root);
            var source = new XmlSource { Id = "src", Name = "Kaynak", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" }, IntervalMinutes = 30 }; store.SaveSource(source);
            var run = runs.Start("src", "h-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromMinutes(5), source.ConfigRevision);
            store.Import(source, new[] { new CatalogProduct { SourceId = "src", SourceKind = "xml", Sku = "SKU-1", Name = "Bir", Price = 10, Currency = "TRY", Cost = 4, Stock = 10, Active = true } }, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = source.ConfigRevision, ObservedAtUtc = Now.AddMinutes(-30) });
            runs.Complete(run, new ImportSummary(1, 0, 0));
            var product = store.Products().Single();
            store.SaveStockPolicy(new StockPolicy { Channel = "local", Shop = "s1", SafetyStock = 1, MaximumStock = null, Enabled = true });
            store.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "S1", SafetyStock = 0, MaximumStock = null, Enabled = true });
            orders.SaveManual(Order("local", "s1", "o-1"));

            // Two holds on one store, one on another: each store's projection subtracts its own, said so.
            var first = store.Reserve(product.Id, "local", "s1", "o-1", 3, Now, TimeSpan.FromHours(48)); var second = store.Reserve(product.Id, "local", "s1", "", 2, Now, null, "vitrin");
            store.Reserve(product.Id, "etsy", "S1", "o-9", 4, Now, TimeSpan.FromHours(2));
            var local = store.ProjectStock("local", "s1", product.Id, Now.AddMinutes(5));
            Assert.AreEqual((10, 4, 5, StockProjection.Fresh, true), (local.Stock, local.Available, local.Reserved, local.State, local.Dispatchable), "10 minus a safety stock of 1 minus 5 held"); StringAssert.Contains(local.Words, "rezerve 5 adet");
            Assert.AreEqual((6, 4), (store.ProjectStock("etsy", "S1", product.Id, Now.AddMinutes(5)).Available, store.ProjectStock("etsy", "S1", product.Id, Now.AddMinutes(5)).Reserved));
            Assert.IsTrue(new AuditStore(root).List(20).Any(x => x.Action == "reserve" && x.Detail.Contains("sipariş o-1", StringComparison.Ordinal)), "the hold is on the audit trail by order and count");
            StringAssert.Contains(Assert.ThrowsException<ArgumentException>(() => store.Reserve(product.Id, "local", "s1", "o-2", 0, Now, null)).Message, "pozitif");

            // The report over the real stores: the orphan (o-9 never arrived) is stale, the rest fresh; releasing one gives its units back; an expired hold gives them back by itself.
            var report = store.ReservationAging(Now.AddMinutes(5));
            Assert.AreEqual((3, 1, 9), (report.Active, report.Stale, report.ReservedUnits), report.Headline); Assert.AreEqual("o-9", report.StaleRows.Single().OrderId); StringAssert.Contains(report.StaleRows.Single().Words, "siparişi bulunamadı");
            var released = new StockReservationStore(root).Release(second.Id, "vitrin bitti", Now.AddMinutes(10)); Assert.AreEqual((StockReservation.Released, "vitrin bitti"), (released.State, released.ReleaseReason));
            Assert.AreEqual(6, store.ProjectStock("local", "s1", product.Id, Now.AddMinutes(15)).Available);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => new StockReservationStore(root).Release(second.Id, "yine", Now)).Message, "zaten serbest");
            Assert.AreEqual((10, 0), (store.ProjectStock("etsy", "S1", product.Id, Now.AddHours(3)).Available, store.ProjectStock("etsy", "S1", product.Id, Now.AddHours(3)).Reserved), "the two-hour hold has expired in effect");
            Assert.AreEqual(StockReservation.Expired, store.ReservationAging(Now.AddHours(3)).Rows.Single(r => r.OrderId == "o-9").State);

            // Restart: the holds and the projection read back.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root);
            Assert.AreEqual(3, new StockReservationStore(root).List().Count); Assert.AreEqual(first.Id, new StockReservationStore(root).List("local", "s1").Single(r => r.State == StockReservation.Active).Id);
            Assert.AreEqual((6, 3), (reopened.ProjectStock("local", "s1", product.Id, Now.AddMinutes(15)).Available, reopened.ProjectStock("local", "s1", product.Id, Now.AddMinutes(15)).Reserved));
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
