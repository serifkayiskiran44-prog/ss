using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public class ProductCardBulkTests
{
    [TestMethod]
    public void BulkLockProtectsNameAndMetadataKeepsProductIdentity()
    {
        var dir = Path.Combine(Path.GetTempPath(), "card-bulk-" + Guid.NewGuid().ToString("N")); var store = new CatalogStore(dir);
        var product = store.CreateManual(new() { Sku = "A", Name = "Food" }); var operations = new BulkProductOperations(store, new ChannelProductsStore(dir));
        operations.Apply(operations.Preview([product], new(BulkProductOperationKind.SetNameLock, "true"), BulkSelectionScope.Selected), true);
        var locked = store.Products().Single(); Assert.IsTrue(locked.LockName);
        var blocked = operations.Preview([locked], new(BulkProductOperationKind.SetName, "Changed")); Assert.AreEqual("SKIP", blocked.Lines.Single().Status);
        operations.Apply(operations.Preview([locked], new(BulkProductOperationKind.SetShelf, "R-21")), true);
        var after = store.Products().Single(); Assert.AreEqual("R-21", after.Shelf); Assert.AreEqual(product.Id, after.Id); Assert.AreEqual(product.LocalNumber, after.LocalNumber);
    }

    [TestMethod]
    public void InvalidLockValueAndOversizedMetadataAreRejectedBeforePreview()
    {
        var dir = Path.Combine(Path.GetTempPath(), "card-bulk-" + Guid.NewGuid().ToString("N")); var ops = new BulkProductOperations(new CatalogStore(dir), new ChannelProductsStore(dir));
        Assert.ThrowsException<ArgumentException>(() => ops.Preview([], new(BulkProductOperationKind.SetStockLock, "yes")));
        Assert.ThrowsException<ArgumentException>(() => ops.Preview([], new(BulkProductOperationKind.SetShelf, new string('A', 101))));
    }
}
