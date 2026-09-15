using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2650: a persisted PricingProfiles.SourceField that fails to
/// parse must never silently fall back to Cost. It must be typed corruption
/// that blocks selection/evaluation for that profile while leaving other
/// profiles usable.
[TestClass]
public sealed class PricingProfileCorruptionTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "pricing-corrupt-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void SetRawSourceField(string root, string id, string rawValue)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE PricingProfiles SET SourceField=$v WHERE Id=$id";
        cmd.Parameters.AddWithValue("$v", rawValue); cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void NormalCostAndPriceRoundTripUnaffected() => WithRoot(root =>
    {
        var store = new PricingProfileStore(root);
        var cost = store.Save(new PricingProfile { Name = "A", Formula = "x", SourceField = PricingSourceField.Cost });
        var price = store.Save(new PricingProfile { Name = "B", Formula = "x", SourceField = PricingSourceField.Price });
        Assert.AreEqual(PricingSourceField.Cost, store.Find(cost.Id)!.SourceField);
        Assert.AreEqual(PricingSourceField.Price, store.Find(price.Id)!.SourceField);
        Assert.AreEqual(0, store.CorruptProfiles().Count);
    });

    [DataTestMethod]
    [DataRow("Unknown")]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("2")]
    [DataRow("cost")]
    [DataRow("COST")]
    [DataRow("Côst")]
    public void UnrecognizedOrWronglyCasedSourceFieldNeverSilentlyFallsBackToCost(string raw) => WithRoot(root =>
    {
        var store = new PricingProfileStore(root);
        var profile = store.Save(new PricingProfile { Name = "A", Formula = "x", SourceField = PricingSourceField.Cost });
        SetRawSourceField(root, profile.Id, raw);
        Assert.ThrowsException<PricingProfileCorruptException>(() => store.Find(profile.Id));
    });

    [TestMethod]
    public void CorruptProfileIsExcludedFromListButOtherProfilesRemainListable() => WithRoot(root =>
    {
        var store = new PricingProfileStore(root);
        var healthy = store.Save(new PricingProfile { Name = "Healthy", Formula = "x", SourceField = PricingSourceField.Price });
        var corrupt = store.Save(new PricingProfile { Name = "Corrupt", Formula = "x", SourceField = PricingSourceField.Cost });
        SetRawSourceField(root, corrupt.Id, "Unknown");

        var list = store.List();
        Assert.AreEqual(1, list.Count);
        Assert.AreEqual(healthy.Id, list[0].Id);
        Assert.AreEqual(1, store.CorruptProfiles().Count);
        Assert.AreEqual(corrupt.Id, store.CorruptProfiles()[0].Id);
    });

    [TestMethod]
    public void CorruptProfileCannotBeEvaluatedEvenIndirectlyThroughFind() => WithRoot(root =>
    {
        var store = new PricingProfileStore(root);
        var profile = store.Save(new PricingProfile { Name = "A", Formula = "x*2", SourceField = PricingSourceField.Cost });
        SetRawSourceField(root, profile.Id, "Unknown");
        Assert.ThrowsException<PricingProfileCorruptException>(() => store.Find(profile.Id));
        // No product/catalog mutation path exists through Find alone; the guard is
        // that a caller can never obtain a PricingProfile object for this row to
        // pass into Evaluate() in the first place.
    });

    [TestMethod]
    public void RestartKeepsCorruptionStateDeterministicAndDoesNotAutoRepair() => WithRoot(root =>
    {
        var id = new PricingProfileStore(root).Save(new PricingProfile { Name = "A", Formula = "x", SourceField = PricingSourceField.Cost }).Id;
        SetRawSourceField(root, id, "Unknown");
        var reopened = new PricingProfileStore(root);
        Assert.ThrowsException<PricingProfileCorruptException>(() => reopened.Find(id));
        Assert.AreEqual(1, reopened.CorruptProfiles().Count);
    });

    [TestMethod]
    public void ValidFormulaWithCorruptSourceFieldIsStillCorrupt() => WithRoot(root =>
    {
        var store = new PricingProfileStore(root);
        var profile = store.Save(new PricingProfile { Name = "A", Formula = "x*1.5+2", SourceField = PricingSourceField.Cost });
        SetRawSourceField(root, profile.Id, "Unknown");
        Assert.ThrowsException<PricingProfileCorruptException>(() => store.Find(profile.Id));
    });
}
