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

// #812 in the real shell: collapsing keeps every entry's name for the tooltip and the accessibility tree, keeps
// it keyboard-focusable, and a second window over the same data directory opens the way the first was left.
[TestClass]
public sealed class NavigationSidebarWindowTests
{
    [TestMethod]
    public void CollapsingKeepsNamesTooltipsAndKeyboardFocusAndSurvivesAReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "sidebar-win-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var first = new MainWindow(root); first.Show(); Drain(first);
                var column = (ColumnDefinition)first.FindName("SidebarColumn");
                var list = (ListBox)first.FindName("NavigationList");
                var entries = list.Items.OfType<ListBoxItem>().Where(i => i.Tag is string).ToList();
                var expected = entries.ToDictionary(i => (string)i.Tag, i => AutomationProperties.GetName(i));
                Assert.IsTrue(entries.Count > 5);
                Assert.AreEqual(NavigationSidebar.Default.ExpandedWidth, column.ActualWidth, 0.5);

                Toggle(first); Drain(first);

                Assert.AreEqual(NavigationSidebar.CollapsedWidth, column.ActualWidth, 0.5, "Collapsed is the icons-only width.");
                Assert.AreEqual(Visibility.Collapsed, ((TextBox)first.FindName("NavigationSearchBox")).Visibility);
                foreach (var item in entries)
                {
                    var key = (string)item.Tag;
                    Assert.AreEqual(expected[key], AutomationProperties.GetName(item), $"{key}: the accessible name does not change with the mode.");
                    Assert.AreEqual(expected[key], item.ToolTip?.ToString(), $"{key}: with the label gone, the tooltip is the label.");
                    Assert.IsTrue(item.Content!.ToString()!.Length <= 2, $"{key}: icons-only prints a glyph, got '{item.Content}'.");
                    Assert.IsTrue(item.Focusable, $"{key} must stay keyboard-reachable while collapsed.");
                }
                foreach (var header in list.Items.OfType<ListBoxItem>().Where(i => i.Tag is null))
                    Assert.AreEqual(Visibility.Collapsed, header.Visibility, "A group title has no icon form; it is hidden rather than squeezed.");

                var target = entries.First(i => (string)i.Tag == "products");
                Keyboard.Focus(target); Drain(first);
                Assert.IsTrue(target.IsKeyboardFocused, "An icons-only entry still takes real keyboard focus.");

                first.Close(); Drain(first);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                var second = new MainWindow(root); second.Show(); Drain(second);
                var reopened = (ColumnDefinition)second.FindName("SidebarColumn");
                Assert.AreEqual(NavigationSidebar.CollapsedWidth, reopened.ActualWidth, 0.5, "The choice survives a restart.");
                Toggle(second); Drain(second);
                Assert.AreEqual(NavigationSidebar.Default.ExpandedWidth, reopened.ActualWidth, 0.5, "Expanding returns to the width that was in use.");
                var products = ((ListBox)second.FindName("NavigationList")).Items.OfType<ListBoxItem>().First(i => (string)i.Tag == "products");
                Assert.AreEqual(expected["products"], products.Content?.ToString(), "Expanded prints the full label again.");
                second.Close(); Drain(second);
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

    static void Toggle(MainWindow window) => typeof(MainWindow).GetMethod("ToggleSidebar", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
