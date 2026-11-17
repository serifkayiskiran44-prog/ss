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
        product = new CatalogStore(directory).CreateManual(new() { Sku = "LOCAL-SKU", Barcode = "869000000001", Name = "Item", Price = 10, Stock = 0, Currency = "TRY" });
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
    public void BoundProductCannotBeDeletedThroughNormalCatalogDelete()
    {
        SeedBinding();

        var error = Assert.ThrowsException<InvalidOperationException>(() => new CatalogStore(directory).DeleteProduct(product));

        StringAssert.Contains(error.Message, "mağaza bağlantısı");
        Assert.AreEqual("LOCAL-SKU", new CatalogStore(directory).Products().Single().Sku);
        Assert.AreEqual("869000000001", new CatalogStore(directory).Products().Single().Barcode);
    }

    [TestMethod]
    public void UndoCannotRemoveAProductThatHasAnAccountBinding()
    {
        SeedBinding();
        var receipt = new CatalogUndoReceipt("remove-bound", Array.Empty<CatalogProduct>(), new[] { product });

        var error = Assert.ThrowsException<InvalidOperationException>(() => new CatalogStore(directory).Undo(receipt));

        StringAssert.Contains(error.Message, "mağaza bağlantısı");
        Assert.AreEqual(product.Id, new CatalogStore(directory).Products().Single().Id);
        Assert.IsNotNull(new ProductChannelBindingStore(directory).Get(product.Id, new MarketplaceConnectionStore(directory).List(false).Single().Id));
    }

    [TestMethod]
    public void CorruptRowRecoveryCannotRemoveARealProductThatHasAnAccountBinding()
    {
        var connection = SeedBinding();
        using (var database = new SqliteConnection("Data Source=" + Path.Combine(directory, "catalog.db")))
        {
            database.Open();
            using (var indexes = database.CreateCommand())
            {
                indexes.CommandText = "DROP INDEX IF EXISTS IX_CatalogProducts_Sku;DROP INDEX IF EXISTS IX_CatalogProducts_Barcode;DROP INDEX IF EXISTS IX_CatalogProducts_Brand;DROP INDEX IF EXISTS IX_CatalogProducts_Category";
                indexes.ExecuteNonQuery();
            }
            using var corrupt = database.CreateCommand();
            corrupt.CommandText = "UPDATE CatalogProducts SET Json='{broken' WHERE Id=$id";
            corrupt.Parameters.AddWithValue("$id", product.Id);
            corrupt.ExecuteNonQuery();
        }

        var error = Assert.ThrowsException<InvalidOperationException>(() => new CatalogStore(directory).DeleteCorruptRow(product.Id));

        StringAssert.Contains(error.Message, "mağaza bağlantısı");
        Assert.AreEqual(1, new CatalogStore(directory).CorruptProducts().Count);
        Assert.IsNotNull(new ProductChannelBindingStore(directory).Get(product.Id, connection.Id));
    }

    [TestMethod]
    public void VerifiedLegacyProfilesMigrateOnceWithoutChangingWorkspaceOrListings()
    {
        CredentialStore.Save(new EtsyCredentials("etsy-key", "etsy-secret", "etsy-token", "303"), directory);
        new TrendyolSettingsStore(Path.Combine(directory, "trendyol.bin")).Save(new TrendyolSettings("101", "trendyol-key", "trendyol-secret", "Trendyol"));
        var migration = new MarketplaceConnectionMigration(directory).ImportLegacy();
        Assert.AreEqual(2, migration.Count(x => x.State == MarketplaceConnectionMigrationState.Imported));
        var connections = new MarketplaceConnectionStore(directory);
        var trendyol = connections.Find("trendyol", "101")!;
        var etsy = connections.Find("etsy", "303")!;
        SeedWorkspaceProfiles(trendyol, etsy);
        var trendyolStore = new TrendyolWorkspaceStore(directory);
        var etsyStore = new EtsyWorkspaceStore(directory);
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

    [TestMethod]
    public void EnabledNotConfiguredAccountsDoNotAuthorizeLegacyProfileMigration()
    {
        var connections = new MarketplaceConnectionStore(directory);
        var trendyol = connections.Save("trendyol", "101", "Trendyol", true);
        var etsy = connections.Save("etsy", "303", "Etsy", true);
        Assert.AreEqual("NOT_CONFIGURED", trendyol.Status);
        Assert.AreEqual("NOT_CONFIGURED", etsy.Status);
        SeedWorkspaceProfiles(trendyol, etsy);
        var store = new ProductChannelBindingStore(directory);

        var migrated = store.MigrateVerifiedProfiles();

        Assert.AreEqual(0, migrated);
        Assert.AreEqual(0, store.List().Count);
    }

    [TestMethod]
    public void CompensatingConnectionDeleteCannotOrphanAProductBinding()
    {
        var connections = new MarketplaceConnectionStore(directory);
        var connection = connections.Save("trendyol", "101", "Trendyol", true);
        var bindings = new ProductChannelBindingStore(directory);
        bindings.Save(NewBinding(connection.Id, "11", "REMOTE", "REMOTE-BAR"), 0);

        var deleted = connections.DeleteIfUntouchedSinceCreate(connection.Id, connection.Revision);

        Assert.IsFalse(deleted, "A compensation path must not delete connection metadata once a product binding depends on it.");
        Assert.IsNotNull(connections.Get(connection.Id));
        Assert.IsNotNull(bindings.Get(product.Id, connection.Id));
    }

    ProductChannelBinding NewBinding(string connectionId, string remoteId, string remoteSku, string remoteBarcode) =>
        new(product.Id, connectionId, remoteId, remoteSku, remoteBarcode, true, true, true, "", "", "Active", 0, default);

    MarketplaceConnection SeedBinding()
    {
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", "101", "Trendyol", true);
        new ProductChannelBindingStore(directory).Save(NewBinding(connection.Id, "11", "REMOTE", "REMOTE-BAR"), 0);
        return connection;
    }

    void SeedWorkspaceProfiles(MarketplaceConnection trendyol, MarketplaceConnection etsy)
    {
        var trendyolStore = new TrendyolWorkspaceStore(directory);
        var trendyolState = trendyolStore.Load(trendyol.ShopId);
        trendyolState.Products.Add(new("TY-BAR", "TY-SKU", "Remote", 77, 2, 10, 12, true));
        trendyolState.Profiles.Add(new() { ProductId = product.Id, IntegrationCode = "TY-BAR", ListingBarcode = "TY-BAR", CategoryId = 5, DeliveryTemplateId = "delivery" });
        trendyolStore.Save(trendyolState);
        var etsyStore = new EtsyWorkspaceStore(directory);
        var etsyState = etsyStore.Load(etsy.ShopId);
        etsyState.Listings.Add(new(88, "Remote", "active", 2, 10, "USD", "ETSY-SKU"));
        etsyState.Profiles.Add(new() { ProductId = product.Id, ListingId = 88, TaxonomyId = 9, TemplateId = "template" });
        etsyStore.Save(etsyState);
    }
}
