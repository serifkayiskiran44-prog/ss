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
using System.Text.Json;
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
    public void RestrictedSettingsCapabilitiesDisableUiAndRejectUnsupportedPatches() => InSta(() =>
    {
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", "101", "Restricted", true);
        var registry = new MarketplaceAdapterRegistry(new[]
        {
            new RestrictedAdapter("trendyol", new(new HashSet<MarketplaceOperation> { MarketplaceOperation.ProductsRead }))
        });
        var store = new MarketplaceShopSettingsStore(directory, registry);

        Assert.ThrowsException<InvalidOperationException>(() => store.SavePatch(connection.Id,
            new(ProductRules: new(ManagePrice: true)), 0, connection.Revision));
        Assert.ThrowsException<InvalidOperationException>(() => store.SavePatch(connection.Id,
            new(ProductRules: new(DefaultCategoryId: "42")), 0, connection.Revision));
        Assert.ThrowsException<InvalidOperationException>(() => store.SavePatch(connection.Id,
            new(OrderRules: new(Enabled: true)), 0, connection.Revision));
        Assert.ThrowsException<InvalidOperationException>(() => store.SavePatch(connection.Id,
            new(Sync: new(OrdersEnabled: true)), 0, connection.Revision));

        var panel = new MarketplaceShopSettingsPanel(connection.Id, directory, registry);
        Assert.IsFalse(Walk(panel).OfType<CheckBox>().Single(x => Equals(x.Content, "Fiyatı varsayılan olarak yönet")).IsEnabled);
        Assert.IsFalse(Walk(panel).OfType<TextBox>().Single(x => x.Name == "MarketplaceDefaultCategory").IsEnabled);
        Assert.IsFalse(Walk(panel).OfType<TabItem>().Single(x => Equals(x.Header, "Sipariş kuralları")).IsEnabled);
        var productSchedule = Walk(panel).OfType<CheckBox>().Single(x => Equals(x.Content, "Ürün okumayı zamanla"));
        Assert.IsFalse(productSchedule.IsEnabled);
        StringAssert.Contains(productSchedule.ToolTip!.ToString()!, "desteklenmiyor");
        Assert.IsFalse(Walk(panel).OfType<CheckBox>().Single(x => Equals(x.Content, "Sipariş okumayı zamanla")).IsEnabled);
    });

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
            Sync = firstInitial.Sync with { IntervalMinutes = 30 }
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

        CollectionAssert.IsSubsetOf(new[] { "Sku", "Gtin", "Name", "LocalStock", "LocalPrice", "RemoteStock", "RemotePrice", "Category", "Brand", "RemoteStateText", "Error", "ManagementState" }, paths);
        var filterLabels = Walk(products).OfType<ComboBox>().SelectMany(combo => combo.Items.Cast<object>()).Select(item => item.ToString()).ToArray();
        Assert.IsTrue(filterLabels.Contains("Tümü"));
        Assert.IsFalse(filterLabels.Contains("All"));
        Assert.IsTrue(Walk(products).OfType<Button>().Any(x => x.Name == "MarketplaceSelectAllFiltered"));
        CollectionAssert.AreEqual(new[] { "Etkinlik", "Bağlantı", "Ürün kuralları", "Sipariş kuralları", "Senkronizasyon" },
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
        Assert.IsTrue(captured.Rows.All(x => x.RemoteId.StartsWith("remote-", StringComparison.Ordinal)));
        Assert.IsTrue(captured.Rows.All(x => x.ManageContent && x.ManagePrice && x.ManageStock));
    });

    [TestMethod]
    public async Task TrendyolSpecialistPlanUsesTask4BindingAndBlocksChangedBindingBeforeHttp()
    {
        var account = new TrendyolSettings("101", "key", "secret", "tests");
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", account.SupplierId, "Trendyol", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "T-ONLY", Name = "Binding only", Stock = 7, Currency = "TRY" });
        var bindings = new ProductChannelBindingStore(directory);
        var binding = bindings.Save(new(product.Id, connection.Id, "9001", "REMOTE-SKU", "REMOTE-BARCODE",
            true, true, true, "", "", "Approved", 0, default), 0);
        var store = new TrMarketplaceHubDesktop.Trendyol.TrendyolWorkspaceStore(directory);
        var state = store.Load(account.SupplierId);
        state.ProductsUpdatedUtc = DateTime.UtcNow;
        state.Products.Add(new("REMOTE-BARCODE", "REMOTE-SKU", "Remote", 9001, 2, 10, 10, true));
        store.Save(state);
        var model = new MarketplaceShopProductsModel(connection.Id, directory);
        var specialist = model.PreviewSpecialist(model.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.StockPreview);

        var plan = store.Preview(account, new[] { product.Id }, TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.Stock, connection.Id);
        Assert.IsNotNull(plan.Rows.Single().ItemJson, plan.Rows.Single().Detail);
        model.AssociateSpecialistPlan(specialist, plan.Id);
        using (var database = new SqliteConnection("Data Source=" + Path.Combine(directory, "catalog.db")))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "UPDATE ProductChannelBindings SET ManageStock=0 WHERE ProductId=$product AND ConnectionId=$connection";
            command.Parameters.AddWithValue("$product", product.Id); command.Parameters.AddWithValue("$connection", connection.Id); command.ExecuteNonQuery();
        }
        var transport = new CountingTrendyolHandler();
        using var http = new HttpClient(transport);
        using var client = new TrMarketplaceHubDesktop.Trendyol.TrendyolApiClient(account, http);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => store.SendAsync(plan.Id, account, true, client));
        Assert.AreEqual(0, transport.Writes);
        Assert.AreEqual(0, store.Receipts(account.SupplierId).Count);
    }

    [TestMethod]
    public async Task EtsySpecialistPlanUsesTask4BindingAndBlocksChangedIdentityBeforeHttp()
    {
        var credentials = new EtsyCredentials("key", "secret", "88.token", "123", GrantedScopes: new[] { "listings_r", "listings_w" });
        var connection = new MarketplaceConnectionStore(directory).Save("etsy", credentials.ShopId, "Etsy", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "E-ONLY", Name = "Binding only", Stock = 8, Price = 20, Currency = "USD" });
        var bindings = new ProductChannelBindingStore(directory);
        var binding = bindings.Save(new(product.Id, connection.Id, "456", "E-ONLY", "",
            true, true, true, "", "", "active", 0, default), 0);
        new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceStore(directory).Save(new() { ShopId = credentials.ShopId, Currency = "USD" });
        var api = new CountingEtsyHandler();
        using var http = new HttpClient(api);
        var service = new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceService(directory, http);
        var model = new MarketplaceShopProductsModel(connection.Id, directory);
        var specialist = model.PreviewSpecialist(model.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.StockPreview);

        var plan = await service.PreviewAsync(credentials, new[] { product.Id }, TrMarketplaceHubDesktop.Etsy.EtsyOperation.Stock,
            connectionId: connection.Id);
        Assert.IsTrue(plan.Rows.Single().CanSend, plan.Rows.Single().Detail);
        Assert.AreEqual(456L, plan.Rows.Single().ListingId);
        model.AssociateSpecialistPlan(specialist, plan.Id);
        using (var database = new SqliteConnection("Data Source=" + Path.Combine(directory, "catalog.db")))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "UPDATE ProductChannelBindings SET RemoteId='999' WHERE ProductId=$product AND ConnectionId=$connection";
            command.Parameters.AddWithValue("$product", product.Id); command.Parameters.AddWithValue("$connection", connection.Id); command.ExecuteNonQuery();
        }

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.SendAsync(credentials, plan.Id, true));
        Assert.AreEqual(0, api.Writes);
        Assert.AreEqual(0, new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceStore(directory).Receipts(credentials.ShopId).Count);
    }

    [TestMethod]
    public async Task EtsySpecialistPlanRevalidatesAfterLastMomentChangeBeforeClaim()
    {
        var credentials = new EtsyCredentials("key", "secret", "88.token", "123", GrantedScopes: new[] { "listings_r", "listings_w" });
        var connection = new MarketplaceConnectionStore(directory).Save("etsy", credentials.ShopId, "Etsy", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "E-RACE", Name = "Binding race", Stock = 8, Price = 20, Currency = "USD" });
        var bindings = new ProductChannelBindingStore(directory);
        var binding = bindings.Save(new(product.Id, connection.Id, "456", "E-ONLY", "",
            true, true, true, "", "", "active", 0, default), 0);
        new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceStore(directory).Save(new() { ShopId = credentials.ShopId, Currency = "USD" });
        var api = new CountingEtsyHandler();
        using var http = new HttpClient(api);
        var service = new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceService(directory, http);
        var model = new MarketplaceShopProductsModel(connection.Id, directory);
        var specialist = model.PreviewSpecialist(model.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.StockPreview);
        var plan = await service.PreviewAsync(credentials, new[] { product.Id }, TrMarketplaceHubDesktop.Etsy.EtsyOperation.Stock,
            connectionId: connection.Id);
        model.AssociateSpecialistPlan(specialist, plan.Id);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.SendAsync(credentials, plan.Id, true,
            beforeClaim: () =>
            {
                using var database = new SqliteConnection("Data Source=" + Path.Combine(directory, "catalog.db"));
                database.Open(); using var command = database.CreateCommand();
                command.CommandText = "UPDATE ProductChannelBindings SET ManageStock=0 WHERE ProductId=$product AND ConnectionId=$connection";
                command.Parameters.AddWithValue("$product", product.Id); command.Parameters.AddWithValue("$connection", connection.Id);
                command.ExecuteNonQuery();
            }));

        Assert.AreEqual(0, api.Writes);
        Assert.AreEqual(0, new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceStore(directory).Receipts(credentials.ShopId).Count);
    }

    [TestMethod]
    public void TrendyolAccountUpdateRejectsUnboundLegacyProfileBeforePlan()
    {
        var account = new TrendyolSettings("101", "key", "secret", "tests");
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", account.SupplierId, "Trendyol", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "T-LEGACY", Name = "Legacy only", Stock = 5, Currency = "TRY" });
        var store = new TrMarketplaceHubDesktop.Trendyol.TrendyolWorkspaceStore(directory);
        var state = store.Load(account.SupplierId);
        state.ProductsUpdatedUtc = DateTime.UtcNow;
        state.Products.Add(new("LEGACY-BARCODE", "LEGACY-SKU", "Remote", 9001, 2, 10, 10, true));
        state.Profiles.Add(new() { ProductId = product.Id, IntegrationCode = "LEGACY-BARCODE" });
        store.Save(state);
        var transport = new CountingTrendyolHandler();

        Assert.ThrowsException<InvalidOperationException>(() => store.Preview(account, new[] { product.Id },
            TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.Stock, connection.Id));

        Assert.AreEqual(0, transport.Writes);
    }

    [TestMethod]
    public async Task EtsyAccountUpdateRejectsUnboundLegacyProfileBeforePlan()
    {
        var credentials = new EtsyCredentials("key", "secret", "88.token", "123", GrantedScopes: new[] { "listings_r", "listings_w" });
        var connection = new MarketplaceConnectionStore(directory).Save("etsy", credentials.ShopId, "Etsy", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "E-ONLY", Name = "Legacy only", Stock = 5, Price = 20, Currency = "USD" });
        var workspace = new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceStore(directory);
        var state = workspace.Load(credentials.ShopId);
        state.Currency = "USD";
        state.Profiles.Add(new() { ProductId = product.Id, ListingId = 456 });
        workspace.Save(state);
        var api = new CountingEtsyHandler();
        using var http = new HttpClient(api);
        var service = new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceService(directory, http);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.PreviewAsync(credentials, new[] { product.Id },
            TrMarketplaceHubDesktop.Etsy.EtsyOperation.Stock, connectionId: connection.Id));

        Assert.AreEqual(0, api.Writes);
    }

    [TestMethod]
    public void TrendyolStockPreviewRejectsInitiallyDisabledManagementFlag()
    {
        var account = new TrendyolSettings("101", "key", "secret", "tests");
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", account.SupplierId, "Trendyol", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "T-DISABLED", Name = "Disabled", Stock = 5, Currency = "TRY" });
        new ProductChannelBindingStore(directory).Save(new(product.Id, connection.Id, "9001", "REMOTE-SKU", "REMOTE-BARCODE",
            true, true, false, "", "", "Approved", 0, default), 0);
        var model = new MarketplaceShopProductsModel(connection.Id, directory);
        var selection = model.SelectPage(new[] { product.Id });
        var store = new TrMarketplaceHubDesktop.Trendyol.TrendyolWorkspaceStore(directory);
        var transport = new CountingTrendyolHandler();

        Assert.ThrowsException<InvalidOperationException>(() => model.PreviewSpecialist(selection, MarketplaceShopBulkOperation.StockPreview));
        Assert.ThrowsException<InvalidOperationException>(() => store.Preview(account, new[] { product.Id },
            TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.Stock, connection.Id));
        Assert.AreEqual(0, transport.Writes);
    }

    [TestMethod]
    public void TrendyolWorkspaceRowsResolveSharedContentVariantsByRemoteBarcode()
    {
        var account = new TrendyolSettings("101", "key", "secret", "tests");
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", account.SupplierId, "Trendyol", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "LOCAL-2", Barcode = "LOCAL-BARCODE", Name = "Variant two", Stock = 5, Price = 20, Currency = "TRY" });
        new ProductChannelBindingStore(directory).Save(new(product.Id, connection.Id, "9001", "REMOTE-2", "BARCODE-2",
            true, true, true, "", "", "Approved", 0, default), 0);
        var workspace = new TrMarketplaceHubDesktop.Trendyol.TrendyolWorkspaceStore(directory);
        var state = workspace.Load(account.SupplierId);
        state.Products.Add(new("BARCODE-1", "REMOTE-1", "Variant one", 9001, 2, 10, 12, true));
        state.Products.Add(new("BARCODE-2", "REMOTE-2", "Variant two", 9001, 7, 30, 35, true));
        workspace.Save(state);

        var row = new MarketplaceShopProductsModel(connection.Id, directory).Filter(new()).Single(x => x.ProductId == product.Id);

        Assert.AreEqual(7, row.RemoteStock);
        Assert.AreEqual(30m, row.RemotePrice);
    }

    [TestMethod]
    public async Task EtsyStockPreviewRejectsInitiallyDisabledManagementFlag()
    {
        var credentials = new EtsyCredentials("key", "secret", "88.token", "123", GrantedScopes: new[] { "listings_r", "listings_w" });
        var connection = new MarketplaceConnectionStore(directory).Save("etsy", credentials.ShopId, "Etsy", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "E-ONLY", Name = "Disabled", Stock = 5, Price = 20, Currency = "USD" });
        new ProductChannelBindingStore(directory).Save(new(product.Id, connection.Id, "456", "E-ONLY", "",
            true, true, false, "", "", "active", 0, default), 0);
        new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceStore(directory).Save(new() { ShopId = credentials.ShopId, Currency = "USD" });
        var model = new MarketplaceShopProductsModel(connection.Id, directory);
        var selection = model.SelectPage(new[] { product.Id });
        var api = new CountingEtsyHandler();
        using var http = new HttpClient(api);
        var service = new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceService(directory, http);

        Assert.ThrowsException<InvalidOperationException>(() => model.PreviewSpecialist(selection, MarketplaceShopBulkOperation.StockPreview));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.PreviewAsync(credentials, new[] { product.Id },
            TrMarketplaceHubDesktop.Etsy.EtsyOperation.Stock, connectionId: connection.Id));
        Assert.AreEqual(0, api.Writes);
    }

    [TestMethod]
    public void CreatePreviewRetainsIntentionalUnboundSelectionPath()
    {
        var connection = new MarketplaceConnectionStore(directory).Save("etsy", "123", "Etsy", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "CREATE", Name = "Create", Stock = 1, Price = 10, Currency = "USD" });
        var model = new MarketplaceShopProductsModel(connection.Id, directory);

        var preview = model.PreviewSpecialist(model.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.CreatePreview);

        Assert.AreEqual(0L, preview.Rows.Single().BindingVersion);
        Assert.AreEqual("", preview.Rows.Single().RemoteId);

        new ProductChannelBindingStore(directory).Save(Binding(product.Id, connection.Id, "appeared-after-preview", "active"), 0);
        Assert.ThrowsException<InvalidOperationException>(() => model.AssociateSpecialistPlan(preview, "late-create-plan"));
    }

    [TestMethod]
    public async Task ScopedCreatePreviewRejectsAnExistingBindingAcrossCommonAndChannelWorkspaces()
    {
        var trendyolAccount = new TrendyolSettings("101", "key", "secret", "tests");
        var trendyolConnection = new MarketplaceConnectionStore(directory).Save("trendyol", trendyolAccount.SupplierId, "Trendyol", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "BOUND-CREATE", Barcode = "BOUND-BARCODE", Name = "Bound", Stock = 1, Price = 10, Currency = "TRY" });
        new ProductChannelBindingStore(directory).Save(Binding(product.Id, trendyolConnection.Id, "501", "Active"), 0);
        var common = new MarketplaceShopProductsModel(trendyolConnection.Id, directory);

        Assert.ThrowsException<InvalidOperationException>(() =>
            common.PreviewSpecialist(common.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.CreatePreview));
        Assert.ThrowsException<InvalidOperationException>(() =>
            new TrMarketplaceHubDesktop.Trendyol.TrendyolWorkspaceStore(directory).Preview(
                trendyolAccount, new[] { product.Id }, TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.Create, trendyolConnection.Id));

        var etsyConnection = new MarketplaceConnectionStore(directory).Save("etsy", "123", "Etsy", true);
        new ProductChannelBindingStore(directory).Save(Binding(product.Id, etsyConnection.Id, "601", "active"), 0);
        new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceStore(directory).Save(new() { ShopId = "123", Currency = "USD" });
        using var http = new HttpClient(new EtsyShopHandler());
        var etsy = new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceService(directory, http);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => etsy.PreviewAsync(
            new EtsyCredentials("key", "secret", "88.token", "123", GrantedScopes: new[] { "listings_r", "listings_w" }),
            new[] { product.Id }, TrMarketplaceHubDesktop.Etsy.EtsyOperation.CreateDraft, connectionId: etsyConnection.Id));
    }

    [TestMethod]
    public void ScopedTrendyolCreatePreviewRejectsAStaleWorkspaceRemoteIdentity()
    {
        var account = new TrendyolSettings("101", "key", "secret", "tests");
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", account.SupplierId, "Trendyol", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "STALE-CREATE", Barcode = "NEW-BARCODE", Name = "Stale", Stock = 1, Price = 10, Currency = "TRY" });
        var workspace = new TrMarketplaceHubDesktop.Trendyol.TrendyolWorkspaceStore(directory);
        var state = workspace.Load(account.SupplierId);
        state.ProductsUpdatedUtc = DateTime.UtcNow;
        state.Products.Add(new("OLD-BARCODE", product.Sku, product.Name, 901, 1, 10, 10, true));
        state.Profiles.Add(new() { ProductId = product.Id, IntegrationCode = "OLD-BARCODE" });
        workspace.Save(state);

        var error = Assert.ThrowsException<InvalidOperationException>(() => workspace.Preview(
            account, new[] { product.Id }, TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.Create, connection.Id));

        StringAssert.Contains(error.Message, "bağlı olmayan");
    }

    [TestMethod]
    public void ScopedTrendyolPlanWithoutACompletedSpecialistAssociationCannotClaimOrWrite()
    {
        var account = new TrendyolSettings("101", "key", "secret", "tests");
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", account.SupplierId, "Trendyol", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "SCOPED", Barcode = "SCOPED-B", Name = "Scoped", Stock = 8, Price = 10, Currency = "TRY" });
        new ProductChannelBindingStore(directory).Save(new(product.Id, connection.Id, "701", product.Sku, product.Barcode, true, true, true, "", "", "Approved", 0, default), 0);
        var workspace = new TrMarketplaceHubDesktop.Trendyol.TrendyolWorkspaceStore(directory);
        var state = workspace.Load(account.SupplierId);
        state.ProductsUpdatedUtc = DateTime.UtcNow;
        state.Products.Add(new(product.Barcode, product.Sku, product.Name, 701, 1, 10, 10, true));
        state.Profiles.Add(new() { ProductId = product.Id, IntegrationCode = product.Barcode });
        workspace.Save(state);
        var plan = workspace.Preview(account, new[] { product.Id }, TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.Stock, connection.Id);
        var transport = new CountingTrendyolHandler();
        using var http = new HttpClient(transport);
        using var client = new TrMarketplaceHubDesktop.Trendyol.TrendyolApiClient(account, http);

        Assert.ThrowsException<InvalidOperationException>(() => WaitFor(workspace.SendAsync(plan.Id, account, true, client)));

        Assert.AreEqual(0, transport.Writes);
        Assert.AreEqual(0, workspace.Receipts(account.SupplierId).Count);
    }

    [TestMethod]
    public void FailedTrendyolSpecialistAssociationClearsThePreviousVisiblePlanAndDisablesSend() => InSta(() =>
    {
        var account = new TrendyolSettings("101", "key", "secret", "tests");
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", account.SupplierId, "Trendyol", true);
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "RACE", Barcode = "RACE-B", Name = "Race", Stock = 9, Price = 10, Currency = "TRY" });
        var bindings = new ProductChannelBindingStore(directory);
        bindings.Save(new(product.Id, connection.Id, "801", product.Sku, product.Barcode, true, true, true, "", "", "Approved", 0, default), 0);
        var workspace = new TrMarketplaceHubDesktop.Trendyol.TrendyolWorkspaceStore(directory);
        var state = workspace.Load(account.SupplierId);
        state.ProductsUpdatedUtc = DateTime.UtcNow;
        state.Products.Add(new(product.Barcode, product.Sku, product.Name, 801, 1, 10, 10, true));
        state.Profiles.Add(new() { ProductId = product.Id, IntegrationCode = product.Barcode });
        workspace.Save(state);
        var panel = new TrendyolWorkspacePanel(connection.Id, directory);
        var model = new MarketplaceShopProductsModel(connection.Id, directory);
        var firstSpecialist = model.PreviewSpecialist(model.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.StockPreview);
        var first = workspace.Preview(account, new[] { product.Id }, TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.Stock, connection.Id);
        var present = typeof(TrendyolWorkspacePanel).GetMethod("PresentPreview", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        present.Invoke(panel, new object?[] { first, firstSpecialist });
        var send = Walk(panel).OfType<Button>().Single(x => x.Name == "TrendyolSend");
        Assert.IsTrue(send.IsEnabled, "The first associated plan establishes the stale enabled-send state.");

        var specialist = model.PreviewSpecialist(model.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.StockPreview);
        var next = workspace.Preview(account, new[] { product.Id }, TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.Stock, connection.Id);
        using (var database = new SqliteConnection("Data Source=" + Path.Combine(directory, "catalog.db")))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "UPDATE ProductChannelBindings SET ManageStock=0 WHERE ProductId=$product AND ConnectionId=$connection";
            command.Parameters.AddWithValue("$product", product.Id); command.Parameters.AddWithValue("$connection", connection.Id); command.ExecuteNonQuery();
        }
        Assert.ThrowsException<System.Reflection.TargetInvocationException>(() => present.Invoke(panel, new object?[] { next, specialist }));

        Assert.IsFalse(send.IsEnabled);
        Assert.AreEqual("", panel.AccountSpecialistPlanId);
        Assert.IsNull(panel.AccountSpecialistPreview);
        Assert.AreEqual(0, panel.AccountSpecialistPlanProductIds.Count);
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(0, workspace.Receipts(account.SupplierId).Count);
    });

    [TestMethod]
    public void ScopedTrendyolDirectTabRequiresManagedBindingsAndRevalidatesBeforeSend() => InSta(() =>
    {
        var account = new TrendyolSettings("101", "key", "secret", "tests");
        var connection = new MarketplaceConnectionStore(directory).Save("trendyol", account.SupplierId, "Trendyol", true);
        new MarketplaceCredentialVault(directory).Save(connection.Id, connection.Channel, connection.ShopId, account);
        var catalog = new CatalogStore(directory);
        var unlinked = catalog.CreateManual(new() { Sku = "T-UNLINKED", Name = "Unlinked", Stock = 5, Currency = "TRY" });
        var disabled = catalog.CreateManual(new() { Sku = "T-DISABLED", Name = "Disabled", Stock = 5, Currency = "TRY" });
        var changed = catalog.CreateManual(new() { Sku = "T-CHANGED", Name = "Changed", Stock = 5, Currency = "TRY" });
        var successful = catalog.CreateManual(new() { Sku = "T-SUCCESS", Name = "Success", Stock = 5, Currency = "TRY" });
        var products = new[] { (unlinked, "T-B1", 901L), (disabled, "T-B2", 902L), (changed, "T-B3", 903L), (successful, "T-B4", 904L) };
        var workspace = new TrMarketplaceHubDesktop.Trendyol.TrendyolWorkspaceStore(directory);
        var state = workspace.Load(account.SupplierId); state.ProductsUpdatedUtc = DateTime.UtcNow;
        foreach (var (product, barcode, remoteId) in products)
        {
            state.Products.Add(new(barcode, product.Sku, product.Name, remoteId, 1, 10, 10, true));
            state.Profiles.Add(new() { ProductId = product.Id, IntegrationCode = barcode });
        }
        workspace.Save(state);
        var bindings = new ProductChannelBindingStore(directory);
        bindings.Save(new(disabled.Id, connection.Id, "902", disabled.Sku, "T-B2", true, true, false, "", "", "Approved", 0, default), 0);
        bindings.Save(new(changed.Id, connection.Id, "903", changed.Sku, "T-B3", true, true, true, "", "", "Approved", 0, default), 0);
        bindings.Save(new(successful.Id, connection.Id, "904", successful.Sku, "T-B4", true, true, true, "", "", "Approved", 0, default), 0);
        var panel = new TrendyolWorkspacePanel(connection.Id, directory);
        var grid = Walk(panel).OfType<DataGrid>().Single(x => x.Name == "TrendyolProducts");
        Walk(panel).OfType<ComboBox>().Single(x => x.Name == "TrendyolOperationMode").SelectedIndex = (int)TrMarketplaceHubDesktop.Trendyol.TrendyolOperation.Stock;
        var preview = Walk(panel).OfType<Button>().Single(x => x.Name == "TrendyolBuildPreview");
        var send = Walk(panel).OfType<Button>().Single(x => x.Name == "TrendyolSend");

        SelectProduct(grid, unlinked.Id); preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.IsFalse(send.IsEnabled);
        SelectProduct(grid, disabled.Id); preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.IsFalse(send.IsEnabled);

        SelectProduct(grid, changed.Id); preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsNotNull(panel.AccountSpecialistPreview); Assert.IsTrue(send.IsEnabled);
        using (var database = new SqliteConnection("Data Source=" + Path.Combine(directory, "catalog.db")))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "UPDATE ProductChannelBindings SET ManageStock=0 WHERE ProductId=$product AND ConnectionId=$connection";
            command.Parameters.AddWithValue("$product", changed.Id); command.Parameters.AddWithValue("$connection", connection.Id); command.ExecuteNonQuery();
        }
        var transport = new CountingTrendyolHandler(); using var http = new HttpClient(transport); using var client = new TrMarketplaceHubDesktop.Trendyol.TrendyolApiClient(account, http);
        Assert.ThrowsException<InvalidOperationException>(() => WaitFor(workspace.SendAsync(panel.AccountSpecialistPlanId, account, true, client)));
        Assert.AreEqual(0, transport.Writes);

        SelectProduct(grid, successful.Id); preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsNotNull(panel.AccountSpecialistPreview); Assert.IsTrue(send.IsEnabled);
        WaitFor(workspace.SendAsync(panel.AccountSpecialistPlanId, account, true, client));
        Assert.AreEqual(1, transport.Writes);
    });

    [TestMethod]
    public void ScopedEtsyDirectTabRequiresManagedBindingsAndRevalidatesBeforeSend() => InSta(() =>
    {
        var credentials = new EtsyCredentials("key", "secret", "88.token", "123", GrantedScopes: new[] { "listings_r", "listings_w" });
        var connection = new MarketplaceConnectionStore(directory).Save("etsy", credentials.ShopId, "Etsy", true);
        new MarketplaceCredentialVault(directory).Save(connection.Id, connection.Channel, connection.ShopId, credentials);
        var catalog = new CatalogStore(directory);
        var unlinked = catalog.CreateManual(new() { Sku = "E-1", Name = "Unlinked", Stock = 5, Price = 20, Currency = "USD" });
        var disabled = catalog.CreateManual(new() { Sku = "E-2", Name = "Disabled", Stock = 5, Price = 20, Currency = "USD" });
        var changed = catalog.CreateManual(new() { Sku = "E-3", Name = "Changed", Stock = 5, Price = 20, Currency = "USD" });
        var successful = catalog.CreateManual(new() { Sku = "E-4", Name = "Success", Stock = 5, Price = 20, Currency = "USD" });
        var workspace = new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceStore(directory);
        var state = workspace.Load(credentials.ShopId); state.Currency = "USD"; state.ShopName = "Shop";
        state.Profiles.Add(new() { ProductId = unlinked.Id, ListingId = 451 });
        state.Profiles.Add(new() { ProductId = disabled.Id, ListingId = 452 });
        state.Profiles.Add(new() { ProductId = changed.Id, ListingId = 453 });
        state.Profiles.Add(new() { ProductId = successful.Id, ListingId = 454 });
        workspace.Save(state);
        var bindings = new ProductChannelBindingStore(directory);
        bindings.Save(new(disabled.Id, connection.Id, "452", "", "", true, true, false, "", "", "active", 0, default), 0);
        bindings.Save(new(changed.Id, connection.Id, "453", "", "", true, true, true, "", "", "active", 0, default), 0);
        bindings.Save(new(successful.Id, connection.Id, "454", "", "", true, true, true, "", "", "active", 0, default), 0);
        var transport = new DirectEtsyHandler(); using var panel = new EtsyWorkspacePanel(connection.Id, directory, transport);
        var grid = Walk(panel).OfType<DataGrid>().Single(x => x.Name == "EtsyProducts");
        var preview = Walk(panel).OfType<Button>().Single(x => x.Name == "EtsyPreview_Stock");
        var send = Walk(panel).OfType<Button>().Single(x => x.Name == "EtsySend");

        SelectProduct(grid, unlinked.Id); preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); WaitFor(panel.AccountSpecialistPreviewTask); Assert.IsFalse(send.IsEnabled);
        SelectProduct(grid, disabled.Id); preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); WaitFor(panel.AccountSpecialistPreviewTask); Assert.IsFalse(send.IsEnabled);

        SelectProduct(grid, changed.Id); preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); WaitFor(panel.AccountSpecialistPreviewTask);
        Assert.IsNotNull(panel.AccountSpecialistPreview); Assert.IsTrue(send.IsEnabled);
        using (var database = new SqliteConnection("Data Source=" + Path.Combine(directory, "catalog.db")))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "UPDATE ProductChannelBindings SET ManageStock=0 WHERE ProductId=$product AND ConnectionId=$connection";
            command.Parameters.AddWithValue("$product", changed.Id); command.Parameters.AddWithValue("$connection", connection.Id); command.ExecuteNonQuery();
        }
        using var directHttp = new HttpClient(transport, false); var service = new TrMarketplaceHubDesktop.Etsy.EtsyWorkspaceService(directory, directHttp);
        Assert.ThrowsException<InvalidOperationException>(() => WaitFor(service.SendAsync(credentials, panel.AccountSpecialistPlanId, true)));
        Assert.AreEqual(0, transport.Writes);

        SelectProduct(grid, successful.Id); preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); WaitFor(panel.AccountSpecialistPreviewTask);
        Assert.IsNotNull(panel.AccountSpecialistPreview); Assert.IsTrue(send.IsEnabled);
        WaitFor(service.SendAsync(credentials, panel.AccountSpecialistPlanId, true));
        Assert.AreEqual(1, transport.Writes);
    });

    [TestMethod]
    public void SameRevisionFailedEvidenceInvalidatesAssociatedSpecialistPlan()
    {
        var connections = new MarketplaceConnectionStore(directory); connections.List();
        var connection = connections.Get("trendyol:default")!;
        Assert.AreEqual(ConnectionTestApplyResult.Applied, connections.RecordTest(connection.Id, connection.Revision, true));
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "ASSOC", Name = "Associated", Currency = "TRY" });
        new ProductChannelBindingStore(directory).Save(Binding(product.Id, connection.Id, "remote", "Active"), 0);
        var model = new MarketplaceShopProductsModel(connection.Id, directory);
        var preview = model.PreviewSpecialist(model.SelectPage(new[] { product.Id }), MarketplaceShopBulkOperation.StockPreview);
        model.AssociateSpecialistPlan(preview, "channel-plan");
        Assert.AreEqual(ConnectionTestApplyResult.Applied, connections.RecordTest(connection.Id, connection.Revision, false, "offline"));

        Assert.ThrowsException<InvalidOperationException>(() => model.ValidateSpecialistPlan("channel-plan"));
    }

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

    sealed class CountingTrendyolHandler : HttpMessageHandler
    {
        public int Writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) Interlocked.Increment(ref Writes);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"batchRequestId\":\"batch\"}", Encoding.UTF8, "application/json")
            });
        }
    }

    sealed class CountingEtsyHandler : HttpMessageHandler
    {
        public int Writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                Interlocked.Increment(ref Writes);
                return Json(path.EndsWith("inventory", StringComparison.Ordinal) ? Inventory() : "{\"listing_id\":456}");
            }
            if (path.EndsWith("/shops/123", StringComparison.Ordinal))
                return Json("{\"shop_id\":123,\"user_id\":88,\"shop_name\":\"Shop\",\"currency_code\":\"USD\"}");
            if (path.EndsWith("/listings/456", StringComparison.Ordinal))
                return Json("{\"listing_id\":456,\"shop_id\":123,\"title\":\"Item\",\"description\":\"Description\",\"state\":\"active\",\"quantity\":3,\"last_modified_timestamp\":1,\"price\":{\"amount\":1000,\"divisor\":100,\"currency_code\":\"USD\"},\"skus\":[\"E-ONLY\"]}");
            if (path.EndsWith("/inventory", StringComparison.Ordinal)) return Json(Inventory());
            throw new InvalidOperationException("Unexpected route: " + path);
        }

        static string Inventory() => "{\"products\":[{\"product_id\":11,\"sku\":\"E-ONLY\",\"property_values\":[],\"offerings\":[{\"offering_id\":22,\"price\":{\"amount\":1000,\"divisor\":100,\"currency_code\":\"USD\"},\"quantity\":3,\"is_enabled\":true}]}],\"price_on_property\":[],\"quantity_on_property\":[],\"sku_on_property\":[],\"readiness_state_on_property\":[]}";
        static Task<HttpResponseMessage> Json(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }

    sealed class DirectEtsyHandler : HttpMessageHandler
    {
        public int Writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get)
            {
                Interlocked.Increment(ref Writes);
                return Json(path.EndsWith("inventory", StringComparison.Ordinal) ? Inventory() : "{\"listing_id\":454}");
            }
            if (path.EndsWith("/shops/123", StringComparison.Ordinal))
                return Json("{\"shop_id\":123,\"user_id\":88,\"shop_name\":\"Shop\",\"currency_code\":\"USD\"}");
            if (path.EndsWith("/inventory", StringComparison.Ordinal)) return Json(Inventory());
            var text = path.Split('/').Last();
            if (long.TryParse(text, out var listing) && listing is >= 451 and <= 454)
                return Json($"{{\"listing_id\":{listing},\"shop_id\":123,\"title\":\"Item\",\"description\":\"Description\",\"state\":\"active\",\"quantity\":3,\"last_modified_timestamp\":1,\"price\":{{\"amount\":1000,\"divisor\":100,\"currency_code\":\"USD\"}},\"skus\":[]}}");
            throw new InvalidOperationException("Unexpected route: " + path);
        }

        static string Inventory() => "{\"products\":[{\"product_id\":11,\"sku\":\"REMOTE\",\"property_values\":[],\"offerings\":[{\"offering_id\":22,\"price\":{\"amount\":1000,\"divisor\":100,\"currency_code\":\"USD\"},\"quantity\":3,\"is_enabled\":true}]}],\"price_on_property\":[],\"quantity_on_property\":[],\"sku_on_property\":[],\"readiness_state_on_property\":[]}";
        static Task<HttpResponseMessage> Json(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }

    static void SelectProduct(DataGrid grid, string productId)
    {
        grid.UnselectAll();
        grid.SelectedItem = grid.Items.Cast<object>().Single(item =>
            Equals(item.GetType().GetProperty("Id")?.GetValue(item), productId));
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
