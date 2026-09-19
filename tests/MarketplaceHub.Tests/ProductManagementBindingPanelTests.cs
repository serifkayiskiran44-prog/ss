#nullable enable
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Etsy;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class ProductManagementBindingPanelTests
{
    string directory = "";

    [TestInitialize]
    public void Setup() => directory = Path.Combine(Path.GetTempPath(), "product-management-binding-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                return;
            }
            catch (IOException) when (attempt < 19) { Thread.Sleep(50); }
        }
    }

    [TestMethod]
    public void ExplicitSelectionIsImmutableAndTargetsCarryOnlyEligibleExactConnectionIds()
    {
        var catalog = new CatalogStore(directory);
        var first = catalog.CreateManual(new() { Sku = "A", Name = "A", Currency = "TRY" });
        var second = catalog.CreateManual(new() { Sku = "B", Name = "B", Currency = "TRY" });
        var selected = new List<string> { first.Id, second.Id };
        var connections = new MarketplaceConnectionStore(directory);
        var trendyol = Connected(connections, "trendyol", "101", "Trendyol A");
        var etsy = Connected(connections, "etsy", "202", "Etsy A");
        connections.Save("trendyol", "303", "Disabled", false);
        connections.Save("etsy", "404", "Not configured", true);

        var model = new ProductConnectionsModel(directory, selected, new FakeSnapshotProvider(), new FakeCreationHandoff());
        selected.Clear();

        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, model.SelectedProductIds.ToArray());
        CollectionAssert.AreEquivalent(new[] { trendyol.Id, etsy.Id }, model.Targets.Select(x => x.ConnectionId).ToArray());
        Assert.ThrowsException<InvalidOperationException>(() => new ProductConnectionsModel(directory, Array.Empty<string>(), new FakeSnapshotProvider(), new FakeCreationHandoff()));
    }

    [TestMethod]
    public void BadgesDetailsAndPartialFlagEditsRemainBoundToExactAccounts()
    {
        var catalog = new CatalogStore(directory);
        var product = catalog.CreateManual(new() { Sku = "A", Barcode = "BAR", Name = "A", Currency = "TRY" });
        var connections = new MarketplaceConnectionStore(directory);
        var first = Connected(connections, "trendyol", "101", "Shop One");
        var second = Connected(connections, "trendyol", "202", "Shop Two");
        var bindings = new ProductChannelBindingStore(directory);
        bindings.Save(Binding(product.Id, first.Id, "r1", true, false, true), 0);
        bindings.Save(Binding(product.Id, second.Id, "r2", false, true, false), 0);
        var model = new ProductConnectionsModel(directory, new[] { product.Id }, new FakeSnapshotProvider(), new FakeCreationHandoff());

        var badges = model.Badges(product.Id);
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, badges.Select(x => x.ConnectionId).ToArray());
        CollectionAssert.AreEquivalent(new[] { "T1", "T2" }, badges.Select(x => x.Text).ToArray());
        var detail = model.Details.Single(x => x.ConnectionId == first.Id);

        var updated = model.UpdateFlags(product.Id, first.Id, new ProductBindingFlagEdit(null, true, null), detail.Version);

        Assert.IsTrue(updated.ManageContent, "An unspecified content flag must be preserved.");
        Assert.IsTrue(updated.ManagePrice);
        Assert.IsTrue(updated.ManageStock, "An unspecified stock flag must be preserved.");
        Assert.AreEqual("r1", updated.RemoteId);
        Assert.ThrowsException<InvalidOperationException>(() => model.UpdateFlags(product.Id, first.Id, new ProductBindingFlagEdit(false, null, null), detail.Version));
    }

    [TestMethod]
    public void ConnectApplyRequiresExplicitReviewAndHandsMissingRowsToPreviewOnly()
    {
        var catalog = new CatalogStore(directory);
        var exact = catalog.CreateManual(new() { Sku = "A", Barcode = "BAR-A", Name = "A", Currency = "TRY" });
        var missing = catalog.CreateManual(new() { Sku = "B", Barcode = "BAR-B", Name = "B", Currency = "TRY" });
        var connection = Connected(new MarketplaceConnectionStore(directory), "trendyol", "101", "Shop");
        var remote = new FakeSnapshotProvider();
        remote.Set(connection, new ProductChannelRemoteRow(connection.Id, connection.ShopId, "r1", "REMOTE", "BAR-A"));
        var handoff = new FakeCreationHandoff();
        var model = new ProductConnectionsModel(directory, new[] { exact.Id, missing.Id }, remote, handoff);

        Assert.IsFalse(model.CanApply);
        var preview = model.PreviewConnection(connection.Id);
        Assert.AreEqual(1, preview.Rows.Count(x => x.Outcome == ProductChannelMatchOutcome.Matched));
        Assert.AreEqual(1, preview.Rows.Count(x => x.Outcome == ProductChannelMatchOutcome.NewListingCandidate));
        Assert.IsFalse(model.CanApply, "Automatic barcode matches still require an explicit reviewed choice.");

        model.Review(new[] { new ProductChannelMatchReview(exact.Id, "r1") });
        Assert.IsTrue(model.CanApply);
        var receipt = model.Apply();

        Assert.AreEqual(1, receipt.AppliedBindings.Count);
        CollectionAssert.AreEqual(new[] { missing.Id }, receipt.NewListingCandidates.ToArray());
        CollectionAssert.AreEqual(new[] { missing.Id }, handoff.ProductIds.ToArray());
        Assert.AreEqual(0, handoff.RemoteWrites, "A new-listing handoff must never send or create a listing automatically.");
    }

    [TestMethod]
    public void EtsyCreationHandoffReplacesPriorSelectionAndAcknowledgesOnlyAPersistedExactPlan() => InSta(() =>
    {
        var catalog = new CatalogStore(directory);
        var product = catalog.CreateManual(new() { Sku = "A", Name = "A", Currency = "TRY" });
        var other = catalog.CreateManual(new() { Sku = "B", Name = "B", Currency = "TRY" });
        var connections = new MarketplaceConnectionStore(directory);
        var first = Connected(connections, "etsy", "202", "Etsy A");
        var second = Connected(connections, "etsy", "303", "Etsy B");
        var model = new ProductConnectionsModel(directory, new[] { product.Id }, new FakeSnapshotProvider());
        model.PreviewConnection(first.Id);
        var bindingReceipt = model.Apply();
        var requestId = bindingReceipt.CreationPreviewId;
        var inbox = new ProductChannelCreationPreviewInbox(directory);

        Assert.AreEqual(requestId, inbox.Pending(first.Id).Single().Id);
        Assert.AreEqual(0, inbox.Pending(second.Id).Count);
        var workspace = new EtsyWorkspacePanel(first.Id, directory);
        try
        {
            var products = LogicalWalk(workspace).OfType<DataGrid>().Single(grid => grid.Name == "EtsyProducts");
            products.SelectedItem = products.Items.Cast<object>().Single(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)! == other.Id);
            var handoff = LogicalWalk(workspace).OfType<Button>().Single(button => button.Name == "EtsyCreationHandoffButton");
            Assert.IsTrue(handoff.IsEnabled);
            handoff.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(requestId, inbox.Pending(first.Id).Single().Id, "Opening the handoff must not acknowledge it before a plan is persisted.");
            CollectionAssert.AreEqual(new[] { product.Id }, workspace.CreationHandoffProductIds.ToArray());
            var exactIds = (IReadOnlyList<string>)Invoke(workspace, "PreviewProductIds", EtsyOperation.CreateDraft)!;
            CollectionAssert.AreEqual(new[] { product.Id }, exactIds.ToArray(), "The prior B selection must not substitute for handoff product A.");

            var plan = new EtsyOperationPlan
            {
                ShopId = first.ShopId,
                Operation = EtsyOperation.CreateDraft,
                Rows = new[] { new EtsyPreviewRow { ProductId = product.Id } }
            };
            Assert.ThrowsException<TargetInvocationException>(() => InvokeVoid(workspace, "CompleteCreationHandoff", plan));
            using (var database = new SqliteConnection($"Data Source={Path.Combine(directory, "catalog.db")}"))
            {
                database.Open();
                using var command = database.CreateCommand();
                command.CommandText = "INSERT INTO EtsyWorkspacePlans(Id,ShopId,Json) VALUES($id,$shop,$json)";
                command.Parameters.AddWithValue("$id", plan.Id);
                command.Parameters.AddWithValue("$shop", plan.ShopId);
                command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(plan));
                command.ExecuteNonQuery();
            }
            InvokeVoid(workspace, "CompleteCreationHandoff", plan);
            Assert.AreEqual(0, inbox.Pending(first.Id).Count);
            Assert.AreEqual(0, workspace.CreationHandoffProductIds.Count);
        }
        finally { workspace.Dispose(); }
    });

    [TestMethod]
    public void FailedApplyLeavesNoConsumableCreationRequestAcrossRestart()
    {
        var catalog = new CatalogStore(directory);
        var product = catalog.CreateManual(new() { Sku = "A", Name = "A", Currency = "TRY" });
        var connection = Connected(new MarketplaceConnectionStore(directory), "etsy", "202", "Etsy");
        var remote = new FakeSnapshotProvider();
        remote.Set(connection);
        var handoff = new StalingCreationHandoff(directory, catalog, product);
        var model = new ProductConnectionsModel(directory, new[] { product.Id }, remote, handoff);
        model.PreviewConnection(connection.Id);

        Assert.ThrowsException<InvalidOperationException>(() => model.Apply());
        Assert.IsFalse(string.IsNullOrWhiteSpace(handoff.RequestId), "The request must have been enqueued before the forced stale apply failure.");
        Assert.AreEqual(0, new ProductChannelCreationPreviewInbox(directory).Pending(connection.Id).Count);
        Assert.AreEqual(0, new ProductChannelCreationPreviewInbox(directory).Pending(connection.Id).Count, "A restart must not make the failed apply request consumable.");
    }

    [TestMethod]
    public void StaleRemoteSnapshotBlocksConnectionApply()
    {
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "A", Barcode = "BAR", Name = "A", Currency = "TRY" });
        var connection = Connected(new MarketplaceConnectionStore(directory), "trendyol", "101", "Shop");
        var remote = new FakeSnapshotProvider();
        remote.Set(connection, new ProductChannelRemoteRow(connection.Id, connection.ShopId, "r1", "A", "BAR"));
        var model = new ProductConnectionsModel(directory, new[] { product.Id }, remote, new FakeCreationHandoff());
        var preview = model.PreviewConnection(connection.Id);
        model.Review(new[] { new ProductChannelMatchReview(product.Id, "r1") });
        remote.Set(connection, new ProductChannelRemoteRow(connection.Id, connection.ShopId, "r1", "A", "BAR", State: "changed"));

        Assert.ThrowsException<InvalidOperationException>(() => model.Apply());
        Assert.IsNull(new ProductChannelBindingStore(directory).Get(product.Id, connection.Id));
        Assert.AreEqual(preview.ConnectionId, connection.Id);
    }

    [TestMethod]
    public void LocalUnlinkUsesPreviewAndCasWhileRemotePathOnlyCreatesDispatchPreview()
    {
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "A", Barcode = "BAR", Name = "A", Currency = "TRY" });
        var connection = Connected(new MarketplaceConnectionStore(directory), "etsy", "202", "Etsy");
        var bindings = new ProductChannelBindingStore(directory);
        var saved = bindings.Save(Binding(product.Id, connection.Id, "r1", true, true, true), 0);
        var remoteRouter = new FakeRemoteDeactivationRouter();
        var model = new ProductConnectionsModel(directory, new[] { product.Id }, new FakeSnapshotProvider(), new FakeCreationHandoff(), remoteDeactivation: remoteRouter);

        Assert.ThrowsException<InvalidOperationException>(() => model.ApplyLocalUnlink(null!, true));
        var stale = model.PreviewLocalUnlink(product.Id, connection.Id);
        bindings.Save(saved with { ManageStock = false }, saved.Version);
        Assert.ThrowsException<InvalidOperationException>(() => model.ApplyLocalUnlink(stale, true));
        Assert.IsNotNull(bindings.Get(product.Id, connection.Id));

        var current = model.PreviewLocalUnlink(product.Id, connection.Id);
        var forged = current with { Id = "forged-preview" };
        Assert.ThrowsException<InvalidOperationException>(() => model.ApplyLocalUnlink(forged, true));
        var receipt = model.ApplyLocalUnlink(current, true);
        Assert.IsNull(bindings.Get(product.Id, connection.Id));
        Assert.IsFalse(receipt.RemoteChanged);

        var restored = bindings.Save(Binding(product.Id, connection.Id, "r1", true, true, true), 0);
        var dispatchPreview = model.PreviewRemoteDeactivate(product.Id, connection.Id);
        Assert.AreEqual(restored.RemoteId, dispatchPreview.RemoteId);
        Assert.AreEqual(connection.Id, dispatchPreview.ConnectionId);
        Assert.AreEqual(1, remoteRouter.PreviewCalls);
        Assert.IsNotNull(bindings.Get(product.Id, connection.Id), "Creating a remote-deactivation preview must not unlink before a channel receipt exists.");
        Assert.AreEqual(0, remoteRouter.RemoteWrites);
        remoteRouter.Complete(dispatchPreview);
        var remoteReceipt = model.CompleteRemoteDeactivateAndUnlink(dispatchPreview);
        Assert.IsTrue(remoteReceipt.RemoteChanged);
        Assert.IsNull(bindings.Get(product.Id, connection.Id));
    }

    [TestMethod]
    public void ProductionRemoteDeactivateRouterRequiresApprovalAndSuccessfulChannelReceiptBeforeUnlink()
    {
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "A", Name = "A", Currency = "TRY" });
        var connection = Connected(new MarketplaceConnectionStore(directory), "etsy", "202", "Etsy");
        var bindings = new ProductChannelBindingStore(directory);
        bindings.Save(Binding(product.Id, connection.Id, "456", true, true, true), 0);
        var model = new ProductConnectionsModel(directory, new[] { product.Id }, new FakeSnapshotProvider(), new FakeCreationHandoff());

        Assert.IsTrue(model.CanPreviewRemoteDeactivate(product.Id, connection.Id));
        var preview = model.PreviewRemoteDeactivate(product.Id, connection.Id);
        Assert.ThrowsException<InvalidOperationException>(() => model.CompleteRemoteDeactivateAndUnlink(preview));
        model.ApproveRemoteDeactivate(preview, true);
        var dispatches = new ProductRemoteDeactivationDispatchStore(directory);
        Assert.AreEqual(preview.Id, dispatches.Pending(connection.Id).Single().Id);
        _ = new EtsyWorkspaceStore(directory);
        const string channelPlanId = "etsy-plan";
        var channelReceipt = new EtsyOperationReceipt { PlanId = channelPlanId, ProductId = product.Id, ListingId = 456, Status = "Claimed" };
        using (var database = new SqliteConnection($"Data Source={Path.Combine(directory, "catalog.db")}"))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "INSERT INTO EtsyWorkspaceReceipts(PlanId,ProductId,ShopId,Operation,OperationKey,Status,Json) VALUES($plan,$product,$shop,'Deactivate',$key,'Claimed',$json)";
            command.Parameters.AddWithValue("$plan", channelPlanId);
            command.Parameters.AddWithValue("$product", product.Id);
            command.Parameters.AddWithValue("$shop", connection.ShopId);
            command.Parameters.AddWithValue("$key", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(channelReceipt));
            command.ExecuteNonQuery();
        }
        dispatches.AttachChannelPlan(preview.Id, connection.Id, channelPlanId);
        Assert.ThrowsException<InvalidOperationException>(() => model.CompleteRemoteDeactivateAndUnlink(preview));
        Assert.IsNotNull(bindings.Get(product.Id, connection.Id), "A claimed/in-flight channel receipt must never unlink.");
        channelReceipt.Status = "Succeeded";
        using (var database = new SqliteConnection($"Data Source={Path.Combine(directory, "catalog.db")}"))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "UPDATE EtsyWorkspaceReceipts SET Status='Succeeded',Json=$json WHERE PlanId=$plan AND ProductId=$product";
            command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(channelReceipt));
            command.Parameters.AddWithValue("$plan", channelPlanId);
            command.Parameters.AddWithValue("$product", product.Id);
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        var receipt = model.CompleteRemoteDeactivateAndUnlink(preview);

        Assert.IsTrue(receipt.RemoteChanged);
        Assert.IsNull(bindings.Get(product.Id, connection.Id));
    }

    [TestMethod]
    public void LocalBindingDeleteAtomicallyFencesTheExactConnectionRevision()
    {
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "A", Name = "A", Currency = "TRY" });
        var connections = new MarketplaceConnectionStore(directory);
        var connection = Connected(connections, "trendyol", "101", "Shop");
        var bindings = new ProductChannelBindingStore(directory);
        var binding = bindings.Save(Binding(product.Id, connection.Id, "r1", true, true, true), 0);
        connections.Save(connection.Channel, connection.ShopId, "Renamed shop", true);

        Assert.ThrowsException<InvalidOperationException>(() =>
            bindings.Delete(product.Id, connection.Id, binding.Version, connection.Revision));
        Assert.IsNotNull(bindings.Get(product.Id, connection.Id), "A stale account preview must never remove the local binding.");
    }

    [TestMethod]
    public void SourcePreviewFencesCatalogAndXmlSourceAndPreservesSalesBindings()
    {
        var catalog = new CatalogStore(directory);
        var source = new XmlSource { Id = "xml-a", Name = "XML A", Location = "https://example.test/feed.xml", ItemPath = "//item", Enabled = true };
        catalog.SaveSource(source);
        var product = catalog.CreateManual(new() { Sku = "A", Name = "A", Currency = "TRY", Stock = 8 });
        var account = Connected(new MarketplaceConnectionStore(directory), "trendyol", "101", "Shop");
        new ProductChannelBindingStore(directory).Save(Binding(product.Id, account.Id, "r1", true, true, true), 0);
        var inventory = new InventoryLocationStore(directory);
        var physical = inventory.CreatePhysicalStore("store-1", "Physical shop");
        inventory.SetBalance(product.Id, physical.Id, 3, 0);
        var model = new ProductSourceModel(directory, product.Id);

        Assert.IsFalse(model.CanApply);
        var stale = model.PreviewChange(ProductFieldGroup.Price, ProductSourceKind.Xml, source.Id);
        source.Name = "Changed";
        catalog.SaveSource(source);
        Assert.ThrowsException<InvalidOperationException>(() => new ProductSourceBindingStore(directory).Save(
            new(product.Id, ProductFieldGroup.Price, ProductSourceKind.Xml, source.Id, true, 0, default),
            0, stale.ProductUpdatedUtc, stale.SourceRevision));
        Assert.ThrowsException<InvalidOperationException>(() => model.Apply(stale));

        var current = model.PreviewChange(ProductFieldGroup.Price, ProductSourceKind.Xml, source.Id);
        Assert.ThrowsException<InvalidOperationException>(() => model.Apply(current with { Kind = ProductSourceKind.Manual, SourceId = "", SourceRevision = "" }));
        var applied = model.Apply(current);
        Assert.AreEqual(ProductSourceKind.Xml, applied.Kind);
        Assert.AreEqual(source.Id, applied.SourceId);
        Assert.AreEqual(1, new ProductChannelBindingStore(directory).List(product.Id).Count, "Source edits must not create or remove sales bindings.");
        Assert.AreEqual(8, model.Balances.Single(x => x.LocationId == InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(3, model.Balances.Single(x => x.LocationId == physical.Id).Quantity);
    }

    [TestMethod]
    public void WpfCommandsStayDisabledUntilExactSelectionAndPreviewExist() => InSta(() =>
    {
        var catalog = new CatalogStore(directory);
        var first = catalog.CreateManual(new() { Sku = "A", Barcode = "BAR-A", Name = "A", Currency = "TRY" });
        catalog.CreateManual(new() { Sku = "B", Barcode = "BAR-B", Name = "B", Currency = "TRY" });
        catalog.SaveSource(new XmlSource { Id = "supplier-feed", Name = "Tedarikçi XML", Location = "https://example.test/feed.xml" });
        var connection = Connected(new MarketplaceConnectionStore(directory), "trendyol", "101", "Shop");
        var remote = new FakeSnapshotProvider();
        remote.Set(connection, new ProductChannelRemoteRow(connection.Id, connection.ShopId, "r1", "A", "BAR-A"));
        var window = new MainWindow(directory);
        try
        {
            var grid = GetField<DataGrid>(window, "products");
            var connect = LogicalWalk((DependencyObject)window.Content).OfType<Button>().Single(button => button.Name == "ProductConnectSelectedButton");
            var connectionsButton = LogicalWalk((DependencyObject)window.Content).OfType<Button>().Single(button => button.Name == "ProductConnectionsButton");
            var sourceButton = LogicalWalk((DependencyObject)window.Content).OfType<Button>().Single(button => button.Name == "ProductSourceButton");
            var sourceFilter = LogicalWalk((DependencyObject)window.Content).OfType<ComboBox>().Single(box => box.Name == "ProductSourceFilter");
            var sourceLabels = sourceFilter.Items.Cast<object>().Select(item => item.GetType().GetProperty("Label")!.GetValue(item)?.ToString()).ToArray();
            CollectionAssert.Contains(sourceLabels, "Tüm kaynaklar");
            CollectionAssert.Contains(sourceLabels, "Manuel ürünler");
            CollectionAssert.Contains(sourceLabels, "XML · Tedarikçi XML");
            Assert.IsFalse(connect.IsEnabled);
            Assert.IsFalse(connectionsButton.IsEnabled);
            Assert.IsFalse(sourceButton.IsEnabled);
            Assert.ThrowsException<TargetInvocationException>(() => Invoke(window, "SelectedProductIdsForConnection"));
            grid.SelectedItem = grid.Items.Cast<CatalogProduct>().Single(x => x.Id == first.Id);
            Assert.IsTrue(connect.IsEnabled);
            Assert.IsTrue(connectionsButton.IsEnabled);
            Assert.IsTrue(sourceButton.IsEnabled);
            var ids = (IReadOnlyList<string>)Invoke(window, "SelectedProductIdsForConnection")!;
            CollectionAssert.AreEqual(new[] { first.Id }, ids.ToArray());

            grid.SelectAll();
            Assert.IsTrue(connect.IsEnabled);
            Assert.IsTrue(connectionsButton.IsEnabled);
            Assert.IsFalse(sourceButton.IsEnabled);
            grid.UnselectAll();
            Assert.IsFalse(connect.IsEnabled);
            Assert.IsFalse(connectionsButton.IsEnabled);
            Assert.IsFalse(sourceButton.IsEnabled);
            grid.SelectedItem = grid.Items.Cast<CatalogProduct>().Single(x => x.Id == first.Id);

            var dialog = new ProductConnectionsWindow(directory, ids, remote, new FakeCreationHandoff());
            Assert.IsFalse(dialog.IsApplyEnabled);
            var controls = LogicalWalk((DependencyObject)dialog.Content).OfType<FrameworkElement>().ToArray();
            Assert.IsTrue(controls.Any(x => x.Name == "ProductManageContentEdit"));
            Assert.IsTrue(controls.Any(x => x.Name == "ProductManagePriceEdit"));
            Assert.IsTrue(controls.Any(x => x.Name == "ProductManageStockEdit"));
            Assert.IsFalse(((Button)controls.Single(x => x.Name == "ProductBindingFlagsApplyButton")).IsEnabled);
            dialog.Close();

            var source = new ProductSourceWindow(directory, first.Id);
            Assert.IsFalse(source.IsApplyEnabled);
            source.Close();
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void ProductGridBuildsStoreBindingMenuFromRegisteredAccountsAndPinsChosenTarget() => InSta(() =>
    {
        var catalog = new CatalogStore(directory);
        var firstProduct = catalog.CreateManual(new() { Sku = "A", Barcode = "BAR-A", Name = "A", Currency = "TRY" });
        var secondProduct = catalog.CreateManual(new() { Sku = "B", Barcode = "BAR-B", Name = "B", Currency = "TRY" });
        var connections = new MarketplaceConnectionStore(directory);
        var firstStore = Connected(connections, "trendyol", "101", "Trendyol Ana Mağaza");
        var secondStore = Connected(connections, "trendyol", "202", "Trendyol İkinci Mağaza");
        var window = new MainWindow(directory);
        try
        {
            var grid = GetField<DataGrid>(window, "products");
            grid.SelectedItems.Add(grid.Items.Cast<CatalogProduct>().Single(product => product.Id == firstProduct.Id));
            grid.SelectedItems.Add(grid.Items.Cast<CatalogProduct>().Single(product => product.Id == secondProduct.Id));
            string? openedConnection = null;
            var menu = (MenuItem)Invoke(window, "BuildMarketplaceBindingMenu", new Action<string>(id => openedConnection = id))!;

            Assert.AreEqual("Bağlantı sağlanacak mağazalar", menu.Header);
            Assert.IsTrue(menu.IsEnabled);
            var trendyol = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Trendyol"));
            var accounts = trendyol.Items.OfType<MenuItem>().ToArray();
            CollectionAssert.AreEqual(new[] { "Trendyol Ana Mağaza · 101", "Trendyol İkinci Mağaza · 202" }, accounts.Select(item => item.Header?.ToString()).ToArray());
            var connect = accounts[1].Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Seçili ürünleri bağla"));
            connect.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.AreEqual(secondStore.Id, openedConnection);

            var dialog = new ProductConnectionsWindow(directory, new[] { firstProduct.Id, secondProduct.Id }, initialConnectionId: firstStore.Id);
            try { Assert.AreEqual(firstStore.Id, dialog.SelectedConnectionId); }
            finally { dialog.Close(); }
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void WpfPreviewOffersExplicitManualSkuChoice() => InSta(() =>
    {
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "SKU-A", Barcode = "LOCAL", Name = "A", Currency = "TRY" });
        var connection = Connected(new MarketplaceConnectionStore(directory), "trendyol", "101", "Shop");
        var remote = new FakeSnapshotProvider();
        remote.Set(connection,
            new ProductChannelRemoteRow(connection.Id, connection.ShopId, "r1", "SKU-A", "REMOTE-1"),
            new ProductChannelRemoteRow(connection.Id, connection.ShopId, "r2", "SKU-A", "REMOTE-2"));
        var window = new ProductConnectionsWindow(directory, new[] { product.Id }, remote, new FakeCreationHandoff());
        try
        {
            var target = GetField<ComboBox>(window, "targets");
            target.SelectedValue = connection.Id;
            GetField<Button>(window, "previewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var grid = GetField<DataGrid>(window, "previewRows");
            var choice = grid.ItemsSource.Cast<ProductConnectionReviewRow>().Single();
            CollectionAssert.AreEqual(new[] { "r1", "r2" }, choice.RemoteOptions.ToArray());
            Assert.AreEqual("", choice.SelectedRemoteId, "SKU evidence must never auto-select a remote listing.");

            choice.SelectedRemoteId = "r1";
            GetField<Button>(window, "reviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var reviewed = grid.ItemsSource.Cast<ProductConnectionReviewRow>().Single();
            Assert.IsTrue(reviewed.Reviewed);
            Assert.IsTrue(GetField<Button>(window, "applyButton").IsEnabled);
            reviewed.SelectedRemoteId = "r2";
            Assert.IsFalse(reviewed.Reviewed);
            Assert.IsFalse(GetField<Button>(window, "applyButton").IsEnabled, "Changing shown identity after review must invalidate apply.");
            GetField<Button>(window, "reviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(GetField<Button>(window, "applyButton").IsEnabled);
            GetField<Button>(window, "applyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual("r2", new ProductChannelBindingStore(directory).Get(product.Id, connection.Id)!.RemoteId);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void ChangingTargetAccountInvalidatesTheReviewedPreviewAndApply() => InSta(() =>
    {
        var product = new CatalogStore(directory).CreateManual(new() { Sku = "A", Barcode = "BAR", Name = "A", Currency = "TRY" });
        var connections = new MarketplaceConnectionStore(directory);
        var first = Connected(connections, "trendyol", "101", "Shop A");
        var second = Connected(connections, "trendyol", "202", "Shop B");
        var remote = new FakeSnapshotProvider();
        remote.Set(first, new ProductChannelRemoteRow(first.Id, first.ShopId, "r1", "A", "BAR"));
        remote.Set(second, new ProductChannelRemoteRow(second.Id, second.ShopId, "r2", "A", "BAR"));
        var window = new ProductConnectionsWindow(directory, new[] { product.Id }, remote, new FakeCreationHandoff());
        try
        {
            var target = GetField<ComboBox>(window, "targets");
            target.SelectedValue = first.Id;
            GetField<Button>(window, "previewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            GetField<Button>(window, "reviewButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(GetField<Button>(window, "applyButton").IsEnabled);

            target.SelectedValue = second.Id;

            Assert.IsFalse(GetField<Button>(window, "applyButton").IsEnabled);
            Assert.AreEqual(0, GetField<DataGrid>(window, "previewRows").Items.Count);
            Assert.IsNull(GetField<ProductConnectionsModel>(window, "model").CurrentPreview);
        }
        finally { window.Close(); }
    });

    static MarketplaceConnection Connected(MarketplaceConnectionStore store, string channel, string shopId, string name)
    {
        var connection = store.Save(channel, shopId, name, true);
        Assert.AreEqual(ConnectionTestApplyResult.Applied, store.RecordTest(connection.Id, connection.Revision, true));
        return store.Get(connection.Id)!;
    }

    static ProductChannelBinding Binding(string productId, string connectionId, string remoteId, bool content, bool price, bool stock) =>
        new(productId, connectionId, remoteId, "REMOTE-SKU", "REMOTE-BAR", content, price, stock, "category", "template", "Active", 0, default);

    static T GetField<T>(object target, string name) => (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
        ?? throw new InvalidOperationException(name));

    static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, args)
        ?? throw new MissingMethodException(target.GetType().Name, name);

    static void InvokeVoid(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(target.GetType().Name, name);
        method.Invoke(target, args);
    }

    static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Walk(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    static IEnumerable<DependencyObject> LogicalWalk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalWalk(child)) yield return descendant;
    }

    static void InSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "STA UI test timed out.");
        if (error is not null) throw error;
    }

    sealed class FakeSnapshotProvider : IProductChannelRemoteSnapshotProvider
    {
        readonly Dictionary<string, ProductChannelRemoteSnapshot> snapshots = new(StringComparer.Ordinal);
        public void Set(MarketplaceConnection connection, params ProductChannelRemoteRow[] rows) => snapshots[connection.Id] = new(connection.Id, connection.ShopId, rows);
        public ProductChannelRemoteSnapshot Read(MarketplaceConnection connection) => snapshots.TryGetValue(connection.Id, out var snapshot)
            ? snapshot
            : new(connection.Id, connection.ShopId, Array.Empty<ProductChannelRemoteRow>());
    }

    sealed class FakeCreationHandoff : IProductChannelCreationPreviewHandoff
    {
        public IReadOnlyList<string> ProductIds { get; private set; } = Array.Empty<string>();
        public int RemoteWrites { get; private set; }
        public string Preview(MarketplaceConnection connection, IReadOnlyList<string> productIds)
        {
            ProductIds = productIds.ToArray();
            return "creation-preview";
        }
    }

    sealed class StalingCreationHandoff(string directory, CatalogStore catalog, CatalogProduct product) : IProductChannelCreationPreviewHandoff
    {
        public string RequestId { get; private set; } = "";
        public string Preview(MarketplaceConnection connection, IReadOnlyList<string> productIds)
        {
            RequestId = new LocalProductCreationPreviewHandoff(directory).Preview(connection, productIds);
            product.Name += " changed";
            catalog.SaveProduct(product);
            return RequestId;
        }
    }

    sealed class FakeRemoteDeactivationRouter : IProductRemoteDeactivationPreviewRouter
    {
        ProductRemoteDeactivationReceipt? receipt;
        ProductRemoteDeactivationPreview? latest;
        public int PreviewCalls { get; private set; }
        public int RemoteWrites { get; private set; }
        public bool Supports(MarketplaceConnection connection, IMarketplaceAdapter adapter) => true;
        public ProductRemoteDeactivationPreview Preview(MarketplaceConnection connection, ProductChannelBinding binding)
        {
            PreviewCalls++;
            return latest = new("dispatch-preview", binding.ProductId, connection.Id, binding.RemoteId, binding.Version, connection.Revision, DateTime.UtcNow);
        }
        public void Approve(ProductRemoteDeactivationPreview preview, bool explicitlyApproved)
        { if (!explicitlyApproved) throw new InvalidOperationException("Approval required."); latest = preview; }
        public ProductRemoteDeactivationPreview? Latest(string productId, string connectionId) =>
            latest is { } value && value.ProductId == productId && value.ConnectionId == connectionId ? value : null;
        public ProductRemoteDeactivationReceipt? Receipt(string previewId) => receipt?.PreviewId == previewId ? receipt : null;
        public void Complete(ProductRemoteDeactivationPreview preview) => receipt = new(preview.Id, preview.ProductId, preview.ConnectionId, preview.RemoteId, true, "dispatch-receipt", DateTime.UtcNow);
    }
}
