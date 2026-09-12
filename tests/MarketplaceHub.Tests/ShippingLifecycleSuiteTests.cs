using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #789 (TEST: Shipping lifecycle integration suite). Real OrdersStore on disk, the real normalizer the Etsy
// client runs on every receipt, the real merge rules of an API re-save over local observations, and the
// delivery SLA the order list shows. Fixture: one order, up to two packages, times fixed relative to `now`.
[TestClass]
public sealed class ShippingLifecycleSuiteTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "shipping-lifecycle-" + Guid.NewGuid().ToString("N"));
    static void Cleanup(string root) { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    static OrderSnapshot ApiOrder(DateTimeOffset updated, params OrderShipment[] shipments)
    {
        var order = new OrderSnapshot { Marketplace = "Etsy", ShopId = "shop-1", OrderId = "r-1", RawStatus = "Paid", Source = "Etsy API", UpdatedAt = updated, SourceUpdatedAt = updated, LastSync = updated, Items = { new OrderItem { Title = "Kupa", Sku = "K1", Quantity = 2 } } };
        order.Shipments.AddRange(shipments);
        return OrderNormalizer.Normalize(order);
    }

    static OrderShipment Package(string id, string carrier, string tracking, string state = "shipped") => new() { Id = id, Carrier = carrier, TrackingNumber = tracking, State = state, Source = "Etsy API (gönderim bildirimi)" };

    [TestMethod]
    public void ASplitShipmentKeepsBothPackagesWithTheirOwnCarrierTrackingAndStateAndAPartialShipmentIsVisibleAsSuch()
    {
        var root = NewRoot();
        try
        {
            var store = new OrdersStore(root);
            store.SaveBatch([ApiOrder(Now.AddDays(-1), Package("s-1", "Yurtiçi Kargo", "TN-1", "in_transit"), Package("s-2", "Aras Kargo", "TN-2", "shipped"))]);

            var order = store.Find("Etsy", "shop-1", "r-1")!;
            Assert.AreEqual(2, order.Shipments.Count);
            Assert.AreEqual("InTransit", order.Shipments.Single(s => s.Id == "s-1").State);
            Assert.AreEqual("Shipped", order.Shipments.Single(s => s.Id == "s-2").State);
            Assert.AreEqual("TN-1, TN-2", order.TrackingNumbers);
            Assert.AreEqual("Yurtiçi Kargo, Aras Kargo", order.Carriers);
            Assert.AreEqual("Yolda, Gönderildi (taşıma doğrulanmadı)", order.DeliveryLabel, "A split shipment shows every distinct package state, not just one.");

            // Partial shipment: the second package has no carrier notification yet.
            store.SaveBatch([ApiOrder(Now, Package("s-1", "Yurtiçi Kargo", "TN-1", "delivered"), Package("s-2", "", "", "unknown"))]);
            var partial = store.Find("Etsy", "shop-1", "r-1")!;
            Assert.AreEqual("Teslim edildi, Bilinmiyor", partial.DeliveryLabel, "One package delivered, one not yet shipped: both states remain visible.");
            Assert.AreEqual("TN-1", partial.TrackingNumbers, "An empty tracking number is not rendered as a stray separator.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void CarrierNamesAreNormalizedOnTheRealEtsyReadPathSoOneCarrierNeverAppearsAsThree()
    {
        var order = ApiOrder(Now, Package("s-1", "yurtici", "TN-1"), Package("s-2", "YURTİÇİ KARGO", "TN-2"), Package("s-3", "Yurtiçi Kargo A.Ş.", "TN-3"), Package("s-4", "aras", "TN-4"), Package("s-5", "Bilinmeyen Taşıyıcı", "TN-5"));

        Assert.AreEqual("Yurtiçi Kargo", order.Shipments[0].Carrier);
        Assert.AreEqual("Yurtiçi Kargo", order.Shipments[1].Carrier);
        Assert.AreEqual("Yurtiçi Kargo", order.Shipments[2].Carrier);
        Assert.AreEqual("Aras Kargo", order.Shipments[3].Carrier);
        Assert.AreEqual("Bilinmeyen Taşıyıcı", order.Shipments[4].Carrier, "An unknown carrier is kept verbatim (trimmed), never guessed.");
        Assert.AreEqual("Yurtiçi Kargo, Aras Kargo, Bilinmeyen Taşıyıcı", order.Carriers);
        Assert.AreEqual("MNG Kargo", OrderNormalizer.NormalizeCarrier("  mng "));
        Assert.AreEqual("", OrderNormalizer.NormalizeCarrier("   "));
    }

    [TestMethod]
    public void DuplicateTrackingIsBlockedAtTheLabelPreviewAndDuplicatePackageIdsAreRejectedBeforeAnySave()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        Assert.AreEqual("PREVIEW_ONLY", ShippingLabelCenter.CreatePreview("Yurtiçi Kargo", "r-1", "TN-1", 1, known).Status);
        Assert.AreEqual("BLOCKED_DUPLICATE", ShippingLabelCenter.CreatePreview("Aras Kargo", "r-2", "TN-1", 1, known).Status, "The same tracking code on another order/carrier is still a duplicate.");
        Assert.AreEqual("PREVIEW_ONLY", ShippingLabelCenter.CreatePreview("Aras Kargo", "r-2", "", 1, known).Status, "A package without a tracking code is not a duplicate of anything.");

        var root = NewRoot();
        try
        {
            var store = new OrdersStore(root);
            var duplicateIds = ApiOrder(Now, Package("s-1", "Aras Kargo", "TN-1"), Package("s-1", "Aras Kargo", "TN-2"));
            Assert.ThrowsException<ArgumentException>(() => store.SaveBatch([duplicateIds]));
            Assert.AreEqual(0, store.ReadAll().Count, "A batch with a duplicate package id is rejected as a whole; nothing is written.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AnOlderStatusArrivingAfterANewerOneIsIgnoredAndAnApiResaveNeverOverwritesLocalObservations()
    {
        var root = NewRoot();
        try
        {
            var store = new OrdersStore(root);
            store.SaveBatch([ApiOrder(Now.AddHours(-2), Package("s-1", "Aras Kargo", "TN-1", "in_transit"))]);
            store.SaveBatch([ApiOrder(Now.AddHours(-3), Package("s-1", "Aras Kargo", "TN-1", "shipped"))]);   // stale, out of order
            Assert.AreEqual("InTransit", store.Find("Etsy", "shop-1", "r-1")!.Shipments.Single().State, "A notification older than what is stored must not regress the package state.");

            // The operator records a local delivery observation, then a newer API pull arrives still saying "shipped".
            var local = store.Find("Etsy", "shop-1", "r-1")!;
            local.Shipments.Single().State = "Delivered"; local.Shipments.Single().TrackingUrl = "https://kargo.example/TN-1";
            store.SaveManual(local);
            store.SaveBatch([ApiOrder(Now, Package("s-1", "Aras Kargo", "TN-1", "shipped"))]);

            var merged = store.Find("Etsy", "shop-1", "r-1")!.Shipments.Single();
            Assert.AreEqual("Delivered", merged.State, "A local observation with history wins over a repeated API 'shipped'.");
            Assert.AreEqual("Yerel / manuel", merged.Source);
            Assert.AreEqual("https://kargo.example/TN-1", merged.TrackingUrl);
            Assert.AreEqual(1, merged.Events.Count(e => e.State == "Delivered"), "The observation history survives the re-save.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void DeliverySlaFlagsAPackageInTransitLongerThanTheAllowanceAndNothingElse()
    {
        var shippedAt = Now.AddDays(-7);
        var late = new OrderShipment { Id = "s-1", State = "InTransit", Events = { new OrderTrackingEvent(shippedAt, "Shipped", "Etsy API"), new OrderTrackingEvent(Now.AddDays(-6), "InTransit", "Yerel / manuel") } };
        var onTime = new OrderShipment { Id = "s-2", State = "InTransit", Events = { new OrderTrackingEvent(Now.AddDays(-2), "Shipped", "Etsy API") } };
        var delivered = new OrderShipment { Id = "s-3", State = "Delivered", Events = { new OrderTrackingEvent(Now.AddDays(-9), "Shipped", "Etsy API"), new OrderTrackingEvent(Now.AddDays(-1), "Delivered", "Yerel / manuel") } };
        var notShipped = new OrderShipment { Id = "s-4", State = "Unknown" };
        var apiWithoutEvents = new OrderShipment { Id = "s-5", State = "Shipped" };   // what the Etsy client produces: no observation events

        var lateSla = OrdersRules.EvaluateSla(late, Now.AddDays(-1), Now, maxTransitDays: 5);
        Assert.AreEqual("DELAYED", lateSla.Status);
        Assert.AreEqual(7, lateSla.DaysInTransit, "Transit is counted from the first shipped/in-transit observation, not the latest.");
        Assert.AreEqual("IN_TRANSIT", OrdersRules.EvaluateSla(onTime, Now, Now, 5).Status);
        Assert.AreEqual("DELIVERED", OrdersRules.EvaluateSla(delivered, Now, Now, 5).Status, "A delivered package is never late, however long it took.");
        Assert.AreEqual("NOT_SHIPPED", OrdersRules.EvaluateSla(notShipped, Now, Now, 5).Status);
        Assert.AreEqual("DELAYED", OrdersRules.EvaluateSla(apiWithoutEvents, Now.AddDays(-6), Now, 5).Status, "Without observations the receipt's source timestamp is the latest the package can have shipped.");
        Assert.AreEqual("IN_TRANSIT", OrdersRules.EvaluateSla(apiWithoutEvents, Now.AddDays(-6), Now, 6).Status, "Exactly the allowance is on time.");
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => OrdersRules.EvaluateSla(onTime, Now, Now, 0));
    }

    [TestMethod]
    public void TheOrderListShowsTheDelayForARealSavedOrderAndTheStateSurvivesARestart()
    {
        var root = NewRoot();
        try
        {
            new OrdersStore(root).SaveBatch([ApiOrder(Now.AddDays(-8), Package("s-1", "Aras Kargo", "TN-1", "shipped"), Package("s-2", "Aras Kargo", "TN-2", "delivered"))]);

            var restarted = new OrdersStore(root);   // a fresh instance is what the app constructs on the next launch
            var order = restarted.Find("Etsy", "shop-1", "r-1")!;
            Assert.AreEqual(2, order.Shipments.Count);
            Assert.AreEqual("Shipped", order.Shipments.Single(s => s.Id == "s-1").State);
            Assert.AreEqual("Delivered", order.Shipments.Single(s => s.Id == "s-2").State);
            var label = order.SlaLabelAt(Now);
            StringAssert.Contains(label, "Gecikti", "The undelivered package shipped 8 days ago is late, and the order says so.");
            StringAssert.Contains(label, "8 gün");
            Assert.AreEqual("Teslim edildi", ApiOrder(Now.AddDays(-8), Package("s-2", "Aras Kargo", "TN-2", "delivered")).SlaLabelAt(Now), "An order whose every package is delivered has no delay to report.");
        }
        finally { Cleanup(root); }
    }
}
