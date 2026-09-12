using System;
using System.Globalization;
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

[TestClass]
public sealed class ProductCardPriceFieldsUiTests
{
    [TestMethod]
    public void DesiFieldBindsToSelectedProduct()
    {
        Run(f =>
        {
            var desi = f.Editor.Children.OfType<TextBox>().Single(x => BindingOperations.GetBinding(x, TextBox.TextProperty)?.Path.Path == "Desi");
            desi.Text = 2.5m.ToString(CultureInfo.CurrentCulture); // matches the binding's culture-dependent decimal parsing (e.g. "2,5" under tr-TR).
            desi.GetBindingExpression(TextBox.TextProperty).UpdateSource();

            Assert.AreEqual(2.5m, ((CatalogProduct)f.Editor.DataContext).Desi);
        });
    }

    [TestMethod]
    public void AddingAndRemovingAPriceFieldUpdatesTheListAndThePersistedProduct()
    {
        Run(f =>
        {
            f.NameBox.Text = "Etsy sabit"; f.ValueBox.Text = "219.90"; f.CurrencyBox.Text = "TRY";
            f.AddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            f.Drain();

            var stored = new CatalogStore(f.Root).FindProduct(f.ProductId)!;
            Assert.AreEqual(1, stored.PriceFields.Count);
            Assert.AreEqual("Etsy sabit", stored.PriceFields[0].Name);
            StringAssert.Contains(f.PriceFieldsList.Children.OfType<Panel>().Single().Children.OfType<TextBlock>().Single().Text, "Etsy sabit");

            var removeButton = f.PriceFieldsList.Children.OfType<Panel>().Single().Children.OfType<Button>().Single();
            removeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            f.Drain();

            Assert.AreEqual(0, new CatalogStore(f.Root).FindProduct(f.ProductId)!.PriceFields.Count);
            Assert.AreEqual(0, f.PriceFieldsList.Children.Count);
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
        public readonly string Root = Path.Combine(Path.GetTempPath(), "price-fields-ui-" + Guid.NewGuid().ToString("N"));
        public MainWindow Window; public DataGrid Grid; public StackPanel Editor; public StackPanel PriceFieldsList;
        public TextBox NameBox, ValueBox, CurrencyBox; public Button AddButton;
        public string ProductId;

        public Fixture()
        {
            var store = new CatalogStore(Root);
            var source = new XmlSource { Id = "fixture", Name = "Fixture" };
            store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 10 } });
            ProductId = store.Products().Single().Id;

            Window = new MainWindow(Root); Window.Show();
            typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Window, new object[] { "products", true });
            Grid = (DataGrid)Field("products"); Editor = (StackPanel)Field("productEditor");
            PriceFieldsList = (StackPanel)Field("priceFieldsList");
            Grid.SelectedItem = Grid.Items.OfType<CatalogProduct>().Single();
            Drain();

            var addRow = Editor.Children.OfType<WrapPanel>().Single();
            var boxes = addRow.Children.OfType<TextBox>().ToList();
            NameBox = boxes[0]; ValueBox = boxes[1]; CurrencyBox = boxes[2];
            AddButton = addRow.Children.OfType<Button>().Single();
        }

        public object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Window);
        public void Drain() { Window.UpdateLayout(); Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        public void Dispose()
        {
            // If a prior assertion left the draft dirty, closing pumps a modal "unsaved changes" dialog
            // (MainWindow.OnClosing -> ResolveProductEdit); auto-discard it so cleanup can never deadlock.
            Window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Window.OwnedWindows.Cast<Window>().SingleOrDefault();
                if (dialog == null) return;
                var panel = (StackPanel)dialog.Content;
                var buttons = panel.Children.OfType<Panel>().SelectMany(x => x.Children.OfType<Button>());
                buttons.SingleOrDefault(x => (string)x.Content == "Vazgeç")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
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
