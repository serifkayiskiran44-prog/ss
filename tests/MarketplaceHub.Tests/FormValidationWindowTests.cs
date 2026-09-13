using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #820 in the real product card: saving a record with blockers in two sections is refused, the summary shows at
// the top, each broken input carries its own message, the first blocking input takes keyboard focus, the typed
// value never appears, and fixing the record clears every message and lets the save through.
[TestClass]
public sealed class FormValidationWindowTests
{
    [TestMethod]
    public void SaveIsRefusedWithFocusOnTheFirstBlockerAndClearsOnceFixed()
    {
        var root = Path.Combine(Path.GetTempPath(), "formval-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            try
            {
                var store = new CatalogStore(root);
                var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" };
                store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 10, Currency = "TRY" } });
                window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "products", true });
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Drain(window);
                grid.SelectedItem = grid.Items.OfType<CatalogProduct>().Single(); Drain(window);
                var editor = (StackPanel)typeof(MainWindow).GetField("productEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var edit = (CatalogProduct)typeof(MainWindow).GetField("edit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var save = typeof(MainWindow).GetMethod("SaveProductEdit", BindingFlags.Instance | BindingFlags.NonPublic)!;
                TextBox Box(string property) => Descendants(editor).OfType<TextBox>().Single(b => BindingOperations.GetBinding(b, TextBox.TextProperty)?.Path.Path == property);
                TextBlock SlotOf(TextBox box) => ((StackPanel)box.Parent).Children.OfType<TextBlock>().Last();

                // Two blockers in two sections: the name (content) and the currency (price-stock).
                edit.Name = ""; edit.Currency = "SECRET1234";
                Box("Name").Text = ""; Box("Currency").Text = "SECRET1234"; Drain(window);
                Exception refused = null;
                try { save.Invoke(window, null); } catch (TargetInvocationException ex) { refused = ex.InnerException; }
                Drain(window); // the section switch realizes its content, and the deferred focus lands

                Assert.IsInstanceOfType(refused, typeof(InvalidOperationException), "A record with blockers is not saved.");
                Assert.AreEqual("Product A", store.Products().Single().Name, "Nothing reached the store.");
                var summary = (StackPanel)typeof(MainWindow).GetField("productValidationPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Assert.AreEqual(Visibility.Visible, summary.Visibility, "The summary is at the top of the form.");
                Assert.AreEqual(Visibility.Visible, SlotOf(Box("Name")).Visibility, "The name row carries its own message.");
                Assert.AreEqual(Visibility.Visible, SlotOf(Box("Currency")).Visibility, "The currency row carries its own message, in another section.");
                Assert.IsFalse(SlotOf(Box("Currency")).Text.Contains("SECRET1234") || refused!.Message.Contains("SECRET1234"), "The typed value is never echoed.");
                Assert.IsTrue(Box("Name").IsKeyboardFocused, $"Focus lands on the first blocking input (focused: {Keyboard.FocusedElement?.GetType().Name ?? "none"}, name visible: {Box("Name").IsVisible}, window active: {window.IsActive}).");

                // Fix both; the save goes through and every message clears.
                edit.Name = "Product A"; edit.Currency = "TRY";
                Box("Name").Text = "Product A"; Box("Currency").Text = "TRY"; Drain(window);
                save.Invoke(window, null); Drain(window);
                Assert.AreEqual(Visibility.Collapsed, SlotOf(Box("Name")).Visibility);
                Assert.AreEqual(Visibility.Collapsed, SlotOf(Box("Currency")).Visibility);
                Assert.AreEqual("TRY", store.Products().Single().Currency);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                // A failed assertion can leave the draft dirty; Close() then asks about unsaved changes and a modal
                // question would hang the host. Answer it with "Vazgeç" as soon as it appears, then close.
                if (window is not null)
                {
                    var w = window;
                    w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        var dialog = w.OwnedWindows.Cast<Window>().SingleOrDefault();
                        if (dialog?.Content is Panel panel)
                            panel.Children.OfType<Panel>().SelectMany(x => x.Children.OfType<Button>()).FirstOrDefault(x => (string)x.Content == "Vazgeç")?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    }));
                }
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    try { Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
