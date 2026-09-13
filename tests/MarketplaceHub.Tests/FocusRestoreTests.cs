using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #872 (DESIGN: Focus restore after async refresh). A refresh rebinds a list and the framework drops the keyboard;
// the shared restore puts it back on the same entity's row and cell when it is still listed, on the row at the
// same index (or the last) when the entity is gone, on the list itself when it is empty — and leaves the keyboard
// alone when it was elsewhere, so a person typing in the box that caused the refresh keeps typing. Everything is
// driven by the keyboard's own focus, never the mouse.
[TestClass]
public sealed class FocusRestoreTests
{
    sealed record Row(string Sku, string Name);

    [TestMethod]
    public void TheKeyboardReturnsToTheSameEntityANeighbourOrTheListAndStaysPutWhenItWasElsewhere()
    {
        RunSta(() =>
        {
            static string Key(object o) => ((Row)o).Sku;
            List<Row> Rows(params string[] skus) => skus.Select(s => new Row(s, "Ad " + s)).ToList();
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Width = 320, Height = 160, ItemsSource = Rows("SKU-1", "SKU-2", "SKU-3") };
            grid.Columns.Add(GridColumns.Text("SKU", "Sku", 100)); grid.Columns.Add(GridColumns.Text("Ad", "Name", 160));
            var list = new ListBox { Width = 200, Height = 120, DisplayMemberPath = "Name", ItemsSource = Rows("A", "B", "C") };
            var search = new TextBox { Width = 200 };
            var host = new StackPanel(); host.Children.Add(search); host.Children.Add(grid); host.Children.Add(list);
            var window = new Window { Content = host, Width = 420, Height = 400, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DesignTokens.Source });
            try
            {
                window.Show(); Drain(window);
                DataGridCell Cell(int row, int column) => (DataGridCell)grid.Columns[column].GetCellContent((DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(row))!.Parent;
                Cell(1, 1).Focus(); Drain(window); Assert.IsTrue(Cell(1, 1).IsKeyboardFocused);

                // Captured: the entity, the column, the index.
                var snapshot = FocusRestore.Capture(grid, Key);
                Assert.IsTrue(snapshot.Inside); Assert.AreEqual("SKU-2", snapshot.Key); Assert.AreEqual(1, snapshot.ColumnIndex); Assert.AreEqual(1, snapshot.Index);

                // Same entity after a rebind with fresh objects: the same cell again.
                grid.ItemsSource = Rows("SKU-1", "SKU-2", "SKU-3"); Drain(window);
                Assert.AreEqual(FocusRestoreOutcome.Same, FocusRestore.Restore(grid, snapshot, Key)); Drain(window);
                var focused = (DataGridCell)Keyboard.FocusedElement!; Assert.AreEqual("SKU-2", ((Row)focused.DataContext).Sku); Assert.AreEqual(1, focused.Column.DisplayIndex);

                // The entity is gone: the row at the same index takes its place, deterministically.
                snapshot = FocusRestore.Capture(grid, Key);
                grid.ItemsSource = Rows("SKU-1", "SKU-3", "SKU-4"); Drain(window);
                Assert.AreEqual(FocusRestoreOutcome.Neighbour, FocusRestore.Restore(grid, snapshot, Key)); Drain(window);
                focused = (DataGridCell)Keyboard.FocusedElement!; Assert.AreEqual("SKU-3", ((Row)focused.DataContext).Sku); Assert.AreEqual(1, focused.Column.DisplayIndex);

                // The list shrank below the index: the last row.
                snapshot = FocusRestore.Capture(grid, Key); Assert.AreEqual(1, snapshot.Index);
                grid.ItemsSource = Rows("SKU-9"); Drain(window);
                Assert.AreEqual(FocusRestoreOutcome.Neighbour, FocusRestore.Restore(grid, snapshot, Key)); Drain(window);
                Assert.AreEqual("SKU-9", ((Row)((DataGridCell)Keyboard.FocusedElement!).DataContext).Sku);

                // The list is empty: the list itself has the keyboard.
                snapshot = FocusRestore.Capture(grid, Key);
                grid.ItemsSource = Rows(); Drain(window);
                Assert.AreEqual(FocusRestoreOutcome.Owner, FocusRestore.Restore(grid, snapshot, Key)); Drain(window);
                Assert.IsTrue(grid.IsKeyboardFocused, "the empty list itself holds the keyboard");

                // The keyboard was elsewhere (the search box that caused the refresh): untouched, and it stays there.
                grid.ItemsSource = Rows("SKU-1", "SKU-2"); Drain(window);
                search.Focus(); Drain(window); Assert.IsTrue(search.IsKeyboardFocused);
                snapshot = FocusRestore.Capture(grid, Key); Assert.IsFalse(snapshot.Inside); Assert.IsNull(snapshot.Key);
                grid.ItemsSource = Rows("SKU-1", "SKU-2", "SKU-3"); Drain(window);
                Assert.AreEqual(FocusRestoreOutcome.Untouched, FocusRestore.Restore(grid, snapshot, Key)); Drain(window);
                Assert.IsTrue(search.IsKeyboardFocused, "typing continues");

                // A list box: the same item, then its neighbour.
                Drain(window); ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1)).Focus(); Drain(window);
                snapshot = FocusRestore.Capture(list, Key); Assert.IsTrue(snapshot.Inside); Assert.AreEqual("B", snapshot.Key); Assert.AreEqual(1, snapshot.Index);
                list.ItemsSource = Rows("A", "B", "C"); Drain(window);
                Assert.AreEqual(FocusRestoreOutcome.Same, FocusRestore.Restore(list, snapshot, Key)); Drain(window);
                Assert.AreEqual("B", ((Row)((ListBoxItem)Keyboard.FocusedElement!).DataContext).Sku);
                snapshot = FocusRestore.Capture(list, Key);
                list.ItemsSource = Rows("A", "C"); Drain(window);
                Assert.AreEqual(FocusRestoreOutcome.Neighbour, FocusRestore.Restore(list, snapshot, Key)); Drain(window);
                Assert.AreEqual("C", ((Row)((ListBoxItem)Keyboard.FocusedElement!).DataContext).Sku);
            }
            finally { window.Close(); }
        });
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
