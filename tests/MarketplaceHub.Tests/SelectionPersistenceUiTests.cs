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

// #873 on the real main window: a multi-selection on the product grid survives a refresh by the products' ids, the
// product card still follows the anchor; a selected product deleted elsewhere drops out of the selection and the
// selection bar says so in plain words; a page turn says the selection is not on this page; the bulk panel's grid
// keeps its selection across the reload its search causes (the first run of this test picked a product the earlier
// step had deleted, and the reload rightly dropped it — the expectation, not the code, was wrong).
[TestClass]
public sealed class SelectionPersistenceUiTests
{
    [TestMethod]
    public void TheProductGridAndTheBulkPanelKeepTheirSelectionByIdentityAndSayWhatIsGone()
    {
        var root = Path.Combine(Path.GetTempPath(), "selection-persistence-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var store = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" }; store.SaveSource(source);
                store.Import(source, Enumerable.Range(1, 4).Select(i => new CatalogProduct { SourceId = source.Id, Sku = $"SKU-{i}", Name = $"Ürün {i}", Price = 10, Stock = 30, Currency = "TRY" }).ToList());
                window = new MainWindow(root); window.Show(); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");

                // Products: three selected, the third the anchor; a refresh keeps all three and the card on the anchor.
                Navigate(window, "products"); Drain(window);
                var products = (DataGrid)Field(window, "products");
                products.SelectedItem = products.Items[2]; products.SelectedItems.Add(products.Items[0]); products.SelectedItems.Add(products.Items[3]); Drain(window);
                var anchorSku = ((CatalogProduct)products.SelectedItem).Sku; var selectedSkus = products.SelectedItems.Cast<CatalogProduct>().Select(p => p.Sku).OrderBy(s => s).ToList();
                Invoke(window, "RefreshProducts"); Drain(window);
                CollectionAssert.AreEqual(selectedSkus, products.SelectedItems.Cast<CatalogProduct>().Select(p => p.Sku).OrderBy(s => s).ToList(), "the same three by id");
                Assert.AreEqual(anchorSku, ((CatalogProduct)products.SelectedItem!).Sku, "the anchor is still the selected item");
                Assert.AreEqual(anchorSku, ((CatalogProduct)Field(window, "edit")).Sku, "the card follows the anchor");
                var detail = (TextBlock)Field(window, "productSelectionDetail"); Assert.IsFalse(detail.Text.Contains("görünmüyor"), "nothing is gone");

                // One of them is deleted elsewhere: the selection drops it and the bar says so.
                var gone = products.SelectedItems.Cast<CatalogProduct>().First(p => p.Sku != anchorSku);
                store.DeleteProduct(gone); Invoke(window, "RefreshProducts"); Drain(window);
                Assert.AreEqual(2, products.SelectedItems.Count, "the two still listed");
                Assert.IsFalse(products.SelectedItems.Cast<CatalogProduct>().Any(p => p.Sku == gone.Sku));
                var bar = (Border)Field(window, "productSelectionBar"); Assert.AreEqual(Visibility.Visible, bar.Visibility);
                StringAssert.Contains(detail.Text, "1 seçili öğe bu listede görünmüyor"); StringAssert.Contains(detail.Text, "seçimden çıkarıldı");

                // The user changes the selection: the note is gone.
                products.SelectedItems.Clear(); products.SelectedItems.Add(products.Items[0]); Drain(window);
                Assert.IsFalse(detail.Text.Contains("görünmüyor"), "a new selection clears the note");

                // The bulk panel: two selected, the search changes and the list reloads; both are selected again.
                Navigate(window, "bulk-products"); Drain(window);
                var bulkGrid = Descendants(tabs).OfType<DataGrid>().First(g => g.SelectionMode == DataGridSelectionMode.Extended && g.Items.Count >= 3);
                // The bulk grid was built before the delete above and still lists the deleted product; pick two that still exist.
                var alive = bulkGrid.Items.Cast<CatalogProduct>().Where(p => p.Sku is "SKU-2" or "SKU-3").ToList();
                bulkGrid.SelectedItem = alive[0]; bulkGrid.SelectedItems.Add(alive[1]); Drain(window);
                var bulkSkus = bulkGrid.SelectedItems.Cast<CatalogProduct>().Select(p => p.Sku).OrderBy(s => s).ToList();
                var bulkSearch = Descendants(tabs).OfType<TextBox>().First(t => t.IsVisible && t.ToolTip?.ToString()?.StartsWith("SKU, barkod, ad") == true);
                bulkSearch.Text = "Ürün"; Drain(window);
                var bulkStatus = Descendants(tabs).OfType<TextBlock>().Where(t => t.Text.Contains("ürün ·")).Select(t => t.Text).FirstOrDefault() ?? "(no status)";
                CollectionAssert.AreEqual(bulkSkus, bulkGrid.SelectedItems.Cast<CatalogProduct>().Select(p => p.Sku).OrderBy(s => s).ToList(), $"the bulk selection survives its reload (before: {string.Join(",", bulkSkus)}; after: {string.Join(",", bulkGrid.SelectedItems.Cast<CatalogProduct>().Select(p => p.Sku))}; items: {bulkGrid.Items.Count}; status: {bulkStatus})");
                bulkSearch.Text = "SKU-3"; Drain(window);
                var status = Descendants(tabs).OfType<TextBlock>().First(t => t.Text.Contains("seçimden çıkarıldı"));
                StringAssert.Contains(status.Text, "1 seçili öğe bu listede görünmüyor", "a narrower search drops the one not listed and says so");
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

    static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    static void Invoke(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

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
