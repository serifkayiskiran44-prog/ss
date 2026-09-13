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

// #813 in the real window: a product link and a source link land on their screens *selected*, Back re-selects
// what the previous crumb named, a crumb whose entity was deleted degrades to its screen, and a link naming a
// store that is not an enabled connection on disk never moves the window.
[TestClass]
public sealed class WorkspaceLinkWindowTests
{
    [TestMethod]
    public void ProductAndSourceLinksSelectTheirEntityAndBackReselectsThePreviousOne()
    {
        Run(f =>
        {
            Assert.IsTrue(f.Open(f.ProductLink("A", "Product A")));
            Assert.AreEqual("products", f.CurrentRoute);
            Assert.AreEqual(f.ProductId("A"), ((CatalogProduct)f.Products.SelectedItem).Id, "The landing screen selects the product the link names.");
            StringAssert.Contains(f.Breadcrumb.Text, "Product A");

            Assert.IsTrue(f.Open(new DrillTarget("xml", "XML yönetimi", DashboardStoreFilter.AllStoresKey, "source", "feed-1", "Fixture feed")));
            Assert.AreEqual("xml", f.CurrentRoute);
            Assert.AreEqual("feed-1", ((XmlSource)f.Sources.SelectedItem).Id, "A source link selects the source.");
            StringAssert.Contains(f.Breadcrumb.Text, "Fixture feed");

            f.Back();
            Assert.AreEqual("products", f.CurrentRoute);
            Assert.AreEqual(f.ProductId("A"), ((CatalogProduct)f.Products.SelectedItem).Id, "Back restores the selection the crumb names, not just the screen.");
        });
    }

    [TestMethod]
    public void ACrumbWhoseProductWasDeletedFallsBackToTheProductScreen()
    {
        Run(f =>
        {
            Assert.IsTrue(f.Open(f.ProductLink("A", "Product A")));
            Assert.IsTrue(f.Open(f.ProductLink("B", "Product B")));
            f.Store.DeleteProduct(f.Store.FindProduct(f.ProductId("A"))!);

            f.Back();

            Assert.AreEqual("products", f.CurrentRoute, "The screen is still worth returning to.");
            Assert.IsFalse(f.Breadcrumb.Text.Contains("Product A"), "The crumb stops naming a record that no longer exists.");
        });
    }

    [TestMethod]
    public void ALinkNamingAStoreThatIsNotAnEnabledConnectionNeverMovesTheWindow()
    {
        Run(f =>
        {
            foreach (var store in new[] { "allegro|shop-b", "ALLEGRO|shop-a", "etsy|shop-a" })
            {
                Assert.IsFalse(f.Open(f.ProductLink("A", "Product A") with { StoreKey = store }), $"'{store}' is not an enabled connection on disk.");
                Assert.AreEqual("dashboard", f.CurrentRoute);
            }
            Assert.IsTrue(f.Open(f.ProductLink("A", "Product A") with { StoreKey = "allegro|shop-a" }), "The one enabled connection is allowed.");
            Assert.AreEqual("products", f.CurrentRoute);
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
        public readonly string Root = Path.Combine(Path.GetTempPath(), "wslink-" + Guid.NewGuid().ToString("N"));
        public MainWindow Window; public CatalogStore Store;

        public Fixture()
        {
            Store = new CatalogStore(Root);
            var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" };
            Store.Import(source, new[]
            {
                new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 10 },
                new CatalogProduct { SourceId = source.Id, Sku = "B", Name = "Product B", Price = 12, Stock = 3 },
            });
            new MarketplaceConnectionStore(Root).Save("allegro", "shop-a", "Allegro Shop", true);
            new MarketplaceConnectionStore(Root).Save("allegro", "shop-off", "Disabled Shop", false);
            Window = new MainWindow(Root); Window.Show();
            typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, new object[] { "dashboard", true });
            Drain();
        }

        public string ProductId(string sku) => Store.Products().Single(p => p.Sku == sku).Id;
        public DrillTarget ProductLink(string sku, string label) => new("products", "Ürün yönetimi", DashboardStoreFilter.AllStoresKey, "product", ProductId(sku), label);
        public DataGrid Products => (DataGrid)Field("products");
        public ListBox Sources => (ListBox)Field("sources");
        public TextBlock Breadcrumb => (TextBlock)Window.FindName("BreadcrumbText");
        public string CurrentRoute => (string)typeof(MainWindow).GetField("currentRoute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);
        public bool Open(DrillTarget target)
        {
            var opened = (bool)typeof(MainWindow).GetMethod("OpenWorkspaceLink", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, new object[] { target })!;
            Drain(); return opened;
        }
        public void Back() { ((Button)Window.FindName("BackButton")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(); }
        object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);
        public void Drain() { Window.UpdateLayout(); Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        public void Dispose()
        {
            Window.Close();
            for (var attempt = 0; attempt < 30; attempt++)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(Root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }
}
