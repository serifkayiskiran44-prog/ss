using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1971 (order date-range filter, explicit UTC boundary
/// semantics) and #1972 (marketplace/shop multi-select filter).
[TestClass]
public sealed class OrderFilterTests
{
    static OrderSnapshot Order(string marketplace, string shop, string orderId, DateTimeOffset updatedAt) =>
        new() { Marketplace = marketplace, ShopId = shop, OrderId = orderId, UpdatedAt = updatedAt, StockDecisionLabel = "Stok bekliyor" };

    static readonly string[] None = Array.Empty<string>();

    [TestMethod]
    public void NoDateFilterMatchesEverything()
    {
        var order = Order("Etsy", "Shop1", "A1", DateTimeOffset.UtcNow.AddYears(-5));
        Assert.IsTrue(OrderFilterCriteria.Matches(order, "", null, None, None, "Tümü", null, null));
    }

    [TestMethod]
    public void TodayRangeIncludesOrderUpdatedTodayAndExcludesYesterday()
    {
        var today = DateTimeOffset.UtcNow.Date;
        var fromUtc = new DateTimeOffset(today, TimeSpan.Zero);
        var toUtc = new DateTimeOffset(today.AddDays(1).AddTicks(-1), TimeSpan.Zero);

        var todayOrder = Order("Etsy", "Shop1", "A1", today.AddHours(10));
        var yesterdayOrder = Order("Etsy", "Shop1", "A2", today.AddDays(-1).AddHours(10));

        Assert.IsTrue(OrderFilterCriteria.Matches(todayOrder, "", null, None, None, "Tümü", fromUtc, toUtc));
        Assert.IsFalse(OrderFilterCriteria.Matches(yesterdayOrder, "", null, None, None, "Tümü", fromUtc, toUtc));
    }

    [TestMethod]
    public void CustomRangeIsInclusiveOfBothEndpoints()
    {
        var from = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 1, 20, 23, 59, 59, TimeSpan.Zero);

        var atStart = Order("Etsy", "Shop1", "A1", from);
        var atEnd = Order("Etsy", "Shop1", "A2", to);
        var beforeStart = Order("Etsy", "Shop1", "A3", from.AddTicks(-1));
        var afterEnd = Order("Etsy", "Shop1", "A4", to.AddTicks(1));

        Assert.IsTrue(OrderFilterCriteria.Matches(atStart, "", null, None, None, "Tümü", from, to));
        Assert.IsTrue(OrderFilterCriteria.Matches(atEnd, "", null, None, None, "Tümü", from, to));
        Assert.IsFalse(OrderFilterCriteria.Matches(beforeStart, "", null, None, None, "Tümü", from, to));
        Assert.IsFalse(OrderFilterCriteria.Matches(afterEnd, "", null, None, None, "Tümü", from, to));
    }

    [TestMethod]
    public void OnlyFromBoundarySetExcludesEarlierOrders()
    {
        var from = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var earlier = Order("Etsy", "Shop1", "A1", from.AddDays(-1));
        var later = Order("Etsy", "Shop1", "A2", from.AddDays(1));
        Assert.IsFalse(OrderFilterCriteria.Matches(earlier, "", null, None, None, "Tümü", from, null));
        Assert.IsTrue(OrderFilterCriteria.Matches(later, "", null, None, None, "Tümü", from, null));
    }

    [TestMethod]
    public void OnlyToBoundarySetExcludesLaterOrders()
    {
        var to = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var earlier = Order("Etsy", "Shop1", "A1", to.AddDays(-1));
        var later = Order("Etsy", "Shop1", "A2", to.AddDays(1));
        Assert.IsTrue(OrderFilterCriteria.Matches(earlier, "", null, None, None, "Tümü", null, to));
        Assert.IsFalse(OrderFilterCriteria.Matches(later, "", null, None, None, "Tümü", null, to));
    }

    [TestMethod]
    public void EmptyMarketplaceAndShopSelectionMeansAllAreIncluded()
    {
        var order = Order("Etsy", "Shop1", "A1", DateTimeOffset.UtcNow);
        Assert.IsTrue(OrderFilterCriteria.Matches(order, "", null, None, None, "Tümü", null, null));
    }

    [TestMethod]
    public void TwoShopsSameOrderIdMultiSourceFilterKeepsBothDistinct()
    {
        var now = DateTimeOffset.UtcNow;
        var shopA = Order("Etsy", "ShopA", "ORDER-1", now);
        var shopB = Order("Etsy", "ShopB", "ORDER-1", now);

        // Selecting only ShopA must keep the ShopA row and drop the ShopB row,
        // even though both share the same marketplace and OrderId - shop identity
        // alone must disambiguate, proving no accidental de-dup by OrderId.
        var onlyShopA = new[] { "ShopA" };
        Assert.IsTrue(OrderFilterCriteria.Matches(shopA, "", null, None, onlyShopA, "Tümü", null, null));
        Assert.IsFalse(OrderFilterCriteria.Matches(shopB, "", null, None, onlyShopA, "Tümü", null, null));

        // Selecting both shops keeps both distinct rows.
        var bothShops = new[] { "ShopA", "ShopB" };
        Assert.IsTrue(OrderFilterCriteria.Matches(shopA, "", null, None, bothShops, "Tümü", null, null));
        Assert.IsTrue(OrderFilterCriteria.Matches(shopB, "", null, None, bothShops, "Tümü", null, null));
    }

    [TestMethod]
    public void MarketplaceFilterIsCaseInsensitiveAndUnmatchedSelectionYieldsEmptyResult()
    {
        var order = Order("Etsy", "Shop1", "A1", DateTimeOffset.UtcNow);
        Assert.IsTrue(OrderFilterCriteria.Matches(order, "", null, new[] { "etsy" }, None, "Tümü", null, null));
        Assert.IsFalse(OrderFilterCriteria.Matches(order, "", null, new[] { "Trendyol" }, None, "Tümü", null, null));
    }

    [TestMethod]
    public void MultiSelectMarketplaceMatchesAnySelectedValue()
    {
        var etsyOrder = Order("Etsy", "Shop1", "A1", DateTimeOffset.UtcNow);
        var trendyolOrder = Order("Trendyol", "Shop2", "A2", DateTimeOffset.UtcNow);
        var ozonOrder = Order("Ozon", "Shop3", "A3", DateTimeOffset.UtcNow);
        var selected = new[] { "Etsy", "Trendyol" };

        Assert.IsTrue(OrderFilterCriteria.Matches(etsyOrder, "", null, selected, None, "Tümü", null, null));
        Assert.IsTrue(OrderFilterCriteria.Matches(trendyolOrder, "", null, selected, None, "Tümü", null, null));
        Assert.IsFalse(OrderFilterCriteria.Matches(ozonOrder, "", null, selected, None, "Tümü", null, null));
    }

    [TestMethod]
    public void CombinedShopAndDateFilterBothMustMatch()
    {
        var from = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 5, 31, 23, 59, 59, TimeSpan.Zero);
        var rightShopRightDate = Order("Etsy", "ShopA", "A1", new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero));
        var rightShopWrongDate = Order("Etsy", "ShopA", "A2", new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var wrongShopRightDate = Order("Etsy", "ShopB", "A3", new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero));
        var shops = new[] { "ShopA" };

        Assert.IsTrue(OrderFilterCriteria.Matches(rightShopRightDate, "", null, None, shops, "Tümü", from, to));
        Assert.IsFalse(OrderFilterCriteria.Matches(rightShopWrongDate, "", null, None, shops, "Tümü", from, to));
        Assert.IsFalse(OrderFilterCriteria.Matches(wrongShopRightDate, "", null, None, shops, "Tümü", from, to));
    }
}
