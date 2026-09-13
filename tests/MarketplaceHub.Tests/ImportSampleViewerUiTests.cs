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

// #882 on the real XML page: a feed whose first item carries a 300 000-character description, a binary-looking brand
// and a Unicode title is read; the mapping table's samples show the description cut with the explicit mark, the
// brand as the fixed binary word and the title intact, the mapping summary says the sample item was cut, and the
// huge value's tail never reaches the samples or the operations log.
[TestClass]
public sealed class ImportSampleViewerUiTests
{
    [TestMethod]
    public void TheMappingTableShowsBoundedSamplesWithTheTruncatedStateAndLogsNoPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "sample-viewer-window-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var feed = Path.Combine(root, "feed.xml");
                var hugeDescription = new string('d', 300_000) + " HUGEMARKER-END";
                File.WriteAllText(feed, "<Products><Product><Code>S1</Code><Title>Kırmızı Elbise 👗 Yaz Koleksiyonu</Title><Brand> abc</Brand><Cost>4</Cost><Stock>30</Stock><Desc>" + hugeDescription + "</Desc><Img>https://cdn.example.com/1.jpg</Img></Product><Product><Code>S2</Code><Title>Ürün 2</Title><Brand>Marka</Brand><Cost>4</Cost><Stock>30</Stock><Desc>Kısa</Desc><Img>https://cdn.example.com/2.jpg</Img></Product></Products>");
                var store = new CatalogStore(root);
                store.SaveSource(new XmlSource { Id = "feed-1", Name = "Fixture feed", Location = feed, ItemPath = "/Products/Product", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY",
                    Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title", ["Brand"] = "Brand", ["Cost"] = "Cost", ["Stock"] = "Stock", ["Description"] = "Desc", ["ImageUrls"] = "Img" } });
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show();
                Navigate(window, "xml"); Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);
                var inspect = (Task)typeof(MainWindow).GetMethod("InspectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                WaitUntil(window, () => inspect.IsCompleted, "the XML read");
                if (inspect.IsFaulted) throw inspect.Exception!.GetBaseException();

                var mappings = (List<MappingEntry>)typeof(MainWindow).GetField("mappings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var description = mappings.Single(m => m.Key == "Description").Sample;
                StringAssert.StartsWith(description, new string('d', ImportMappingTable.SampleLength)); StringAssert.Contains(description, "[kısaltıldı:"); Assert.IsTrue(description.Length < 120, description.Length.ToString());
                Assert.IsFalse(description.Contains("HUGEMARKER", StringComparison.Ordinal), "the tail of a huge value never reaches the sample");
                Assert.AreEqual(ImportSampleViewer.BinaryHidden, mappings.Single(m => m.Key == "Brand").Sample);
                StringAssert.StartsWith(mappings.Single(m => m.Key == "Name").Sample, "Kırmızı Elbise 👗");
                Assert.AreEqual("S1", mappings.Single(m => m.Key == "Sku").Sample);
                var summary = (TextBlock)typeof(MainWindow).GetField("mappingSummary", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                StringAssert.Contains(summary.Text, ImportSampleViewer.ItemTruncatedNote);

                var log = Path.Combine(root, "operations.log");
                if (File.Exists(log)) Assert.IsFalse(File.ReadAllText(log).Contains("HUGEMARKER", StringComparison.Ordinal), "no raw payload in the operations log");
                Assert.IsFalse(mappings.Any(m => m.Sample.Contains("HUGEMARKER", StringComparison.Ordinal) || m.Reason.Contains("HUGEMARKER", StringComparison.Ordinal)));
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

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
