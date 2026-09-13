using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #815 in the real window: a product row's status tooltip is the shared template, shows on keyboard focus (not
// only on hover) and is exposed to assistive technology as the row's help text.
[TestClass]
public sealed class StatusTooltipWindowTests
{
    [TestMethod]
    public void AStatusRowTooltipIsReachableByKeyboardAndReadByAssistiveTech()
    {
        var root = Path.Combine(Path.GetTempPath(), "tooltip-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var store = new CatalogStore(root);
                var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" };
                store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "OFF", Name = "Pasif ürün", Price = 10, Stock = 4, Active = false } });
                var window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "products", true });
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Drain(window);

                var product = grid.Items.OfType<CatalogProduct>().Single(p => p.Sku == "OFF");
                grid.ScrollIntoView(product); Drain(window);
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(product);
                Assert.IsNotNull(row, "The row must be realized to carry a tooltip.");

                var tooltip = row.ToolTip as string;
                Assert.IsNotNull(tooltip);
                StringAssert.StartsWith(tooltip, "Durum: ", "The row tooltip is the shared template.");
                StringAssert.Contains(tooltip, "Sonraki adım: ");
                Assert.IsTrue(ToolTipService.GetShowsToolTipOnKeyboardFocus(row) == true, "A tooltip only a mouse can open is not accessible.");
                Assert.AreEqual(tooltip, AutomationProperties.GetHelpText(row), "Assistive tech hears the same text a hover shows.");

                grid.SelectedItem = product; row.Focusable = true; Keyboard.Focus(row); Drain(window);
                Assert.IsTrue(row.IsKeyboardFocusWithin || row.IsKeyboardFocused, "The row takes keyboard focus, which is what opens the tooltip without a mouse.");

                window.Close(); Drain(window);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
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

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
