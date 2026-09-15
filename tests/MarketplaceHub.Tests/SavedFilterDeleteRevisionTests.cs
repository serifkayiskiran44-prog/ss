using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2663: CatalogFilterStore.Delete must be a compare-and-delete
/// over a persisted revision, so a stale saved-filter screen can never delete a
/// replacement someone else already saved under the same Name, and a
/// delete+recreate of the same Name can never let an old handle's revision
/// number accidentally match the recreated row (ABA).
[TestClass]
public sealed class SavedFilterDeleteRevisionTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "filter-revision-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ListDeleteWithCurrentRevisionRemovesTheRow() => WithRoot(root =>
    {
        var store = new CatalogFilterStore(root);
        store.Save("A", new CatalogFilter());
        var saved = store.List().Single();
        Assert.AreEqual(CatalogFilterStore.DeleteResult.Deleted, store.Delete("A", saved.Revision));
        Assert.AreEqual(0, store.List().Count);
    });

    [TestMethod]
    public void ConcurrentSaveThenStaleDeleteIsRejectedAndReplacementSurvives() => WithRoot(root =>
    {
        var store = new CatalogFilterStore(root);
        store.Save("A", new CatalogFilter { Brands = ["Nike"] });
        var staleHandle = store.List().Single(); // revision 1

        store.Save("A", new CatalogFilter { Brands = ["Adidas"] }); // revision -> 2

        Assert.AreEqual(CatalogFilterStore.DeleteResult.Stale, store.Delete("A", staleHandle.Revision));
        var survivor = store.List().Single();
        CollectionAssert.AreEqual(new[] { "Adidas" }, survivor.Filter.Brands);
    });

    [TestMethod]
    public void TwoConcurrentDeletesOnlyOneMutatesTheOtherIsIdempotent() => WithRoot(root =>
    {
        var store = new CatalogFilterStore(root);
        store.Save("A", new CatalogFilter());
        var saved = store.List().Single();

        Assert.AreEqual(CatalogFilterStore.DeleteResult.Deleted, store.Delete("A", saved.Revision));
        Assert.AreEqual(CatalogFilterStore.DeleteResult.AlreadyDeleted, store.Delete("A", saved.Revision));
    });

    [TestMethod]
    public void DeleteOfNeverExistingNameIsAlreadyDeletedNotStale() => WithRoot(root =>
    {
        var store = new CatalogFilterStore(root);
        Assert.AreEqual(CatalogFilterStore.DeleteResult.AlreadyDeleted, store.Delete("Never", 0));
    });

    [TestMethod]
    public void RestartPreservesRevisionForReliableDelete() => WithRoot(root =>
    {
        new CatalogFilterStore(root).Save("A", new CatalogFilter());
        var reopened = new CatalogFilterStore(root);
        var saved = reopened.List().Single();
        Assert.AreEqual(CatalogFilterStore.DeleteResult.Deleted, reopened.Delete("A", saved.Revision));
    });

    [TestMethod]
    public void DeleteThenRecreateInvalidatesTheOldHandlesRevisionAba() => WithRoot(root =>
    {
        var store = new CatalogFilterStore(root);
        store.Save("A", new CatalogFilter());
        store.Save("A", new CatalogFilter()); // revision 2
        var oldHandle = store.List().Single(); // revision 2

        store.Delete("A", oldHandle.Revision);
        store.Save("A", new CatalogFilter { Brands = ["Recreated"] }); // reuse the same Name
        var recreated = store.List().Single();

        Assert.AreNotEqual(oldHandle.Revision, recreated.Revision, "The durable floor must prevent revision reuse for the same Name after delete.");
        Assert.AreEqual(CatalogFilterStore.DeleteResult.Stale, store.Delete("A", oldHandle.Revision), "The stale pre-delete handle must never be able to delete the recreated row.");
        Assert.AreEqual(1, store.List().Count);
    });

    [TestMethod]
    public void RevisionIncreasesMonotonicallyOnEachSave() => WithRoot(root =>
    {
        var store = new CatalogFilterStore(root);
        store.Save("A", new CatalogFilter());
        store.Save("A", new CatalogFilter());
        store.Save("A", new CatalogFilter());
        Assert.AreEqual(3, store.List().Single().Revision);
    });

    [TestMethod]
    public void UnicodeAndMaxLengthNamesRoundTripWithRevision() => WithRoot(root =>
    {
        var store = new CatalogFilterStore(root);
        var name = new string('ş', 100);
        store.Save(name, new CatalogFilter());
        var saved = store.List().Single();
        Assert.AreEqual(1, saved.Revision);
        Assert.AreEqual(CatalogFilterStore.DeleteResult.Deleted, store.Delete(name, saved.Revision));
    });

    [TestMethod]
    public void LegacyRowWithoutRevisionColumnDefaultsToZeroAndCanStillBeDeleted() => WithRoot(root =>
    {
        _ = new CatalogFilterStore(root); // creates schema
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO CatalogFilterViews(Name,Json) VALUES('Legacy','{}')";
            cmd.ExecuteNonQuery();
        }
        var reopened = new CatalogFilterStore(root);
        var legacy = reopened.List().Single();
        Assert.AreEqual(0, legacy.Revision);
        Assert.AreEqual(CatalogFilterStore.DeleteResult.Deleted, reopened.Delete("Legacy", 0));
    });

    [TestMethod]
    public void CorruptFilterOwnerBehaviorIsUnaffectedByRevisionChange() => WithRoot(root =>
    {
        var store = new CatalogFilterStore(root);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO CatalogFilterViews(Name,Json,Revision) VALUES('Bad','{not-json',1)";
            cmd.ExecuteNonQuery();
        }
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptFilters().Count);
        store.DeleteCorruptFilter("Bad");
        Assert.AreEqual(0, store.CorruptFilters().Count);
    });
}
