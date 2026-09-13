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

// #810, in the real window rather than the model: the breadcrumb the operator reads, the back button's enabled
// state, and the refusal of a wrong-store deep link all have to come from the same trail the tests above cover.
[TestClass]
public sealed class DashboardDrillThroughWindowTests
{
    [TestMethod]
    public void DrillingThroughFromTheBoardShowsTheTrailAndBackWalksItToTheRoot()
    {
        Run(f =>
        {
            Assert.AreEqual("Genel bakış", f.Breadcrumb.Text, "A board with nothing behind it is the whole trail.");
            Assert.IsFalse(f.BackButton.IsEnabled);

            f.Drill(new DrillRequest(new DrillTarget("products", "Ürün yönetimi", DashboardStoreFilter.AllStoresKey, "anomaly", "oversell", "Aşırı satış riski"), Array.Empty<string>()));

            Assert.AreEqual("products", f.CurrentRoute);
            StringAssert.Contains(f.Breadcrumb.Text, "Aşırı satış riski", "The crumb names the card that was followed, not just the screen it opened.");
            Assert.IsTrue(f.BackButton.IsEnabled);

            f.BackButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            f.Drain();

            Assert.AreEqual("dashboard", f.CurrentRoute);
            Assert.AreEqual("Genel bakış", f.Breadcrumb.Text);
            Assert.IsFalse(f.BackButton.IsEnabled, "Back at the root has nowhere left to go.");
        });
    }

    [TestMethod]
    public void ADeepLinkNamingAStoreThisSessionCannotUseDoesNotMoveTheWindow()
    {
        Run(f =>
        {
            f.Drill(new DrillRequest(new DrillTarget("orders", "Siparişler", "etsy|shop-admin", "order", "1001", "1001 numaralı sipariş"), new[] { "etsy|shop-a" }));

            Assert.AreEqual("dashboard", f.CurrentRoute, "A wrong-store link must not open the screen it names.");
            Assert.AreEqual("Genel bakış", f.Breadcrumb.Text);
            Assert.IsFalse(f.BackButton.IsEnabled);
        });
    }

    [TestMethod]
    public void SwitchingTheBoardsStoreReturnsToTheBoardAndClearsTheTrailItCameFrom()
    {
        Run(f =>
        {
            f.Drill(new DrillRequest(new DrillTarget("products", "Ürün yönetimi", "etsy|shop-a", "anomaly", "oversell", "Aşırı satış riski"), new[] { "etsy|shop-a" }));
            Assert.AreEqual("products", f.CurrentRoute);

            typeof(MainWindow).GetMethod("SwitchDashboardStore", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Window, new object[] { "ozon|shop-c" });
            f.Drain();

            Assert.AreEqual("dashboard", f.CurrentRoute, "The trail belonged to the store that was just switched away from.");
            Assert.IsFalse(f.BackButton.IsEnabled);
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
        public readonly string Root = Path.Combine(Path.GetTempPath(), "drill-" + Guid.NewGuid().ToString("N"));
        public MainWindow Window;

        public Fixture()
        {
            var store = new CatalogStore(Root);
            var source = new XmlSource { Id = "fixture", Name = "Fixture" };
            store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 10 } });
            Window = new MainWindow(Root); Window.Show();
            Navigate("dashboard");
            Drain();
        }

        public TextBlock Breadcrumb => (TextBlock)Window.FindName("BreadcrumbText");
        public Button BackButton => (Button)Window.FindName("BackButton");
        public string CurrentRoute => (string)typeof(MainWindow).GetField("currentRoute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);
        public void Navigate(string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, new object[] { key, true });
        public void Drill(DrillRequest request)
        {
            typeof(MainWindow).GetMethod("DrillThrough", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, new object[] { request });
            Drain();
        }
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
