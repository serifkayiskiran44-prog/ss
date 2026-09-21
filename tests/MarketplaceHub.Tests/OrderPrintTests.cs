using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class OrderPrintTests
{
    [TestMethod]
    public void Code128EncoderAddsStartChecksumAndStop()
    {
        var bars=Code128B.Encode("ABC-123");
        Assert.IsTrue(bars.Count>20);
        Assert.AreEqual(2,bars[^1]);
        Assert.ThrowsException<ArgumentException>(()=>Code128B.Encode("şifre"));
    }

    [TestMethod]
    public void PrintDocumentUsesOrderAndShipmentValues()
    {
        var order=new OrderSnapshot{Marketplace="Trendyol",ShopId="42",OrderId="100",ShippingName="Ali Veli",ShippingAddress="Adres",Items=[new(){Title="Ürün",Sku="SKU-1",Quantity=1}],Shipments=[new(){Id="99",TrackingNumber="7330001",Carrier="Kargo"}]};
        var document=OrderPrintComposer.Compose(order,OrderPrintKind.CargoLabel);
        StringAssert.Contains(document.PlainText,"7330001");
        StringAssert.Contains(document.PlainText,"Ali Veli");
    }
}
