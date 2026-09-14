using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2534: concurrent edits to the same taxonomy mapping key
/// must not let the last writer silently win - Map(...,expectedVersion)
/// must reject a stale/conflicting write instead of overwriting it.
[TestClass]
public sealed class TaxonomyMappingCasTests
{
    static TaxonomyStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "taxonomy-mapping-cas-" + Guid.NewGuid().ToString("N"));
        return new TaxonomyStore(root);
    }

    static TaxonomyEntry Entry(TaxonomyStore store, string name) => store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = name });

    [TestMethod]
    public void NewMappingWithExpectedVersionZeroSucceeds()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = Entry(store, "Marka");
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1", expectedVersion: 0);
            Assert.AreEqual(brand.Id, store.Resolve(TaxonomyKind.Brand, "ext-1", "etsy", "shop1"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NormalUpdateWithCorrectVersionSucceeds()
    {
        var store = NewStore(out var root);
        try
        {
            var brandA = Entry(store, "MarkaA"); var brandB = Entry(store, "MarkaB");
            store.Map(TaxonomyKind.Brand, "ext-1", brandA.Id, "etsy", "shop1", expectedVersion: 0);
            var version = store.GetMappingVersion(TaxonomyKind.Brand, "ext-1", "etsy", "shop1");
            store.Map(TaxonomyKind.Brand, "ext-1", brandB.Id, "etsy", "shop1", expectedVersion: version);
            Assert.AreEqual(brandB.Id, store.Resolve(TaxonomyKind.Brand, "ext-1", "etsy", "shop1"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TwoEditorsSameKeyOnlyFirstSaveWins()
    {
        var store = NewStore(out var root);
        try
        {
            var brandA = Entry(store, "MarkaA"); var brandB = Entry(store, "MarkaB");
            // Both editors load the screen when the mapping doesn't exist yet (version 0).
            var editorASnapshot = store.GetMappingVersion(TaxonomyKind.Brand, "ext-1", "etsy", "shop1");
            var editorBSnapshot = store.GetMappingVersion(TaxonomyKind.Brand, "ext-1", "etsy", "shop1");

            store.Map(TaxonomyKind.Brand, "ext-1", brandA.Id, "etsy", "shop1", editorASnapshot);
            var ex = Assert.ThrowsException<TaxonomyMappingConflictException>(() => store.Map(TaxonomyKind.Brand, "ext-1", brandB.Id, "etsy", "shop1", editorBSnapshot));
            Assert.IsFalse(string.IsNullOrWhiteSpace(ex.Message));

            Assert.AreEqual(brandA.Id, store.Resolve(TaxonomyKind.Brand, "ext-1", "etsy", "shop1"), "Editor A's save must not be overwritten by editor B's stale save.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ASaveThenStaleBSaveIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            var brandA = Entry(store, "MarkaA"); var brandB = Entry(store, "MarkaB"); var brandC = Entry(store, "MarkaC");
            store.Map(TaxonomyKind.Brand, "ext-1", brandA.Id, "etsy", "shop1", 0);
            var staleVersion = 0; // B loaded before A's save

            var afterA = store.GetMappingVersion(TaxonomyKind.Brand, "ext-1", "etsy", "shop1");
            store.Map(TaxonomyKind.Brand, "ext-1", brandB.Id, "etsy", "shop1", afterA);

            Assert.ThrowsException<TaxonomyMappingConflictException>(() => store.Map(TaxonomyKind.Brand, "ext-1", brandC.Id, "etsy", "shop1", staleVersion));
            Assert.AreEqual(brandB.Id, store.Resolve(TaxonomyKind.Brand, "ext-1", "etsy", "shop1"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ConcurrentFirstCreateOnlyOneWinsWithTypedConflict()
    {
        var store = NewStore(out var root);
        try
        {
            var brandA = Entry(store, "MarkaA"); var brandB = Entry(store, "MarkaB");
            store.Map(TaxonomyKind.Brand, "ext-1", brandA.Id, "etsy", "shop1", 0);
            Assert.ThrowsException<TaxonomyMappingConflictException>(() => store.Map(TaxonomyKind.Brand, "ext-1", brandB.Id, "etsy", "shop1", 0));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SameDesiredLocalIdReplayWithCorrectVersionSucceeds()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = Entry(store, "Marka");
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1", 0);
            var version = store.GetMappingVersion(TaxonomyKind.Brand, "ext-1", "etsy", "shop1");
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1", version);
            Assert.AreEqual(brand.Id, store.Resolve(TaxonomyKind.Brand, "ext-1", "etsy", "shop1"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TwoShopsSameExternalKeyAreIndependent()
    {
        var store = NewStore(out var root);
        try
        {
            var brandA = Entry(store, "MarkaA"); var brandB = Entry(store, "MarkaB");
            store.Map(TaxonomyKind.Brand, "ext-1", brandA.Id, "etsy", "shop1", 0);
            store.Map(TaxonomyKind.Brand, "ext-1", brandB.Id, "etsy", "shop2", 0);
            Assert.AreEqual(brandA.Id, store.Resolve(TaxonomyKind.Brand, "ext-1", "etsy", "shop1"));
            Assert.AreEqual(brandB.Id, store.Resolve(TaxonomyKind.Brand, "ext-1", "etsy", "shop2"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TwoMarketplacesAreIndependent()
    {
        var store = NewStore(out var root);
        try
        {
            var brandA = Entry(store, "MarkaA"); var brandB = Entry(store, "MarkaB");
            store.Map(TaxonomyKind.Brand, "ext-1", brandA.Id, "etsy", "shop1", 0);
            store.Map(TaxonomyKind.Brand, "ext-1", brandB.Id, "trendyol", "shop1", 0);
            Assert.AreEqual(brandA.Id, store.Resolve(TaxonomyKind.Brand, "ext-1", "etsy", "shop1"));
            Assert.AreEqual(brandB.Id, store.Resolve(TaxonomyKind.Brand, "ext-1", "trendyol", "shop1"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MapWithoutExpectedVersionStillWorksForBulkCallers()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = Entry(store, "Marka");
            store.MapBulk(TaxonomyKind.Brand, "etsy", "shop1", new[] { new TaxonomySuggestion("ext-1", brand.Id, brand.Name, "SUGGESTED") }, true);
            Assert.AreEqual(brand.Id, store.Resolve(TaxonomyKind.Brand, "ext-1", "etsy", "shop1"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void VersionIncrementsAcrossUnconditionalAndCasWrites()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = Entry(store, "Marka");
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1"); // no expectedVersion (unconditional)
            var v1 = store.GetMappingVersion(TaxonomyKind.Brand, "ext-1", "etsy", "shop1");
            Assert.AreEqual(1, v1);
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1", v1);
            var v2 = store.GetMappingVersion(TaxonomyKind.Brand, "ext-1", "etsy", "shop1");
            Assert.AreEqual(2, v2);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesVersionForCasContinuity()
    {
        var root = Path.Combine(Path.GetTempPath(), "taxonomy-mapping-cas-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TaxonomyStore(root);
            var brand = Entry(store, "Marka");
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1", 0);

            var reopened = new TaxonomyStore(root);
            var version = reopened.GetMappingVersion(TaxonomyKind.Brand, "ext-1", "etsy", "shop1");
            Assert.AreEqual(1, version);
            Assert.ThrowsException<TaxonomyMappingConflictException>(() => reopened.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1", 0));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TargetDeactivatedAfterPreviewStillBlocksMapWithItsOwnError()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = Entry(store, "Marka");
            brand.Active = false; store.Save(brand);
            Assert.ThrowsException<InvalidOperationException>(() => store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1", 0));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
