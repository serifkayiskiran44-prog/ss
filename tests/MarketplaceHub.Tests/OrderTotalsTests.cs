using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class OrderTotalsTests
{
    [TestMethod]
    public void CalculatesNetVatAndGrossFromGrossUnitPrices()
    {
        var order = new OrderSnapshot
        {
            Marketplace = "Trendyol", ShopId = "shop", OrderId = "4176405766",
            Items = [new OrderItem { Title = "Mama", Sku = "SKU-1", Quantity = 1, UnitPrice = 99.90m, VatRate = 20m }]
        };

        var totals = OrderTotalsCalculator.Calculate(order);

        Assert.AreEqual(83.25m, totals.Net);
        Assert.AreEqual(16.65m, totals.Vat);
        Assert.AreEqual(99.90m, totals.Gross);
    }

    [TestMethod]
    public void UsesExplicitNetAndGrossWithoutSilentlyDiscardingMismatch()
    {
        var order = new OrderSnapshot
        {
            Marketplace = "Trendyol", ShopId = "shop", OrderId = "A1",
            NetTotal = 80m, VatTotal = 16m, Total = 90m,
            Items = [new OrderItem { Title = "Ürün", Sku = "SKU", Quantity = 1, UnitPrice = 96m, VatRate = 20m }]
        };

        var totals = OrderTotalsCalculator.Calculate(order);

        Assert.AreEqual(80m, totals.Net);
        Assert.AreEqual(16m, totals.Vat);
        Assert.AreEqual(90m, totals.Gross);
        Assert.AreEqual(-6m, totals.SourceDifference);
        Assert.IsTrue(totals.HasMismatch);
    }

    [TestMethod]
    public void DiscountsAndCommissionRemainVisibleOperationalAmounts()
    {
        var order = new OrderSnapshot
        {
            Marketplace = "Trendyol", ShopId = "shop", OrderId = "A2",
            DiscountTotal = 15.84m, CommissionTotal = 12.50m,
            Items = [new OrderItem { Title = "Ürün", Sku = "SKU", Quantity = 2, UnitPrice = 120m, VatRate = 20m }]
        };

        var totals = OrderTotalsCalculator.Calculate(order);

        Assert.AreEqual(200m, totals.Net);
        Assert.AreEqual(40m, totals.Vat);
        Assert.AreEqual(240m, totals.Gross);
        Assert.AreEqual(15.84m, totals.Discount);
        Assert.AreEqual(12.50m, totals.Commission);
    }
}
