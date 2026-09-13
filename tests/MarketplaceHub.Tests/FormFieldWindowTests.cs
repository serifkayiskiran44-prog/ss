using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #819 in the real product card: the field the store refuses to save without (the name) is marked required for
// the eye and spoken as required for the ear, and every text input is labeled by its own label.
[TestClass]
public sealed class FormFieldWindowTests
{
    [TestMethod]
    public void TheProductCardsNameFieldIsRequiredForTheEyeAndTheEarAndEveryInputIsLabeled()
    {
        var root = Path.Combine(Path.GetTempPath(), "formfield-" + Guid.NewGuid().ToString("N"));
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

                var boxes = Descendants(editor).OfType<TextBox>().Where(b => BindingOperations.GetBinding(b, TextBox.TextProperty) is not null).ToList();
                Assert.IsTrue(boxes.Count > 5, "The card has bound text inputs.");
                foreach (var box in boxes)
                {
                    var label = AutomationProperties.GetLabeledBy(box) as TextBlock;
                    Assert.IsNotNull(label, $"Input bound to '{BindingOperations.GetBinding(box, TextBox.TextProperty)!.Path.Path}' has no label for assistive tech.");
                    Assert.IsTrue(AutomationProperties.GetName(box).Length > 0);
                }

                var name = boxes.Single(b => BindingOperations.GetBinding(b, TextBox.TextProperty)!.Path.Path == "Name");
                var nameLabel = (TextBlock)AutomationProperties.GetLabeledBy(name);
                var labelText = string.Concat(nameLabel.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
                StringAssert.EndsWith(labelText, FormField.RequiredMarker, "The name is required: the marker is on the label.");
                StringAssert.EndsWith(AutomationProperties.GetName(name), FormField.RequiredWord, "The name is required: the word is in the spoken name.");

                var description = boxes.FirstOrDefault(b => BindingOperations.GetBinding(b, TextBox.TextProperty)!.Path.Path == "Description");
                if (description is not null)
                    Assert.IsFalse(AutomationProperties.GetName(description).EndsWith(FormField.RequiredWord, StringComparison.Ordinal), "An optional field is not called required.");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
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
