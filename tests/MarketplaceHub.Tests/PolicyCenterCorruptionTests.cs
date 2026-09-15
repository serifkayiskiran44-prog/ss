using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2558: a malformed/legacy-invalid StockPolicies or
/// PricePolicies row must never crash ListStockPolicies()/ListPricePolicies()
/// - it must be isolated and reported, never auto-deleted/repaired/normalized.
[TestClass]
public sealed class PolicyCenterCorruptionTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "policy-center-corrupt-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void InsertRawStockPolicy(string root, string channel, string shop, string json)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO StockPolicies(Channel,Shop,Json) VALUES($c,$s,$j)";
        cmd.Parameters.AddWithValue("$c", channel); cmd.Parameters.AddWithValue("$s", shop); cmd.Parameters.AddWithValue("$j", json);
        cmd.ExecuteNonQuery();
    }

    static void InsertRawPricePolicy(string root, string channel, string shop, string json)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO PricePolicies(Channel,Shop,Json) VALUES($c,$s,$j)";
        cmd.Parameters.AddWithValue("$c", channel); cmd.Parameters.AddWithValue("$s", shop); cmd.Parameters.AddWithValue("$j", json);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void NormalStockAndPricePolicyListsAreUnaffected() => WithRoot(root =>
    {
        var store = new CatalogStore(root);
        store.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "shop-a", SafetyStock = 2 });
        store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop-a", Currency = "USD" });
        Assert.AreEqual(1, store.ListStockPolicies().Count);
        Assert.AreEqual(1, store.ListPricePolicies().Count);
        Assert.AreEqual(0, store.CorruptStockPolicies().Count);
        Assert.AreEqual(0, store.CorruptPricePolicies().Count);
    });

    [TestMethod]
    public void OneMalformedStockPolicyDoesNotHideHealthyOnes() => WithRoot(root =>
    {
        var store = new CatalogStore(root);
        store.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "shop-a" });
        store.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "shop-b" });
        InsertRawStockPolicy(root, "etsy", "shop-bad", "{not-json");
        Assert.AreEqual(2, store.ListStockPolicies().Count);
        Assert.AreEqual(1, store.CorruptStockPolicies().Count);
    });

    [TestMethod]
    public void OneMalformedPricePolicyDoesNotHideHealthyOnes() => WithRoot(root =>
    {
        var store = new CatalogStore(root);
        store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop-a", Currency = "USD" });
        InsertRawPricePolicy(root, "etsy", "shop-bad", "{not-json");
        Assert.AreEqual(1, store.ListPricePolicies().Count);
        Assert.AreEqual(1, store.CorruptPricePolicies().Count);
    });

    [TestMethod]
    public void TruncatedJsonIsQuarantined() => WithRoot(root =>
    {
        var store = new CatalogStore(root);
        InsertRawStockPolicy(root, "etsy", "shop-a", "{\"Channel\":\"etsy\"");
        Assert.AreEqual(0, store.ListStockPolicies().Count);
        Assert.AreEqual(1, store.CorruptStockPolicies().Count);
    });

    [TestMethod]
    public void NullJsonLiteralIsQuarantined() => WithRoot(root =>
    {
        var store = new CatalogStore(root);
        InsertRawStockPolicy(root, "etsy", "shop-a", "null");
        Assert.AreEqual(0, store.ListStockPolicies().Count);
        Assert.AreEqual(1, store.CorruptStockPolicies().Count);
    });

    [TestMethod]
    public void WrongShopIdentityInPayloadIsQuarantined() => WithRoot(root =>
    {
        var store = new CatalogStore(root);
        var wrongShop = System.Text.Json.JsonSerializer.Serialize(new StockPolicy { Channel = "etsy", Shop = "shop-b" });
        InsertRawStockPolicy(root, "etsy", "shop-a", wrongShop);
        Assert.AreEqual(0, store.ListStockPolicies().Count);
        var corrupt = store.CorruptStockPolicies().Single();
        Assert.AreEqual("shop-a", corrupt.Shop);
        StringAssert.Contains(corrupt.Reason, "Kimlik");
    });

    [TestMethod]
    public void LegacyInvalidNumericStockValuesAreQuarantinedNotAccepted() => WithRoot(root =>
    {
        var store = new CatalogStore(root);
        var negative = System.Text.Json.JsonSerializer.Serialize(new StockPolicy { Channel = "etsy", Shop = "shop-a", SafetyStock = -5 });
        InsertRawStockPolicy(root, "etsy", "shop-a", negative);
        Assert.AreEqual(0, store.ListStockPolicies().Count);
        Assert.AreEqual(1, store.CorruptStockPolicies().Count);
    });

    [TestMethod]
    public void LegacyInvalidPricePolicyFieldsAreQuarantined() => WithRoot(root =>
    {
        var store = new CatalogStore(root);
        var invalid = System.Text.Json.JsonSerializer.Serialize(new PricePolicy { Channel = "etsy", Shop = "shop-a", Currency = "USD", TryPerUnit = -1 });
        InsertRawPricePolicy(root, "etsy", "shop-a", invalid);
        Assert.AreEqual(0, store.ListPricePolicies().Count);
        Assert.AreEqual(1, store.CorruptPricePolicies().Count);
    });

    [TestMethod]
    public void DuplicateDisplayLabelDifferentShopsRemainIndependentlyListable() => WithRoot(root =>
    {
        var store = new CatalogStore(root);
        store.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "Mağaza A" });
        store.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "Mağaza A (2)" });
        Assert.AreEqual(2, store.ListStockPolicies().Count);
    });

    [TestMethod]
    public void RestartKeepsCorruptionStateDeterministic() => WithRoot(root =>
    {
        var store = new CatalogStore(root);
        store.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "shop-a" });
        InsertRawStockPolicy(root, "etsy", "shop-bad", "{not-json");
        var reopened = new CatalogStore(root);
        Assert.AreEqual(1, reopened.ListStockPolicies().Count);
        Assert.AreEqual(1, reopened.CorruptStockPolicies().Count);
    });

    [TestMethod]
    public void HealthyPolicyPreviewStillWorksWhileAnotherRowIsCorrupt() => WithRoot(root =>
    {
        var store = new CatalogStore(root);
        var product = store.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Ürün", Price = 10, Stock = 20, Currency = "USD" });
        store.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "shop-a", SafetyStock = 2 });
        InsertRawStockPolicy(root, "etsy", "shop-bad", "{not-json");
        var preview = store.PreviewStockDetailed("etsy", "shop-a", product.Id);
        Assert.AreEqual(18, preview.AvailableStock);
    });
}
