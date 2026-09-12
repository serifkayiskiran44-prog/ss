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

// #793 (DESIGN: Product list density switch). Compact and comfortable are applied to the same DataGrid that
// owns the list -- no parallel control, no second style tree -- and font size, row height and hit target scale
// together so a compact row stays clickable. The switch keeps the selection, keeps virtualization on, and the
// choice is part of the persisted view settings.
[TestClass]
public sealed class ProductListDensityTests
{
    [TestMethod]
    public void TheTwoModesScaleFontRowHeightAndHitTargetTogetherAndCompactStaysClickable()
    {
        var comfortable = ProductListDensity.Metrics(ProductListDensity.Comfortable);
        var compact = ProductListDensity.Metrics(ProductListDensity.Compact);

        Assert.IsTrue(compact.RowHeight < comfortable.RowHeight, "Compact must actually be denser.");
        Assert.IsTrue(compact.FontSize < comfortable.FontSize, "Font scales with the row, otherwise the text just gets more cramped padding.");
        Assert.IsTrue(compact.ThumbnailSize < comfortable.ThumbnailSize);
        Assert.IsTrue(compact.RowHeight >= 24, "A row must remain a usable pointer/touch target; 24 DIP is the floor.");
        Assert.IsTrue(compact.FontSize >= 11, "Text must stay legible at 100% DPI.");
        Assert.IsTrue(compact.ThumbnailSize <= compact.RowHeight, "A thumbnail may never be taller than the row it sits in.");
        Assert.IsTrue(comfortable.ThumbnailSize <= comfortable.RowHeight);
        foreach (var metrics in new[] { comfortable, compact })
        {
            Assert.IsTrue(metrics.CellPadding.Left > 0 && metrics.CellPadding.Top >= 0, "Cells keep horizontal breathing room in both modes.");
            Assert.IsTrue(metrics.RowHeight >= metrics.FontSize * 1.6, "Row height stays proportional to the text it holds.");
        }
        Assert.AreEqual(ProductListDensity.Comfortable, ProductListDensity.Normalize("nonsense"), "An unknown mode falls back to comfortable, never to an empty style.");
        Assert.AreEqual(ProductListDensity.Compact, ProductListDensity.Normalize(" COMPACT "));
    }

    [TestMethod]
    public void SwitchingDensityKeepsTheSelectionAndVirtualizationAndPersistsAcrossARestart()
    {
        Run(root =>
        {
            using (var first = new Session(root))
            {
                Assert.AreEqual(ProductListDensity.Comfortable, first.CurrentDensity(), "Comfortable is the default.");
                var comfortableHeight = first.Grid.RowHeight;
                first.Grid.SelectedItem = first.Row("B");
                first.Drain();

                first.SetDensity(ProductListDensity.Compact);
                first.Drain();

                Assert.AreEqual("B", ((CatalogProduct)first.Grid.SelectedItem).Sku, "The switch must not drop the operator's selection.");
                Assert.IsTrue(first.Grid.RowHeight < comfortableHeight, "The live grid actually got denser.");
                Assert.AreEqual(ProductListDensity.Metrics(ProductListDensity.Compact).RowHeight, first.Grid.RowHeight);
                Assert.AreEqual(ProductListDensity.Metrics(ProductListDensity.Compact).FontSize, first.Grid.FontSize);
                Assert.IsTrue(first.Grid.EnableRowVirtualization, "Virtualization must survive the restyle -- this list holds 100k rows.");
                Assert.AreEqual(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(first.Grid));
                Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(first.Grid));
            }

            using var second = new Session(root);
            Assert.AreEqual(ProductListDensity.Compact, second.CurrentDensity(), "The density choice is part of the persisted view settings.");
            Assert.AreEqual(ProductListDensity.Metrics(ProductListDensity.Compact).RowHeight, second.Grid.RowHeight);
        });
    }

    sealed class Session : IDisposable
    {
        public readonly MainWindow Window; public readonly DataGrid Grid;
        public Session(string root)
        {
            if (!Directory.Exists(root))
            {
                var store = new CatalogStore(root); var source = new XmlSource { Id = "fixture", Name = "Fixture" };
                store.Import(source, new[]
                {
                    new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Ürün A", Price = 10, Stock = 3 },
                    new CatalogProduct { SourceId = source.Id, Sku = "B", Name = "Ürün B", Price = 20, Stock = 4 },
                });
            }
            Window = new MainWindow(root); Window.Show();
            typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, new object[] { "products", true });
            Grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
            Drain();
        }
        public CatalogProduct Row(string sku) => Grid.Items.OfType<CatalogProduct>().Single(x => x.Sku == sku);
        public string CurrentDensity() => (string)typeof(MainWindow).GetMethod("CurrentProductDensity", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, null)!;
        public void SetDensity(string mode) => DensityBox().SelectedItem = DensityBox().Items.Cast<object>().Single(i => ProductListDensity.Normalize(i.ToString()) == mode);
        ComboBox DensityBox() => Descendants(Window).OfType<ComboBox>().Single(c => c.Name == "ProductDensityBox");
        public void Drain() { Window.UpdateLayout(); Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        public void Dispose() { Window.Close(); Drain(); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject node)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) { yield return child; foreach (var d in Descendants(child)) yield return d; }
        }
    }

    static void Run(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "product-density-" + Guid.NewGuid().ToString("N"));
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
