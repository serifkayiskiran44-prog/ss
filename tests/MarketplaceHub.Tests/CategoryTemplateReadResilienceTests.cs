using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2645: a malformed persisted UpdatedUtc in
/// CategoryFieldTemplates must never resolve to DateTime.MinValue and pass
/// as a normal row; Get() must distinguish corrupt from missing, and Save()
/// must never silently overwrite a corrupt existing (Category,Channel,Shop)
/// scope.
[TestClass]
public sealed class CategoryTemplateReadResilienceTests
{
    static CategoryTemplateStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "category-template-resilience-" + Guid.NewGuid().ToString("N"));
        return new CategoryTemplateStore(root);
    }

    static void InsertRawRow(string root, string id, string categoryId, string channel, string shopId, string updatedUtc, int version = 1)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO CategoryFieldTemplates(Id,CategoryId,Channel,ShopId,Version,Brand,Currency,VatRate,UpdatedUtc) VALUES($id,$cat,$channel,$shop,$version,NULL,NULL,NULL,$updated)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$cat", categoryId); cmd.Parameters.AddWithValue("$channel", channel); cmd.Parameters.AddWithValue("$shop", shopId);
        cmd.Parameters.AddWithValue("$version", version); cmd.Parameters.AddWithValue("$updated", updatedUtc);
        cmd.ExecuteNonQuery();
    }

    static CategoryFieldTemplate Template(string categoryId) => new() { CategoryId = categoryId, Brand = "Marka" };

    [TestMethod]
    public void ValidRoundTripWorksNormally()
    {
        var store = NewStore(out var root);
        try
        {
            var saved = store.Save(Template("cat1"));
            var loaded = store.Get("cat1", "local", "default");
            Assert.IsNotNull(loaded);
            Assert.AreEqual(saved.UpdatedUtc, loaded!.UpdatedUtc);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedUpdatedUtcDoesNotDropOtherHealthyRows()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(Template("cat1"));
            InsertRawRow(root, "bad-1", "cat2", "local", "default", "not-a-date");

            var list = store.List();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("cat1", list[0].CategoryId);

            var corrupt = store.CorruptTemplates();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedUpdatedUtcNeverResolvesToMinValue()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "cat1", "local", "default", "");
            Assert.IsFalse(store.List().Any(t => t.Id == "bad-1"));
            Assert.AreEqual(1, store.CorruptTemplates().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void GetThrowsDistinctTypedErrorForCorruptRowVsMissing()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "cat1", "local", "default", "junk");
            Assert.ThrowsException<CategoryTemplateCorruptException>(() => store.Get("cat1", "local", "default"));
            Assert.IsNull(store.Get("cat-missing", "local", "default"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SaveOverCorruptExistingScopeFailsClosedInsteadOfSilentlyOverwriting()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "cat1", "local", "default", "junk");
            Assert.ThrowsException<CategoryTemplateCorruptException>(() => store.Save(Template("cat1")));
            Assert.AreEqual(1, store.CorruptTemplates().Count, "The corrupt row must still be present, not overwritten.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SaveStillWorksForANewScope()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "cat1", "local", "default", "junk");
            store.Save(Template("cat2"));
            Assert.AreEqual(1, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesCorruptRowDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "category-template-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            new CategoryTemplateStore(root);
            InsertRawRow(root, "bad-1", "cat1", "local", "default", "junk");

            var reopened = new CategoryTemplateStore(root);
            Assert.AreEqual(0, reopened.List().Count);
            Assert.AreEqual(1, reopened.CorruptTemplates().Count);
            Assert.ThrowsException<CategoryTemplateCorruptException>(() => reopened.Get("cat1", "local", "default"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-a", "cat1", "local", "default", "1");
            InsertRawRow(root, "bad-b", "cat2", "local", "default", "2");
            InsertRawRow(root, "bad-c", "cat3", "local", "default", "3");

            var corrupt = store.CorruptTemplates();
            Assert.AreEqual(3, corrupt.Count);
            CollectionAssert.AreEquivalent(new[] { "bad-a", "bad-b", "bad-c" }, corrupt.Select(c => c.Id).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void VersionExistsButTimestampCorruptIsStillTreatedAsCorrupt()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "bad-1", "cat1", "local", "default", "junk", version: 5);
            Assert.AreEqual(1, store.CorruptTemplates().Count);
            Assert.ThrowsException<CategoryTemplateCorruptException>(() => store.Get("cat1", "local", "default"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawBrandOrCurrency()
    {
        var store = NewStore(out var root);
        try
        {
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO CategoryFieldTemplates(Id,CategoryId,Channel,ShopId,Version,Brand,Currency,VatRate,UpdatedUtc) VALUES('bad-1','cat1','local','default',1,'SECRET-BRAND','XYZ',NULL,'junk')";
                cmd.ExecuteNonQuery();
            }
            var corrupt = store.CorruptTemplates().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("SECRET-BRAND|XYZ"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
