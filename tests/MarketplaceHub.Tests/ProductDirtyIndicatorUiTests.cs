using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #802 on the real path: editing a field marks its section's tab, lists the field by name in the indicator,
// and the per-section undo restores that section only -- through the actual MainWindow, not the model alone.
[TestClass]
public sealed class ProductDirtyIndicatorUiTests
{
    [TestMethod]
    public void EditingAFieldMarksItsSectionAndTheSectionUndoRestoresOnlyThatSection()
    {
        Run(root =>
        {
            using var session = new Session(root);
            Assert.IsFalse(session.State().IsDirty, "A freshly bound product has nothing unsaved.");
            Assert.IsTrue(session.Tabs().All(t => !((string)t.Header).EndsWith("•", StringComparison.Ordinal)));

            session.SetField("Price", "250");
            session.Invoke("RefreshProductDirtyIndicator");
            session.Drain();

            var state = session.State();
            Assert.AreEqual("price-stock", state.Sections.Single().Key);
            CollectionAssert.AreEqual(new[] { "Satış fiyatı" }, state.Sections.Single().Fields.ToArray());
            Assert.IsTrue(((string)session.Tab("price-stock").Header).EndsWith("•", StringComparison.Ordinal), "The dirty section's tab is marked.");
            Assert.IsFalse(((string)session.Tab("content").Header).EndsWith("•", StringComparison.Ordinal));

            var bar = session.DirtyBar();
            Assert.AreEqual(Visibility.Visible, bar.Visibility);
            var text = string.Join(" ", Descendants(bar).OfType<TextBlock>().Select(t => t.Text));
            StringAssert.Contains(text, "Satış fiyatı", "The indicator names the field...");
            Assert.IsFalse(text.Contains("250", StringComparison.Ordinal), "...and never its value: " + text);

            session.SetField("Description", "Yeni açıklama");
            session.Invoke("RefreshProductDirtyIndicator");
            session.Drain();
            Assert.AreEqual(2, session.State().Sections.Count);

            Descendants(bar).OfType<Button>().First(b => (string)b.Tag == "price-stock").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            session.Drain();

            var after = session.State();
            Assert.AreEqual("content", after.Sections.Single().Key, "Undoing one section leaves the other section's unsaved work alone.");
            Assert.AreEqual(10m, ((CatalogProduct)session.Editor.DataContext).Price);
            Assert.AreEqual("Yeni açıklama", ((CatalogProduct)session.Editor.DataContext).Description);
        });
    }

    sealed class Session : IDisposable
    {
        public readonly MainWindow Window; public readonly StackPanel Editor; readonly DataGrid grid;
        public Session(string root)
        {
            var store = new CatalogStore(root); var source = new XmlSource { Id = "fixture", Name = "Fixture" };
            store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Ürün A", Description = "Açıklama", Price = 10, Stock = 3 } });
            Window = new MainWindow(root); Window.Show();
            Invoke("Navigate", "products", true);
            grid = (DataGrid)Field("products"); Editor = (StackPanel)Field("productEditor");
            grid.SelectedItem = grid.Items.OfType<CatalogProduct>().Single(p => p.Sku == "A");
            Drain();
        }
        public object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
        public void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args.Length == 0 ? null : args);
        public ProductDirtyState State() => (ProductDirtyState)typeof(MainWindow).GetMethod("CurrentProductDirtyState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, null)!;
        public StackPanel DirtyBar() => (StackPanel)Field("productDirtyBar");
        public IEnumerable<TabItem> Tabs() => ((TabControl)Field("productWorkspaceTabs")).Items.OfType<TabItem>();
        public TabItem Tab(string key) => Tabs().Single(t => (string)t.Tag == key);
        public void SetField(string property, string value)
        {
            var box = Descendants(Editor).OfType<TextBox>().First(t => BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path == property);
            box.Text = value; box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        }
        public void Drain() { Window.UpdateLayout(); Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        public void Dispose()
        {
            Window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Window.OwnedWindows.Cast<Window>().SingleOrDefault();
                if (dialog?.Content is Panel panel)
                    panel.Children.OfType<Panel>().SelectMany(x => x.Children.OfType<Button>()).FirstOrDefault(x => (string)x.Content == "Vazgeç")?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }));
            Window.Close();
        }
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) { yield return child; foreach (var d in Descendants(child)) yield return d; }
    }

    static void Run(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "dirty-indicator-" + Guid.NewGuid().ToString("N"));
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
