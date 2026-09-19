using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class UnifiedOrdersPanelTests
{
    [TestMethod]
    public void SingleOrdersScreenShowsAccountStatusSourceReviewFiltersAndManualSale() => InSta(root =>
    {
        var panel = OrdersPanel.Create(root);
        var controls = Walk(panel).ToArray();

        Assert.IsNotNull(controls.OfType<ListBox>().SingleOrDefault(control => control.Name == "MarketplaceOrderConnectionFilter"));
        Assert.IsNotNull(controls.OfType<ListBox>().SingleOrDefault(control => control.Name == "MarketplaceOrderStatusFilter"));
        Assert.IsNotNull(controls.OfType<ListBox>().SingleOrDefault(control => control.Name == "MarketplaceOrderSourceFilter"));
        Assert.IsNotNull(controls.OfType<ComboBox>().SingleOrDefault(control => control.Name == "MarketplaceOrderReviewFilter"));
        Assert.IsNotNull(controls.OfType<Button>().SingleOrDefault(control => control.Name == "ManualStoreSaleButton"));
        Assert.IsNotNull(controls.OfType<Button>().SingleOrDefault(control => control.Name == "InventoryLocationsButton"));
        var grid = controls.OfType<DataGrid>().Single();
        Assert.IsTrue(grid.Columns.Any(column => Equals(column.Header, "Hesap")));
    });

    [TestMethod]
    public void ProductInventoryViewExposesPreviewThenApplyTransferControls() => InSta(root =>
    {
        var catalog = new CatalogStore(root);
        var product = catalog.CreateManual(new() { Sku = "SKU", Name = "Product", Stock = 4, Currency = "TRY" });
        new InventoryLocationStore(root).CreatePhysicalStore("shop", "Shop");
        var window = new ProductSourceWindow(root, product.Id);
        try
        {
            window.Show(); window.UpdateLayout();
            var controls = Walk(window).ToArray();
            Assert.IsNotNull(controls.OfType<ComboBox>().SingleOrDefault(control => control.Name == "InventoryTransferFrom"));
            Assert.IsNotNull(controls.OfType<ComboBox>().SingleOrDefault(control => control.Name == "InventoryTransferTo"));
            Assert.IsNotNull(controls.OfType<Button>().SingleOrDefault(control => control.Name == "InventoryTransferPreviewButton"));
            Assert.IsNotNull(controls.OfType<Button>().SingleOrDefault(control => control.Name == "InventoryTransferApplyButton"));
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void AccountFilterUsesConnectionIdWhileShowingDuplicateDisplayNames() => InSta(root =>
    {
        var store = new OrdersStore(root);
        store.SaveBatch(new[]
        {
            RemoteOrder("connection-a", "Shared name", "shop-a", "ORDER-A"),
            RemoteOrder("connection-b", "Shared name", "shop-b", "ORDER-B")
        });
        var window = new Window { Content = OrdersPanel.Create(root), Width = 1200, Height = 700 };
        try
        {
            window.Show(); window.UpdateLayout();
            var controls = Walk(window).ToArray();
            var filter = controls.OfType<ListBox>().Single(control => control.Name == "MarketplaceOrderConnectionFilter");
            var options = filter.Items.Cast<OrderConnectionFilterOption>().ToArray();
            Assert.AreEqual(2, options.Length);
            Assert.AreEqual(1, options.Select(option => option.ConnectionId).Distinct(StringComparer.Ordinal).Count(id => id == "connection-a"));
            CollectionAssert.AreEquivalent(
                new[] { "Shared name · Trendyol · shop-a", "Shared name · Trendyol · shop-b" },
                options.Select(option => option.Label).ToArray());

            filter.SelectedItems.Add(options.Single(option => option.ConnectionId == "connection-a"));
            window.UpdateLayout();

            var rows = controls.OfType<DataGrid>().Single().ItemsSource.Cast<OrderSnapshot>().ToArray();
            Assert.AreEqual(1, rows.Length);
            Assert.AreEqual("connection-a", rows[0].ConnectionId);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void TrendyolRemoteOrderIsReadOnlyAndReviewRequiredStockActionIsDisabled() => InSta(root =>
    {
        var remote = RemoteOrder("trendyol-1", "Trendyol", "101", "REMOTE-1");
        remote.ReviewRequired = true; remote.ReviewReason = "Binding missing";
        new OrdersStore(root).SaveBatch(new[] { remote });
        var window = new Window { Content = OrdersPanel.Create(root), Width = 1200, Height = 700 };
        try
        {
            window.Show(); window.UpdateLayout();
            var grid = Walk(window).OfType<DataGrid>().Single();
            grid.SelectedItem = grid.Items.Cast<OrderSnapshot>().Single(); window.UpdateLayout();
            var controls = Walk(window).ToArray();
            Assert.IsTrue(controls.OfType<TextBox>().Single(control => control.Name == "OrderRawStatusField").IsReadOnly);
            Assert.IsTrue(controls.OfType<TextBox>().Single(control => control.Name == "OrderPaymentStatusField").IsReadOnly);
            Assert.IsFalse(controls.OfType<Button>().Single(control => control.Name == "OrderApplyStockButton").IsEnabled);
            Assert.IsFalse(controls.OfType<Button>().Any(control => control.Name == "OrderItemAddButton"));
            Assert.IsFalse(controls.OfType<Button>().Any(control => control.Name == "OrderSaveButton"));
            Assert.ThrowsException<InvalidOperationException>(() => new OrdersStore(root).SaveManual(remote));
            Assert.AreEqual("Trendyol API", new OrdersStore(root).ReadAll().Single().Source);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void PhysicalSaleOrderCannotExposeOnlineStockAction() => InSta(root =>
    {
        var catalog = new CatalogStore(root);
        var product = catalog.CreateManual(new() { Sku = "PHYSICAL", Name = "Physical", Stock = 10, Currency = "TRY" });
        var inventory = new InventoryLocationStore(root);
        var location = inventory.CreatePhysicalStore("physical", "Physical shop");
        inventory.SetBalance(product.Id, location.Id, 3, 0);
        var sales = new ManualSaleService(root);
        sales.Apply(sales.Preview(product.Id, location.Id, 1), approved: true);
        var window = new Window { Content = OrdersPanel.Create(root), Width = 1200, Height = 700 };
        try
        {
            window.Show(); window.UpdateLayout();
            var grid = Walk(window).OfType<DataGrid>().Single();
            grid.SelectedItem = grid.Items.Cast<OrderSnapshot>().Single(); window.UpdateLayout();
            var stock = Walk(window).OfType<Button>().Single(control => control.Name == "OrderApplyStockButton");
            Assert.IsFalse(stock.IsEnabled);
            Assert.AreEqual(10, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        }
        finally { window.Close(); }
    });

    static OrderSnapshot RemoteOrder(string connectionId, string displayName, string shop, string id) => new()
    {
        Marketplace = "Trendyol", ShopId = shop, OrderId = id, ConnectionId = connectionId,
        ConnectionDisplayName = displayName, Source = "Trendyol API", RawStatus = "created",
        UpdatedAt = DateTimeOffset.UtcNow, SourceUpdatedAt = DateTimeOffset.UtcNow,
        Items = [new() { Title = "Remote", Sku = "REMOTE", Quantity = 1 }]
    };

    static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    [TestMethod]
    public void FreshProfileCanCreatePhysicalLocationTransferStockAndCompleteManualSale() => InSta(root =>
    {
        var catalog = new CatalogStore(root);
        var product = catalog.CreateManual(new() { Sku = "FRESH", Name = "Fresh", Stock = 5, Currency = "TRY" });
        var locations = new InventoryLocationManagementModel(root);

        var physical = locations.CreatePhysicalStore("Kadikoy");
        Assert.ThrowsException<InvalidOperationException>(() => locations.CreatePhysicalStore(" kadikoy "));
        Assert.ThrowsException<ArgumentException>(() => locations.CreatePhysicalStore("  "));
        var preview = locations.PreviewTransfer(product.Id, InventoryLocationStore.OnlineLocationId, physical.Id, 3);
        locations.ApplyTransfer(preview);
        var sale = new ManualSaleService(root);
        sale.Apply(sale.Preview(product.Id, physical.Id, 2), approved: true);

        var inventory = new InventoryLocationStore(root);
        Assert.AreEqual(2, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(1, inventory.GetBalance(product.Id, physical.Id).Quantity);
        Assert.AreEqual(1, new OrdersStore(root).ReadAll().Count(order => order.IsPhysicalSale));
    });

    static void InSta(Action<string> action)
    {
        Exception? failure = null;
        var root = Path.Combine(Path.GetTempPath(), "unified-orders-ui-" + Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            try { action(root); }
            catch (Exception error) { failure = error; }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
}
