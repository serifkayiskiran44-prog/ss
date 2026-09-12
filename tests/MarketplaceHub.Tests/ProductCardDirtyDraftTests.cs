using System;
using System.IO;
using System.Collections.Generic;
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

[TestClass]
public sealed class ProductCardDirtyDraftTests
{
    [TestMethod]
    public void SecondaryTabsFollowSelectedProductAndClearWhenSelectionClears()
    {
        Run(f => {
            var workspace = (TabControl)((TabItem)((ScrollViewer)f.Editor.Parent).Parent).Parent;
            // Sections are addressed by their catalogue key (#801), not by position: the tab order is a design
            // decision that has already changed once, and an index-based test silently follows it to the wrong pane.
            TabItem Section(string key) => workspace.Items.OfType<TabItem>().Single(t => (string)t.Tag == key);
            StackPanel Body(string key) => (StackPanel)((ScrollViewer)Section(key).Content).Content;
            // A section body may nest its fields under headings and hints, so the fields are searched for in the
            // subtree rather than assumed to be the panel's direct children.
            static IEnumerable<TextBox> Fields(DependencyObject node) =>
                LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().SelectMany(c => c is TextBox box ? new[] { box } : Fields(c).ToArray());
            var media = Body("media");
            var provenance = Body("audit");
            workspace.SelectedItem = Section("audit"); f.Drain();
            Assert.AreSame(f.Editor.DataContext, provenance.DataContext);
            Assert.AreEqual("fixture", Fields(provenance).First().Text, "The audit section leads with the product's source identity.");
            f.Grid.SelectedItem = f.Row("B"); f.Drain();
            Assert.AreEqual("B", ((CatalogProduct)provenance.DataContext).Sku);
            workspace.SelectedItem = Section("media"); f.Drain();
            Assert.AreSame(f.Editor.DataContext, media.DataContext);
            f.Grid.SelectedItem = null; f.Drain();
            Assert.IsNull(media.DataContext);
            Assert.IsTrue(Fields(media).All(x => x.Text == ""), "Clearing the selection empties the section's fields.");
        });
    }

    [DataTestMethod]
    [DataRow("Kaydet", "Unsaved A draft", "B")]
    [DataRow("Vazgeç", "Original A", "B")]
    [DataRow("İptal", "Original A", "A")]
    public void SelectionRequiresExplicitDecision(string choice, string persistedName, string selectedSku)
    {
        Run(f => {
            f.EditName();
            f.Answer(choice);
            f.Grid.SelectedItem = f.Row("B");
            f.Drain();
            Assert.AreEqual(1, f.Prompts);
            Assert.AreEqual(selectedSku, ((CatalogProduct)f.Grid.SelectedItem).Sku);
            Assert.AreEqual(persistedName, new CatalogStore(f.Root).Products().Single(x => x.Sku == "A").Name);
            if (choice == "İptal") Assert.AreEqual("Unsaved A draft", f.Name.Text);
            else { f.Grid.SelectedItem = f.Row("A"); f.Drain(); Assert.AreEqual(persistedName, f.Name.Text); }
        });
    }

    [TestMethod]
    public void CancelRefreshAndCloseKeepDraftAndWindowAlive()
    {
        Run(f => {
            f.EditName(); f.Answer("İptal"); f.Call("RefreshProducts"); f.Drain();
            Assert.AreEqual(1, f.Prompts); Assert.AreEqual("Unsaved A draft", f.Name.Text);
            f.Answer("İptal"); f.Window.Close(); f.Drain();
            Assert.AreEqual(2, f.Prompts); Assert.IsTrue(f.Window.IsVisible);
            Assert.AreEqual("Unsaved A draft", f.Name.Text);
        });
    }

    [TestMethod]
    public void StaleSaveDoesNotNavigateOrOverwriteConcurrentEdit()
    {
        Run(f => {
            f.EditName();
            var store = new CatalogStore(f.Root); var current = store.Products().Single(x => x.Sku == "A");
            current.Name = "Concurrent edit"; store.SaveProduct(current);
            f.Answer("Kaydet"); f.Grid.SelectedItem = f.Row("B"); f.Drain();
            Assert.AreEqual(1, f.Prompts); Assert.AreEqual("A", ((CatalogProduct)f.Grid.SelectedItem).Sku);
            Assert.AreEqual("Unsaved A draft", f.Name.Text);
            Assert.AreEqual("Concurrent edit", store.Products().Single(x => x.Sku == "A").Name);
        });
    }

