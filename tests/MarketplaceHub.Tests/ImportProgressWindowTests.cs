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

// #827 in the real XML page: reading and previewing a local file drives the six stages from real events -- the
// first five end done with real counts, apply stays pending, the parse and preview totals are the item count,
// the download reports a count with no percentage, and the cancel button is hidden once nothing runs.
[TestClass]
public sealed class ImportProgressWindowTests
{
    [TestMethod]
    public void ReadAndPreviewDriveTheStagesFromRealEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "progress-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                Directory.CreateDirectory(root);
                var feed = Path.Combine(root, "feed.xml");
                File.WriteAllText(feed, "<Products>" + string.Concat(Enumerable.Range(1, 3).Select(i => $"<Product><Code>S{i}</Code><Title>Ürün {i}</Title><Cost>4</Cost><Stock>30</Stock><Desc>Uzun bir açıklama metni.</Desc><Img>https://cdn.example.com/{i}.jpg</Img></Product>")) + "</Products>");
                var store = new CatalogStore(root);
                store.SaveSource(new XmlSource { Id = "feed-1", Name = "Fixture feed", Location = feed, ItemPath = "/Products/Product", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY",
                    Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title", ["Cost"] = "Cost", ["Stock"] = "Stock", ["Description"] = "Desc", ["ImageUrls"] = "Img" } });
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "xml", true });
                Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);
                var state = (ImportProgressState)typeof(MainWindow).GetField("importProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Assert.IsTrue(state.Snapshot(DateTime.UtcNow).All(s => s.Status == ImportProgressStatus.Pending), "Nothing has run: every stage is pending, no bar moves.");

                foreach (var step in new[] { "InspectAsync", "PreviewAsync" })
                {
                    var task = (Task)typeof(MainWindow).GetMethod(step, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                    for (var i = 0; i < 400 && !task.IsCompleted; i++) { Drain(window); Thread.Sleep(25); }
                    Assert.IsTrue(task.IsCompletedSuccessfully, step + " must finish: " + task.Exception?.GetBaseException().Message);
                }
                Drain(window);

                var snapshot = state.Snapshot(DateTime.UtcNow);
                foreach (var stage in new[] { ImportProgressStage.Download, ImportProgressStage.Read, ImportProgressStage.Parse, ImportProgressStage.Validate, ImportProgressStage.Preview })
                    Assert.AreEqual(ImportProgressStatus.Done, snapshot.Single(s => s.Stage == stage).Status, $"{stage} ended done from its own event.");
                Assert.AreEqual(ImportProgressStatus.Pending, snapshot.Single(s => s.Stage == ImportProgressStage.Apply).Status, "Nothing was applied.");
                Assert.AreEqual(3, snapshot.Single(s => s.Stage == ImportProgressStage.Parse).Total, "The parse total is the real item count.");
                Assert.AreEqual(3, snapshot.Single(s => s.Stage == ImportProgressStage.Preview).Done);
                Assert.AreEqual(100d, snapshot.Single(s => s.Stage == ImportProgressStage.Preview).Percent);
                Assert.IsTrue(snapshot.Single(s => s.Stage == ImportProgressStage.Download).Done > 0, "The download reports what it read.");
                Assert.IsNull(state.Running);

                var panel = (StackPanel)typeof(MainWindow).GetField("importProgressPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Assert.AreEqual(ImportProgressState.Stages.Count, panel.Children.Count, "One row per stage.");
                var bars = panel.Children.OfType<DockPanel>().SelectMany(r => r.Children.OfType<ProgressBar>()).ToList();
                Assert.AreEqual(6, bars.Count);
                Assert.IsTrue(bars.All(b => !b.IsIndeterminate), "Nothing is running, so no bar is indeterminate.");
                Assert.AreEqual(100d, bars.Single(b => (ImportProgressStage)b.Tag == ImportProgressStage.Preview).Value);
                Assert.AreEqual(0d, bars.Single(b => (ImportProgressStage)b.Tag == ImportProgressStage.Apply).Value, "A pending stage shows nothing -- no fake percentage.");
                var cancel = (Button)typeof(MainWindow).GetField("importCancelButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Assert.AreEqual(Visibility.Collapsed, cancel.Visibility, "Nothing runs, nothing to cancel.");
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
