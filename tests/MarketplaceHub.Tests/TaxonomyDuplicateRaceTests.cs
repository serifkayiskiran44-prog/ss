using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2533: case-insensitive Name/Value uniqueness must be
/// enforced atomically at the DB level (a unique index on the normalized
/// key), not by a race-prone SELECT-then-write.
[TestClass]
public sealed class TaxonomyDuplicateRaceTests
{
    static TaxonomyStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "taxonomy-dup-race-" + Guid.NewGuid().ToString("N"));
        return new TaxonomyStore(root);
    }

    [TestMethod]
    public void SingleCreateSucceeds()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            Assert.AreEqual(1, store.List(TaxonomyKind.Brand).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ExactDuplicateIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" }));
            Assert.AreEqual(1, store.List(TaxonomyKind.Brand).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CaseInsensitiveDuplicateIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "NIKE" }));
            Assert.AreEqual(1, store.List(TaxonomyKind.Brand).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TurkishUnicodeCaseCollisionIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Şirket" });
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "şirket" }));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ConcurrentSameNameCreateOnlyOneSucceeds()
    {
        var store = NewStore(out var root);
        try
        {
            var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            {
                try { store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = i % 2 == 0 ? "Nike" : "NIKE" }); return true; }
                catch (InvalidOperationException) { return false; }
            })).ToArray();
            Task.WaitAll(tasks);

            Assert.AreEqual(1, tasks.Count(t => t.Result), "Exactly one concurrent create for the same case-insensitive name must succeed.");
            Assert.AreEqual(1, store.List(TaxonomyKind.Brand).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ConcurrentRenameIntoACollisionIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            var a = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Alpha" });
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Beta" });

            a.Name = "BETA";
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(a));

            var names = store.List(TaxonomyKind.Brand).Select(x => x.Name).ToArray();
            CollectionAssert.AreEquivalent(new[] { "Alpha", "Beta" }, names);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SameIdUpdateIsNotTreatedAsACollisionWithItself()
    {
        var store = NewStore(out var root);
        try
        {
            var entry = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            entry.Active = false;
            store.Save(entry); // same Id, same Name/Value - must not self-collide
            var list = store.List(TaxonomyKind.Brand);
            Assert.AreEqual(1, list.Count);
            Assert.IsFalse(list.Single().Active);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SameNameDifferentValueDoesNotCollide()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Attribute, Name = "Renk", Value = "Kırmızı" });
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Attribute, Name = "Renk", Value = "Mavi" });
            Assert.AreEqual(2, store.List(TaxonomyKind.Attribute).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WhitespaceIsTrimmedBeforeCollisionCheck()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "  Nike  " }));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DifferentKindsWithSameNameDoNotCollide()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Nike" });
            Assert.AreEqual(1, store.List(TaxonomyKind.Brand).Count);
            Assert.AreEqual(1, store.List(TaxonomyKind.Category).Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartStillEnforcesUniquenessAndPreservesExistingEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "taxonomy-dup-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            new TaxonomyStore(root).Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            var reopened = new TaxonomyStore(root);
            Assert.AreEqual(1, reopened.List(TaxonomyKind.Brand).Count);
            Assert.ThrowsException<InvalidOperationException>(() => reopened.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "nike" }));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MappingReferencesSurviveAFailedRenameAttempt()
    {
        var store = NewStore(out var root);
        try
        {
            var a = store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Alpha" });
            store.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Beta" });
            store.Map(TaxonomyKind.Brand, "ext-1", a.Id, "etsy", "shop1", 0);

            a.Name = "BETA";
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(a));

            Assert.AreEqual(a.Id, store.Resolve(TaxonomyKind.Brand, "ext-1", "etsy", "shop1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
