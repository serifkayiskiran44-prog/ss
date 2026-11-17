using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class TrendyolCancelPreviewTests
{
    static OrderSnapshot Order(string status = "Created", int quantity = 2) => new()
    {
        Marketplace = "Trendyol", ShopId = "42", OrderId = "ORD-1", RawStatus = status,
        SourceUpdatedAt = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero),
        Shipments = [new OrderShipment { Id = "9001" }],
        Items = [new OrderItem { MarketplaceLineId = 11, Quantity = quantity, Title = "Mama" }]
    };

    [TestMethod]
    public void PreviewListsExactLinesAndConfirmsOnlyAgainstTheSameOrderState()
    {
        var preview = TrendyolCancelPreview.Build(Order(), 500);

        Assert.AreEqual(9001, preview.PackageId);
        Assert.AreEqual(1, preview.Lines.Count);
        StringAssert.Contains(preview.Summary, "Mama");
        StringAssert.Contains(preview.Summary, "2");
        preview.RequireUnchanged(Order());
    }

    [TestMethod]
    public void ConfirmIsRefusedWhenTheOrderChangedSincePreview()
    {
        var preview = TrendyolCancelPreview.Build(Order(), 500);

        Assert.ThrowsException<InvalidOperationException>(() => preview.RequireUnchanged(Order(quantity: 1)));
        Assert.ThrowsException<InvalidOperationException>(() => preview.RequireUnchanged(Order(status: "Shipped")));
    }

    [TestMethod]
    public void AlreadyCancelledOrdersCannotBePreviewed()
    {
        Assert.ThrowsException<InvalidOperationException>(() => TrendyolCancelPreview.Build(Order(status: "Cancelled"), 500));
    }
}
