using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #873 (DESIGN: Selection persistence across refresh). A rebind, a page, a sort or a filter replaces a list's objects;
// the selection is kept by stable keys: every selected entity listed again is selected again with the anchor first,
// the ones gone are dropped and counted so the list can say so, and a selection never crosses a store.
[TestClass]
public sealed class SelectionPersistenceTests
{
    sealed record Row(string Sku, string Name);

    [TestMethod]
    public void SelectionFollowsStableKeysAcrossRebindSortFilterPagingAndDeleteAndNeverCrossesAStore()
    {
        RunSta(() =>
        {
            static string Key(object o) => ((Row)o).Sku;
            List<Row> Rows(params string[] skus) => skus.Select(s => new Row(s, "Ad " + s)).ToList();
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended, Width = 320, Height = 200, ItemsSource = Rows("SKU-1", "SKU-2", "SKU-3", "SKU-4") };
            grid.Columns.Add(GridColumns.Text("SKU", "Sku", 100));
            var list = new ListBox { Width = 200, Height = 120, DisplayMemberPath = "Name", ItemsSource = Rows("A", "B", "C") };
            var host = new StackPanel(); host.Children.Add(grid); host.Children.Add(list);
            var window = new Window { Content = host, Width = 420, Height = 400, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DesignTokens.Source });
            try
            {
                window.Show(); Drain(window);
                // Multi-select: the anchor (SKU-3) and two more.
                grid.SelectedItem = grid.Items[2]; grid.SelectedItems.Add(grid.Items[0]); grid.SelectedItems.Add(grid.Items[3]); Drain(window);
                var snapshot = SelectionPersistence.Capture(grid, Key);
                Assert.AreEqual("SKU-3", snapshot.AnchorKey); CollectionAssert.AreEquivalent(new[] { "SKU-1", "SKU-3", "SKU-4" }, snapshot.Keys.ToList()); Assert.AreEqual("SKU-3", snapshot.Keys[0], "the anchor comes first");

                // A rebind with fresh objects (an update): all three again, the anchor still the selected item.
                grid.ItemsSource = Rows("SKU-1", "SKU-2", "SKU-3", "SKU-4"); Drain(window);
                var result = SelectionPersistence.Restore(grid, snapshot, Key); Drain(window);
                Assert.AreEqual(3, result.Kept); Assert.AreEqual(0, result.Missing); Assert.IsNull(result.Note);
                CollectionAssert.AreEquivalent(new[] { "SKU-1", "SKU-3", "SKU-4" }, grid.SelectedItems.Cast<Row>().Select(r => r.Sku).ToList()); Assert.AreEqual("SKU-3", ((Row)grid.SelectedItem!).Sku);

                // A sort (the same entities in another order): all kept.
                grid.ItemsSource = Rows("SKU-4", "SKU-3", "SKU-2", "SKU-1"); Drain(window);
                result = SelectionPersistence.Restore(grid, SelectionPersistence.Capture(grid, Key), Key); Drain(window);
                Assert.AreEqual(3, grid.SelectedItems.Count); Assert.AreEqual(0, result.Missing);

                // A delete and a filter: SKU-4 deleted, SKU-1 filtered out — one kept, two counted and named in plain words.
                snapshot = SelectionPersistence.Capture(grid, Key);
                grid.ItemsSource = Rows("SKU-3", "SKU-2"); Drain(window);
                result = SelectionPersistence.Restore(grid, snapshot, Key); Drain(window);
                Assert.AreEqual(1, result.Kept); Assert.AreEqual(2, result.Missing); CollectionAssert.AreEquivalent(new[] { "SKU-1", "SKU-4" }, result.MissingKeys.ToList());
                StringAssert.Contains(result.Note, "2 seçili öğe"); StringAssert.Contains(result.Note, "seçimden çıkarıldı");
                Assert.AreEqual(1, grid.SelectedItems.Count); Assert.AreEqual("SKU-3", ((Row)grid.SelectedItem!).Sku);

                // Paging: another page lists none of them — nothing selected, all counted.
                snapshot = SelectionPersistence.Capture(grid, Key);
                grid.ItemsSource = Rows("SKU-201", "SKU-202"); Drain(window);
                result = SelectionPersistence.Restore(grid, snapshot, Key); Drain(window);
                Assert.AreEqual(0, result.Kept); Assert.AreEqual(1, result.Missing); Assert.AreEqual(0, grid.SelectedItems.Count); Assert.IsNull(grid.SelectedItem);

                // An empty selection restores nothing and says nothing.
                Assert.AreSame(SelectionRestoreResult.Nothing, SelectionPersistence.Restore(grid, SelectionPersistence.Capture(grid, Key), Key));

                // A selection never crosses a store: a snapshot taken in one store restores nothing in another, even when the keys match.
                grid.ItemsSource = Rows("SKU-1", "SKU-2"); Drain(window); grid.SelectedItems.Add(grid.Items[0]); Drain(window);
                snapshot = SelectionPersistence.Capture(grid, Key, scope: "etsy/shop-a");
                grid.ItemsSource = Rows("SKU-1", "SKU-2"); Drain(window);
                result = SelectionPersistence.Restore(grid, snapshot, Key, scope: "etsy/shop-b"); Drain(window);
                Assert.AreEqual(0, result.Kept); Assert.AreEqual(1, result.Missing); Assert.AreEqual(0, grid.SelectedItems.Count, "nothing carried across stores");
                result = SelectionPersistence.Restore(grid, snapshot, Key, scope: "etsy/shop-a"); Drain(window);
                Assert.AreEqual(1, result.Kept); Assert.AreEqual("SKU-1", ((Row)grid.SelectedItem!).Sku, "the same store restores");

                // A single-selection list box: the same entity, then gone.
                list.SelectedItem = list.Items[1]; Drain(window);
                snapshot = SelectionPersistence.Capture(list, Key); Assert.AreEqual("B", snapshot.AnchorKey);
                list.ItemsSource = Rows("C", "B", "A"); Drain(window);
                result = SelectionPersistence.Restore(list, snapshot, Key); Assert.AreEqual(1, result.Kept); Assert.AreEqual("B", ((Row)list.SelectedItem!).Sku);
                list.ItemsSource = Rows("A", "C"); Drain(window);
                result = SelectionPersistence.Restore(list, snapshot, Key); Assert.AreEqual(1, result.Missing); Assert.IsNull(list.SelectedItem); StringAssert.Contains(result.Note, "1 seçili öğe");
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
