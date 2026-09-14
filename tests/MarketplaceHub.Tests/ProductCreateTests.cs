using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1851 (transactional manual create entry point) and #1852
/// (SKU validation/normalize preview without destructive auto-change).
[TestClass]
public sealed class ProductCreateTests
{
    static CatalogStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "product-create-" + Guid.NewGuid().ToString("N"));
        return new CatalogStore(root);
    }

    [TestMethod]
    public void ValidCreateAssignsIdAndManualProvenance()
    {
        var store = NewStore(out var root);
        try
        {
            var created = store.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Urun", Price = 10, Currency = "USD" });
            Assert.IsFalse(string.IsNullOrWhiteSpace(created.Id));
            Assert.AreEqual("manual", created.SourceKind);
            Assert.AreEqual("manual", created.PriceSource);
            Assert.AreEqual("manual", created.StockSource);
            Assert.AreEqual("", created.SourceId);
            Assert.AreEqual(1, store.Products().Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DuplicateExactIdentityIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Urun 1", Price = 10, Currency = "USD" });
            Assert.ThrowsException<InvalidOperationException>(() => store.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Urun 2", Price = 20, Currency = "USD" }));
            Assert.AreEqual(1, store.Products().Count, "Rejected create must not partially insert.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CaseAndWhitespaceCollisionIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(new CatalogProduct { Sku = "abc-123", Name = "Urun 1", Price = 10, Currency = "USD" });
            Assert.ThrowsException<InvalidOperationException>(() => store.CreateManual(new CatalogProduct { Sku = "  ABC-123  ", Name = "Urun 2", Price = 20, Currency = "USD" }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TurkishCharacterCaseCollisionIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            var sku1 = "\u015eEK-1"; // uppercase S-cedilla
            var sku2 = "\u015fek-1"; // lowercase s-cedilla, same letters otherwise
            store.CreateManual(new CatalogProduct { Sku = sku1, Name = "Urun 1", Price = 10, Currency = "USD" });
            Assert.ThrowsException<InvalidOperationException>(() => store.CreateManual(new CatalogProduct { Sku = sku2, Name = "Urun 2", Price = 20, Currency = "USD" }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LeadingZerosArePreservedNotNumericallyNormalized()
    {
        var store = NewStore(out var root);
        try
        {
            var created = store.CreateManual(new CatalogProduct { Sku = "007", Name = "Urun", Price = 10, Currency = "USD" });
            Assert.AreEqual("007", created.Sku);
            Assert.AreEqual("007", store.Products().Single().Sku);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WhitespaceOnlySkuIsTrimmedAndRequiresBarcodeOrSku()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() => store.CreateManual(new CatalogProduct { Sku = "   ", Barcode = "", Name = "Urun", Price = 10, Currency = "USD" }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ControlCharacterInSkuIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            var skuWithControlChar = "SKU-1" + (char)7;
            Assert.ThrowsException<InvalidOperationException>(() => store.CreateManual(new CatalogProduct { Sku = skuWithControlChar, Name = "Urun", Price = 10, Currency = "USD" }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PreviewIdentityCollisionReportsMatchWithoutCreating()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Urun 1", Price = 10, Currency = "USD" });
            var collision = store.PreviewIdentityCollision(" sku-1 ", "");
            Assert.IsNotNull(collision);
            Assert.AreEqual("Sku", collision!.Field);
            Assert.AreEqual(1, store.Products().Count, "Preview must never write anything.");

            Assert.IsNull(store.PreviewIdentityCollision("SKU-2", ""));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPersistsManuallyCreatedProduct()
    {
        var root = Path.Combine(Path.GetTempPath(), "product-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            new CatalogStore(root).CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Urun", Price = 10, Currency = "USD" });
            var reopened = new CatalogStore(root);
            Assert.AreEqual(1, reopened.Products().Count);
            Assert.AreEqual("manual", reopened.Products().Single().SourceKind);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InvalidInputBoundsAreRejected()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() => store.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "", Price = 10, Currency = "USD" }));
            Assert.ThrowsException<InvalidOperationException>(() => store.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Urun", Price = -1, Currency = "USD" }));
            Assert.ThrowsException<InvalidOperationException>(() => store.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Urun", Price = 10, Currency = "US" }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
