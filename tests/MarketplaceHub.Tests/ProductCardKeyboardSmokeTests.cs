using System;
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

[TestClass]
public sealed class ProductCardKeyboardSmokeTests
{
    [TestMethod]
    public void EveryProductWorkspaceTabIsKeyboardReachableAndFocusableTabsAcceptRealKeyboardFocus()
    {
        Run(f => {
            var workspace = (TabControl)((TabItem)((ScrollViewer)f.Editor.Parent).Parent).Parent;
            Assert.AreEqual(5, workspace.Items.Count, "Product card must expose all five workspace tabs.");

            for (var i = 0; i < workspace.Items.Count; i++)
            {
                workspace.SelectedIndex = i;
                f.Drain();
                var tabItem = (TabItem)workspace.Items[i];
                Assert.IsTrue(tabItem.Focusable, $"Tab {i} ('{tabItem.Header}') header must stay keyboard-reachable.");

                var content = ((ScrollViewer)tabItem.Content).Content;
                var firstTextBox = FindFirstTextBox(content as DependencyObject);
                if (firstTextBox is null) continue; // Read-only summary tabs (e.g. Pazaryerleri) carry no editable control.

                var focused = Keyboard.Focus(firstTextBox);
                f.Drain();
                Assert.AreSame(firstTextBox, focused, $"Tab {i} ('{tabItem.Header}') first field must accept real keyboard focus.");
                Assert.IsTrue(firstTextBox.IsKeyboardFocused, $"Tab {i} ('{tabItem.Header}') first field must report IsKeyboardFocused after Keyboard.Focus.");
            }
        });
    }

    static TextBox FindFirstTextBox(DependencyObject root)
    {
        if (root is null) return null;
        if (root is TextBox direct) return direct;
        var count = VisualTreeHelperChildCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindFirstTextBox(child);
            if (found != null) return found;
        }
        return null;
    }

    static int VisualTreeHelperChildCount(DependencyObject root) => root is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetChildrenCount(root) : 0;

    static void Run(Action<Fixture> test)
    {
        Exception failure = null;
        var thread = new Thread(() => { try { using var f = new Fixture(); test(f); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "card-keyboard-" + Guid.NewGuid().ToString("N"));
        public MainWindow Window; public DataGrid Grid; public StackPanel Editor;

        public Fixture()
        {
            var store = new CatalogStore(Root);
            var source = new XmlSource { Id = "fixture", Name = "Fixture" };
            store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 10 } });

            Window = new MainWindow(Root); Window.Show();
            typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Window, new object[] { "products", true });
            Grid = (DataGrid)Field("products"); Editor = (StackPanel)Field("productEditor");
            Grid.SelectedItem = Grid.Items.OfType<CatalogProduct>().Single(x => x.Sku == "A");
            Drain();
        }

        public object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Window);
        public void Drain() { Window.UpdateLayout(); Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        public void Dispose()
        {
            Window.Close();
            // Selecting the seeded XmlSource during MainWindow construction fires a fire-and-forget health check
            // (source-health.db) that can still be closing its connection when Directory.Delete runs; clear the
            // pool before every retry attempt, not just once.
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
