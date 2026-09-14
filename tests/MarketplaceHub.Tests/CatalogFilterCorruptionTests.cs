using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2586: a malformed CatalogFilterViews row must not make other
/// saved filters unreachable, must never be treated as "missing" (and so
/// silently overwritten by a normal Save under the same name), and recovery
/// must be an explicit, transactional, re-validated action.
[TestClass]
public sealed class CatalogFilterCorruptionTests
{
    static CatalogFilterStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "catalog-filter-corrupt-" + Guid.NewGuid().ToString("N"));
        return new CatalogFilterStore(root);
    }

    // Same rationale as CatalogCorruptionTests.InsertRawRow: CatalogFilterViews has no
    // expression indexes, so a syntactically invalid JSON string inserts fine here
    // (unlike CatalogProducts) - this directly simulates the row corruption itself.
    static void InsertRawRow(string root, string name, string json)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO CatalogFilterViews(Name,Json) VALUES($name,$json) ON CONFLICT(Name) DO UPDATE SET Json=excluded.Json";
        cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$json", json);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void ValidFilterWorksNormally()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save("Standart", new CatalogFilter { Active = true, Brands = ["A"] });
            var loaded = store.List().Single();
            Assert.AreEqual("Standart", loaded.Name);
            Assert.IsTrue(loaded.Filter.Active);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedRowDoesNotHideValidRows()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save("Iyi", new CatalogFilter { Brands = ["A"] });
            InsertRawRow(root, "Kötü", "{\"Brands\":[\"A\",");

            var list = store.List();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("Iyi", list[0].Name);

            var corrupt = store.CorruptFilters();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("Kötü", corrupt[0].Name);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TruncatedJsonProducesRecoveryState()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "Bozuk", "{\"Brands\":[");
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptFilters().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NullArrayMemberProducesRecoveryState()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "Bozuk", "{\"Brands\":null}");
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptFilters().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MissingLegacyFieldIsNotCorruptUsesDefault()
    {
        var store = NewStore(out var root);
        try
        {
            // Old row saved before some field existed: the key is simply absent (not
            // null), so the C# default ([]) applies - this must not be corrupt.
            InsertRawRow(root, "Eski", "{\"Active\":true}");
            var list = store.List();
            Assert.AreEqual(1, list.Count);
            CollectionAssert.AreEqual(Array.Empty<string>(), list[0].Filter.Brands);
            Assert.AreEqual(0, store.CorruptFilters().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OversizedArrayProducesRecoveryState()
    {
        var store = NewStore(out var root);
        try
        {
            var manyBrands = string.Join(",", Enumerable.Range(0, 200).Select(i => $"\"B{i}\""));
            InsertRawRow(root, "Bozuk", $"{{\"Brands\":[{manyBrands}]}}");
            Assert.AreEqual(0, store.List().Count);
            var corrupt = store.CorruptFilters().Single();
            Assert.AreEqual("Array payload exceeds bounds", corrupt.Reason);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OversizedPayloadIsRejectedWithoutFullParse()
    {
        var store = NewStore(out var root);
        try
        {
            var huge = "{\"Brands\":[\"" + new string('x', CatalogFilterStore.MaxFilterJsonBytes + 10) + "\"]}";
            InsertRawRow(root, "Bozuk", huge);
            var corrupt = store.CorruptFilters().Single();
            Assert.AreEqual("Oversized payload", corrupt.Reason);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawValues()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "Bozuk", "{\"Brands\":[\"SECRET-BRAND-VALUE\",");
            var corrupt = store.CorruptFilters().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("SECRET-BRAND-VALUE"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SaveOverExistingCorruptNameIsRejectedNotSilentlyOverwritten()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "Bozuk", "{\"Brands\":[");
            var ex = Assert.ThrowsException<InvalidOperationException>(() => store.Save("Bozuk", new CatalogFilter { Brands = ["Yeni"] }));
            StringAssert.Contains(ex.Message, "bozuk");
            Assert.AreEqual(1, store.CorruptFilters().Count, "The corrupt row must still be present, not overwritten.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SaveRejectsOversizedArrayUpFront()
    {
        var store = NewStore(out var root);
        try
        {
            var tooMany = Enumerable.Range(0, 101).Select(i => "B" + i).ToArray();
            Assert.ThrowsException<ArgumentException>(() => store.Save("X", new CatalogFilter { Brands = tooMany }));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeleteCorruptFilterRemovesOnlyThatRecord()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save("Iyi", new CatalogFilter { Brands = ["A"] });
            InsertRawRow(root, "Bozuk", "{\"Brands\":[");

            store.DeleteCorruptFilter("Bozuk");

            Assert.AreEqual(0, store.CorruptFilters().Count);
            Assert.AreEqual(1, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeleteCorruptFilterRefusesAHealthyRow()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save("Iyi", new CatalogFilter { Brands = ["A"] });
            Assert.ThrowsException<InvalidOperationException>(() => store.DeleteCorruptFilter("Iyi"));
            Assert.AreEqual(1, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeleteCorruptFilterOnMissingNameThrows()
    {
        var store = NewStore(out var root);
        try { Assert.ThrowsException<InvalidOperationException>(() => store.DeleteCorruptFilter("nope")); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesReviewRequiredState()
    {
        var root = Path.Combine(Path.GetTempPath(), "catalog-filter-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            new CatalogFilterStore(root).Save("Iyi", new CatalogFilter { Brands = ["A"] });
            InsertRawRow(root, "Bozuk", "{\"Brands\":[");

            var reopened = new CatalogFilterStore(root);
            Assert.AreEqual(1, reopened.List().Count);
            Assert.AreEqual(1, reopened.CorruptFilters().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "A", "{\"Brands\":[");
            InsertRawRow(root, "B", "not json");
            InsertRawRow(root, "C", "{\"Brands\":null}");

            var corrupt = store.CorruptFilters();
            Assert.AreEqual(3, corrupt.Count);
            CollectionAssert.AreEquivalent(new[] { "A", "B", "C" }, corrupt.Select(c => c.Name).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnicodeNameRoundTrips()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save("Ürünlerim - Şüpheli", new CatalogFilter { Brands = ["Ç"] });
            var loaded = store.List().Single();
            Assert.AreEqual("Ürünlerim - Şüpheli", loaded.Name);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
