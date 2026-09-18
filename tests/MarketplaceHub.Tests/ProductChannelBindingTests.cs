using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Etsy;
using TrMarketplaceHubDesktop.Trendyol;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class ProductChannelBindingTests
{
    string directory = "";
    CatalogProduct product = null!;

    [TestInitialize]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "product-channel-bindings-" + Guid.NewGuid().ToString("N"));
        product = new CatalogStore(directory).CreateManual(new() { Sku = "LOCAL-SKU", Barcode = "869000000001", Name = "Item", Price = 10, Stock = 2, Currency = "TRY" });
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [TestMethod]
    public void SameProductCanBindIndependentlyToTwoTrendyolAccountsAndEtsy()
    {
        var connections = new MarketplaceConnectionStore(directory);
        var trendyolA = connections.Save("trendyol", "101", "Trendyol A", true);
        var trendyolB = connections.Save("trendyol", "202", "Trendyol B", true);
        var etsy = connections.Save("etsy", "303", "Etsy", true);
        var store = new ProductChannelBindingStore(directory);

        var a = store.Save(NewBinding(trendyolA.Id, "remote-a", "A-SKU", "A-BAR"), 0);
        var b = store.Save(NewBinding(trendyolB.Id, "remote-b", "B-SKU", "B-BAR"), 0);
        var e = store.Save(NewBinding(etsy.Id, "remote-e", "E-SKU", "E-BAR"), 0);

        CollectionAssert.AreEquivalent(new[] { trendyolA.Id, trendyolB.Id, etsy.Id }, store.List(product.Id).Select(x => x.ConnectionId).ToArray());
        Assert.AreEqual("A-SKU", a.RemoteSku);
        Assert.AreEqual("B-BAR", b.RemoteBarcode);
        Assert.AreEqual(1L, e.Version);
        Assert.AreEqual("LOCAL-SKU", new CatalogStore(directory).Products().Single().Sku);
        Assert.AreEqual("869000000001", new CatalogStore(directory).Products().Single().Barcode);
    }

    [TestMethod]
    public void SaveUsesConnectionScopedUniquenessAndCompareAndSwap()
    {
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", "101", "Trendyol", true);
        var store = new ProductChannelBindingStore(directory);
        var first = store.Save(NewBinding(connection.Id, "11", "REMOTE", "869000000001"), 0);

        Assert.ThrowsException<InvalidOperationException>(() => store.Save(first with { RemoteSku = "CHANGED" }, 0));
        var second = store.Save(first with { RemoteSku = "CHANGED" }, first.Version);

        Assert.AreEqual(2L, second.Version);
        Assert.AreEqual("CHANGED", store.Get(product.Id, connection.Id)!.RemoteSku);
    }

    [TestMethod]
    public void VerifiedLegacyProfilesMigrateOnceWithoutChangingWorkspaceOrListings()
    {
        var connections = new MarketplaceConnectionStore(directory);
        var trendyol = connections.Save("trendyol", "101", "Trendyol", true);
        var etsy = connections.Save("etsy", "303", "Etsy", true);
        var trendyolStore = new TrendyolWorkspaceStore(directory);
        var trendyolState = trendyolStore.Load("101");
        trendyolState.Products.Add(new("TY-BAR", "TY-SKU", "Remote", 77, 2, 10, 12, true));
        trendyolState.Profiles.Add(new() { ProductId = product.Id, IntegrationCode = "TY-BAR", ListingBarcode = "TY-BAR", CategoryId = 5, DeliveryTemplateId = "delivery" });
        trendyolStore.Save(trendyolState);
        var etsyStore = new EtsyWorkspaceStore(directory);
        var etsyState = etsyStore.Load("303");
        etsyState.Listings.Add(new(88, "Remote", "active", 2, 10, "USD", "ETSY-SKU"));
        etsyState.Profiles.Add(new() { ProductId = product.Id, ListingId = 88, TaxonomyId = 9, TemplateId = "template" });
        etsyStore.Save(etsyState);
        var trendyolBefore = JsonSerializer.Serialize(trendyolStore.Load("101"));
        var etsyBefore = JsonSerializer.Serialize(etsyStore.Load("303"));
        var store = new ProductChannelBindingStore(directory);

        var first = store.MigrateVerifiedProfiles();
        var second = new ProductChannelBindingStore(directory).MigrateVerifiedProfiles();

        Assert.AreEqual(2, first);
        Assert.AreEqual(0, second);
        Assert.AreEqual("77", store.Get(product.Id, trendyol.Id)!.RemoteId);
        Assert.AreEqual("88", store.Get(product.Id, etsy.Id)!.RemoteId);
        Assert.AreEqual(trendyolBefore, JsonSerializer.Serialize(trendyolStore.Load("101")));
        Assert.AreEqual(etsyBefore, JsonSerializer.Serialize(etsyStore.Load("303")));
    }

    ProductChannelBinding NewBinding(string connectionId, string remoteId, string remoteSku, string remoteBarcode) =>
        new(product.Id, connectionId, remoteId, remoteSku, remoteBarcode, true, true, true, "", "", "Active", 0, default);
}
