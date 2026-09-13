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

// #824 in the real XML page: after reading a local file, every mapping row shows required / type / a masked
// sample from the first record / a status, the summary names the missing required field, and "İlk soruna git"
// selects that row.
[TestClass]
public sealed class ImportMappingWindowTests
{
    [TestMethod]
    public void AfterAReadTheMappingTableIsReadableAndTheFirstProblemIsOneClickAway()
    {
        var root = Path.Combine(Path.GetTempPath(), "mapping-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            // A bare STA thread has no SynchronizationContext: without this, the continuations of InspectAsync resume on
            // the thread pool and touch the window from the wrong thread (#782).
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                Directory.CreateDirectory(root);
                var feed = Path.Combine(root, "feed.xml");
                File.WriteAllText(feed, "<Products><Product><Code>ABC-1</Code><Title>Kupa</Title><Price>12,50</Price><Mail>ali@example.com token=abc123</Mail></Product><Product><Code>ABC-2</Code><Title>Tabak</Title><Price>9</Price><Mail>x</Mail></Product></Products>");
                var store = new CatalogStore(root);
                store.SaveSource(new XmlSource { Id = "feed-1", Name = "Fixture feed", Location = feed, ItemPath = "/Products/Product", Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Description"] = "Mail" } });
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "xml", true });
                Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);

                var inspect = (Task)typeof(MainWindow).GetMethod("InspectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                for (var i = 0; i < 200 && !inspect.IsCompleted; i++) { Drain(window); Thread.Sleep(25); }
                Assert.IsTrue(inspect.IsCompletedSuccessfully, "The read must finish: " + inspect.Exception?.GetBaseException().Message);
                Drain(window);

                var mappings = (List<MappingEntry>)typeof(MainWindow).GetField("mappings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var sku = mappings.Single(m => m.Key == "Sku");
                Assert.AreEqual("✱ zorunlu (veya barkod)", sku.RequiredMark, "SKU or barcode is one requirement.");
                Assert.AreEqual("ABC-1", sku.Sample, "The sample is the first record's value.");
                StringAssert.StartsWith(sku.StatusLabel, "✔");
                var description = mappings.Single(m => m.Key == "Description");
                Assert.IsFalse(description.Sample.Contains("ali@example.com") || description.Sample.Contains("abc123"), "A sample is masked: " + description.Sample);
                Assert.AreEqual("uzun metin", description.TypeLabel);
                var name = mappings.Single(m => m.Key == "Name");
                if (name.Path.Length == 0)
                {
                    StringAssert.StartsWith(name.StatusLabel, "✖", "A required field the scan could not suggest is a problem the table names.");
                    StringAssert.Contains(name.Reason, "Zorunlu");
                }

                var summary = (TextBlock)typeof(MainWindow).GetField("mappingSummary", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Assert.IsTrue(summary.Text.Contains("eşli"), summary.Text);
                var first = (Button)typeof(MainWindow).GetField("mappingFirstProblem", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var grid = (DataGrid)typeof(MainWindow).GetField("mapping", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                if (first.Visibility == Visibility.Visible)
                {
                    first.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                    Assert.AreEqual(first.Tag, ((MappingEntry)grid.SelectedItem).Key, "The first problem row is selected.");
                }
                Assert.IsTrue(grid.Columns.Any(c => c.Header?.ToString() == "Zorunlu") && grid.Columns.Any(c => c.Header?.ToString() == "Durum") && grid.Columns.Any(c => c.Header?.ToString()?.StartsWith("Örnek") == true), "The readable columns exist.");
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
