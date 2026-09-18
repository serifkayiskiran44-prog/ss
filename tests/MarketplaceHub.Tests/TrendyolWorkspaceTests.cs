using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

namespace MarketplaceHub.Tests;

[TestClass]
public class TrendyolWorkspaceTests
{
    string directory = null!;
    TrendyolWorkspaceStore store = null!;
    [TestInitialize] public void Setup() { directory = Path.Combine(Path.GetTempPath(), "trendyol-" + Guid.NewGuid().ToString("N")); store = new(directory); }
    [TestCleanup] public void Cleanup() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }

    [TestMethod] public void StateIsSellerScopedAndStaleEditorCannotOverwrite()
    {
        var first = store.Load("10"); var stale = store.Load("10");
        first.Brands.Add(new(1, "Marka")); store.Save(first);
        Assert.AreEqual(0, store.Load("20").Brands.Count);
        Assert.ThrowsException<InvalidOperationException>(() => store.Save(stale));
        Assert.AreEqual("Marka", new TrendyolWorkspaceStore(directory).Load("10").Brands.Single().Name);
    }

    [TestMethod] public void MultipleLocalCategoriesCanShareOneRemoteLeaf()
    {
        var taxonomy = new TaxonomyStore(directory);
        var a = taxonomy.Save(new() { Kind = TaxonomyKind.Category, Name = "Ev > Fincan" });
        var b = taxonomy.Save(new() { Kind = TaxonomyKind.Category, Name = "Mutfak > Fincan" });
        var state = store.Load("10"); state.Categories.Add(new(22, "Fincan", "Ev > Fincan", true));
        state.Mappings.Add(new(TaxonomyKind.Category, a.Id, a.Name, 22));
        state.Mappings.Add(new(TaxonomyKind.Category, b.Id, b.Name, 22)); store.Save(state);
        Assert.AreEqual(2, store.Load("10").Mappings.Count);
        Assert.AreEqual(0, taxonomy.Mappings(TaxonomyKind.Category).Count, "Outbound mappings must not invert the existing import mapping table.");
    }

    [TestMethod] public void SuggestionPrefersExactPathButAmbiguousLeafIsNotAccepted()
    {
        TrendyolCategory[] categories = [new(1,"Fincan","Ev > Fincan",true),new(2,"Fincan","Hediyelik > Fincan",true),new(3,"Ev","Ev",false)];
        Assert.AreEqual(1L, TrendyolMatching.Category(" Ev>Fincan ", categories).RemoteId);
        Assert.IsNull(TrendyolMatching.Category("Fincan", categories).RemoteId);
        Assert.IsNull(TrendyolMatching.Category("Ev", categories).RemoteId);
        Assert.AreEqual(9L, TrendyolMatching.Brand("  İPEK  ", [new(9,"ipek")]).RemoteId);
    }

    [TestMethod] public void SkuCanMatchWithoutLocalBarcodeAndConflictsStayUnmatched()
    {
        var product = new CatalogProduct { Sku = "SKU-A" };
        TrendyolRemoteProduct[] remote = [new("BC-A","SKU-A","A",1,3,10m,10m,true),new("BC-B","SKU-B","B",2,1,5m,5m,true)];
        Assert.AreEqual("BC-A", TrendyolMatching.Product(product, "", remote).Barcode);
        product.Barcode = "BC-B";
        Assert.IsNull(TrendyolMatching.Product(product, "", remote).Barcode);
        Assert.AreEqual("BC-A", TrendyolMatching.Product(product, "BC-A", remote).Barcode);
        Assert.AreEqual("BC-B", product.Barcode);
    }

    [TestMethod] public void InvalidCacheDoesNotReplacePreviousSnapshot()
    {
        var state = store.Load("10"); state.Brands.Add(new(1,"Good")); store.Save(state);
        state = store.Load("10"); state.Brands.Add(new(1,"Duplicate"));
        Assert.ThrowsException<InvalidOperationException>(() => store.Save(state));
        Assert.AreEqual(1, store.Load("10").Brands.Count);
    }

    [TestMethod] public void GtinMatchesRemoteBarcodeWithoutChangingLocalIdentity()
    {
        var product=new CatalogProduct{Sku="PTD-175",Barcode="",Gtin="4002064419374"};
        TrendyolRemoteProduct[] remote=[new("4002064419374","LEGACY-SKU","Treats",1,2,10m,10m,true)];
        Assert.AreEqual("4002064419374",TrendyolMatching.Product(product,"",remote).Barcode);
        Assert.AreEqual("",product.Barcode);Assert.AreEqual("PTD-175",product.Sku);
    }
    [TestMethod] public void GtinConflictNeverOverridesBarcodeOrSavedIntegration()
    {
        var product=new CatalogProduct{Sku="LOCAL",Barcode="A",Gtin="B"};
        TrendyolRemoteProduct[] remote=[new("A","REMOTE-A","A",1,2,10m,10m,true),new("B","REMOTE-B","B",2,2,10m,10m,true)];
        Assert.IsNull(TrendyolMatching.Product(product,"",remote).Barcode);
        Assert.AreEqual("A",TrendyolMatching.Product(product,"A",remote).Barcode);
        Assert.IsNull(TrendyolMatching.Product(product,"REMOVED",remote).Barcode);
    }
}
