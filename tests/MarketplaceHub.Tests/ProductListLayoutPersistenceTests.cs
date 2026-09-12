using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #792 (DESIGN: Product list column layout persistence) on the real entry path: MainWindow → products page.
// Column order, width, visibility and the sort choice must survive a restart (a new MainWindow over the same
// data directory), a layout that names a column which no longer exists must fall back safely, and a layout
// never leaks into another store (data directory).
[TestClass]
public sealed class ProductListLayoutPersistenceTests
{
    [TestMethod]
    public void ColumnOrderWidthVisibilityAndSortSurviveARestart()
    {
        Run(root =>
        {
            using (var first = new Session(root))
            {
                first.Column("Name").DisplayIndex = 0;                       // reorder: product name first
                first.Column("Sku").Width = new DataGridLength(222);          // resize
                first.Column("Barcode").Visibility = Visibility.Collapsed;   // hide
                first.SortBy("Price", descending: true);                     // sort through the filter bar
                first.Drain();
            }

            using var second = new Session(root);
            Assert.AreEqual(0, second.Column("Name").DisplayIndex, "Column order was not restored after the restart.");
            Assert.AreEqual(222d, second.Column("Sku").Width.Value, "Column width was not restored after the restart.");
            Assert.AreEqual(Visibility.Collapsed, second.Column("Barcode").Visibility, "Column visibility was not restored after the restart.");
            Assert.AreEqual(Visibility.Visible, second.Column("Sku").Visibility);
            var filter = (CatalogFilter)second.Field("productFilter");
            Assert.AreEqual("Price", filter.SortBy, "The sort choice was not restored after the restart.");
            Assert.IsTrue(filter.SortDescending);
            Assert.AreEqual("Price", second.SortBox().SelectedItem?.ToString(), "The sort control does not show the restored sort.");
        });
    }

    [TestMethod]
    public void ALayoutThatNamesARemovedColumnOrIsCorruptFallsBackWithoutLosingTheRest()
    {
        Run(root =>
        {
            // A layout written by a version that still had a "Ghost" column, plus a real reorder and resize.
            new UiPreferenceStore(root).Set("layout:products", """{"Version":1,"Columns":[{"Key":"Ghost","DisplayIndex":0,"Width":300,"Visible":true},{"Key":"Name","DisplayIndex":1,"Width":180,"Visible":true},{"Key":"Sku","DisplayIndex":2,"Width":150,"Visible":false}],"SortBy":"Stock","SortDescending":false}""");
            using (var session = new Session(root))
            {
                // Ghost is skipped; Name keeps its saved position ahead of Sku, and the columns the saved layout
                // never knew (StatusLabel first, everything after Sku) stay at their own default positions.
                Assert.IsTrue(session.Column("Name").DisplayIndex < session.Column("Sku").DisplayIndex, "The removed column is skipped and the saved columns keep their relative order.");
                Assert.AreEqual(1, session.Column("Name").DisplayIndex);
                Assert.AreEqual(2, session.Column("Sku").DisplayIndex);
                Assert.AreEqual(150d, session.Column("Sku").Width.Value);
                Assert.AreEqual(Visibility.Collapsed, session.Column("Sku").Visibility);
                Assert.AreEqual("Stock", ((CatalogFilter)session.Field("productFilter")).SortBy);
            }

            new UiPreferenceStore(root).Set("layout:products", "{not json");
            using (var session = new Session(root))
            {
                Assert.AreEqual(1, session.Column("Sku").DisplayIndex, "A corrupt layout is ignored and the default layout is used.");
                Assert.AreEqual(Visibility.Visible, session.Column("Sku").Visibility);
                Assert.AreEqual("Name", ((CatalogFilter)session.Field("productFilter")).SortBy);
            }
        });
    }

    [TestMethod]
    public void ALayoutSavedForOneStoreIsNotAppliedToAnotherStore()
    {
        Run(root =>
        {
            var other = Path.Combine(Path.GetTempPath(), "product-layout-other-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var first = new Session(root)) { first.Column("Name").DisplayIndex = 0; first.Drain(); }
                using var second = new Session(other);
                Assert.AreEqual(2, second.Column("Name").DisplayIndex, "Another store starts from the default layout.");
            }
            finally { Cleanup(other); }
        });
    }

    sealed class Session : IDisposable
    {
        public readonly MainWindow Window; readonly DataGrid grid;
        public Session(string root)
        {
            Window = new MainWindow(root); Window.Show();
            typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, new object[] { "products", true });
            grid = (DataGrid)Field("products"); Drain();
        }
        public object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
        public DataGridColumn Column(string property) => grid.Columns.Single(c => c is DataGridBoundColumn bound && bound.Binding is Binding binding && binding.Path.Path == property);
        public ComboBox SortBox() => Descendants(Window).OfType<ComboBox>().Single(c => c.ItemsSource is string[] items && items.Contains("Price") && items.Contains("Stock"));
        public void SortBy(string field, bool descending)
        {
            SortBox().SelectedItem = field;
            Descendants(Window).OfType<CheckBox>().Single(c => c.Content as string == "Azalan").IsChecked = descending;
            Descendants(Window).OfType<Button>().Single(b => b.Content as string == "Filtreleri uygula").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        public void Drain() { Window.UpdateLayout(); Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        public void Dispose() { Window.Close(); Drain(); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject node)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) { yield return child; foreach (var d in Descendants(child)) yield return d; }
        }
    }

    static void Run(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "product-layout-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { test(root); } catch (Exception ex) { failure = ex; } finally { Cleanup(root); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
