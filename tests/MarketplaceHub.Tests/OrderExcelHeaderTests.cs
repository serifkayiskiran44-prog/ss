using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class OrderExcelHeaderTests
{
    [TestMethod]
    public void MojibakeTurkishHeadersStillMatchKnownOrderFields()
    {
        Assert.IsTrue(OrderExcelHeaders.Matches("Sipari� Numaras�", "Sipariş Numarası"));
        Assert.IsTrue(OrderExcelHeaders.Matches("M�şteri Sevk İsmi", "Müşteri Sevk İsmi"));
        Assert.IsTrue(OrderExcelHeaders.Matches("Kdv Tutar�", "KDV Tutarı"));
    }

    [TestMethod]
    public void ImportCatalogContainsFinancialShipmentAndProductFields()
    {
        var keys = ExcelOrderImport.Fields.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in new[] { "PlatformReference", "OrderDate", "VatTotal", "DiscountTotal", "CommissionTotal", "ShippingPhone", "Barcode", "NetUnitPrice", "MarketplaceProductCode" })
            Assert.IsTrue(keys.Contains(key), key + " alanı eksik.");
    }

    [TestMethod]
    public void SuggestsEntegraColumnsByHeader()
    {
        var columns = new[]
        {
            new TrMarketplaceHubDesktop.Catalog.ExcelSheetColumn(1,"A","Sipari� Numaras�","4176405766"),
            new TrMarketplaceHubDesktop.Catalog.ExcelSheetColumn(2,"B","Kdv Tutar�","16,65"),
            new TrMarketplaceHubDesktop.Catalog.ExcelSheetColumn(3,"C","�r�n Kodu","SKU-1")
        };

        Assert.AreEqual("A", OrderExcelHeaders.Suggest("OrderId", columns));
        Assert.AreEqual("B", OrderExcelHeaders.Suggest("VatTotal", columns));
        Assert.AreEqual("C", OrderExcelHeaders.Suggest("Sku", columns));
    }
}
