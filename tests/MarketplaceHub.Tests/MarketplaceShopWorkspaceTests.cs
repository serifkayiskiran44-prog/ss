#nullable enable
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class MarketplaceShopWorkspaceTests
{
    string directory = "";

    [TestInitialize]
    public void Setup() => directory = Path.Combine(Path.GetTempPath(), "shop-workspace-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [TestMethod]
    public void RowsAndStatusFiltersAreScopedToTheExactConnection()
    {
        var (first, second, linked, error, unlinked) = SeedTwoAccounts();
        var bindings = new ProductChannelBindingStore(directory);
        bindings.Save(Binding(linked.Id, first.Id, "remote-a", "Active"), 0);
        bindings.Save(Binding(error.Id, first.Id, "remote-error", "Error: rejected"), 0);
        bindings.Save(Binding(linked.Id, second.Id, "remote-b", "Active"), 0);

        var firstModel = new MarketplaceShopProductsModel(first.Id, directory);
        var secondModel = new MarketplaceShopProductsModel(second.Id, directory);

        CollectionAssert.AreEquivalent(new[] { linked.Id, error.Id, unlinked.Id }, firstModel.Filter(new()).Select(x => x.ProductId).ToArray());
        CollectionAssert.AreEqual(new[] { linked.Id }, firstModel.Filter(new(Binding: MarketplaceShopBindingFilter.Linked)).Select(x => x.ProductId).ToArray());
        CollectionAssert.AreEqual(new[] { unlinked.Id }, firstModel.Filter(new(Binding: MarketplaceShopBindingFilter.Unlinked)).Select(x => x.ProductId).ToArray());
        CollectionAssert.AreEqual(new[] { error.Id }, firstModel.Filter(new(Binding: MarketplaceShopBindingFilter.Error)).Select(x => x.ProductId).ToArray());
        Assert.AreEqual("remote-a", firstModel.Filter(new()).Single(x => x.ProductId == linked.Id).RemoteId);
        Assert.AreEqual("remote-b", secondModel.Filter(new()).Single(x => x.ProductId == linked.Id).RemoteId);
    }

    [TestMethod]
    public void SelectAllFilteredCreatesAnExactImmutableSnapshot()
    {
        var (connection, _, linked, _, _) = SeedTwoAccounts();
        new ProductChannelBindingStore(directory).Save(Binding(linked.Id, connection.Id, "remote-a", "Active"), 0);
        var model = new MarketplaceShopProductsModel(connection.Id, directory);

        var snapshot = model.SelectAllFiltered(new(Binding: MarketplaceShopBindingFilter.Linked));
        var later = new CatalogStore(directory).CreateManual(new() { Sku = "LATER", Name = "Later", Currency = "TRY" });
        new ProductChannelBindingStore(directory).Save(Binding(later.Id, connection.Id, "remote-later", "Active"), 0);

        CollectionAssert.AreEqual(new[] { linked.Id }, snapshot.ProductIds.ToArray());
        CollectionAssert.AreEqual(new[] { linked.Id }, model.Resolve(snapshot).ToArray(), "A saved selection must never expand when the catalog changes.");
    }

    [TestMethod]
    public void BulkMenusAreChannelSpecificAndCapabilityGated()
    {
        var store = new MarketplaceConnectionStore(directory);
        var trendyol = store.Save("trendyol", "101", "Trendyol", true);
        var etsy = store.Save("etsy", "202", "Etsy", true);

        var trendyolMenu = new MarketplaceShopProductsModel(trendyol.Id, directory).BulkOperations;
        var etsyMenu = new MarketplaceShopProductsModel(etsy.Id, directory).BulkOperations;

        CollectionAssert.IsSubsetOf(new[] { MarketplaceShopBulkOperation.Category, MarketplaceShopBulkOperation.Brand, MarketplaceShopBulkOperation.Delivery, MarketplaceShopBulkOperation.PricePreview, MarketplaceShopBulkOperation.StockPreview, MarketplaceShopBulkOperation.ContentPreview }, trendyolMenu.ToArray());
        Assert.IsFalse(trendyolMenu.Contains(MarketplaceShopBulkOperation.Taxonomy));
        CollectionAssert.IsSubsetOf(new[] { MarketplaceShopBulkOperation.Taxonomy, MarketplaceShopBulkOperation.Properties, MarketplaceShopBulkOperation.Shipping, MarketplaceShopBulkOperation.Readiness, MarketplaceShopBulkOperation.CreatePreview, MarketplaceShopBulkOperation.PricePreview, MarketplaceShopBulkOperation.StockPreview }, etsyMenu.ToArray());
        Assert.IsFalse(etsyMenu.Contains(MarketplaceShopBulkOperation.Brand));
    }

    [TestMethod]
    public void ManagementAndMappingChangesRequireApprovedRevisionedPreview()
    {
        var (connection, other, product, _, _) = SeedTwoAccounts();
        var bindings = new ProductChannelBindingStore(directory);
        var saved = bindings.Save(Binding(product.Id, connection.Id, "remote-a", "Active"), 0);
        var otherSaved = bindings.Save(Binding(product.Id, other.Id, "remote-b", "Active") with { CategoryId = "other-category" }, 0);
        var model = new MarketplaceShopProductsModel(connection.Id, directory);
        var selection = model.SelectPage(new[] { product.Id });
        var preview = model.PreviewBulk(selection, MarketplaceShopBulkOperation.Management,
            new(ManageContent: false, ManagePrice: null, ManageStock: false, CategoryId: "new-category"));

        Assert.ThrowsException<InvalidOperationException>(() => model.Apply(preview, explicitlyApproved: false));
        Assert.AreEqual(saved.Version, bindings.Get(product.Id, connection.Id)!.Version);
        var receipt = model.Apply(preview, explicitlyApproved: true);
        var changed = bindings.Get(product.Id, connection.Id)!;

        Assert.AreEqual(connection.Id, receipt.ConnectionId);
        Assert.IsFalse(changed.ManageContent);
        Assert.IsTrue(changed.ManagePrice, "Untouched flags must be preserved.");
        Assert.IsFalse(changed.ManageStock);
        Assert.AreEqual("new-category", changed.CategoryId);
        Assert.AreEqual(otherSaved, bindings.Get(product.Id, other.Id), "The same product's other account binding must remain byte-for-byte unchanged.");
        Assert.AreEqual(0, receipt.RemoteWrites);
    }

    [TestMethod]
    public void StaleBindingOrDisabledAccountInvalidatesBulkApply()
    {
        var (connection, _, product, _, _) = SeedTwoAccounts();
        var bindings = new ProductChannelBindingStore(directory);
        var saved = bindings.Save(Binding(product.Id, connection.Id, "remote-a", "Active"), 0);
        var model = new MarketplaceShopProductsModel(connection.Id, directory);
        var preview = model.PreviewBulk(model.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.Management,
            new(ManagePrice: false));
        bindings.Save(saved with { ManageStock = false }, saved.Version);

        Assert.ThrowsException<InvalidOperationException>(() => model.Apply(preview, true));

        var fresh = new MarketplaceShopProductsModel(connection.Id, directory);
        var disabledPreview = fresh.PreviewBulk(fresh.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.Management,
            new(ManagePrice: false));
        new MarketplaceConnectionStore(directory).SetEnabled(connection.Id, false);
        Assert.ThrowsException<InvalidOperationException>(() => fresh.Apply(disabledPreview, true));
    }

    [TestMethod]
    public void SpecializedOnlyBulkOperationCannotReturnAFakeLocalSuccess()
    {
        var (connection, _, product, _, _) = SeedTwoAccounts();
        new ProductChannelBindingStore(directory).Save(Binding(product.Id, connection.Id, "remote-a", "Active"), 0);
        var model = new MarketplaceShopProductsModel(connection.Id, directory);
        var preview = model.PreviewBulk(model.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.Brand, new(Value: "42"));

        Assert.ThrowsException<InvalidOperationException>(() => model.Apply(preview, true));
        Assert.AreEqual(1L, new ProductChannelBindingStore(directory).Get(product.Id, connection.Id)!.Version);
    }

    [TestMethod]
    public void LocalMappingPreviewRejectsAnUnlinkedProductRatherThanReturningFakeSuccess()
    {
        var (connection, _, _, _, unlinked) = SeedTwoAccounts();
        var model = new MarketplaceShopProductsModel(connection.Id, directory);
        var selection = model.SelectPage(new[] { unlinked.Id });

        Assert.ThrowsException<InvalidOperationException>(() =>
            model.PreviewBulk(selection, MarketplaceShopBulkOperation.Category, new(CategoryId: "42")));
        Assert.IsNull(new ProductChannelBindingStore(directory).Get(unlinked.Id, connection.Id));
    }

    [TestMethod]
    public void SettingsUseExactConnectionIdAndCompareAndSwapRevision()
    {
        var connections = new MarketplaceConnectionStore(directory);
        var first = connections.Save("trendyol", "101", "First", true);
        var second = connections.Save("trendyol", "202", "Second", true);
        var store = new MarketplaceShopSettingsStore(directory);
        var firstInitial = store.Load(first.Id);
        var secondInitial = store.Load(second.Id);

        var saved = store.Save(firstInitial with
        {
            ProductRules = firstInitial.ProductRules with { DefaultCategoryId = "11", DefaultTemplateId = "ship-a", ManagePrice = true },
            Sync = firstInitial.Sync with { ProductsEnabled = true, IntervalMinutes = 30 }
        }, firstInitial.Revision, first.Revision);

        Assert.AreEqual(1L, saved.Revision);
        Assert.AreEqual("11", store.Load(first.Id).ProductRules.DefaultCategoryId);
        Assert.AreEqual("", store.Load(second.Id).ProductRules.DefaultCategoryId);
        Assert.ThrowsException<InvalidOperationException>(() => store.Save(firstInitial, firstInitial.Revision, first.Revision));
        Assert.AreEqual(secondInitial, store.Load(second.Id));
    }

    [TestMethod]
    public void BlankSettingsEditsPreserveExistingRulesAndConnectionChangesFenceSave()
    {
        var connections = new MarketplaceConnectionStore(directory);
        var connection = connections.Save("etsy", "202", "Etsy", true);
        var store = new MarketplaceShopSettingsStore(directory);
        var initial = store.Load(connection.Id);
        var saved = store.Save(initial with { ProductRules = initial.ProductRules with { DefaultTemplateId = "shipping-1", ManageStock = true } }, 0, connection.Revision);

        var merged = store.SavePatch(connection.Id, new(ProductRules: new(DefaultTemplateId: "", ManageStock: null)), saved.Revision, connection.Revision);
        Assert.AreEqual("shipping-1", merged.ProductRules.DefaultTemplateId);
        Assert.IsTrue(merged.ProductRules.ManageStock);

        connections.SetEnabled(connection.Id, false);
        Assert.ThrowsException<InvalidOperationException>(() => store.SavePatch(connection.Id, new(Sync: new(ProductsEnabled: true)), merged.Revision, connection.Revision));
    }

    [TestMethod]
    public void VerifiedSeededAccountKeepsItsOperationalEvidenceDuringSettingsSave()
    {
        var connections = new MarketplaceConnectionStore(directory);
        connections.List();
        var seeded = connections.Get("trendyol:default")!;
        Assert.AreEqual(ConnectionTestApplyResult.Applied, connections.RecordTest(seeded.Id, seeded.Revision, success: true));
        var verified = connections.Get(seeded.Id)!;
        var store = new MarketplaceShopSettingsStore(directory);

        var saved = store.Save(store.Load(verified.Id), expectedRevision: 0, expectedConnectionRevision: verified.Revision);

        Assert.AreEqual(1L, saved.Revision);
    }

    [TestMethod]
    public void MissingDisabledAndCorruptAccountsCannotOpenCommonPanels()
    {
        var connections = new MarketplaceConnectionStore(directory);
        var disabled = connections.Save("trendyol", "101", "Disabled", false);
        Assert.ThrowsException<InvalidOperationException>(() => new MarketplaceShopProductsModel("missing", directory));
        Assert.ThrowsException<InvalidOperationException>(() => new MarketplaceShopProductsModel(disabled.Id, directory));
        Assert.ThrowsException<InvalidOperationException>(() => new MarketplaceShopSettingsStore(directory).Load(disabled.Id));

        var enabled = connections.Save("etsy", "202", "Enabled", true);
        var openModel = new MarketplaceShopProductsModel(enabled.Id, directory);
        connections.SetEnabled(enabled.Id, false);
        Assert.ThrowsException<InvalidOperationException>(() => openModel.Filter(new()));
    }

    [TestMethod]
    public void CommonProductAndSettingsPanelsExposeWideAccountScopedControls() => InSta(() =>
    {
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", "101", "Shop", true);
        var products = new MarketplaceShopProductsPanel(connection.Id, directory);
        var settings = new MarketplaceShopSettingsPanel(connection.Id, directory);
        var grid = Walk(products).OfType<DataGrid>().Single(x => x.Name == "MarketplaceShopProducts");
        var paths = grid.Columns.OfType<DataGridBoundColumn>().Select(x => ((System.Windows.Data.Binding)x.Binding).Path.Path).ToArray();

        CollectionAssert.IsSubsetOf(new[] { "Sku", "Gtin", "Name", "LocalStock", "LocalPrice", "RemoteStock", "RemotePrice", "Category", "Brand", "RemoteState", "Error", "ManagementState" }, paths);
        Assert.IsTrue(Walk(products).OfType<Button>().Any(x => x.Name == "MarketplaceSelectAllFiltered"));
        CollectionAssert.AreEqual(new[] { "Active", "Connection", "Product rules", "Order rules", "Sync" },
            Walk(settings).OfType<TabControl>().Single(x => x.Name == "MarketplaceShopSettingsSections").Items.Cast<TabItem>().Select(x => x.Header!.ToString()).ToArray());
    });

    [TestMethod]
    public void SpecializedTrendyolAndEtsyWorkspacesHostCommonAccountPanelsWithoutLosingControls() => InSta(() =>
    {
        var connections = new MarketplaceConnectionStore(directory);
        var trendyol = connections.Save("trendyol", "101", "Trendyol", true);
        var etsy = connections.Save("etsy", "202", "Etsy", true);
        var trendyolPanel = new TrendyolWorkspacePanel(trendyol.Id, directory);
        using var etsyPanel = new EtsyWorkspacePanel(etsy.Id, directory);

        CollectionAssert.AreEqual(new[] { "Hesap ürünleri", "Trendyol işlemleri" },
            Walk(trendyolPanel).OfType<TabControl>().Single(x => x.Name == "TrendyolAccountProductSections").Items.Cast<TabItem>().Select(x => x.Header!.ToString()).ToArray());
        CollectionAssert.AreEqual(new[] { "Hesap ürünleri", "Etsy işlemleri" },
            Walk(etsyPanel).OfType<TabControl>().Single(x => x.Name == "EtsyAccountProductSections").Items.Cast<TabItem>().Select(x => x.Header!.ToString()).ToArray());
        Assert.IsTrue(Walk(trendyolPanel).OfType<Button>().Any(x => x.Name == "TrendyolBuildPreview"));
        Assert.IsTrue(Walk(etsyPanel).OfType<Button>().Any(x => x.Name == "EtsySend"));
        Assert.IsTrue(Walk(trendyolPanel).OfType<TabItem>().Any(x => Equals(x.Header, "Hesap kuralları")));
        Assert.IsTrue(Walk(etsyPanel).OfType<TabItem>().Any(x => Equals(x.Header, "Hesap kuralları")));
    });

    (MarketplaceConnection first, MarketplaceConnection second, CatalogProduct linked, CatalogProduct error, CatalogProduct unlinked) SeedTwoAccounts()
    {
        var catalog = new CatalogStore(directory);
        var linked = catalog.CreateManual(new() { Sku = "LINK", Barcode = "111", Gtin = "GTIN-1", Name = "Linked", Price = 10, Stock = 4, Currency = "TRY", Category = "Cat", Brand = "Brand" });
        var error = catalog.CreateManual(new() { Sku = "ERR", Barcode = "222", Name = "Error", Price = 20, Stock = 2, Currency = "TRY" });
        var unlinked = catalog.CreateManual(new() { Sku = "NEW", Barcode = "333", Name = "Unlinked", Price = 30, Stock = 1, Currency = "TRY" });
        var connections = new MarketplaceConnectionStore(directory);
        return (connections.Save("trendyol", "101", "First", true), connections.Save("trendyol", "202", "Second", true), linked, error, unlinked);
    }

    static ProductChannelBinding Binding(string productId, string connectionId, string remoteId, string state) =>
        new(productId, connectionId, remoteId, "remote-sku", "remote-barcode", true, true, true, "", "", state, 0, DateTime.MinValue);

    static void InSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Walk(child)) yield return descendant;
    }
}
