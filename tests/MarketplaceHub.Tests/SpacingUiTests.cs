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

// #859 on the real main window, on the five screens the issue names: the settings shell keeps the page margin and
// its entries the control rhythm; the orders screen keeps the section margin; the dashboard's cards keep the inline
// margin; a channel form's root moves to the page margin; the products and XML screens carry the scale's values;
// and at the window's minimum width none of the five screens grows wider than the content area.
[TestClass]
public sealed class SpacingUiTests
{
    [TestMethod]
    public void TheFiveScreensKeepTheirRhythmFromTheScaleAndFitTheNarrowWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "spacing-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                window = new MainWindow(root); window.Show(); Drain(window);
                var content = (TabControl)window.FindName("ModuleTabs");

                Navigate(window, "settings"); Drain(window);
                var categories = Find(Descendants(content).OfType<ListBox>(), l => (string?)l.Tag == "settings-categories", "the settings categories");
                var shell = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(categories)!)!;
                Assert.AreEqual(Spacing.Page, shell.Margin, "The settings shell keeps the page margin.");
                var entry = Find(Descendants(content).OfType<Border>(), b => (string?)b.Tag == "settings-entry", "a settings entry");
                Assert.AreEqual(Spacing.VerticalControl, entry.Padding, "An entry row keeps the control rhythm above and below.");

                Navigate(window, "orders"); Drain(window);
                var ordersRoot = Find(Descendants(content).OfType<DockPanel>(), d => d.Margin == Spacing.Section, "the orders root with the section margin");
                Assert.IsTrue(ordersRoot.IsVisible);

                Navigate(window, "dashboard"); Drain(window);
                WaitUntil(window, () => Descendants(content).OfType<Button>().Any(b => b.Margin == Spacing.Inline && b.Content is StackPanel), "the dashboard's KPI cards with the inline margin");

                Navigate(window, "trendyol"); Drain(window);
                var tabs = Find(Descendants(content).OfType<TabControl>(), t => t.Items.OfType<TabItem>().Any(i => i.Header as string == "Bağlantı"), "the channel tab control"); tabs.SelectedIndex = tabs.Items.Count - 1; Drain(window);
                var channelRoot = Find(Descendants(content).OfType<StackPanel>(), s => s.Children.OfType<TextBlock>().Any(t => t.Text == "Trendyol bağlantısı"), "the Trendyol form root");
                Assert.AreEqual(Spacing.Page, channelRoot.Margin, "A channel form's root moves from its own 18 to the page margin.");

                // The products and XML screens carry the scale's values on their own blocks.
                Navigate(window, "products"); Drain(window);
                Assert.IsTrue(Descendants(content).OfType<FrameworkElement>().Any(e => e.Margin == Spacing.Control || e.Margin == Spacing.Inline || e.Margin == Spacing.BelowControl), "The products screen uses the scale.");
                Navigate(window, "xml"); Drain(window);
                Assert.IsTrue(Descendants(content).OfType<FrameworkElement>().Any(e => e.Margin == Spacing.Control || e.Margin == Spacing.Inline || e.Margin == Spacing.Section), "The XML screen uses the scale.");

                // The narrow window: none of the five screens grows wider than the content area.
                window.Width = window.MinWidth; Drain(window);
                foreach (var route in new[] { "products", "orders", "xml", "dashboard", "settings" })
                {
                    Navigate(window, route); Drain(window);
                    var page = (FrameworkElement)((TabItem)content.SelectedItem).Content;
                    Assert.IsTrue(page.ActualWidth <= content.ActualWidth + 0.5, $"{route}: page {page.ActualWidth} vs content {content.ActualWidth}");
                }
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

    static T Find<T>(IEnumerable<T> items, Func<T, bool> predicate, string what) { var hit = items.FirstOrDefault(predicate); if (hit is null) Assert.Fail($"Could not find {what}."); return hit!; }
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
