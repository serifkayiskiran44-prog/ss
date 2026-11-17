using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class XmlDetailUpdateKeepsManualFieldsTests
{
    [TestMethod]
    public void DetailRefreshDoesNotBlankManuallyMaintainedFieldsTheFeedDoesNotCarry()
    {
        var root = Path.Combine(Path.GetTempPath(), "xml-detail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new CatalogStore(root);
            var source = new XmlSource { Id = "src", Location = "https://example.test/f.xml", UpdateDetails = true };
            catalog.Import(source, [new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Ürün", Price = 10, Currency = "TRY", Brand = "Eski", Category = "Kedi" }]);
            var product = catalog.Products().Single();
            product.Shelf = "A-12"; product.InvoiceName = "Fatura adı"; product.Subtitle = "Alt başlık"; product.ExpiresOn = new DateTime(2027, 1, 1);
            product.XmlAttributes["Desi"] = "3";
            catalog.SaveProduct(product);

            catalog.Import(source, [new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Ürün", Price = 10, Currency = "TRY", Brand = "Yeni", Category = "Kedi" }]);

            var after = catalog.Products().Single();
            Assert.AreEqual("Yeni", after.Brand, "Fields the feed does carry are still refreshed.");
            Assert.AreEqual("A-12", after.Shelf);
            Assert.AreEqual("Fatura adı", after.InvoiceName);
            Assert.AreEqual("Alt başlık", after.Subtitle);
            Assert.AreEqual(new DateTime(2027, 1, 1), after.ExpiresOn);
            Assert.AreEqual("3", after.XmlAttributes["Desi"]);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
