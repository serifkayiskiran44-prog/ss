using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #836 (DESIGN: Order workspace split-pane layout). The detail width is clamped for 1024 through 4K, stacked below
// 900 DIP, remembered as a plain DIP number (garbage reads as the default), the selection is a key that survives
// fresh instances, and a detail never opens for an order outside the filtered store.
[TestClass]
public sealed class OrderWorkspaceLayoutTests
{
    [TestMethod]
    public void TheDetailWidthIsClampedForEveryWorkspaceWidthAndTheModeFlipsBelow900()
    {
        Assert.AreEqual(OrderWorkspaceMode.SideBySide, OrderWorkspaceLayout.Mode(1024)); Assert.AreEqual(OrderWorkspaceMode.SideBySide, OrderWorkspaceLayout.Mode(3840));
        Assert.AreEqual(OrderWorkspaceMode.Stacked, OrderWorkspaceLayout.Mode(899)); Assert.AreEqual(OrderWorkspaceMode.SideBySide, OrderWorkspaceLayout.Mode(0), "Unmeasured: keep the side-by-side default.");

        Assert.AreEqual(370, OrderWorkspaceLayout.ClampDetailWidth(370, 1024));
        Assert.AreEqual(1024 * 0.45, OrderWorkspaceLayout.ClampDetailWidth(900, 1024), "Never more than its share of a 1024 window.");
        Assert.AreEqual(280, OrderWorkspaceLayout.ClampDetailWidth(100, 1024), "Never below the minimum.");
        Assert.AreEqual(1500, OrderWorkspaceLayout.ClampDetailWidth(1500, 3840), "On 4K a wide detail is allowed.");
        Assert.AreEqual(3840 * 0.45, OrderWorkspaceLayout.ClampDetailWidth(2500, 3840));
        Assert.AreEqual(280, OrderWorkspaceLayout.ClampDetailWidth(370, 700), "A 700 DIP workspace: the list keeps 480 only if the detail shrinks to its minimum.");
        Assert.AreEqual(370, OrderWorkspaceLayout.ClampDetailWidth(double.NaN, 1024), "Nonsense requested width: the default.");
        Assert.AreEqual(370, OrderWorkspaceLayout.ClampDetailWidth(370, double.NaN));

        Assert.AreEqual("412.5", OrderWorkspaceLayout.Serialize(412.5), "Invariant DIP number, whatever the culture.");
        Assert.AreEqual(412.5, OrderWorkspaceLayout.Deserialize("412.5")); Assert.AreEqual(370, OrderWorkspaceLayout.Deserialize("")); Assert.AreEqual(370, OrderWorkspaceLayout.Deserialize("abc"));
        Assert.AreEqual(370, OrderWorkspaceLayout.Deserialize("10")); Assert.AreEqual(370, OrderWorkspaceLayout.Deserialize("99999")); Assert.AreEqual(370, OrderWorkspaceLayout.Deserialize("NaN"));
    }

    [TestMethod]
    public void TheSelectionSurvivesFreshInstancesAndADetailNeverOpensOutsideTheFilteredStore()
    {
        var a = new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1" };
        var key = OrderWorkspaceLayout.SelectionKey(a);
        var reloaded = new List<OrderSnapshot> { new() { Marketplace = "etsy", ShopId = "S2", OrderId = "o-1" }, new() { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1" } };
        Assert.AreSame(reloaded[1], OrderWorkspaceLayout.Restore(reloaded, key), "Marketplace, shop and id together -- the same id in another shop is another order.");
        Assert.IsNull(OrderWorkspaceLayout.Restore(new[] { reloaded[0] }, key)); Assert.IsNull(OrderWorkspaceLayout.Restore(reloaded, null));

        Assert.IsTrue(OrderWorkspaceLayout.CanOpenDetail(a, "Tümü", "Tümü")); Assert.IsTrue(OrderWorkspaceLayout.CanOpenDetail(a, null, null));
        Assert.IsTrue(OrderWorkspaceLayout.CanOpenDetail(a, "Etsy", "S1"), "The marketplace matches case-insensitively, the shop exactly.");
        Assert.IsFalse(OrderWorkspaceLayout.CanOpenDetail(a, "Tümü", "S2"), "A shop filter on another store: the detail stays closed.");
        Assert.IsFalse(OrderWorkspaceLayout.CanOpenDetail(a, "trendyol", "Tümü"));
    }
}
