using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class OrderCardWorkspaceTests
{
    [TestMethod]
    public void NewManualOrderGetsSafeDefaults()
    {
        var order = OrderCardRules.NewManual();

        Assert.AreEqual("Manuel", order.Marketplace);
        Assert.AreEqual("default", order.ShopId);
        Assert.AreEqual("TRY", order.Currency);
        Assert.AreEqual("Yeni Sipariş", order.RawStatus);
        Assert.AreEqual("Yerel / manuel", order.Source);
    }

    [TestMethod]
    public void ManualOrderRequiresAtLeastOnePricedProduct()
    {
        var order = OrderCardRules.NewManual();
        order.OrderId = "MAN-1";
        var error = Assert.ThrowsException<ArgumentException>(() => OrderCardRules.ValidateManual(order));
        StringAssert.Contains(error.Message, "ürün");
    }

    [TestMethod]
    public void ManualOrderCalculatesAndStoresTotalsBeforeSave()
    {
        var order = OrderCardRules.NewManual();order.OrderId="MAN-1";
        order.Items.Add(new OrderItem { Sku="SKU-1", Title="Ürün", Quantity=2, UnitPrice=120m, VatRate=20m });

        OrderCardRules.ValidateManual(order);

        Assert.AreEqual(200m,order.NetTotal);
        Assert.AreEqual(40m,order.VatTotal);
        Assert.AreEqual(240m,order.Total);
    }

    [TestMethod]
    public void FinancialSummaryShowsVatAndSourceTotals()
    {
        var order = new OrderSnapshot
        {
            Currency = "TRY", NetTotal = 83.25m, VatTotal = 16.65m,
            DiscountTotal = 5m, CommissionTotal = 7.50m, Total = 99.90m
        };

        var text = OrderCardRules.FinancialSummary(order);

        StringAssert.Contains(text, "KDV: 16,65 TRY");
        StringAssert.Contains(text, "Genel toplam: 99,90 TRY");
    }

    [DataTestMethod]
    [DataRow("Cancelled", true, false, "İptal edildi")]
    [DataRow("UnSupplied", true, false, "Tedarik edilemedi / iptal")]
    [DataRow("Returned", false, true, "İade edildi")]
    [DataRow("UnPacked", false, true, "İade sürecinde")]
    public void MarketplaceStatusControlsCancelledAndReturnPresentation(string raw,bool cancelled,bool returned,string label)
    {
        var order = new OrderSnapshot { RawStatus = raw };

        Assert.AreEqual(cancelled, order.IsCancelled);
        Assert.AreEqual(returned, order.IsReturn);
        Assert.AreEqual(label, order.StatusLabel);
    }
}
