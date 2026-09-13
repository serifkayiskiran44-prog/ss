using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #870 (DESIGN: Context-menu keyboard parity). A shared row-action menu for the grids: it opens from the keyboard
// (the menu key or Shift+F10) anchored at the focused row rather than at the mouse, it is rebuilt from the selection
// at that moment (a label carries the count), a disabled action says why in a tooltip that shows while disabled
// and never runs even if clicked, a destructive action says a confirmation follows and runs only through its own
// guard, the keyboard returns to the row when the menu closes, and the items are hit targets in DIP.
[TestClass]
public sealed class RowActionsTests
{
    sealed record Row(string Sku, string Name);

    [TestMethod]
    public void TheMenuOpensFromTheKeyboardAtTheFocusedRowShowsReasonsAndNeverRunsADisabledOrUnconfirmedAction()
    {
        RunSta(() =>
        {
            var rows = new List<Row> { new("SKU-1", "Bir"), new("SKU-2", "İki"), new("SKU-3", "Üç") };
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, Width = 320, Height = 160, ItemsSource = rows };
            grid.Columns.Add(GridColumns.Text("SKU", "Sku", 100)); grid.Columns.Add(GridColumns.Text("Ad", "Name", 160));
            var executed = new List<string>(); var asked = 0; var confirmed = false;
            IReadOnlyList<RowAction> Actions()
            {
                var n = grid.SelectedItems.Count;
                return new[]
                {
                    new RowAction("inspect", n > 1 ? $"İncele ({n})" : "İncele", () => executed.Add($"inspect:{n}"), n == 0 ? "Önce satır seçin." : null, Gesture: "Ctrl+I"),
                    new RowAction("delete", "Sil", () => { asked++; if (!confirmed) return; executed.Add("delete"); }, n > 1 ? "Silmek için tek satır seçin." : n == 0 ? "Önce satır seçin." : null, Destructive: true),
                };
            }
            RowActionMenu.Attach(grid, Actions); var openings = 0; grid.ContextMenuOpening += (_, e) => openings++;
            var window = new Window { Content = grid, Width = 420, Height = 240, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DesignTokens.Source });
            try
            {
                window.Show(); Drain(window);
                grid.SelectedIndex = 0; Drain(window);
                var row0 = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0); var cell = Descendants(row0).OfType<DataGridCell>().First();
                cell.Focus(); Drain(window); Assert.IsTrue(cell.IsKeyboardFocused, "the keyboard is in a cell");

                // The menu key opens the menu at the focused row.
                PressMenuKey(window); Drain(window);
                var menu = grid.ContextMenu!; Assert.IsTrue(menu.IsOpen, $"the menu key opens the menu (openings={openings}, items={menu.Items.Count}, focused={Keyboard.FocusedElement?.GetType().Name})"); Assert.AreSame(row0, menu.PlacementTarget, "anchored at the focused row, not at the mouse"); Assert.AreEqual(PlacementMode.Bottom, menu.Placement);
                var items = menu.Items.OfType<MenuItem>().ToList(); Assert.AreEqual(2, items.Count);
                var inspect = items.Single(i => (string)i.Tag == "inspect"); Assert.AreEqual("İncele", inspect.Header); Assert.AreEqual("Ctrl+I", inspect.InputGestureText); Assert.IsTrue(inspect.IsEnabled);
                var delete = items.Single(i => (string)i.Tag == "delete"); Assert.AreEqual("Sil…", delete.Header, "a destructive action says a confirmation follows"); Assert.IsTrue(delete.IsEnabled);
                foreach (var item in items) Assert.IsTrue(item.MinHeight >= DesignTokens.HitTargetMinSize, "a menu item is a hit target");
                // The destructive item runs only through its own guard: the guard says no, nothing happens.
                delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.AreEqual(1, asked); CollectionAssert.DoesNotContain(executed, "delete");
                menu.IsOpen = false; Drain(window);
                Assert.IsTrue(row0.IsKeyboardFocusWithin, "the keyboard returns to the row");

                // Two rows: the label carries the count; delete is disabled with its reason, shows it while disabled, and does not run even if clicked.
                grid.SelectedItems.Add(rows[1]); Drain(window); cell.Focus(); Drain(window);
                PressMenuKey(window); Drain(window);
                menu = grid.ContextMenu!; Assert.IsTrue(menu.IsOpen); items = menu.Items.OfType<MenuItem>().ToList();
                inspect = items.Single(i => (string)i.Tag == "inspect"); Assert.AreEqual("İncele (2)", inspect.Header);
                delete = items.Single(i => (string)i.Tag == "delete"); Assert.IsFalse(delete.IsEnabled); Assert.AreEqual("Silmek için tek satır seçin.", delete.ToolTip); Assert.IsTrue(ToolTipService.GetShowOnDisabled(delete));
                delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Assert.AreEqual(1, asked, "a disabled action never runs");
                inspect.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); CollectionAssert.Contains(executed, "inspect:2", "an enabled action runs for the selection");
                menu.IsOpen = false; Drain(window);

                // No selection: the menu still opens, every action disabled with its reason.
                grid.UnselectAll(); Drain(window); cell.Focus(); Drain(window);
                PressMenuKey(window); Drain(window);
                menu = grid.ContextMenu!; Assert.IsTrue(menu.IsOpen); items = menu.Items.OfType<MenuItem>().ToList();
                Assert.IsTrue(items.All(i => !i.IsEnabled && (string)i.ToolTip == "Önce satır seçin."));
                menu.IsOpen = false; Drain(window);

                // The same DIP numbers at 2×: the items are hit targets whatever the scale.
                grid.LayoutTransform = new ScaleTransform(2, 2); Drain(window); grid.SelectedIndex = 0; Drain(window); cell.Focus(); Drain(window);
                PressMenuKey(window); Drain(window);
                menu = grid.ContextMenu!; Assert.IsTrue(menu.IsOpen);
                foreach (var item in menu.Items.OfType<MenuItem>()) Assert.AreEqual(DesignTokens.HitTargetMinSize, item.MinHeight, 0.01);
                menu.IsOpen = false; Drain(window);
            }
            finally { window.Close(); }
        });
    }

    static void PressMenuKey(Window window)
    {
        // A physical key reaches an element as a tunnelling preview key-down on the focused element (a key pushed through the input manager is only seen by its post-process handlers).
        var target = Keyboard.FocusedElement as UIElement ?? window;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }

    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
