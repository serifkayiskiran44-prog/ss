using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2636: a malformed persisted timestamp in TaxonomyEntries,
/// TaxonomyMappings, or TaxonomyMappingHistory must never resolve to
/// DateTime.MinValue and pass as a normal (very old) value - it must isolate
/// to an explicit review-required state instead, without dropping other
/// healthy rows or forcing a corrupt mapping into STALE/MAPPED.
[TestClass]
public sealed class TaxonomyTimestampIntegrityTests
{
    static TaxonomyStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "taxonomy-ts-" + Guid.NewGuid().ToString("N"));
        return new TaxonomyStore(root);
    }

    static void RunSql(string root, string sql, params (string, object)[] parameters)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void NormalRoundTripTimestampIsUnaffected()
    {
        var store = NewStore(out var root);
        try
        {
            var entry = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Marka" });
            var loaded = store.List(TaxonomyKind.Brand).Single();
            Assert.AreEqual(entry.UpdatedUtc, loaded.UpdatedUtc);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedEntryTimestampDoesNotDropOtherEntries()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Iyi" });
            var bad = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Kotu" });
            RunSql(root, "UPDATE TaxonomyEntries SET UpdatedUtc=$v WHERE Id=$id", ("$v", "not-a-date"), ("$id", bad.Id));

            var list = store.List(TaxonomyKind.Brand);
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("Iyi", list[0].Name);

            var corrupt = store.CorruptEntries(TaxonomyKind.Brand);
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual(bad.Id, corrupt[0].Id);
            Assert.AreNotEqual(default, corrupt[0].DetectedUtc);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void EntryTimestampNeverSilentlyBecomesMinValue()
    {
        var store = NewStore(out var root);
        try
        {
            var bad = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "X" });
            RunSql(root, "UPDATE TaxonomyEntries SET UpdatedUtc=$v WHERE Id=$id", ("$v", ""), ("$id", bad.Id));

            Assert.IsFalse(store.List(TaxonomyKind.Category).Any(e => e.Id == bad.Id), "A corrupt-timestamp entry must not appear as a normal (min-date) entry.");
            Assert.AreEqual(1, store.CorruptEntries(TaxonomyKind.Category).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedMappingTimestampYieldsReviewRequiredNotStaleOrMapped()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Marka" });
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1");
            RunSql(root, "UPDATE TaxonomyMappings SET UpdatedUtc=$v WHERE ExternalKey=$key", ("$v", "garbage"), ("$key", "ext-1"));

            var views = store.MappingViews(TaxonomyKind.Brand, "etsy", "shop1");
            var view = views.Single(v => v.ExternalKey == "ext-1");
            Assert.AreEqual("REVIEW_REQUIRED", view.Status);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ValidRecentMappingIsMapped()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Marka" });
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1");
            var view = store.MappingViews(TaxonomyKind.Brand, "etsy", "shop1").Single(v => v.ExternalKey == "ext-1");
            Assert.AreEqual("MAPPED", view.Status);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void GenuinelyStaleMappingIsStillStale()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Marka" });
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1");
            RunSql(root, "UPDATE TaxonomyMappings SET UpdatedUtc=$v WHERE ExternalKey=$key", ("$v", DateTime.UtcNow.AddDays(-400).ToString("O")), ("$key", "ext-1"));

            var view = store.MappingViews(TaxonomyKind.Brand, "etsy", "shop1").Single(v => v.ExternalKey == "ext-1");
            Assert.AreEqual("STALE", view.Status);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedHistoryTimestampIsExcludedNotFakedIntoChronology()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Marka" });
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1");
            RunSql(root, "UPDATE TaxonomyMappingHistory SET ChangedUtc=$v WHERE ExternalKey=$key", ("$v", "corrupt-date"), ("$key", "ext-1"));

            var history = store.History(TaxonomyKind.Brand, "etsy", "shop1");
            Assert.AreEqual(0, history.Count, "The corrupt-timestamp history row must not appear with a fabricated date.");

            var corruptHistory = store.CorruptHistory(TaxonomyKind.Brand, "etsy", "shop1");
            Assert.AreEqual(1, corruptHistory.Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OneMappingCorruptAnotherHealthyBothReportedCorrectly()
    {
        var store = NewStore(out var root);
        try
        {
            var brandGood = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "MarkaIyi" });
            var brandBad = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "MarkaKotu" });
            store.Map(TaxonomyKind.Brand, "ext-good", brandGood.Id, "etsy", "shop1");
            store.Map(TaxonomyKind.Brand, "ext-bad", brandBad.Id, "etsy", "shop1");
            RunSql(root, "UPDATE TaxonomyMappings SET UpdatedUtc=$v WHERE ExternalKey=$key", ("$v", "xx"), ("$key", "ext-bad"));

            var views = store.MappingViews(TaxonomyKind.Brand, "etsy", "shop1");
            Assert.AreEqual("MAPPED", views.Single(v => v.ExternalKey == "ext-good").Status);
            Assert.AreEqual("REVIEW_REQUIRED", views.Single(v => v.ExternalKey == "ext-bad").Status);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void EmptyTimestampStringIsTreatedAsCorruptNotMinDate()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Marka" });
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1");
            RunSql(root, "UPDATE TaxonomyMappings SET UpdatedUtc=$v WHERE ExternalKey=$key", ("$v", ""), ("$key", "ext-1"));

            var view = store.MappingViews(TaxonomyKind.Brand, "etsy", "shop1").Single(v => v.ExternalKey == "ext-1");
            Assert.AreEqual("REVIEW_REQUIRED", view.Status);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WhitespaceTimestampIsTreatedAsCorrupt()
    {
        var store = NewStore(out var root);
        try
        {
            var bad = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "X" });
            RunSql(root, "UPDATE TaxonomyEntries SET UpdatedUtc=$v WHERE Id=$id", ("$v", "   "), ("$id", bad.Id));
            Assert.AreEqual(1, store.CorruptEntries(TaxonomyKind.Category).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TruncatedTimestampIsTreatedAsCorrupt()
    {
        var store = NewStore(out var root);
        try
        {
            var bad = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "X" });
            RunSql(root, "UPDATE TaxonomyEntries SET UpdatedUtc=$v WHERE Id=$id", ("$v", "2026-13-99T99:99:99"), ("$id", bad.Id));
            Assert.AreEqual(1, store.CorruptEntries(TaxonomyKind.Category).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesReviewRequiredState()
    {
        var root = Path.Combine(Path.GetTempPath(), "taxonomy-ts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TaxonomyStore(root);
            var bad = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "X" });
            RunSql(root, "UPDATE TaxonomyEntries SET UpdatedUtc=$v WHERE Id=$id", ("$v", "junk"), ("$id", bad.Id));

            var reopened = new TaxonomyStore(root);
            Assert.AreEqual(0, reopened.List(TaxonomyKind.Category).Count);
            Assert.AreEqual(1, reopened.CorruptEntries(TaxonomyKind.Category).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void EntryWithCorruptTimestampCanStillBeUsedForResolution()
    {
        // ResolveForUse queries Name/Active directly, never UpdatedUtc, so a mapping
        // still resolves correctly even though the target entry's own timestamp is
        // corrupt and it is hidden from List().
        var store = NewStore(out var root);
        try
        {
            var brand = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Marka" });
            store.Map(TaxonomyKind.Brand, "ext-1", brand.Id, "etsy", "shop1");
            RunSql(root, "UPDATE TaxonomyEntries SET UpdatedUtc=$v WHERE Id=$id", ("$v", "bad"), ("$id", brand.Id));

            var resolution = store.ResolveForUse(TaxonomyKind.Brand, "ext-1", "etsy", "shop1");
            Assert.AreEqual(TaxonomyResolution.Ready, resolution.Status);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainNameOrValue()
    {
        var store = NewStore(out var root);
        try
        {
            var bad = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "SECRET-BRAND-NAME" });
            RunSql(root, "UPDATE TaxonomyEntries SET UpdatedUtc=$v WHERE Id=$id", ("$v", "bad"), ("$id", bad.Id));
            var corrupt = store.CorruptEntries(TaxonomyKind.Brand).Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("SECRET-BRAND-NAME"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
