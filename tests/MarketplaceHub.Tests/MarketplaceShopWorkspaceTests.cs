#nullable enable
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Etsy;

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
    public void RestrictedInjectedAdaptersExposeNoMutationOrContentPreviewCommands()
    {
        var store = new MarketplaceConnectionStore(directory);
        var trendyol = store.Save("trendyol", "101", "Trendyol", true);
        var etsy = store.Save("etsy", "202", "Etsy", true);
        var localOnly = new MarketplaceAdapterRegistry(new[] { new RestrictedAdapter("trendyol", MarketplaceCapabilities.LocalOnly) });
        var productsReadOnly = new MarketplaceAdapterRegistry(new[]
        {
            new RestrictedAdapter("etsy", new(new HashSet<MarketplaceOperation> { MarketplaceOperation.ProductsRead }))
        });

        Assert.AreEqual(0, new MarketplaceShopProductsModel(trendyol.Id, directory, localOnly).BulkOperations.Count);
        Assert.AreEqual(0, new MarketplaceShopProductsModel(etsy.Id, directory, productsReadOnly).BulkOperations.Count);
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
    public void FailedConnectionTestAtTheSameRevisionInvalidatesBulkApplyInsideTheTransaction()
    {
        var connections = new MarketplaceConnectionStore(directory);
        connections.List();
        var seeded = connections.Get("trendyol:default")!;
        Assert.AreEqual(ConnectionTestApplyResult.Applied, connections.RecordTest(seeded.Id, seeded.Revision, success: true));
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "TX", Name = "Transaction", Currency = "TRY" });
        var bindings = new ProductChannelBindingStore(directory);
        var binding = bindings.Save(Binding(product.Id, seeded.Id, "remote", "Active"), 0);
        var model = new MarketplaceShopProductsModel(seeded.Id, directory);
        var preview = model.PreviewBulk(model.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.Management, new(ManageStock: false));

        Assert.AreEqual(ConnectionTestApplyResult.Applied, connections.RecordTest(seeded.Id, seeded.Revision, success: false, error: "offline"));

        Assert.ThrowsException<InvalidOperationException>(() => model.Apply(preview, true));
        Assert.AreEqual(binding.Version, bindings.Get(product.Id, seeded.Id)!.Version);
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
    public void SpecialistButtonPersistsTheExactSelectionAndBindingRevisions() => InSta(() =>
    {
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", "101", "Shop", true);
        var catalog = new CatalogStore(directory);
        var bindings = new ProductChannelBindingStore(directory);
        var ids = Enumerable.Range(1, 101).Select(index =>
        {
            var product = catalog.CreateManual(new() { Sku = $"S-{index}", Name = $"Product {index}", Currency = "TRY" });
            bindings.Save(Binding(product.Id, connection.Id, $"remote-{index}", "Active"), 0);
            return product.Id;
        }).ToArray();
        var panel = new MarketplaceShopProductsPanel(connection.Id, directory);
        MarketplaceShopSpecialistPreview? captured = null;
        panel.BulkPreviewRequested += preview => captured = preview;

        Walk(panel).OfType<Button>().Single(x => x.Name == "MarketplaceSelectAllFiltered").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Walk(panel).OfType<Button>().Single(x => x.Name == "MarketplaceBulk_Category").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.IsNotNull(captured);
        Assert.AreEqual(connection.Id, captured.ConnectionId);
        Assert.AreEqual(connection.Revision, captured.ConnectionRevision);
        CollectionAssert.AreEquivalent(ids, captured.Rows.Select(x => x.ProductId).ToArray());
        Assert.IsTrue(captured.Rows.All(x => x.BindingVersion == 1));
    });

    [TestMethod]
    public void SettingsPanelUsesTheConnectionRevisionCapturedWhenTheFormOpened() => InSta(() =>
    {
        var connections = new MarketplaceConnectionStore(directory);
        var connection = connections.Save("etsy", "202", "Etsy", true);
        var panel = new MarketplaceShopSettingsPanel(connection.Id, directory);
        Walk(panel).OfType<CheckBox>().Single(x => Equals(x.Content, "Bu hesap için mağaza kuralları etkin")).IsChecked = false;
        connections.SetEnabled(connection.Id, false);
        connections.SetEnabled(connection.Id, true);

        Walk(panel).OfType<Button>().Single(x => x.Name == "MarketplaceSaveShopSettings")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var persisted = new MarketplaceShopSettingsStore(directory).Load(connection.Id);
        Assert.IsTrue(persisted.Active);
        Assert.AreEqual(0L, persisted.Revision);
        Assert.IsTrue(Walk(panel).OfType<TextBlock>().Any(x => x.Text.Contains("değişti", StringComparison.OrdinalIgnoreCase)));
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

    [DataTestMethod]
    [DataRow("Category", TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.UpdateUnapproved)]
    [DataRow("Brand", TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.UpdateUnapproved)]
    [DataRow("Delivery", TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.Delivery)]
    public void TrendyolSpecialistHandlersBuildRealExactAccountPreviewsBeyondTheVisiblePage(
        string operation, TrMarketplaceHubDesktop.Trendyol.TrendyolOperation expected) => InSta(() =>
    {
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", "101", "Trendyol", true);
        new MarketplaceCredentialVault(directory).Save(connection.Id, connection.Channel, connection.ShopId,
            new TrendyolSettings("101", "key", "secret", "tests"));
        var catalog = new CatalogStore(directory);
        var bindings = new ProductChannelBindingStore(directory);
        var ids = Enumerable.Range(1, 101).Select(index =>
        {
            var product = catalog.CreateManual(new() { Sku = $"T-{index}", Barcode = $"B-{index}", Name = $"Product {index}", Currency = "TRY" });
            bindings.Save(Binding(product.Id, connection.Id, $"remote-{index}", "Active"), 0);
            return product.Id;
        }).ToArray();
        var panel = new TrendyolWorkspacePanel(connection.Id, directory);
        var common = Walk(panel).OfType<MarketplaceShopProductsPanel>().Single();

        Walk(common).OfType<Button>().Single(x => x.Name == "MarketplaceSelectAllFiltered").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Walk(common).OfType<Button>().Single(x => x.Name == $"MarketplaceBulk_{operation}").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.IsNotNull(panel.AccountSpecialistPreview);
        Assert.AreEqual(connection.Revision, panel.AccountSpecialistPreview.ConnectionRevision);
        Assert.IsTrue(panel.AccountSpecialistPreview.Rows.All(row => row.BindingVersion == 1));
        Assert.AreEqual(expected, panel.AccountSpecialistOperation);
        CollectionAssert.AreEquivalent(ids, panel.AccountSpecialistPlanProductIds.ToArray());
    });

    [DataTestMethod]
    [DataRow("Taxonomy")]
    [DataRow("Properties")]
    [DataRow("Shipping")]
    [DataRow("Readiness")]
    public void EtsySpecialistHandlersRouteTheExactAccountSnapshotToContentPreview(string operation) => InSta(() =>
    {
        var connection = new MarketplaceConnectionStore(directory).Save("etsy", "202", "Etsy", true);
        new MarketplaceCredentialVault(directory).Save(connection.Id, connection.Channel, connection.ShopId,
            new EtsyCredentials("key", "secret", "88.token", "202", GrantedScopes: new[] { "listings_r", "listings_w" }));
        var catalog = new CatalogStore(directory);
        var bindings = new ProductChannelBindingStore(directory);
        var ids = Enumerable.Range(1, 101).Select(index =>
        {
            var product = catalog.CreateManual(new() { Sku = $"E-{index}", Name = $"Product {index}", Currency = "USD" });
            bindings.Save(Binding(product.Id, connection.Id, $"remote-{index}", "Active"), 0);
            return product.Id;
        }).ToArray();
        using var panel = new EtsyWorkspacePanel(connection.Id, directory, new EtsyShopHandler());
        var common = Walk(panel).OfType<MarketplaceShopProductsPanel>().Single();

        Walk(common).OfType<Button>().Single(x => x.Name == "MarketplaceSelectAllFiltered").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Walk(common).OfType<Button>().Single(x => x.Name == $"MarketplaceBulk_{operation}").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitFor(panel.AccountSpecialistPreviewTask);

        Assert.IsNotNull(panel.AccountSpecialistPreview);
        Assert.AreEqual(connection.Revision, panel.AccountSpecialistPreview.ConnectionRevision);
        Assert.IsTrue(panel.AccountSpecialistPreview.Rows.All(row => row.BindingVersion == 1));
        Assert.AreEqual(TrMarketplaceHubDesktop.Etsy.EtsyOperation.Content, panel.AccountSpecialistOperation);
        CollectionAssert.AreEquivalent(ids, panel.AccountSpecialistPlanProductIds.ToArray());
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

    sealed class RestrictedAdapter(string channel, MarketplaceCapabilities capabilities) : IMarketplaceAdapter
    {
        public string Channel { get; } = channel;
        public MarketplaceCapabilities Capabilities { get; } = capabilities;
        public Task<IReadOnlyList<RemoteProductIdentity>> ReadProductsAsync(string connectionId, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<OrderSnapshot>> ReadOrdersAsync(string connectionId, DateTime fromUtc, CancellationToken token) =>
            throw new NotSupportedException();
    }

    sealed class EtsyShopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"shop_id\":202,\"user_id\":88,\"shop_name\":\"Shop\",\"currency_code\":\"USD\"}", Encoding.UTF8, "application/json")
            });
    }

    static void WaitFor(Task? task)
    {
        Assert.IsNotNull(task);
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

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
