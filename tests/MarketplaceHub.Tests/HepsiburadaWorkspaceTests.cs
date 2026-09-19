using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Hepsiburada;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class HepsiburadaWorkspaceTests
{
    static CatalogProduct Product(string id, string sku, string barcode) => new() { Id = id, Sku = sku, Barcode = barcode, Name = id };
    static HepsiburadaMerchantProduct Remote(string id, string sku, string barcode, string merchant = "shop") => new(merchant, sku, barcode, id);

    [TestMethod]
    public void BarcodeWinsAndSkuCannotOverrideAContradictoryBarcode()
    {
        var local = Product("p1", "SKU-1", "8691");
        var rows = new[] { Remote("r1", "OTHER", "8691"), Remote("r2", "SKU-1", "8692") };

        var match = HepsiburadaMatching.Suggest(local, rows);

        Assert.AreEqual(ProductChannelMatchOutcome.Matched, match.Outcome);
        Assert.AreEqual("r1", match.RemoteId);
        Assert.AreEqual("Barkod", match.Method);
    }

    [TestMethod]
    public void DuplicateRemoteBarcodeRequiresReview()
    {
        var match = HepsiburadaMatching.Suggest(Product("p1", "SKU", "8691"),
            [Remote("r1", "A", "8691"), Remote("r2", "B", "8691")]);
        Assert.AreEqual(ProductChannelMatchOutcome.Conflict, match.Outcome);
    }

    [TestMethod]
    public void SkuWithContradictoryBarcodeRequiresReviewAndBlankIdentityStaysNew()
    {
        var conflict = HepsiburadaMatching.Suggest(Product("p1", "SKU", "8691"), [Remote("r1", "SKU", "8692")]);
        var blank = HepsiburadaMatching.Suggest(Product("p2", "", ""), [Remote("r2", "", "")]);
        Assert.AreEqual(ProductChannelMatchOutcome.Conflict, conflict.Outcome);
        Assert.AreEqual(ProductChannelMatchOutcome.NewListingCandidate, blank.Outcome);
    }

    [TestMethod]
    public void CachedProductsAndManualMappingsAreConnectionScoped()
    {
        var root = Path.Combine(Path.GetTempPath(), "hb-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new HepsiburadaWorkspaceStore(root);
            var revisionA = store.ReplaceProducts("connection-a", "shop-a", [Remote("r1", "A", "1", "shop-a")]);
            var revisionB = store.ReplaceProducts("connection-b", "shop-b", [Remote("r2", "B", "2", "shop-b")]);

            Assert.AreEqual("r1", store.Read("connection-a").Products.Single().HepsiburadaSku);
            Assert.AreEqual("r2", store.Read("connection-b").Products.Single().HepsiburadaSku);
            var mapping = store.SaveManualMatch("connection-a", "p1", "r1", revisionA);
            Assert.AreEqual(revisionA, mapping.SnapshotRevision);
            Assert.AreEqual(0, store.Read("connection-b").ManualMatches.Count);
            Assert.AreEqual(1, revisionB);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void ManualMatchRejectsStaleSnapshotAndForeignRemoteId()
    {
        var root = Path.Combine(Path.GetTempPath(), "hb-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new HepsiburadaWorkspaceStore(root);
            var revision = store.ReplaceProducts("connection-a", "shop-a", [Remote("r1", "A", "1", "shop-a")]);
            Assert.ThrowsException<InvalidOperationException>(() => store.SaveManualMatch("connection-a", "p1", "r1", revision - 1));
            Assert.ThrowsException<InvalidOperationException>(() => store.SaveManualMatch("connection-a", "p1", "missing", revision));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
