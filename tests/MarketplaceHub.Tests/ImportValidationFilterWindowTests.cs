using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #825 in the real XML page: after a preview, the counts describe the grid, the severity filter narrows it to
// the blocking row, changed-only drops the row identical to the pool, the controls are keyboard-reachable, and
// the filter labels never carry a row value.
[TestClass]
public sealed class ImportValidationFilterWindowTests
{
    [TestMethod]
    public void PreviewFiltersNarrowTheGridWithCountsThatFollow()
    {
        var root = Path.Combine(Path.GetTempPath(), "pvfilter-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                Directory.CreateDirectory(root);
                var feed = Path.Combine(root, "feed.xml");
                File.WriteAllText(feed, "<Products>" +
                    "<Product><Code>SAME</Code><Title>Kupa</Title><Cost>4</Cost><Stock>30</Stock><Desc>Uzun bir açıklama metni burada.</Desc><Img>https://cdn.example.com/a.jpg</Img></Product>" +
                    "<Product><Code>CHG</Code><Title>Tabak</Title><Cost>5</Cost><Stock>30</Stock><Desc>Uzun bir açıklama metni burada.</Desc><Img>https://cdn.example.com/b.jpg</Img></Product>" +
                    "<Product><Code>BAD</Code><Title>Bozuk</Title><Cost>4</Cost><Stock>30</Stock><Img>https://cdn.example.com/c.jpg</Img></Product>" +
                    "</Products>");
                var store = new CatalogStore(root);
                var source = new XmlSource { Id = "feed-1", Name = "Fixture feed", Location = feed, ItemPath = "/Products/Product", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY",
                    Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title", ["Cost"] = "Cost", ["Stock"] = "Stock", ["Description"] = "Desc", ["ImageUrls"] = "Img" } };
                store.SaveSource(source);
                var pool = XmlCatalog.Preview(File.ReadAllText(feed), source);
                store.Import(source, pool.Where(p => p.Sku == "SAME" || p.Sku == "CHG").Select(p => { if (p.Sku == "CHG") p.Cost = 9; return p; }).ToList());
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "xml", true });
                Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);
                foreach (var step in new[] { "InspectAsync", "PreviewAsync" })
                {
                    var task = (Task)typeof(MainWindow).GetMethod(step, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                    for (var i = 0; i < 400 && !task.IsCompleted; i++) { Drain(window); Thread.Sleep(25); }
                    Assert.IsTrue(task.IsCompletedSuccessfully, step + " must finish: " + task.Exception?.GetBaseException().Message);
                }
                Drain(window);

                var grid = (DataGrid)typeof(MainWindow).GetField("preview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var counts = (TextBlock)typeof(MainWindow).GetField("previewCounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var severity = (ComboBox)typeof(MainWindow).GetField("previewSeverity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var changedOnly = (CheckBox)typeof(MainWindow).GetField("previewChangedOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var field = (ComboBox)typeof(MainWindow).GetField("previewField", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

                Assert.AreEqual(3, grid.Items.Count, "Three preview rows before any filter.");
                StringAssert.Contains(counts.Text, "3 / 3");
                Assert.IsTrue(grid.Columns.Any(c => c.Header?.ToString() == "Doğrulama"), "A validation column exists.");

                // The preview itself refuses every blocking row (no name/identity, negatives, duplicates), so on this grid
                // the severity dimension is warnings: the one row without a description.
                severity.SelectedIndex = 2; Drain(window);   // Uyarı
                var flags = (System.Collections.Generic.IReadOnlyList<ImportValidationRow>)typeof(MainWindow).GetField("previewValidation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var fired = string.Join(" | ", flags.Select(f => $"{f.Index}:{f.Highest}:{string.Join(",", f.Fields)}:{f.Change}"));
                Assert.AreEqual(1, grid.Items.Count, "Only the row without a description warns. Fired: " + fired);
                Assert.AreEqual("BAD", ((CatalogProduct)grid.Items[0]).Sku);
                StringAssert.Contains(counts.Text, "1 / 3");

                severity.SelectedIndex = 0; changedOnly.IsChecked = true; changedOnly.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                var shown = grid.Items.OfType<CatalogProduct>().Select(p => p.Sku).ToArray();
                CollectionAssert.DoesNotContain(shown, "SAME", "The row identical to the pool is not a change.");
                CollectionAssert.Contains(shown, "CHG"); CollectionAssert.Contains(shown, "BAD");
                StringAssert.Contains(counts.Text, "2 / 3");

                severity.SelectedIndex = 2; Drain(window);
                Assert.AreEqual(1, grid.Items.Count, "Filters combine: warning AND changed.");
                severity.SelectedIndex = 1; Drain(window);   // Engel: impossible on a previewed row → zero-result state
                Assert.AreEqual(0, grid.Items.Count);
                StringAssert.Contains(counts.Text, "eşleşmiyor", "Zero results is said out loud.");

                Assert.IsTrue(severity.Focusable && field.Focusable && changedOnly.Focusable, "The filters are keyboard-reachable.");
                foreach (var option in field.Items.OfType<string>().Concat(((ComboBox)typeof(MainWindow).GetField("previewReason", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)).Items.OfType<string>()))
                    Assert.IsFalse(option.Contains("Kupa") || option.Contains("Tabak"), "A filter label never carries a row value: " + option);
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

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
