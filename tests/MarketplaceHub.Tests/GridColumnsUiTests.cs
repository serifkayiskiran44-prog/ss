using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #864 on the real main window: the product grid's name column trims a long Turkish name and carries it whole in a
// keyboard-openable cell tooltip while trimmed; a narrowed SKU column ends in an ellipsis with the SKU whole in the
// tooltip and never goes narrower than its header (#867); the dashboard's connections grid trims its long status
// text the same way; a resize that widens the column takes the tooltip away.
[TestClass]
public sealed class GridColumnsUiTests
{
    [TestMethod]
    public void TheProductAndConnectionGridsTrimProseWithTooltipsAndNeverTrimIdentifiers()
    {
        var root = Path.Combine(Path.GetTempPath(), "grid-columns-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var store = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" };
                var name = "Şüpheli işlemlerin çözümlenmesi için özel üretim, çok uzun adlı, ölçülü ürün — 2026 sonbahar koleksiyonu";
                store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "SKU-ÇĞİÖŞÜ-000123456789", Name = name, Price = 10, Stock = 10, Currency = "TRY" } });
                window = new MainWindow(root); window.Show(); Drain(window);
                Navigate(window, "products"); Drain(window);
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var nameColumn = grid.Columns.OfType<DataGridTextColumn>().First(c => ((System.Windows.Data.Binding)c.Binding).Path.Path == "Name");
                var skuColumn = grid.Columns.OfType<DataGridTextColumn>().First(c => ((System.Windows.Data.Binding)c.Binding).Path.Path == "Sku");
                nameColumn.Width = 90; Drain(window);
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
                var cells = Descendants(row).OfType<DataGridCell>().ToList();
                var nameCell = cells[grid.Columns.IndexOf(nameColumn)]; var skuCell = cells[grid.Columns.IndexOf(skuColumn)];
                var nameText = (TextBlock)nameCell.Content; Assert.IsTrue(GridColumns.GetIsTrimmed(nameText), "A 90-DIP name column trims the long name."); Assert.AreEqual(TextTrimming.CharacterEllipsis, nameText.TextTrimming);
                Assert.AreEqual(name, (string)nameCell.ToolTip, "The tooltip carries the whole name."); Assert.IsTrue(ToolTipService.GetShowsToolTipOnKeyboardFocus(nameCell));
                skuColumn.Width = 60; Drain(window); // the column stops at its header's width, still too narrow for the SKU
                var skuText = (TextBlock)skuCell.Content; Assert.AreEqual(TextTrimming.CharacterEllipsis, skuText.TextTrimming); Assert.IsTrue(GridColumns.GetIsTrimmed(skuText), "A narrowed SKU column ends in an ellipsis (#867)."); Assert.AreEqual("SKU-ÇĞİÖŞÜ-000123456789", (string)skuCell.ToolTip, "and the tooltip carries the SKU whole.");
                Assert.IsTrue(skuCell.ActualWidth >= GridColumns.HeaderMinWidth("Stok kodu / SKU") - 1, "a column is never narrower than its header");
                nameColumn.Width = 700; Drain(window);
                Assert.IsFalse(GridColumns.GetIsTrimmed(nameText)); Assert.IsNull(nameCell.ToolTip, "Widened, the tooltip goes away.");

                // The dashboard's connections grid: a status column trims the same way, a keyboard-openable tooltip on the cell.
                Navigate(window, "dashboard"); Drain(window);
                DataGrid? channels = null;
                WaitUntil(window, () => (channels = Descendants((TabControl)window.FindName("ModuleTabs")).OfType<DataGrid>().FirstOrDefault(g => g.Items.Count > 0)) is not null, "the connections grid");
                var status = channels!.Columns.OfType<DataGridTextColumn>().First(c => ((System.Windows.Data.Binding)c.Binding).Path.Path == "Status");
                Assert.AreEqual(TextTrimming.CharacterEllipsis, ((TextBlock)Descendants((DataGridRow)channels.ItemContainerGenerator.ContainerFromIndex(0)).OfType<DataGridCell>().ElementAt(channels.Columns.IndexOf(status)).Content).TextTrimming);
                Assert.AreEqual(true, status.ElementStyle.Setters.OfType<Setter>().Single(x => x.Property == GridColumns.MonitorTrimmingProperty).Value, "The status column carries the trimmed-only tooltip.");
            }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void WaitUntil(Window window, Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++) { Drain(window); if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
