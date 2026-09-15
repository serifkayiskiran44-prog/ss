using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2587: MapBulk must apply an accepted suggestion batch as one
/// all-or-nothing transaction - a single invalid row anywhere in the batch
/// must leave zero mapping/history rows committed, not a partial prefix.
[TestClass]
public sealed class TaxonomyBulkAtomicityTests
{
    static TaxonomyStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "taxonomy-bulk-atomic-" + Guid.NewGuid().ToString("N"));
        return new TaxonomyStore(root);
    }

    static TaxonomyEntry Brand(TaxonomyStore store, string name, bool active = true) =>
        store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = name, Active = active });

    [TestMethod]
    public void TenValidSuggestionsCommitTenMappingsAndTenHistoryRowsAtomically()
    {
        var store = NewStore(out var root);
        try
        {
            var brands = Enumerable.Range(0, 10).Select(i => Brand(store, "Marka " + i)).ToList();
            var suggestions = brands.Select((b, i) => new TaxonomySuggestion("ext-" + i, b.Id, b.Name, "SUGGESTED")).ToList();
            store.MapBulk(TaxonomyKind.Brand, "etsy", "shop1", suggestions, true);

            Assert.AreEqual(10, store.Mappings(TaxonomyKind.Brand).Count);
            Assert.AreEqual(10, store.History(TaxonomyKind.Brand, "etsy", "shop1").Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InvalidFifthSuggestionRollsBackTheEntireBatch()
    {
        var store = NewStore(out var root);
        try
        {
            var brands = Enumerable.Range(0, 9).Select(i => Brand(store, "Marka " + i)).ToList();
            var suggestions = brands.Take(4).Select((b, i) => new TaxonomySuggestion("ext-" + i, b.Id, b.Name, "SUGGESTED")).ToList();
            suggestions.Add(new TaxonomySuggestion("ext-bad", "no-such-local-id", null, "SUGGESTED"));
            suggestions.AddRange(brands.Skip(4).Select((b, i) => new TaxonomySuggestion("ext-" + (5 + i), b.Id, b.Name, "SUGGESTED")));

            Assert.ThrowsException<InvalidOperationException>(() => store.MapBulk(TaxonomyKind.Brand, "etsy", "shop1", suggestions, true));
            Assert.AreEqual(0, store.Mappings(TaxonomyKind.Brand).Count, "None of the first 4 otherwise-valid rows may have been committed.");
            Assert.AreEqual(0, store.History(TaxonomyKind.Brand, "etsy", "shop1").Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InactiveLocalEntryRollsBackTheWholeBatch()
    {
        var store = NewStore(out var root);
        try
        {
            var active1 = Brand(store, "Aktif 1");
            var inactive = Brand(store, "Pasif", active: false);
            var active2 = Brand(store, "Aktif 2");
            var suggestions = new[]
            {
                new TaxonomySuggestion("ext-1", active1.Id, active1.Name, "SUGGESTED"),
                new TaxonomySuggestion("ext-2", inactive.Id, inactive.Name, "SUGGESTED"),
                new TaxonomySuggestion("ext-3", active2.Id, active2.Name, "SUGGESTED"),
            };
            Assert.ThrowsException<InvalidOperationException>(() => store.MapBulk(TaxonomyKind.Brand, "etsy", "shop1", suggestions, true));
            Assert.AreEqual(0, store.Mappings(TaxonomyKind.Brand).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DuplicateExternalKeyWithinTheSameBatchIsRejectedDeterministically()
    {
        var store = NewStore(out var root);
        try
        {
            var brand1 = Brand(store, "Marka 1"); var brand2 = Brand(store, "Marka 2");
            var suggestions = new[]
            {
                new TaxonomySuggestion("DUP", brand1.Id, brand1.Name, "SUGGESTED"),
                new TaxonomySuggestion("dup", brand2.Id, brand2.Name, "SUGGESTED"), // same key, different casing
            };
            Assert.ThrowsException<InvalidOperationException>(() => store.MapBulk(TaxonomyKind.Brand, "etsy", "shop1", suggestions, true));
            Assert.AreEqual(0, store.Mappings(TaxonomyKind.Brand).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SuggestionsWithoutLocalIdAreSkippedWithoutError()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = Brand(store, "Marka");
            var suggestions = new[]
            {
                new TaxonomySuggestion("ext-1", brand.Id, brand.Name, "SUGGESTED"),
                new TaxonomySuggestion("ext-unmatched", null, null, "UNMATCHED"),
            };
            store.MapBulk(TaxonomyKind.Brand, "etsy", "shop1", suggestions, true);
            Assert.AreEqual(1, store.Mappings(TaxonomyKind.Brand).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void EmptyAcceptedBatchIsANoOp()
    {
        var store = NewStore(out var root);
        try
        {
            store.MapBulk(TaxonomyKind.Brand, "etsy", "shop1", new[] { new TaxonomySuggestion("ext-1", null, null, "UNMATCHED") }, true);
            Assert.AreEqual(0, store.Mappings(TaxonomyKind.Brand).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartAfterSuccessfulBulkKeepsMappingsAndHistoryConsistent()
    {
        var root = Path.Combine(Path.GetTempPath(), "taxonomy-bulk-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TaxonomyStore(root);
            var brands = Enumerable.Range(0, 5).Select(i => Brand(store, "Marka " + i)).ToList();
            var suggestions = brands.Select((b, i) => new TaxonomySuggestion("ext-" + i, b.Id, b.Name, "SUGGESTED")).ToList();
            store.MapBulk(TaxonomyKind.Brand, "etsy", "shop1", suggestions, true);

            var reopened = new TaxonomyStore(root);
            Assert.AreEqual(5, reopened.Mappings(TaxonomyKind.Brand).Count);
            Assert.AreEqual(5, reopened.History(TaxonomyKind.Brand, "etsy", "shop1").Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnapprovedBulkThrowsBeforeAnyMutation()
    {
        var store = NewStore(out var root);
        try
        {
            var brand = Brand(store, "Marka");
            var suggestions = new[] { new TaxonomySuggestion("ext-1", brand.Id, brand.Name, "SUGGESTED") };
            Assert.ThrowsException<InvalidOperationException>(() => store.MapBulk(TaxonomyKind.Brand, "etsy", "shop1", suggestions, false));
            Assert.AreEqual(0, store.Mappings(TaxonomyKind.Brand).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
