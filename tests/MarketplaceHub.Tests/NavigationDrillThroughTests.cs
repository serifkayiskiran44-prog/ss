using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #810 (DESIGN: Dashboard drill-through breadcrumbs). A KPI or anomaly card is a question with a context:
// which store the board was filtered to, and which entity the card was counting. Following the card must keep
// that context on the way in, show it in the trail, and hand it back on the way out -- and a link that names a
// store this session cannot use must never open.
[TestClass]
public sealed class NavigationDrillThroughTests
{
    static readonly string[] AllowedStores = { "etsy|shop-a", "ozon|shop-c" };
    static DrillThroughStack Board(string storeKey = DashboardStoreFilter.AllStoresKey) =>
        new(new DrillTarget("dashboard", "Genel bakış", storeKey));

    [TestMethod]
    public void NestedDrillDownKeepsEveryStepInTheTrailWithTheCurrentPageLast()
    {
        var stack = Board();

        Assert.IsTrue(stack.Open(new DrillTarget("products", "Kritik stok", "etsy|shop-a", "anomaly", "oversell", "Aşırı satış riski"), AllowedStores).Allowed);
        Assert.IsTrue(stack.Open(new DrillTarget("product-card", "Ürün kartı", "etsy|shop-a", "product", "SKU-9", "SKU-9 • Kupa"), AllowedStores).Allowed);

        CollectionAssert.AreEqual(new[] { "Genel bakış", "Aşırı satış riski", "SKU-9 • Kupa" }, stack.Crumbs.Select(c => c.Label).ToArray());
        Assert.IsTrue(stack.Crumbs[^1].IsCurrent, "The page you are on is the last crumb, not something the trail leaves out.");
        Assert.IsFalse(stack.Crumbs[0].IsCurrent);
        StringAssert.Contains(stack.TrailText(), "SKU-9 • Kupa");
    }

    [TestMethod]
    public void BackReturnsTheWholeContextItCameFromNotJustTheRoute()
    {
        var stack = Board("etsy|shop-a");
        stack.Open(new DrillTarget("products", "Kritik stok", "etsy|shop-a", "anomaly", "oversell", "Aşırı satış riski"), AllowedStores);
        stack.Open(new DrillTarget("product-card", "Ürün kartı", "etsy|shop-a", "product", "SKU-9", "SKU-9 • Kupa"), AllowedStores);

        var back = stack.Back(_ => true);

        Assert.AreEqual("products", back.Target!.Route);
        Assert.AreEqual("oversell", back.Target.EntityId, "The card you drilled from is the context, not the bare screen.");
        Assert.AreEqual("etsy|shop-a", back.Target.StoreKey, "The board's filter has to come back with it.");
        Assert.AreEqual(2, stack.Crumbs.Count);

        var root = stack.Back(_ => true);
        Assert.AreEqual("dashboard", root.Target!.Route);
        Assert.AreEqual("etsy|shop-a", root.Target.StoreKey);

        var underflow = stack.Back(_ => true);
        Assert.AreEqual("dashboard", underflow.Target!.Route, "Back at the root stays at the root instead of emptying the trail.");
        Assert.AreEqual(1, stack.Crumbs.Count);
        Assert.IsFalse(stack.CanGoBack);
    }

    [TestMethod]
    public void AnEntityThatDisappearedWhileYouWereAwayDropsToItsScreenAndSaysSo()
    {
        var stack = Board();
        stack.Open(new DrillTarget("products", "Ürün yönetimi", DashboardStoreFilter.AllStoresKey, "anomaly", "oversell", "Aşırı satış riski"), AllowedStores);
        stack.Open(new DrillTarget("product-card", "Ürün kartı", DashboardStoreFilter.AllStoresKey, "product", "SKU-9", "SKU-9 • Kupa"), AllowedStores);

        var back = stack.Back(target => target.EntityId != "oversell");

        Assert.AreEqual("products", back.Target!.Route, "A vanished entity still leaves a screen worth returning to.");
        Assert.AreEqual("", back.Target.EntityId);
        Assert.IsTrue(back.DroppedStaleEntity);
        Assert.AreNotEqual("", back.Notice);
        Assert.AreEqual("Ürün yönetimi", stack.Crumbs[^1].Label, "The crumb loses the entity it can no longer point at.");
    }

