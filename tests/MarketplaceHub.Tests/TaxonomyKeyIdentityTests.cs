using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2588: TaxonomyMappings.ExternalKey case-collisions must never
/// crash MappingViews (SQLite's PK on this column is case-sensitive, so "ABC"
/// and "abc" can coexist as two rows), a new Map() write must never itself
/// create a fresh case-equivalent duplicate, and a legacy DB that already has
/// one must surface it as a review-required conflict instead of picking a row
/// silently or crashing.
[TestClass]
public sealed class TaxonomyKeyIdentityTests
{
    static TaxonomyStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "taxonomy-key-identity-" + Guid.NewGuid().ToString("N"));
        return new TaxonomyStore(root);
    }

    static TaxonomyEntry Brand(TaxonomyStore store, string name) => store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = name });

    static void InsertRawMapping(string root, string kind, string market, string shop, string externalKey, string localId)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO TaxonomyMappings(Kind,Marketplace,ShopId,ExternalKey,LocalId,UpdatedUtc,Version) VALUES($kind,$market,$shop,$key,$local,$updated,1)";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$market", market); cmd.Parameters.AddWithValue("$shop", shop);
        cmd.Parameters.AddWithValue("$key", externalKey); cmd.Parameters.AddWithValue("$local", localId); cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void MapWithCaseDifferentKeyReusesTheExistingRowInsteadOfCreatingADuplicate()
    {
        var store = NewStore(out var root);
        try
        {
            var brandA = Brand(store, "Marka A"); var brandB = Brand(store, "Marka B");
            store.Map(TaxonomyKind.Brand, "ABC", brandA.Id, "etsy", "shop1");
            store.Map(TaxonomyKind.Brand, "abc", brandB.Id, "etsy", "shop1"); // same key, different casing

            var mappings = store.Mappings(TaxonomyKind.Brand);
            Assert.AreEqual(1, mappings.Count, "A case-equivalent key must update the existing row, not create a second one.");
            Assert.AreEqual(brandB.Id, mappings.Single().LocalId);
            Assert.AreEqual(brandB.Id, store.Resolve(TaxonomyKind.Brand, "ABC", "etsy", "shop1"), "Resolving via either casing must reach the same, single row.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ReusingTheExistingRowStillEnforcesTheVersionCasContract()
    {
        var store = NewStore(out var root);
        try
        {
            var brandA = Brand(store, "Marka A"); var brandB = Brand(store, "Marka B");
            store.Map(TaxonomyKind.Brand, "ABC", brandA.Id, "etsy", "shop1");
            // Caller queries the version under a *different* casing that has never
            // existed, so it (wrongly) believes expectedVersion=0 - since the write
            // is retargeted to the existing "ABC" row (currently at version 1), this
            // must be rejected as a conflict rather than silently succeeding.
            Assert.ThrowsException<TaxonomyMappingConflictException>(() => store.Map(TaxonomyKind.Brand, "abc", brandB.Id, "etsy", "shop1", expectedVersion: 0));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DistinctShopsMayEachUseCaseDifferentKeysIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = Brand(store, "Marka");
            store.Map(TaxonomyKind.Brand, "ABC", brand.Id, "etsy", "shop1");
            store.Map(TaxonomyKind.Brand, "abc", brand.Id, "etsy", "shop2"); // different shop scope - no collision
            Assert.AreEqual(2, store.Mappings(TaxonomyKind.Brand).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LegacyCaseCollisionInDbDoesNotCrashMappingViewsAndIsFlaggedForReview()
    {
        var store = NewStore(out var root);
        try
        {
            var brandA = Brand(store, "Marka A"); var brandB = Brand(store, "Marka B");
            InsertRawMapping(root, ((int)TaxonomyKind.Brand).ToString(), "etsy", "shop1", "ABC", brandA.Id);
            InsertRawMapping(root, ((int)TaxonomyKind.Brand).ToString(), "etsy", "shop1", "abc", brandB.Id);

            var views = store.MappingViews(TaxonomyKind.Brand, "etsy", "shop1");

            Assert.IsTrue(views.Any(v => v.LocalId == brandA.Id && v.Status == "REVIEW_REQUIRED"));
            Assert.IsTrue(views.Any(v => v.LocalId == brandB.Id && v.Status == "REVIEW_REQUIRED"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NonCollidingLegacyMappingsAreUnaffectedByTheConflictCheck()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = Brand(store, "Marka");
            store.Map(TaxonomyKind.Brand, "unique-key", brand.Id, "etsy", "shop1");
            var views = store.MappingViews(TaxonomyKind.Brand, "etsy", "shop1");
            Assert.AreEqual("MAPPED", views.Single(v => v.LocalId == brand.Id).Status);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartIsIdempotentAfterCaseNormalizedWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "taxonomy-key-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TaxonomyStore(root);
            var brandA = Brand(store, "Marka A"); var brandB = Brand(store, "Marka B");
            store.Map(TaxonomyKind.Brand, "ABC", brandA.Id, "etsy", "shop1");
            store.Map(TaxonomyKind.Brand, "abc", brandB.Id, "etsy", "shop1");

            var reopened = new TaxonomyStore(root);
            Assert.AreEqual(1, reopened.Mappings(TaxonomyKind.Brand).Count);
            Assert.AreEqual(brandB.Id, reopened.Resolve(TaxonomyKind.Brand, "ABC", "etsy", "shop1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
