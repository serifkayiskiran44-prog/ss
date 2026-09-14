using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #948 (ORDER DUPLICATES: receipt/order identity collision detector). Within one store, the same order number
// arriving twice is normally an update -- the same lines and total, or a superset; a payload that shares nothing
// with what is stored looks like a collision and is parked for an operator, never silently overwritten. The same
// number on two different stores is not a collision at all -- each store's orders are its own.
[TestClass]
public sealed class OrderIdentityCollisionTests
{
    static OrderItem Line(string sku, int qty = 1) => new() { Sku = sku, Title = "Ürün " + sku, Quantity = qty, UnitPrice = 10m };
    static OrderSnapshot Order(string marketplace, string shop, string id, decimal? total, string currency, params OrderItem[] items) => new() { Marketplace = marketplace, ShopId = shop, OrderId = id, RawStatus = "paid", Total = total, Currency = currency, Items = items.ToList() };

    [TestMethod]
    public void ASameStoreDuplicateIsAnUpdateACrossStoreSameIdIsValidAndAChangedPayloadIsAConflict()
    {
        var existing = Order("etsy", "S1", "o-1", 100m, "TRY", Line("A", 2), Line("B"));

        // Same store, same number, the same lines and total (a status refresh): an update.
        var update = Order("etsy", "S1", "o-1", 100m, "TRY", Line("A", 2), Line("B")); update.RawStatus = "shipped";
        Assert.AreEqual(OrderIdentityVerdict.SameOrderUpdate, OrderIdentityCollision.Evaluate(existing, update).Outcome);

        // Same store, same number, a superset of lines sharing at least one and the same total-ish shape: still an update (a line added is common when a marketplace corrects an order).
        var superset = Order("etsy", "S1", "o-1", 100m, "TRY", Line("A", 2), Line("B"), Line("C"));
        Assert.AreEqual(OrderIdentityVerdict.SameOrderUpdate, OrderIdentityCollision.Evaluate(existing, superset).Outcome, "sharing at least one line keeps it an update");

        // Same store, same number, nothing in common and a different total: a collision, named, never applied by itself.
        var conflicting = Order("etsy", "S1", "o-1", 40m, "TRY", Line("Z", 5));
        var conflict = OrderIdentityCollision.Evaluate(existing, conflicting); Assert.AreEqual(OrderIdentityVerdict.ConflictingPayload, conflict.Outcome); StringAssert.Contains(conflict.Words, "o-1"); StringAssert.Contains(conflict.Words, "aynı numara başka bir siparişe ait olabilir"); StringAssert.Contains(conflict.Words, "üzerine yazılmadı");

        // A brand new number: new, not a conflict.
        Assert.AreEqual(OrderIdentityVerdict.New, OrderIdentityCollision.Evaluate(null, Order("etsy", "S1", "o-2", 10m, "TRY", Line("A"))).Outcome);

        // Cross-store: the same order number and even the same content on another shop, or another marketplace, is simply that store's own order -- Evaluate is never told about another store's snapshot, so a caller comparing across stores never treats it as a conflict; the store-scoped Find() call ensures existing is always the same store's row or null.
        Assert.AreEqual(OrderIdentityVerdict.New, OrderIdentityCollision.Evaluate(null, Order("ebay", "E1", "o-1", 40m, "TRY", Line("Z", 5))).Outcome, "a different store's lookup starts from null: cross-store identity never collides");
    }

    [TestMethod]
    public void SaveAppliesUpdatesDirectlyParksAConflictLeavingTheExistingOrderUntouchedAndAnOperatorsResolutionAppliesOrKeepsAndItReadsBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "identity-collision-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var orders = new OrdersStore(root); var conflicts = new OrderIdentityConflictStore(root);
            var now = DateTime.UtcNow;
            orders.SaveManual(Order("etsy", "S1", "o-1", 100m, "TRY", Line("A", 2), Line("B")));

            // A conflicting payload: parked, the existing order untouched.
            var conflicting = Order("etsy", "S1", "o-1", 40m, "TRY", Line("Z", 5));
            var result = OrderIdentityCollision.Save(orders, conflicts, conflicting, now);
            Assert.AreEqual((OrderSaveOutcome.NeedsReview, OrderIdentityVerdict.ConflictingPayload), (result.Outcome, result.Verdict.Outcome)); Assert.IsNotNull(result.ConflictId);
            Assert.AreEqual(100m, orders.Find("etsy", "S1", "o-1")!.Total, "the existing order was not overwritten"); Assert.AreEqual(1, conflicts.Open_("etsy", "S1").Count);
            StringAssert.Contains(conflicts.Get(result.ConflictId!.Value)!.ExistingSummary, "TRY");

            // Same-store, same-number update applies directly, no conflict raised.
            var update = Order("etsy", "S1", "o-1", 100m, "TRY", Line("A", 2), Line("B")); update.RawStatus = "shipped"; update.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            var applied = OrderIdentityCollision.Save(orders, conflicts, update, now.AddMinutes(1)); Assert.AreEqual(OrderSaveOutcome.Applied, applied.Outcome); Assert.AreEqual("shipped", orders.Find("etsy", "S1", "o-1")!.RawStatus); // SaveBatch does not normalize status the way SaveManual does

            // The operator resolves the parked conflict by keeping the existing order: nothing changes, refusing a second resolution.
            var kept = conflicts.Resolve(result.ConflictId!.Value, orders, keepIncoming: false, now.AddMinutes(2));
            Assert.AreEqual(OrderIdentityConflictStore.KeptExisting, kept.State); Assert.AreEqual("shipped", orders.Find("etsy", "S1", "o-1")!.RawStatus, "the update from before still stands");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => conflicts.Resolve(result.ConflictId!.Value, orders, false, now)).Message, "zaten çözülmüş");
            Assert.IsTrue(new AuditStore(root).List(20).Any(a => a.Action == "identity-conflict-resolve" && a.Detail.Contains("mevcut sipariş korundu", StringComparison.Ordinal)));

            // A second conflict, this time the operator applies the incoming payload: it overwrites, even though its own timestamp was not advanced.
            var secondConflict = Order("etsy", "S1", "o-1", 40m, "TRY", Line("Z", 5));
            var secondResult = OrderIdentityCollision.Save(orders, conflicts, secondConflict, now.AddMinutes(3));
            var appliedIncoming = conflicts.Resolve(secondResult.ConflictId!.Value, orders, keepIncoming: true, now.AddMinutes(4));
            Assert.AreEqual(OrderIdentityConflictStore.AppliedIncoming, appliedIncoming.State); Assert.AreEqual(40m, orders.Find("etsy", "S1", "o-1")!.Total, "the operator's choice overrides the store's own newer-wins rule");
            Assert.AreEqual(0, conflicts.Open_("etsy", "S1").Count);

            // Restart: the resolved conflicts read back.
            SqliteConnection.ClearAllPools();
            var reopened = new OrderIdentityConflictStore(root);
            Assert.AreEqual(2, reopened.All().Count(x => x.State != OrderIdentityConflictStore.OpenState));
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
