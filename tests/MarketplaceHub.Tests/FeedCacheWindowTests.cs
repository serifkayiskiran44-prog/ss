using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #894 in the real window: a feed the import page reads lands in the cache as the last known good with its address
// kept only as a masked label, the diagnostics line reports the cache, and a data backup leaves the cache out.
[TestClass]
public sealed class FeedCacheWindowTests
{
    [TestMethod]
    public void AReadFeedBecomesTheLastKnownGoodAndTheBackupLeavesTheCacheOut()
    {
        var parent = Path.Combine(Path.GetTempPath(), "feed-cache-window-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "data"); var backup = Path.Combine(parent, "yedek.zip");
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var feed = Path.Combine(root, "feed.xml");
                File.WriteAllText(feed, "<Products><Product><Code>S1</Code><Title>Kupa</Title></Product></Products>");
                new CatalogStore(root).SaveSource(new XmlSource { Id = "feed-1", Name = "Besleme", Location = feed, ItemPath = "/Products/Product", Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title" } });
                SqliteConnection.ClearAllPools();
                window = new MainWindow(root); window.Show(); Drain(window);
                Navigate(window, "xml"); Drain(window);
                var sources = (ListBox)Field(window, "sources"); sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);
                var inspect = (Task)typeof(MainWindow).GetMethod("InspectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                WaitUntil(window, () => inspect.IsCompleted, "the read"); if (inspect.IsFaulted) throw inspect.Exception!.GetBaseException();

                var entries = FeedCache.List(root, "feed-1");
                Assert.AreEqual(1, entries.Count); Assert.AreEqual(FeedCacheOutcome.Success, entries[0].Outcome);
                StringAssert.Contains(FeedCache.ReadLastKnownGood(root, "feed-1")!, "<Code>S1</Code>");
                Assert.IsFalse(entries[0].LocationLabel.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase), "the address label masks the user segment: " + entries[0].LocationLabel);
                Assert.AreEqual("OK", FeedCache.Check(root).Status); StringAssert.Contains(FeedCache.Check(root).Detail, "1 kayıt");

                // A backup carries the sources, not their downloads.
                new DataBackupService(root).Backup(backup, overwrite: true);
                using var zip = ZipFile.OpenRead(backup);
                Assert.IsTrue(zip.Entries.Any(e => e.FullName.EndsWith("catalog.db", StringComparison.OrdinalIgnoreCase)), "the catalogue is in the backup");
                Assert.IsFalse(zip.Entries.Any(e => e.FullName.Contains(FeedCache.FolderName, StringComparison.OrdinalIgnoreCase)), "the cache is not: " + string.Join(", ", zip.Entries.Select(e => e.FullName)));
            }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { SqliteConnection.ClearAllPools(); if (Directory.Exists(parent)) Directory.Delete(parent, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static void Drain(System.Windows.Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void WaitUntil(System.Windows.Window window, Func<bool> condition, string what)
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
