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

// #862 on the real main window: the colours that failed the audit are gone from the screens -- no text block or
// tooltip host draws the old help grey, the old breadcrumb grey or the orange status brush -- the breadcrumb and
// the hints read the muted token, a warning status line reads the warning token, and a selected product row draws
// the token highlight with readable text and a rule on its edge.
[TestClass]
public sealed class ContrastAuditUiTests
{
    [TestMethod]
    public void TheScreensDrawTheFixedColoursAndASelectedRowIsMarkedByMoreThanAFill()
    {
        var root = Path.Combine(Path.GetTempPath(), "contrast-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var store = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" };
                store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 10, Currency = "TRY" } });
                window = new MainWindow(root); window.Show(); Drain(window);
                var content = (TabControl)window.FindName("ModuleTabs");
                var breadcrumb = (TextBlock)window.FindName("BreadcrumbText"); Assert.AreEqual(DesignTokens.TextMutedColor, ((SolidColorBrush)breadcrumb.Foreground).Color);

                var oldHelp = Color.FromRgb(126, 146, 158); var oldBreadcrumb = Color.FromRgb(113, 135, 149); var orange = Colors.DarkOrange; var amber = Color.FromRgb(196, 132, 22);
                foreach (var route in new[] { "dashboard", "products", "orders", "xml", "settings", "trendyol", "ebay" })
                {
                    Navigate(window, route); Drain(window);
                    var offenders = Descendants(window).OfType<TextBlock>().Where(t => t.Foreground is SolidColorBrush b && (b.Color == oldHelp || b.Color == oldBreadcrumb || b.Color == orange || b.Color == amber)).Select(t => $"{route}: '{t.Text[..Math.Min(t.Text.Length, 30)]}'").ToList();
                    Assert.AreEqual(0, offenders.Count, string.Join(" | ", offenders));
                }

                // A form row's help text and a settings hint read the muted token.
                var row = FormField.Build(new FormFieldSpec("Ad", true, "Yardım"), new TextBox()); Assert.AreEqual(DesignTokens.TextMutedColor, ((SolidColorBrush)row.HelpText.Foreground).Color);
                Navigate(window, "settings"); Drain(window);
                var hint = Descendants(content).OfType<TextBlock>().First(t => t.Text.StartsWith("Var olan ayarlar", StringComparison.Ordinal)); Assert.AreEqual(DesignTokens.TextMutedColor, ((SolidColorBrush)hint.Foreground).Color);

                // The eBay form's unconfigured status is a warning in words and in the warning colour, not orange.
                Navigate(window, "ebay"); Drain(window);
                var tabs = Descendants(content).OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(i => i.Header as string == "Bağlantı")); tabs.SelectedIndex = tabs.Items.Count - 1; Drain(window);
                var status = Descendants(content).OfType<TextBlock>().First(t => (string?)t.Tag == "ebay-status"); Assert.AreEqual(DesignTokens.WarningTextColor, ((SolidColorBrush)status.Foreground).Color);

                // A selected product row: the token highlight with readable text (its edge already carries the row-state rule of #794).
                Navigate(window, "products"); Drain(window);
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                grid.SelectedItem = grid.Items.OfType<CatalogProduct>().Single(); grid.Focus(); Drain(window);
                var selectedRow = Descendants(grid).OfType<DataGridRow>().Single(r => r.IsSelected);
                var cell = Descendants(selectedRow).OfType<DataGridCell>().First();
                Assert.AreEqual(DesignTokens.SelectedRowColor, ((SolidColorBrush)cell.Background).Color); Assert.AreEqual(DesignTokens.SelectedRowForegroundColor, ((SolidColorBrush)cell.Foreground).Color);
                Assert.IsTrue(ContrastAudit.Ratio(((SolidColorBrush)cell.Foreground).Color, ((SolidColorBrush)cell.Background).Color) >= 4.5);

                // A selected row on a grid without state borders (the dashboard's connections): the highlight and a rule on its edge.
                Navigate(window, "dashboard"); Drain(window);
                DataGrid? channels = null;
                WaitUntil(window, () => (channels = Descendants(content).OfType<DataGrid>().FirstOrDefault(g => g.Items.Count > 0)) is not null, "the dashboard's connections grid");
                channels!.SelectedIndex = 0; channels.Focus(); Drain(window);
                var channelRow = (DataGridRow)channels.ItemContainerGenerator.ContainerFromIndex(0);
                Assert.IsTrue(channelRow.IsSelected); Assert.AreEqual(DesignTokens.SelectedRowRuleThickness, channelRow.BorderThickness.Left, "The selected row carries a rule on its edge.");
                var channelCell = Descendants(channelRow).OfType<DataGridCell>().First();
                Assert.AreEqual(DesignTokens.SelectedRowColor, ((SolidColorBrush)channelCell.Background).Color); Assert.AreEqual(DesignTokens.SelectedRowForegroundColor, ((SolidColorBrush)channelCell.Foreground).Color);
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