    [TestMethod]
    public void RefreshWhileDecisionIsOpenDoesNotOpenAnotherDialog()
    {
        Run(f => {
            f.EditName(); var nested = false;
            f.Window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                var first = f.Window.OwnedWindows.Cast<Window>().Single();
                f.Window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                    var second = f.Window.OwnedWindows.Cast<Window>().FirstOrDefault(x => x != first);
                    if (second != null) { nested = true; second.Close(); }
                }));
                f.Call("RefreshProducts");
                first.Close();
            }));
            f.Grid.SelectedItem = f.Row("B"); f.Drain();
            Assert.IsFalse(nested, "A refresh callback must not nest a second unsaved-change decision.");
            Assert.AreEqual("A", ((CatalogProduct)f.Grid.SelectedItem).Sku);
            Assert.AreEqual("Unsaved A draft", f.Name.Text);
        });
    }

    [TestMethod]
    public void SaveOnClosePersistsAcrossWindowRestart()
    {
        Run(f => {
            f.EditName(); f.Answer("Kaydet"); f.Window.Close();
            Assert.AreEqual(1, f.Prompts);
            var reopened = new MainWindow(f.Root);
            try
            {
                reopened.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(reopened, new object[] { "products", true });
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reopened);
                grid.SelectedItem = grid.Items.OfType<CatalogProduct>().Single(x => x.Sku == "A");
                reopened.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var editor = (StackPanel)typeof(MainWindow).GetField("productEditor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(reopened);
                var name = editor.Children.OfType<TextBox>().Single(x => BindingOperations.GetBinding(x, TextBox.TextProperty)?.Path.Path == "Name");
                Assert.AreEqual("Unsaved A draft", name.Text);
            }
            finally { reopened.Close(); }
        });
    }

    [TestMethod]
    public void CancelReplacingPageKeepsExistingItemsAndSelection()
    {
        Run(f => {
            f.EditName(); var items = f.Grid.ItemsSource;
            f.Answer("İptal");
            f.Call("ShowProducts", new CatalogPage(Array.Empty<CatalogProduct>(), 0, 0, 0)); f.Drain();
            Assert.AreEqual(1, f.Prompts); Assert.AreSame(items, f.Grid.ItemsSource);
            Assert.AreEqual("A", ((CatalogProduct)f.Grid.SelectedItem).Sku);
            Assert.AreEqual("Unsaved A draft", f.Name.Text);
        });
    }

    [TestMethod]
    public void SaveDuringRefreshReloadsTheSavedVersion()
    {
        Run(f => {
            f.EditName(); f.Answer("Kaydet"); f.Call("RefreshProducts"); f.Drain();
            Assert.AreEqual(1, f.Prompts); Assert.AreEqual("Unsaved A draft", f.Name.Text);
            Assert.AreEqual("Unsaved A draft", f.Row("A").Name);
            f.Name.Text = "Second edit";
            f.Name.GetBindingExpression(TextBox.TextProperty).UpdateSource();
            f.Answer("Kaydet"); f.Grid.SelectedItem = f.Row("B"); f.Drain();
            Assert.AreEqual("Second edit", new CatalogStore(f.Root).Products().Single(x => x.Sku == "A").Name);
        });
    }

    [TestMethod]
    public void InvalidNumericTextCannotBeSilentlyDiscardedOrSaved()
    {
        Run(f => {
            var price = f.Editor.Children.OfType<TextBox>().Single(x => BindingOperations.GetBinding(x, TextBox.TextProperty)?.Path.Path == "Price");
            price.Text = "invalid-price"; price.GetBindingExpression(TextBox.TextProperty).UpdateSource();
            Assert.IsTrue(Validation.GetHasError(price));
            f.Answer("Kaydet"); f.Grid.SelectedItem = f.Row("B"); f.Drain();
            Assert.AreEqual(1, f.Prompts); Assert.AreEqual("A", ((CatalogProduct)f.Grid.SelectedItem).Sku);
            Assert.AreEqual("invalid-price", price.Text);
            Assert.AreEqual(10m, new CatalogStore(f.Root).Products().Single(x => x.Sku == "A").Price);
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
        public readonly string Root = Path.Combine(Path.GetTempPath(), "card-dirty-" + Guid.NewGuid().ToString("N"));
        public MainWindow Window; public DataGrid Grid; public StackPanel Editor; public TextBox Name; public int Prompts;
        public Fixture()
        {
            var store = new CatalogStore(Root); var source = new XmlSource { Id = "fixture", Name = "Fixture" };
            store.Import(source, new[] {
                new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Original A", Price = 10, Stock = 10 },
                new CatalogProduct { SourceId = source.Id, Sku = "B", Name = "Original B", Price = 20, Stock = 10 }
            });
            Window = new MainWindow(Root); Window.Show(); Call("Navigate", "products", true);
            Grid = (DataGrid)Field("products"); Editor = (StackPanel)Field("productEditor");
            Grid.SelectedItem = Row("A"); Drain();
            Name = Editor.Children.OfType<TextBox>().Single(x => BindingOperations.GetBinding(x, TextBox.TextProperty)?.Path.Path == "Name");
        }
        public object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Window);
        public void Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Window, args);
        public CatalogProduct Row(string sku) => Grid.Items.OfType<CatalogProduct>().Single(x => x.Sku == sku);
        public void Drain() { Window.UpdateLayout(); Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        public void EditName() { Name.Text = "Unsaved A draft"; Name.GetBindingExpression(TextBox.TextProperty).UpdateSource(); Assert.AreEqual("Unsaved A draft", ((CatalogProduct)Editor.DataContext).Name); }
        public void Answer(string choice)
        {
            Window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                var dialog = Window.OwnedWindows.Cast<Window>().SingleOrDefault();
                if (dialog == null) return;
                Prompts++;
                var panel = (StackPanel)dialog.Content;
                var buttons = panel.Children.OfType<Panel>().SelectMany(x => x.Children.OfType<Button>());
                buttons.Single(x => (string)x.Content == choice).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
        }
        public void Dispose()
        {
            Answer("Vazgeç"); Window.Close();
            // Selecting the seeded XmlSource during MainWindow construction fires a fire-and-forget health check
            // (source-health.db) that can still be closing its connection when Directory.Delete runs; clear the
            // pool before every retry attempt, not just once, since a connection opened after an earlier clear
            // would otherwise never be released.
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
