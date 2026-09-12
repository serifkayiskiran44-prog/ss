using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #796, on the real entry path: the drawer opens from the product row's inspect command, closes on Esc, and
// contains nothing the operator could type into -- read-only is verified against the realized visual tree, not
// just asserted about the model.
[TestClass]
public sealed class ProductQuickInspectDrawerTests
{
    [TestMethod]
    public void TheDrawerOpensOnTheInspectCommandClosesOnEscapeAndOffersNoWayToEditAnything()
    {
        Run(root =>
        {
            using var session = new Session(root);
            var drawer = session.Drawer();
            Assert.AreEqual(Visibility.Collapsed, drawer.Visibility, "The drawer stays out of the way until asked for.");

            session.Grid.SelectedItem = session.Row("A"); session.Drain();
            session.Invoke("OpenProductInspect"); session.Drain();

            Assert.AreEqual(Visibility.Visible, drawer.Visibility);
            var texts = Descendants(drawer).OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.IsTrue(texts.Any(t => t.Contains("SKU: A", StringComparison.Ordinal)), "The drawer shows the selected row's identity: " + string.Join(" | ", texts));
            Assert.IsTrue(texts.Any(t => t.StartsWith("Stok:", StringComparison.Ordinal)));
            Assert.IsTrue(texts.Any(t => t.StartsWith("Son hata:", StringComparison.Ordinal)));

            var editable = Descendants(drawer).Where(d => d is TextBox or CheckBox or ComboBox or DataGrid).ToList();
            Assert.AreEqual(0, editable.Count, "A quick-inspect drawer must expose no input control: " + string.Join(", ", editable.Select(e => e.GetType().Name)));
            var buttons = Descendants(drawer).OfType<Button>().Select(b => b.Content as string).ToList();
            CollectionAssert.AreEqual(new[] { "Kapat (Esc)" }, buttons, "Close is the only command in the drawer.");

            session.Invoke("CloseProductInspect"); session.Drain();
            Assert.AreEqual(Visibility.Collapsed, drawer.Visibility);

            // The keyboard route is the same command, bound on the grid.
            var open = session.Grid.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.I && b.Modifiers == ModifierKeys.Control);
            var close = session.Grid.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.Escape);
            open.Command.Execute(null); session.Drain();
            Assert.AreEqual(Visibility.Visible, drawer.Visibility, "Ctrl+I opens the drawer.");
            close.Command.Execute(null); session.Drain();
            Assert.AreEqual(Visibility.Collapsed, drawer.Visibility, "Esc closes it.");
        });
    }

    [TestMethod]
    public void ReopeningAfterTheDataChangedShowsTheNewValuesRatherThanTheFirstSnapshot()
    {
        Run(root =>
        {
            using var session = new Session(root);
            session.Grid.SelectedItem = session.Row("A"); session.Drain();
            session.Invoke("OpenProductInspect"); session.Drain();
            Assert.IsTrue(Descendants(session.Drawer()).OfType<TextBlock>().Any(t => t.Text == "Stok: 3"));

            var store = new CatalogStore(root);
            var product = store.Products().Single(p => p.Sku == "A");
            product.Stock = 41; store.SaveProduct(product);
            session.Invoke("RefreshProducts"); session.Drain();
            session.Grid.SelectedItem = session.Row("A"); session.Drain();
            session.Invoke("OpenProductInspect"); session.Drain();

            Assert.IsTrue(Descendants(session.Drawer()).OfType<TextBlock>().Any(t => t.Text == "Stok: 41"), "A stale drawer would still say 3.");
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
            Invoke("Navigate", "products", true);
            Grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
            Drain();
        }
        public Border Drawer() => (Border)typeof(MainWindow).GetField("productInspectDrawer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
        public CatalogProduct Row(string sku) => Grid.Items.OfType<CatalogProduct>().Single(x => x.Sku == sku);
        public void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args.Length == 0 ? null : args);
        public void Drain() { Window.UpdateLayout(); Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        public void Dispose() { Window.Close(); Drain(); }
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) { yield return child; foreach (var d in Descendants(child)) yield return d; }
    }

    static void Run(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "product-inspect-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { test(root); } catch (Exception ex) { failure = ex; }
            finally
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    try { if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
