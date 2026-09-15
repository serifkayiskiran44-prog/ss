using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2609: UiPreferenceStore.ListViews must isolate a corrupt
/// (unparsable UpdatedUtc) saved-view row instead of crashing the whole
/// module's saved-view list, and recovery must be explicit and re-validated.
[TestClass]
public sealed class UiPreferenceViewCorruptionTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "ui-view-corrupt-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void InsertRawView(string root, string module, string name, string updatedUtc, string payload = "{}")
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "ui-preferences.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO UiViews(Module,Name,Payload,UpdatedUtc) VALUES($module,$name,$payload,$updated)";
        cmd.Parameters.AddWithValue("$module", module); cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$payload", payload); cmd.Parameters.AddWithValue("$updated", updatedUtc);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void NormalRoundTripIsUnaffected() => WithRoot(root =>
    {
        var store = new UiPreferenceStore(root);
        store.SaveView("orders", "Bugün", "{}");
        Assert.AreEqual(1, store.ListViews("orders").Count);
        Assert.AreEqual(0, store.CorruptViews("orders").Count);
    });

    [TestMethod]
    public void TenValidViewsSurviveOneCorruptRow() => WithRoot(root =>
    {
        var store = new UiPreferenceStore(root);
        for (var i = 0; i < 10; i++) store.SaveView("orders", "View " + i, "{}");
        InsertRawView(root, "orders", "Bad", "not-a-date");

        var views = store.ListViews("orders");
        Assert.AreEqual(10, views.Count, "The screen must not crash/close; all 10 valid views must load.");
        Assert.AreEqual(1, store.CorruptViews("orders").Count);
    });

    [TestMethod]
    public void CorruptRowIsDistinguishedFromHealthyViaDiagnostics() => WithRoot(root =>
    {
        var store = new UiPreferenceStore(root);
        InsertRawView(root, "orders", "Bad", "junk");
        var corrupt = store.CorruptViews("orders").Single();
        Assert.AreEqual("orders", corrupt.Module);
        Assert.AreEqual("Bad", corrupt.Name);
    });

    [TestMethod]
    public void DeleteCorruptViewRemovesOnlyTheCorruptRow() => WithRoot(root =>
    {
        var store = new UiPreferenceStore(root);
        store.SaveView("orders", "Good", "{}");
        InsertRawView(root, "orders", "Bad", "junk");

        store.DeleteCorruptView("orders", "Bad");

        Assert.AreEqual(1, store.ListViews("orders").Count);
        Assert.AreEqual(0, store.CorruptViews("orders").Count);
    });

    [TestMethod]
    public void DeleteCorruptViewRefusesAHealthyView() => WithRoot(root =>
    {
        var store = new UiPreferenceStore(root);
        store.SaveView("orders", "Good", "{}");
        Assert.ThrowsException<InvalidOperationException>(() => store.DeleteCorruptView("orders", "Good"));
        Assert.AreEqual(1, store.ListViews("orders").Count);
    });

    [TestMethod]
    public void DeleteCorruptViewThrowsWhenAlreadyGone() => WithRoot(root =>
    {
        var store = new UiPreferenceStore(root);
        Assert.ThrowsException<InvalidOperationException>(() => store.DeleteCorruptView("orders", "Missing"));
    });

    [TestMethod]
    public void RestartKeepsCorruptionClassificationDeterministicNoInfiniteLoop() => WithRoot(root =>
    {
        _ = new UiPreferenceStore(root);
        InsertRawView(root, "orders", "Bad", "junk");
        var reopened = new UiPreferenceStore(root);
        // Two consecutive calls must behave identically - no growing/self-healing
        // state and no exception on either call.
        Assert.AreEqual(0, reopened.ListViews("orders").Count);
        Assert.AreEqual(1, reopened.CorruptViews("orders").Count);
        Assert.AreEqual(0, reopened.ListViews("orders").Count);
        Assert.AreEqual(1, reopened.CorruptViews("orders").Count);
    });

    [TestMethod]
    public void DifferentModulesAreIsolatedFromEachOthersCorruption() => WithRoot(root =>
    {
        var store = new UiPreferenceStore(root);
        store.SaveView("products", "Ok", "{}");
        InsertRawView(root, "orders", "Bad", "junk");
        Assert.AreEqual(1, store.ListViews("products").Count);
        Assert.AreEqual(0, store.CorruptViews("products").Count);
        Assert.AreEqual(0, store.ListViews("orders").Count);
        Assert.AreEqual(1, store.CorruptViews("orders").Count);
    });

    [TestMethod]
    public void EmptyDbReturnsEmptyListsWithoutError() => WithRoot(root =>
    {
        var store = new UiPreferenceStore(root);
        Assert.AreEqual(0, store.ListViews("orders").Count);
        Assert.AreEqual(0, store.CorruptViews("orders").Count);
    });

    [TestMethod]
    public void SaveThenDeleteCorruptRaceLeavesTheRealViewIntact() => WithRoot(root =>
    {
        var store = new UiPreferenceStore(root);
        InsertRawView(root, "orders", "Slot", "junk");
        // A user re-saves the same (module,name) with valid data before anyone
        // gets to click "delete corrupt" - the corrupt-only re-check must then
        // refuse to delete the now-healthy row.
        store.SaveView("orders", "Slot", "{\"fixed\":true}");
        Assert.ThrowsException<InvalidOperationException>(() => store.DeleteCorruptView("orders", "Slot"));
        Assert.AreEqual(1, store.ListViews("orders").Count);
    });
}
