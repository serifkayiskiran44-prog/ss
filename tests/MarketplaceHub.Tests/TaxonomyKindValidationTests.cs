using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2532: an undefined/corrupt TaxonomyKind value (persisted or
/// passed in) must never be treated as a valid Category/Brand/Attribute -
/// every public boundary must reject it, and a legacy row with a bad Kind
/// must not surface as a normal entry under any valid kind's List().
[TestClass]
public sealed class TaxonomyKindValidationTests
{
    static TaxonomyStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "taxonomy-kind-" + Guid.NewGuid().ToString("N"));
        return new TaxonomyStore(root);
    }

    static void InsertRawEntry(string root, string id, int kind, string name)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO TaxonomyEntries(Id,Kind,Name,Value,Active,UpdatedUtc) VALUES($id,$kind,$name,'',1,$updated)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void NormalCategoryBrandAttributeRoundTrip()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Kategori" });
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Marka" });
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Attribute, Name = "Özellik" });
            Assert.AreEqual(1, store.List(TaxonomyKind.Category).Count);
            Assert.AreEqual(1, store.List(TaxonomyKind.Brand).Count);
            Assert.AreEqual(1, store.List(TaxonomyKind.Attribute).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(3)]
    [DataRow(99)]
    [DataRow(int.MaxValue)]
    public void SaveRejectsUndefinedKindValues(int rawKind)
    {
        var store = NewStore(out var root);
        try
        {
            var entry = new TaxonomyEntry { Kind = (TaxonomyKind)rawKind, Name = "X" };
            Assert.ThrowsException<ArgumentException>(() => store.Save(entry));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(3)]
    [DataRow(99)]
    public void ListMapResolveRejectUndefinedKind(int rawKind)
    {
        var store = NewStore(out var root);
        try
        {
            var badKind = (TaxonomyKind)rawKind;
            Assert.ThrowsException<ArgumentException>(() => store.List(badKind));
            Assert.ThrowsException<ArgumentException>(() => store.Map(badKind, "ext-1", "some-id"));
            Assert.ThrowsException<ArgumentException>(() => store.Resolve(badKind, "ext-1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LegacyInvalidKindRowDoesNotSurfaceUnderAnyValidKindsList()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Iyi" });
            InsertRawEntry(root, "bad-1", 99, "Kotu");

            Assert.AreEqual(1, store.List(TaxonomyKind.Category).Count);
            Assert.AreEqual(0, store.List(TaxonomyKind.Brand).Count);
            Assert.AreEqual(0, store.List(TaxonomyKind.Attribute).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LegacyInvalidKindRowIsSurfacedByCorruptKindEntries()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawEntry(root, "bad-1", 99, "Kotu");
            var corrupt = store.CorruptKindEntries();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InvalidMappingKindIsRejectedBeforeAnyWrite()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Marka" });
            Assert.ThrowsException<ArgumentException>(() => store.Map((TaxonomyKind)99, "ext-1", brand.Id));
            Assert.AreEqual(0, store.Mappings(TaxonomyKind.Brand).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartStillRejectsInvalidKindAndPreservesValidEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "taxonomy-kind-" + Guid.NewGuid().ToString("N"));
        try
        {
            new TaxonomyStore(root).Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Iyi" });
            InsertRawEntry(root, "bad-1", 99, "Kotu");

            var reopened = new TaxonomyStore(root);
            Assert.AreEqual(1, reopened.List(TaxonomyKind.Category).Count);
            Assert.AreEqual(1, reopened.CorruptKindEntries().Count);
            Assert.ThrowsException<ArgumentException>(() => reopened.List((TaxonomyKind)99));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ValidEntryOrderAndIdentityAreUnaffectedByAnInvalidKindRowElsewhere()
    {
        var store = NewStore(out var root);
        try
        {
            var a = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Alfa" });
            var b = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Beta" });
            InsertRawEntry(root, "bad-1", 99, "Zulu");

            var list = store.List(TaxonomyKind.Category);
            Assert.AreEqual(2, list.Count);
            CollectionAssert.AreEqual(new[] { a.Id, b.Id }, list.Select(x => x.Id).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
