using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2666: ExcelProfileStore.Save/Delete must be a compare-and-swap/
/// compare-and-delete over a durable revision, so a stale editor can never
/// silently overwrite or delete a profile someone else already changed, and a
/// delete+recreate of the same Id can never let an old handle's revision number
/// accidentally match the recreated row (ABA).
[TestClass]
public sealed class ExcelProfileRevisionIntegrityTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "excel-profile-revision-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NewProfileCreateRestartReadRevisionRoundTrips() => WithRoot(root =>
    {
        var profile = new ExcelImportProfile { Name = "A" };
        new ExcelProfileStore(root).Save(profile);
        Assert.AreEqual(1, profile.Revision);
        var reopened = new ExcelProfileStore(root).Find(profile.Id)!;
        Assert.AreEqual(1, reopened.Revision);
    });

    [TestMethod]
    public void RevisionIncreasesMonotonicallyOnEachSave() => WithRoot(root =>
    {
        var store = new ExcelProfileStore(root);
        var profile = new ExcelImportProfile { Name = "A" };
        store.Save(profile);
        store.Save(profile);
        store.Save(profile);
        Assert.AreEqual(3, profile.Revision);
    });

    [TestMethod]
    public void TwoEditorsReadSameRevisionOnlyFirstSaveSucceeds() => WithRoot(root =>
    {
        var store = new ExcelProfileStore(root);
        var original = new ExcelImportProfile { Name = "A" };
        store.Save(original); // revision 1

        var editorA = store.Find(original.Id)!; editorA.Name = "A - edited by A";
        var editorB = store.Find(original.Id)!; editorB.Name = "A - edited by B";

        store.Save(editorA); // succeeds, revision -> 2
        Assert.ThrowsException<InvalidOperationException>(() => store.Save(editorB));

        Assert.AreEqual("A - edited by A", store.Find(original.Id)!.Name);
    });

    [TestMethod]
    public void SaveAfterAnotherEditorsSaveCannotBeFollowedByAStaleDelete() => WithRoot(root =>
    {
        var store = new ExcelProfileStore(root);
        var original = new ExcelImportProfile { Name = "A" };
        store.Save(original); // revision 1

        var staleHandle = store.Find(original.Id)!; // revision 1
        original.Name = "A - edited";
        store.Save(original); // revision -> 2

        Assert.ThrowsException<InvalidOperationException>(() => store.Delete(staleHandle.Id, staleHandle.Revision));
        Assert.IsNotNull(store.Find(original.Id));
    });

    [TestMethod]
    public void DeleteThenRecreateInvalidatesTheOldHandlesRevisionAba() => WithRoot(root =>
    {
        var store = new ExcelProfileStore(root);
        var profile = new ExcelImportProfile { Name = "A" };
        store.Save(profile); store.Save(profile); store.Save(profile); // revision 3
        var oldHandle = store.Find(profile.Id)!; // revision 3

        store.Delete(profile.Id, profile.Revision);

        // Recreate a *different* profile reusing the same Id (simulating an
        // undo/restore or a deliberate re-add flow) - it must never land on the
        // same revision number the deleted handle remembers.
        var recreated = new ExcelImportProfile { Id = profile.Id, Name = "Recreated" };
        store.Save(recreated);
        Assert.AreNotEqual(oldHandle.Revision, recreated.Revision, "The durable floor must prevent revision reuse for the same Id after delete.");

        // The stale pre-delete handle must never be able to mutate the recreated row.
        Assert.ThrowsException<InvalidOperationException>(() => store.Save(oldHandle));
        Assert.ThrowsException<InvalidOperationException>(() => store.Delete(profile.Id, oldHandle.Revision));
    });

    [TestMethod]
    public void ConcurrentSaveAndDeleteRaceOnlyOneWins() => WithRoot(root =>
    {
        var store = new ExcelProfileStore(root);
        var profile = new ExcelImportProfile { Name = "A" };
        store.Save(profile); // revision 1
        var handleForDelete = store.Find(profile.Id)!; // revision 1

        profile.Name = "A - edited";
        store.Save(profile); // revision -> 2, wins the race

        Assert.ThrowsException<InvalidOperationException>(() => store.Delete(handleForDelete.Id, handleForDelete.Revision));
        Assert.IsNotNull(store.Find(profile.Id));
    });

    [TestMethod]
    public void DeleteRejectsUnknownId() => WithRoot(root =>
    {
        var store = new ExcelProfileStore(root);
        Assert.ThrowsException<InvalidOperationException>(() => store.Delete(Guid.NewGuid().ToString("N"), 0));
    });

    [TestMethod]
    public void CorruptRowIsIsolatedAndNeverBecomesAValidProfile() => WithRoot(root =>
    {
        var store = new ExcelProfileStore(root);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "excel-profiles.db") }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO ExcelProfiles(Id,Name,Json,UpdatedUtc,Revision) VALUES('bad','Bad','{not-json','2026-01-01T00:00:00Z',1)";
            cmd.ExecuteNonQuery();
        }
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptProfiles().Count);
        Assert.ThrowsException<ExcelProfileCorruptException>(() => store.Find("bad"));
    });

    [TestMethod]
    public void ExistingFieldRegressionsSurviveTheRevisionChange() => WithRoot(root =>
    {
        var store = new ExcelProfileStore(root);
        var profile = new ExcelImportProfile
        {
            Name = "Tedarikçi A", CultureName = "tr-TR", SheetName = "Ürünler", HeaderRow = 2,
            ExpectedHeaders = ["SKU", "Ürün"], VisibleFields = ["Sku", "Price"],
        };
        profile.ColumnMappings["Sku"] = "SKU"; profile.HeaderAliases["urun"] = "Name"; profile.Defaults["Currency"] = "USD";
        store.Save(profile);
        var reopened = store.Find(profile.Id)!;
        Assert.AreEqual("tr-TR", reopened.CultureName);
        Assert.AreEqual("Ürünler", reopened.SheetName);
        Assert.AreEqual(2, reopened.HeaderRow);
        Assert.AreEqual("SKU", reopened.ColumnMappings["Sku"]);
        Assert.AreEqual("Name", reopened.HeaderAliases["urun"]);
        Assert.AreEqual("USD", reopened.Defaults["Currency"]);
    });
}
