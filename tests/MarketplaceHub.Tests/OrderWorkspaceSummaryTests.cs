using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class OrderWorkspaceSummaryTests
{
    [TestMethod]
    public void SummarySeparatesPreparationTransitReturnAndExceptionQueues()
    {
        var orders = new[]
        {
            Order("Preparing"),
            Order("InTransit"),
            Order("Returned"),
            Order("Exception"),
            new OrderSnapshot { Marketplace = "Etsy", ShopId = "shop", OrderId = "no-package" }
        };

        var result = OrderWorkspaceSummary.From(orders);

        Assert.AreEqual(5, result.Total);
        Assert.AreEqual(2, result.Preparation);
        Assert.AreEqual(1, result.InTransit);
        Assert.AreEqual(1, result.Returns);
        Assert.AreEqual(1, result.Exceptions);
    }

    static OrderSnapshot Order(string state) => new()
    {
        Marketplace = "Etsy", ShopId = "shop", OrderId = state,
        Shipments = [new OrderShipment { Id = state, State = state }]
    };
}
