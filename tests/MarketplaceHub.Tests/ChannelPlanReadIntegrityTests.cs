using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2665: ChannelProductsStore.Find/List must isolate a corrupt or
/// identity-mismatched persisted ChannelPlans row instead of throwing an
/// unhandled JsonException or silently materializing the wrong plan.
[TestClass]
public sealed class ChannelPlanReadIntegrityTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "channel-plan-integrity-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void InsertRawRow(string root, string channel, string shop, string product, string json, int version = 0)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "channel_products.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO ChannelPlans(ChannelId,ShopId,ProductId,Json,Version) VALUES($c,$s,$p,$j,$v)";
        cmd.Parameters.AddWithValue("$c", channel); cmd.Parameters.AddWithValue("$s", shop); cmd.Parameters.AddWithValue("$p", product); cmd.Parameters.AddWithValue("$j", json); cmd.Parameters.AddWithValue("$v", version);
        cmd.ExecuteNonQuery();
    }

    static ChannelProductPlan Valid(string channel, string shop, string product) => new() { ChannelId = channel, ShopId = shop, ProductId = product, Currency = "USD", PlannedPrice = 10, PlannedStock = 1 };

    [TestMethod]
    public void NormalRoundTripThroughSaveFindListIsUnaffected() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        store.Save(Valid("etsy", "shop-a", "p1"));
        var found = store.Find("etsy", "shop-a", "p1");
        Assert.IsNotNull(found);
        Assert.AreEqual(1, store.List().Count);
        Assert.AreEqual(0, store.CorruptPlans().Count);
    });

    [TestMethod]
    public void MalformedJsonIsIsolatedFromFindAndList() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        InsertRawRow(root, "etsy", "shop-a", "p1", "{not-json");
        Assert.ThrowsException<ChannelPlanCorruptException>(() => store.Find("etsy", "shop-a", "p1"));
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptPlans().Count);
    });

    [TestMethod]
    public void NinetyNineHealthyRowsSurviveOneCorruptRowInTheSameList() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        for (var i = 0; i < 99; i++) store.Save(Valid("etsy", "shop-a", "p" + i));
        InsertRawRow(root, "etsy", "shop-a", "p-bad", "not json at all");
        var rows = store.List();
        Assert.AreEqual(99, rows.Count);
        Assert.AreEqual(1, store.CorruptPlans().Count);
    });

    [TestMethod]
    public void WrongShopIdentityInPayloadIsQuarantinedNotTrusted() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        var wrongShopJson = System.Text.Json.JsonSerializer.Serialize(new ChannelProductPlan { ChannelId = "etsy", ShopId = "shop-b", ProductId = "p1", Currency = "USD" });
        InsertRawRow(root, "etsy", "shop-a", "p1", wrongShopJson);
        Assert.ThrowsException<ChannelPlanCorruptException>(() => store.Find("etsy", "shop-a", "p1"));
        var corrupt = store.CorruptPlans().Single();
        Assert.AreEqual("shop-a", corrupt.ShopId);
        StringAssert.Contains(corrupt.Reason, "kimlik");
    });

    [TestMethod]
    public void NullJsonLiteralIsQuarantined() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        InsertRawRow(root, "etsy", "shop-a", "p1", "null");
        Assert.ThrowsException<ChannelPlanCorruptException>(() => store.Find("etsy", "shop-a", "p1"));
    });

    [TestMethod]
    public void InvalidPersistedCurrencyIsQuarantinedNotAcceptedAsTrusted() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        var badCurrencyJson = System.Text.Json.JsonSerializer.Serialize(new ChannelProductPlan { ChannelId = "etsy", ShopId = "shop-a", ProductId = "p1", Currency = "usd" });
        InsertRawRow(root, "etsy", "shop-a", "p1", badCurrencyJson);
        Assert.ThrowsException<ChannelPlanCorruptException>(() => store.Find("etsy", "shop-a", "p1"));
    });

    [TestMethod]
    public void CorruptRowInOneShopDoesNotBlockAHealthyRowInAnotherShopForTheSameProduct() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        store.Save(Valid("etsy", "shop-b", "p1"));
        InsertRawRow(root, "etsy", "shop-a", "p1", "{corrupt");
        var healthy = store.Find("etsy", "shop-b", "p1");
        Assert.IsNotNull(healthy);
        Assert.ThrowsException<ChannelPlanCorruptException>(() => store.Find("etsy", "shop-a", "p1"));
    });

    [TestMethod]
    public void CorruptRowSurvivesRestartUntouchedUntilExplicitRepair() => WithRoot(root =>
    {
        _ = new ChannelProductsStore(root);
        InsertRawRow(root, "etsy", "shop-a", "p1", "{corrupt");
        var reopened = new ChannelProductsStore(root);
        Assert.AreEqual(1, reopened.CorruptPlans().Count);
        Assert.ThrowsException<ChannelPlanCorruptException>(() => reopened.Find("etsy", "shop-a", "p1"));
    });

    [TestMethod]
    public void SaveOverAnExistingCorruptRowStillEnforcesStaleVersionCheck() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        InsertRawRow(root, "etsy", "shop-a", "p1", "{corrupt", version: 3);
        var attempt = Valid("etsy", "shop-a", "p1"); // Version defaults to 0
        Assert.ThrowsException<InvalidOperationException>(() => store.Save(attempt), "A corrupt existing row at Version=3 must not be silently overwritten by a caller assuming Version=0.");
    });
}
