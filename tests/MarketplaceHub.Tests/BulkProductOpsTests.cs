using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1931 (selection vs filter scope kept as explicit state) and
/// #1932 (status-change preview with before/after values, revision guard, cancel).
[TestClass]
public sealed class BulkProductOpsTests
{
    static (CatalogStore Catalog, BulkProductOperations Ops, string Root) NewStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "bulk-ops-" + Guid.NewGuid().ToString("N"));
        var catalog = new CatalogStore(root);
        return (catalog, new BulkProductOperations(catalog, new ChannelProductsStore(root)), root);
    }

    static CatalogProduct Seed(CatalogStore catalog, string sku, bool active = true)
    {
        var source = new XmlSource { Id = "src", Location = "https://example.test/feed.xml" };
        catalog.SaveSource(source);
        catalog.Import(source, [new CatalogProduct { SourceId = "src", Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "USD", Active = active }]);
        return catalog.Products().Single(p => p.Sku == sku);
    }

    [TestMethod]
    public void SingleSelectedProductUsesSelectedScope()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var a = Seed(catalog, "SKU-A"); Seed(catalog, "SKU-B");
            var preview = ops.Preview([a], new BulkProductOperationRequest(BulkProductOperationKind.Deactivate), BulkSelectionScope.Selected);
            Assert.AreEqual(BulkSelectionScope.Selected, preview.Scope);
            Assert.AreEqual(1, preview.Lines.Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleSelectedProductsAreAllIncludedInSelectedScope()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var a = Seed(catalog, "SKU-A"); var b = Seed(catalog, "SKU-B"); Seed(catalog, "SKU-C");
            var preview = ops.Preview([a, b], new BulkProductOperationRequest(BulkProductOperationKind.Deactivate), BulkSelectionScope.Selected);
            Assert.AreEqual(2, preview.Lines.Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NoSelectionFallsBackToFilteredAllScope()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            Seed(catalog, "SKU-A"); Seed(catalog, "SKU-B");
            var allProducts = catalog.Products();
            var preview = ops.Preview(allProducts, new BulkProductOperationRequest(BulkProductOperationKind.Deactivate), BulkSelectionScope.FilteredAll);
            Assert.AreEqual(BulkSelectionScope.FilteredAll, preview.Scope);
            Assert.AreEqual(2, preview.Lines.Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FilterChangeProducesADifferentScopedPreview()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            Seed(catalog, "ALPHA-1"); Seed(catalog, "BETA-1");
            var narrow = catalog.Products("ALPHA");
            var preview1 = ops.Preview(narrow, new BulkProductOperationRequest(BulkProductOperationKind.Deactivate), BulkSelectionScope.FilteredAll);
            Assert.AreEqual(1, preview1.Lines.Count);

            var broad = catalog.Products();
            var preview2 = ops.Preview(broad, new BulkProductOperationRequest(BulkProductOperationKind.Deactivate), BulkSelectionScope.FilteredAll);
            Assert.AreEqual(2, preview2.Lines.Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MixedActiveAndInactiveProductsShowCorrectPerRowBeforeAfterAndStatus()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var active = Seed(catalog, "SKU-ACTIVE", active: true);
            var inactive = Seed(catalog, "SKU-INACTIVE", active: false);
            var preview = ops.Preview([active, inactive], new BulkProductOperationRequest(BulkProductOperationKind.Deactivate), BulkSelectionScope.Selected);

            var activeRow = preview.Lines.Single(l => l.Sku == "SKU-ACTIVE");
            var inactiveRow = preview.Lines.Single(l => l.Sku == "SKU-INACTIVE");
            Assert.AreEqual("Aktif", activeRow.Before); Assert.AreEqual("Pasif", activeRow.After); Assert.AreEqual("READY", activeRow.Status);
            Assert.AreEqual("Pasif", inactiveRow.Before); Assert.AreEqual("Pasif", inactiveRow.After); Assert.AreEqual("SKIP", inactiveRow.Status, "Already-inactive product must be SKIP, not re-applied.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StaleProductChangedAfterPreviewCancelsTheWholeBatch()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var a = Seed(catalog, "SKU-A"); var b = Seed(catalog, "SKU-B");
            var preview = ops.Preview([a, b], new BulkProductOperationRequest(BulkProductOperationKind.Deactivate), BulkSelectionScope.Selected);

            var live = catalog.Products().Single(p => p.Sku == "SKU-A"); live.Name = "changed"; catalog.SaveProduct(live);

            Assert.ThrowsException<InvalidOperationException>(() => ops.Apply(preview, true));
            Assert.IsTrue(catalog.Products().Single(p => p.Sku == "SKU-B").Active, "A stale row anywhere in the batch must cancel the whole atomic apply, including unrelated rows.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CancellationBeforeCommitAppliesNothing()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var a = Seed(catalog, "SKU-A");
            var preview = ops.Preview([a], new BulkProductOperationRequest(BulkProductOperationKind.Deactivate), BulkSelectionScope.Selected);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            Assert.ThrowsException<OperationCanceledException>(() => ops.Apply(preview, true, cts.Token));
            Assert.IsTrue(catalog.Products().Single().Active, "Cancelled apply must not deactivate anything.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NotApprovedNeverApplies()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var a = Seed(catalog, "SKU-A");
            var preview = ops.Preview([a], new BulkProductOperationRequest(BulkProductOperationKind.Deactivate), BulkSelectionScope.Selected);
            Assert.ThrowsException<InvalidOperationException>(() => ops.Apply(preview, false));
            Assert.IsTrue(catalog.Products().Single().Active);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPersistsAppliedStatusChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "bulk-ops-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new CatalogStore(root);
            var ops = new BulkProductOperations(catalog, new ChannelProductsStore(root));
            var a = Seed(catalog, "SKU-A");
            var preview = ops.Preview([a], new BulkProductOperationRequest(BulkProductOperationKind.Deactivate), BulkSelectionScope.Selected);
            var result = ops.Apply(preview, true);
            Assert.AreEqual(1, result.Applied);

            var reopened = new CatalogStore(root);
            Assert.IsFalse(reopened.Products().Single().Active);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
