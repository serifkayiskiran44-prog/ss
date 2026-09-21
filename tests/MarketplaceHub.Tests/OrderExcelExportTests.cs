using ClosedXML.Excel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class OrderExcelExportTests
{
    [TestMethod]
    public void DetailedExportWritesOneRowPerProductWithVatAndCustomerFields()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".xlsx");
        try
        {
            var order=new OrderSnapshot{Marketplace="Trendyol",ShopId="shop",OrderId="A1",CustomerName="Müşteri",VatTotal=20m,Total=120m,Currency="TRY",InvoiceNumber="EAR2026000000001",InvoiceUrl="https://example.test/invoice",BillingCountry="Türkiye",ShippingCountry="Türkiye",Items=[new OrderItem{Sku="SKU-1",Title="Ürün",Quantity=1,UnitPrice=120m,VatRate=20m,PurchasePrice=70m,DiscountAmount=3m,CommissionAmount=5m}]};

            OrderExcelExport.WriteDetailed(path,[order]);

            using var book=new XLWorkbook(path);var sheet=book.Worksheet("Siparişler");
            Assert.AreEqual("Sipariş Numarası",sheet.Cell(1,3).GetString());
            Assert.AreEqual("A1",sheet.Cell(2,3).GetString());
            Assert.AreEqual("Müşteri",sheet.Cell(2,7).GetString());
            Assert.AreEqual("SKU-1",sheet.Cell(2,31).GetString());
            Assert.AreEqual(20m,sheet.Cell(2,26).GetValue<decimal>());
            var headers=sheet.Row(1).CellsUsed().ToDictionary(c=>c.GetString(),c=>c.Address.ColumnNumber);
            Assert.AreEqual("Türkiye",sheet.Cell(2,headers["Fatura Ülkesi"]).GetString());
            Assert.AreEqual("https://example.test/invoice",sheet.Cell(2,headers["Fatura URL"]).GetString());
            Assert.AreEqual(70m,sheet.Cell(2,headers["Alış Fiyatı"]).GetValue<decimal>());
            Assert.AreEqual(3m,sheet.Cell(2,headers["Ürün İndirimi"]).GetValue<decimal>());
            Assert.AreEqual(5m,sheet.Cell(2,headers["Ürün Komisyonu"]).GetValue<decimal>());
        }
        finally{if(File.Exists(path))File.Delete(path);}
    }
}