    [TestMethod]
    public void SwitchingTheBoardsStoreDropsTheTrailThatBelongedToTheOldOne()
    {
        var stack = Board("etsy|shop-a");
        stack.Open(new DrillTarget("products", "Kritik stok", "etsy|shop-a", "anomaly", "oversell", "Aşırı satış riski"), AllowedStores);
        stack.Open(new DrillTarget("orders", "Siparişler", "etsy|shop-a", "order", "1001", "1001 numaralı sipariş"), AllowedStores);

        var current = stack.SwitchStore("ozon|shop-c");

        Assert.AreEqual("dashboard", current.Route, "A trail dug through one store's data means nothing in another's.");
        Assert.AreEqual("ozon|shop-c", current.StoreKey);
        Assert.AreEqual(1, stack.Crumbs.Count);
        Assert.IsFalse(stack.CanGoBack);

        var wide = Board("etsy|shop-a");
        wide.Open(new DrillTarget("products", "Kritik stok", "etsy|shop-a", "anomaly", "oversell", "Aşırı satış riski"), AllowedStores);
        Assert.AreEqual("products", wide.SwitchStore("etsy|shop-a").Route, "Re-selecting the store you are already on is not a switch and keeps the trail.");
        Assert.AreEqual(2, wide.Crumbs.Count);
    }

    [TestMethod]
    public void ALinkNamingAStoreThisSessionCannotUseNeverOpens()
    {
        var stack = Board();

        foreach (var forged in new[] { "etsy|shop-admin", "ETSY|SHOP-A", "etsy|shop-a ", "trendyol|shop-z" })
        {
            var attempt = stack.Open(new DrillTarget("products", "Ürünler", forged, "product", "SKU-1", "SKU-1"), AllowedStores);
            Assert.IsFalse(attempt.Allowed, $"'{forged}' is not a store this session offers, so the drill-through must not open.");
            Assert.AreNotEqual("", attempt.Notice);
        }
        Assert.AreEqual(1, stack.Crumbs.Count, "A blocked link leaves the trail exactly as it was.");

        var scoped = Board("etsy|shop-a");
        var crossStore = scoped.Open(new DrillTarget("orders", "Siparişler", "ozon|shop-c", "order", "1001", "1001"), AllowedStores);
        Assert.IsFalse(crossStore.Allowed, "While the board is filtered to one store, a link into another store's row is a wrong-store deep link.");
        Assert.IsTrue(scoped.Open(new DrillTarget("orders", "Siparişler", DashboardStoreFilter.AllStoresKey, "order", "1001", "1001"), AllowedStores).Allowed,
            "An unscoped screen is still reachable from a filtered board.");
    }

    [TestMethod]
    public void ALongTrailElidesItsMiddleInsteadOfGrowingWithoutEnd()
    {
        var stack = Board();
        foreach (var step in new[] { "a", "b", "c", "d", "e" })
            stack.Open(new DrillTarget("products", "Adım " + step, DashboardStoreFilter.AllStoresKey, "product", step, "Adım " + step), AllowedStores);

        var text = stack.TrailText();

        StringAssert.Contains(text, "Genel bakış", StringComparison.CurrentCultureIgnoreCase);
        StringAssert.Contains(text, "Adım e");
        StringAssert.Contains(text, "…");
        Assert.IsFalse(text.Contains("Adım b"), "The middle of a long trail is elided, not printed in full.");
        Assert.IsTrue(stack.Crumbs.Count > 4, "Eliding is a display decision; Back still walks every step.");
    }
}
