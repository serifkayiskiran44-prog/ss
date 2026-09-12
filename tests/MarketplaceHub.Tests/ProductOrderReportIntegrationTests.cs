using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

[TestClass]
public sealed class ProductOrderReportIntegrationTests
{
    [TestMethod]
    public void OrderReportPanelShowsOnlySelectedProductsOwnOrdersAndClearsOnDeselection()
    {
        Run(f => {
            f.Grid.SelectedItem = f.Row("A"); f.Drain();
            Assert.AreEqual("Sipariş: 2 · Adet: 5 · Son sipariş: A-2 · etsy / shop-1", f.OrderSummary.Text);

            f.Grid.SelectedItem = f.Row("B"); f.Drain();
            Assert.AreEqual("Sipariş: 1 · Adet: 1 · Son sipariş: B-1 · trendyol / shop-2", f.OrderSummary.Text);

            f.Grid.SelectedItem = f.Row("C"); f.Drain();
            Assert.AreEqual("Sipariş: 0 · Adet: 0 · Son sipariş: — · —", f.OrderSummary.Text);

            f.Grid.SelectedItem = null; f.Drain();
            Assert.AreEqual("Ürün seçince yerel sipariş özeti burada görünür.", f.OrderSummary.Text);
        });
    }

    [TestMethod]
    public void ChannelTabShowsOnlySelectedProductsOwnMappingAndBothInstancesStayInSync()
    {
        Run(f => {
            f.Grid.SelectedItem = f.Row("A"); f.Drain();
            StringAssert.Contains(f.ChannelSummary.Text, "eşleme offer-A");
            Assert.AreEqual(f.ChannelSummary.Text, f.ChannelSummaryMirror.Text);

            f.Grid.SelectedItem = f.Row("B"); f.Drain();
            StringAssert.Contains(f.ChannelSummary.Text, "allegro / shop-a");
            Assert.IsFalse(f.ChannelSummary.Text.Contains("eşleme"), "B has no mapping and must not show A's leftover eşleme id.");
            Assert.AreEqual(f.ChannelSummary.Text, f.ChannelSummaryMirror.Text);

            f.Grid.SelectedItem = null; f.Drain();
            Assert.AreEqual("Ürün seçince kanal planları burada görünür.", f.ChannelSummary.Text);
            Assert.AreEqual(f.ChannelSummary.Text, f.ChannelSummaryMirror.Text);
        });
    }

    static void Run(Action<Fixture> test)
    {
        Exception failure = null;
        var thread = new Thread(() => { try { using var f = new Fixture(); test(f); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "order-report-" + Guid.NewGuid().ToString("N"));
        public MainWindow Window; public DataGrid Grid; public TextBlock OrderSummary; public TextBlock ChannelSummary; public TextBlock ChannelSummaryMirror;

        public Fixture()
        {
            var store = new CatalogStore(Root);
            var source = new XmlSource { Id = "fixture", Name = "Fixture" };
            store.Import(source, new[] {
                new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 5 },
                new CatalogProduct { SourceId = source.Id, Sku = "B", Name = "Product B", Price = 20, Stock = 5 },
                new CatalogProduct { SourceId = source.Id, Sku = "C", Name = "Product C without orders", Price = 30, Stock = 5 }
            });

            new OrdersStore(Root).SaveBatch(new[] {
                new OrderSnapshot { Marketplace = "etsy", ShopId = "shop-1", OrderId = "A-1", UpdatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), Items = [new() { Sku = "A", Title = "Product A", Quantity = 2 }] },
                new OrderSnapshot { Marketplace = "etsy", ShopId = "shop-1", OrderId = "A-2", UpdatedAt = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), Items = [new() { Sku = "A", Title = "Product A", Quantity = 3 }] },
                new OrderSnapshot { Marketplace = "trendyol", ShopId = "shop-2", OrderId = "B-1", UpdatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), Items = [new() { Sku = "B", Title = "Product B", Quantity = 1 }] }
            });

            var productA = store.Products().Single(x => x.Sku == "A");
            new MarketplaceConnectionStore(Root).Save("allegro", "shop-a", "Allegro Shop", true);
            new MarketplaceMappingStore(Root).Save(new("allegro", "shop-a", productA.Id, "offer-A"));

            Window = new MainWindow(Root); Window.Show();
            typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Window, new object[] { "products", true });
            Grid = (DataGrid)Field("products");
            OrderSummary = (TextBlock)Field("productOrderSummary");
            ChannelSummary = (TextBlock)Field("productChannelSummary");
            ChannelSummaryMirror = (TextBlock)Field("productChannelSummaryMirror");
            Drain();
        }

        public object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Window);
        public CatalogProduct Row(string sku) => Grid.Items.OfType<CatalogProduct>().Single(x => x.Sku == sku);
        public void Drain() { Window.UpdateLayout(); Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        public void Dispose() { Window.Close(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
    }
}
