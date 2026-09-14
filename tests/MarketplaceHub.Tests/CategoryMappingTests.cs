using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1883 (mapping create/update/remove CRUD lifecycle) and
/// #1884 (resolve-time usability preflight for the mapping target).
[TestClass]
public sealed class CategoryMappingTests
{
    static TaxonomyStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "category-mapping-" + Guid.NewGuid().ToString("N"));
        return new TaxonomyStore(root);
    }

    [TestMethod]
    public void CreateUpdateAndRemoveMappingLifecycle()
    {
        var taxonomy = NewStore(out var root);
        try
        {
            var a = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            var b = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Bilgisayar" });

            taxonomy.Map(TaxonomyKind.Category, "ext-1", a.Id, "etsy", "shop-a");
            Assert.AreEqual(a.Id, taxonomy.Resolve(TaxonomyKind.Category, "ext-1", "etsy", "shop-a"));

            taxonomy.Map(TaxonomyKind.Category, "ext-1", b.Id, "etsy", "shop-a");
            Assert.AreEqual(b.Id, taxonomy.Resolve(TaxonomyKind.Category, "ext-1", "etsy", "shop-a"), "Mapping the same external key again must update, not duplicate.");

            taxonomy.Unmap(TaxonomyKind.Category, "ext-1", "etsy", "shop-a");
            Assert.IsNull(taxonomy.Resolve(TaxonomyKind.Category, "ext-1", "etsy", "shop-a"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RemovingAnUnknownMappingFailsClosed()
    {
        var taxonomy = NewStore(out var root);
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() => taxonomy.Unmap(TaxonomyKind.Category, "does-not-exist"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MappingWithWrongKindIdIsRejected()
    {
        var taxonomy = NewStore(out var root);
        try
        {
            var brand = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            Assert.ThrowsException<InvalidOperationException>(() => taxonomy.Map(TaxonomyKind.Category, "ext-1", brand.Id));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesMappings()
    {
        var root = Path.Combine(Path.GetTempPath(), "category-mapping-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new TaxonomyStore(root);
            var entry = first.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            first.Map(TaxonomyKind.Category, "ext-1", entry.Id);

            var reopened = new TaxonomyStore(root);
            Assert.AreEqual(entry.Id, reopened.Resolve(TaxonomyKind.Category, "ext-1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NotMappedExternalKeyReturnsNotMapped()
    {
        var taxonomy = NewStore(out var root);
        try
        {
            var result = taxonomy.ResolveForUse(TaxonomyKind.Category, "unmapped-key");
            Assert.AreEqual(TaxonomyResolution.NotMapped, result.Status);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InactiveTargetIsReportedNotSilentlyUsable()
    {
        var taxonomy = NewStore(out var root);
        try
        {
            var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            taxonomy.Map(TaxonomyKind.Category, "ext-1", entry.Id);
            taxonomy.Save(new TaxonomyEntry { Id = entry.Id, Kind = TaxonomyKind.Category, Name = "Elektronik", Active = false });

            var result = taxonomy.ResolveForUse(TaxonomyKind.Category, "ext-1");
            Assert.AreEqual(TaxonomyResolution.TargetInactive, result.Status);
            Assert.AreEqual(entry.Id, result.LocalId);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ReactivatingTargetMakesItReadyAgain()
    {
        var taxonomy = NewStore(out var root);
        try
        {
            var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            taxonomy.Map(TaxonomyKind.Category, "ext-1", entry.Id);
            taxonomy.Save(new TaxonomyEntry { Id = entry.Id, Kind = TaxonomyKind.Category, Name = "Elektronik", Active = false });
            Assert.AreEqual(TaxonomyResolution.TargetInactive, taxonomy.ResolveForUse(TaxonomyKind.Category, "ext-1").Status);

            taxonomy.Save(new TaxonomyEntry { Id = entry.Id, Kind = TaxonomyKind.Category, Name = "Elektronik", Active = true });
            Assert.AreEqual(TaxonomyResolution.Ready, taxonomy.ResolveForUse(TaxonomyKind.Category, "ext-1").Status);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MissingTargetIsDetectedInsteadOfSilentlyResolving()
    {
        var taxonomy = NewStore(out var root);
        try
        {
            var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            taxonomy.Map(TaxonomyKind.Category, "ext-1", entry.Id);

            // Simulate a pre-existing orphaned mapping (e.g. from data created before
            // the Delete() dependency guard existed) by removing the entry directly -
            // the app's own guarded Delete() can no longer produce this on its own.
            var path = Path.Combine(root, "catalog.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM TaxonomyEntries WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", entry.Id);
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var result = taxonomy.ResolveForUse(TaxonomyKind.Category, "ext-1");
            Assert.AreEqual(TaxonomyResolution.TargetMissing, result.Status);
            Assert.AreEqual(entry.Id, result.LocalId);
            Assert.IsNull(result.LocalName);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
