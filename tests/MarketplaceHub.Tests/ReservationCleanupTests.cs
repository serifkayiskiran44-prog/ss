using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #938 (STOCK: stale reservation cleanup workflow). A dry-run plan says which holds would be released and why --
// expired ones, holds for cancelled or completed orders, orphans missing longer than a day -- and which are kept: an
// open order's hold always, a fresh orphan, the operator's own open-ended hold. The apply takes a reason, judges every
// planned release again at that moment (an order that arrived since protects its hold), releases with the reason and
// audits each; the releases read back after a restart.
[TestClass]
public sealed class ReservationCleanupTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static StockReservation Res(long id, string orderId, int qty, double ageHours, double? ttlHours, string state = StockReservation.Active)
        => new(id, "p1", "SKU-1", "local", "s1", orderId, qty, Now.AddHours(-ageHours), ttlHours is { } t ? Now.AddHours(-ageHours + t) : null, state, null, "", "");
    static OrderSnapshot Order(string channel, string shop, string id, string status = "Yeni", string? shipment = null) { var o = new OrderSnapshot { Marketplace = channel, ShopId = shop, OrderId = id, RawStatus = status, Items = { new OrderItem { Title = "Bir", Sku = "SKU-1", Quantity = 1 } } }; if (shipment is not null) o.Shipments.Add(new OrderShipment { Id = "s", State = shipment }); return o; }

    [TestMethod]
    public void ThePlanReleasesExpiredCancelledCompletedAndOldOrphansAndKeepsOpenOrdersFreshOrphansAndManualHolds()
    {
        var orders = new[] { Order("local", "s1", "o-open"), Order("local", "s1", "o-cancel", status: "İptal edildi"), Order("local", "s1", "o-done", shipment: "Delivered") };
        var reservations = new[]
        {
            Res(1, "o-open", 2, ageHours: 2, ttlHours: 48),          // open order, fresh: keep
            Res(2, "o-open", 2, ageHours: 30, ttlHours: 24),         // expired, but the order is open: keep, said so
            Res(3, "o-gone", 3, ageHours: 30, ttlHours: null),       // orphan for 30 h: release
            Res(4, "o-new", 1, ageHours: 2, ttlHours: null),         // orphan for 2 h: keep, the order may not have arrived
            Res(5, "o-cancel", 2, ageHours: 5, ttlHours: null),      // cancelled order: release
            Res(6, "", 4, ageHours: 200, ttlHours: null),            // manual, open-ended: keep
            Res(7, "o-done", 1, ageHours: 50, ttlHours: null, state: StockReservation.Released), // released: not in the plan
            Res(8, "o-done", 1, ageHours: 10, ttlHours: null),       // completed order: release
            Res(9, "o-cancel", 1, ageHours: 10, ttlHours: 5),        // expired and cancelled: release as expired
        };
        var plan = ReservationCleanup.Plan(reservations, orders, Now);
        Assert.AreEqual((8, 4, 4), (plan.Rows.Count, plan.ReleaseCount, plan.KeepCount), plan.Words); StringAssert.Contains(plan.Words, "4 rezervasyon serbest bırakılacak, 4 korunacak; önizleme — hiçbir şey değişmedi");
        var byId = plan.Rows.ToDictionary(r => r.Id);
        Assert.AreEqual(ReservationCleanupRow.Keep, byId[1].Action); StringAssert.Contains(byId[1].Reason, "açık sipariş; asla temizlenmez");
        Assert.AreEqual((ReservationCleanupRow.Keep, StockReservation.Expired), (byId[2].Action, byId[2].State)); StringAssert.Contains(byId[2].Reason, "süresi dolmuş ama sipariş açık; korundu");
        Assert.AreEqual(ReservationCleanupRow.Release, byId[3].Action); StringAssert.Contains(byId[3].Reason, "öksüz: siparişi 30 saattir yok (sınır 24 sa)");
        Assert.AreEqual(ReservationCleanupRow.Keep, byId[4].Action); StringAssert.Contains(byId[4].Reason, "öksüz ama 2 saatlik; sipariş henüz gelmemiş olabilir");
        Assert.AreEqual(ReservationCleanupRow.Release, byId[5].Action); StringAssert.Contains(byId[5].Reason, "sipariş iptal");
        Assert.AreEqual(ReservationCleanupRow.Keep, byId[6].Action); StringAssert.Contains(byId[6].Reason, "elle tutulmuş");
        Assert.IsFalse(byId.ContainsKey(7), "a released hold is not in the plan");
        Assert.AreEqual(ReservationCleanupRow.Release, byId[8].Action); StringAssert.Contains(byId[8].Reason, "sipariş tamamlandı");
        Assert.AreEqual(ReservationCleanupRow.Release, byId[9].Action); StringAssert.Contains(byId[9].Reason, "süresi dolmuş (bitiş 2026-09-14 07:00 UTC)");
        CollectionAssert.AreEqual(new long[] { 3, 5, 8, 9 }, plan.Releasable.Select(r => r.Id).ToArray());
        StringAssert.Contains(ReservationCleanup.Plan(Array.Empty<StockReservation>(), orders, Now).Words, "Temizlenecek rezervasyon yok");
    }

    [TestMethod]
    public void TheApplyJudgesAgainProtectsAHoldWhoseOrderArrivedReleasesTheRestWithTheReasonAndAuditsAndReadsBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "cleanup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root); var orders = new OrdersStore(root);
            var source = new XmlSource { Id = "src", Name = "Kaynak", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" }, IntervalMinutes = 30 }; store.SaveSource(source);
            var run = runs.Start("src", "h-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromMinutes(5), source.ConfigRevision);
            store.Import(source, new[] { new CatalogProduct { SourceId = "src", SourceKind = "xml", Sku = "SKU-1", Name = "Bir", Price = 10, Currency = "TRY", Cost = 4, Stock = 10, Active = true } }, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = source.ConfigRevision, ObservedAtUtc = Now.AddMinutes(-30) });
            runs.Complete(run, new ImportSummary(1, 0, 0));
            var product = store.Products().Single();
            store.SaveStockPolicy(new StockPolicy { Channel = "local", Shop = "s1", SafetyStock = 0, MaximumStock = null, Enabled = true });
            orders.SaveManual(Order("local", "s1", "o-open")); orders.SaveManual(Order("local", "s1", "o-cancel", status: "İptal"));
            var open = store.Reserve(product.Id, "local", "s1", "o-open", 2, Now, TimeSpan.FromHours(48));
            var orphan = store.Reserve(product.Id, "local", "s1", "o-late", 3, Now.AddHours(-30), null);
            var expired = store.Reserve(product.Id, "local", "s1", "", 1, Now.AddHours(-2), TimeSpan.FromHours(1));
            var cancelled = store.Reserve(product.Id, "local", "s1", "o-cancel", 2, Now, null);

            // The dry-run: three releases, one keep; nothing changed.
            var plan = store.PlanReservationCleanup(Now);
            Assert.AreEqual((3, 1), (plan.ReleaseCount, plan.KeepCount), plan.Words); CollectionAssert.AreEquivalent(new[] { orphan.Id, expired.Id, cancelled.Id }, plan.Releasable.Select(r => r.Id).ToArray());
            Assert.AreEqual(4, new StockReservationStore(root).List().Count(r => r.State == StockReservation.Active), "a plan releases nothing");
            StringAssert.Contains(Assert.ThrowsException<ArgumentException>(() => store.ApplyReservationCleanup(plan, "  ", Now)).Message, "gerekçesi gerekli");

            // The order the orphan waited for arrives between the plan and the apply: its hold is protected; the rest are released with the reason and audited.
            orders.SaveManual(Order("local", "s1", "o-late"));
            var outcome = store.ApplyReservationCleanup(plan, "haftalık temizlik", Now.AddMinutes(5));
            CollectionAssert.AreEquivalent(new[] { expired.Id, cancelled.Id }, outcome.Released.ToArray()); CollectionAssert.AreEqual(new[] { orphan.Id }, outcome.Protected.ToArray());
            StringAssert.Contains(outcome.Words, "2 rezervasyon serbest bırakıldı, 1 korundu/atlandı; gerekçe: haftalık temizlik"); StringAssert.Contains(outcome.Lines.Single(l => l.StartsWith("#" + orphan.Id, StringComparison.Ordinal)), "korundu — açık sipariş");
            var after = new StockReservationStore(root).List().ToDictionary(r => r.Id);
            Assert.AreEqual((StockReservation.Active, StockReservation.Active, StockReservation.Released, StockReservation.Released), (after[open.Id].State, after[orphan.Id].State, after[expired.Id].State, after[cancelled.Id].State));
            StringAssert.StartsWith(after[cancelled.Id].ReleaseReason, "temizlik: haftalık temizlik · sipariş iptal");
            Assert.AreEqual(2, new AuditStore(root).List(50).Count(x => x.Action == ReservationCleanup.AuditAction && x.Detail.Contains("gerekçe: haftalık temizlik", StringComparison.Ordinal)));
            Assert.AreEqual(5, store.ProjectStock("local", "s1", product.Id, Now.AddMinutes(10)).Available, "the open order's 2 and the protected 3 are still held");

            // Restart: the releases read back; a new plan keeps the protected hold now that its order is open.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root);
            Assert.AreEqual(2, new StockReservationStore(root).List().Count(r => r.State == StockReservation.Released));
            var again = reopened.PlanReservationCleanup(Now.AddMinutes(15)); Assert.AreEqual(0, again.ReleaseCount, again.Words); StringAssert.Contains(again.Rows.Single(r => r.Id == orphan.Id).Reason, "açık sipariş");
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
