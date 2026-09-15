using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2594: concurrent Add/SetSortOrder/Delete/SetPrimary on
/// ProductMedia must never produce a duplicate logical order for the same
/// product, must never leave more or fewer than one primary, and a stale
/// SetSortOrder must be rejected rather than silently applied.
[TestClass]
public sealed class MediaOrderingConcurrencyTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "media-ordering-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static bool HasDuplicateOrders(MediaStore store, string productId) =>
        store.List(productId).GroupBy(x => x.SortOrder).Any(g => g.Count() > 1);

    [TestMethod]
    public void TwentyParallelAddsProduceNoDuplicateOrder()
    {
        WithRoot(root =>
        {
            var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            {
                var store = new MediaStore(root);
                store.Add("p1", $"https://example.test/img{i}.jpg");
            })).ToArray();
            Task.WaitAll(tasks);

            var store = new MediaStore(root);
            var rows = store.List("p1");
            Assert.AreEqual(20, rows.Count);
            Assert.IsFalse(HasDuplicateOrders(store, "p1"), "Twenty concurrent Add() calls must never allocate the same logical order.");
            CollectionAssert.AreEquivalent(Enumerable.Range(0, 20).ToList(), rows.Select(x => x.SortOrder).ToList());
        });
    }

    [TestMethod]
    public void SwapBasedReorderNeverProducesADuplicateOrTemporaryGap()
    {
        WithRoot(root =>
        {
            var store = new MediaStore(root);
            var a = store.Add("p1", "https://example.test/a.jpg");
            var b = store.Add("p1", "https://example.test/b.jpg");
            var c = store.Add("p1", "https://example.test/c.jpg");
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, new[] { a.SortOrder, b.SortOrder, c.SortOrder });

            store.SetSortOrder(c.Id, 0); // move c to front, swapping with a
            var rows = store.List("p1").ToDictionary(x => x.Id);
            Assert.AreEqual(0, rows[c.Id].SortOrder);
            Assert.AreEqual(2, rows[a.Id].SortOrder);
            Assert.AreEqual(1, rows[b.Id].SortOrder);
            Assert.IsFalse(HasDuplicateOrders(store, "p1"));
        });
    }

    [TestMethod]
    public void StaleReorderIsRejectedNotSilentlyApplied()
    {
        WithRoot(root =>
        {
            var store = new MediaStore(root);
            var a = store.Add("p1", "https://example.test/a.jpg");
            var staleSnapshot = a.UpdatedUtc;
            store.SetSortOrder(a.Id, 5); // someone else reorders first (no-sibling-at-5 case still updates row)
            Assert.ThrowsException<InvalidOperationException>(() => store.SetSortOrder(a.Id, 9, staleSnapshot));
        });
    }

    [TestMethod]
    public void DeletingThePrimaryPromotesExactlyOneNewPrimary()
    {
        WithRoot(root =>
        {
            var store = new MediaStore(root);
            var a = store.Add("p1", "https://example.test/a.jpg"); // becomes primary (order 0)
            var b = store.Add("p1", "https://example.test/b.jpg");
            store.Delete(a.Id);
            var rows = store.List("p1");
            Assert.AreEqual(1, rows.Count(x => x.IsPrimary));
            Assert.IsTrue(rows.Single(x => x.Id == b.Id).IsPrimary);
        });
    }

    [TestMethod]
    public void DeletingTheOnlyMediaLeavesNoPrimary()
    {
        WithRoot(root =>
        {
            var store = new MediaStore(root);
            var a = store.Add("p1", "https://example.test/a.jpg");
            store.Delete(a.Id);
            Assert.AreEqual(0, store.List("p1").Count);
        });
    }

    [TestMethod]
    public void SetPrimaryAlwaysLeavesExactlyOnePrimary()
    {
        WithRoot(root =>
        {
            var store = new MediaStore(root);
            var a = store.Add("p1", "https://example.test/a.jpg");
            var b = store.Add("p1", "https://example.test/b.jpg");
            store.SetPrimary(b.Id);
            var rows = store.List("p1");
            Assert.AreEqual(1, rows.Count(x => x.IsPrimary));
            Assert.IsTrue(rows.Single(x => x.Id == b.Id).IsPrimary);
        });
    }

    [TestMethod]
    public void ExplicitConflictingSortOrderOnAddIsRejectedWithAClearMessage()
    {
        WithRoot(root =>
        {
            var store = new MediaStore(root);
            store.Add("p1", "https://example.test/a.jpg", sortOrder: 0);
            var error = Assert.ThrowsException<InvalidOperationException>(() => store.Add("p1", "https://example.test/b.jpg", sortOrder: 0));
            StringAssert.Contains(error.Message, "sıra");
        });
    }

    [TestMethod]
    public void RestartPreservesOrderAfterConcurrentWrites()
    {
        var root = NewRoot();
        try
        {
            var store = new MediaStore(root);
            store.Add("p1", "https://example.test/a.jpg");
            store.Add("p1", "https://example.test/b.jpg");
            var reopened = new MediaStore(root);
            var rows = reopened.List("p1");
            Assert.AreEqual(2, rows.Count);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, rows.Select(x => x.SortOrder).ToList());
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DistinctProductsNeverCollideOnOrder()
    {
        WithRoot(root =>
        {
            var store = new MediaStore(root);
            var a = store.Add("p1", "https://example.test/a.jpg");
            var b = store.Add("p2", "https://example.test/b.jpg");
            Assert.AreEqual(0, a.SortOrder);
            Assert.AreEqual(0, b.SortOrder);
        });
    }
}
