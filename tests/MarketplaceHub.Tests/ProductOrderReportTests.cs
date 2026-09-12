using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TrMarketplaceHubDesktop.Tests;

[TestClass]
public sealed class ProductOrderReportTests
{
    [TestMethod]
    public void BuildUsesOnlyMatchingSkuAndShowsQuantityAndLatestOrder()
    {
        var orders = new[]
        {
            new OrderSnapshot { Marketplace = "etsy", ShopId = "one", OrderId = "old", UpdatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), Items = [new() { Sku = "SKU-A", Title = "A", Quantity = 2 }] },
            new OrderSnapshot { Marketplace = "etsy", ShopId = "one", OrderId = "new", UpdatedAt = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), Items = [new() { Sku = "SKU-A", Title = "A", Quantity = 3 }, new() { Sku = "SKU-B", Title = "B", Quantity = 9 }] }
        };

        var report = ProductOrderReport.Build("SKU-A", orders);

        Assert.AreEqual(2, report.OrderCount);
        Assert.AreEqual(5, report.Quantity);
        Assert.AreEqual("new", report.LatestOrderId);
        Assert.AreEqual("etsy / one", report.LatestChannelShop);
    }

    [TestMethod]
    public void BuildReturnsEmptyForBlankSkuOrNoMatchingOrders()
    {
        var report = ProductOrderReport.Build("", [new OrderSnapshot { Marketplace = "etsy", ShopId = "one", OrderId = "x", Items = [new() { Sku = "SKU-A", Title = "A", Quantity = 1 }] }]);

        Assert.AreEqual(0, report.OrderCount);
        Assert.AreEqual(0, report.Quantity);
        Assert.AreEqual("—", report.LatestOrderId);
    }
}
