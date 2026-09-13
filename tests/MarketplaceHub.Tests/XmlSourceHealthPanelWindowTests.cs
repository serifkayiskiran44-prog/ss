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

// #830 in the real XML page: selecting a source composes the detail panel from what is recorded -- a timed-out
// source with a failed run reads Başarısız with the reason (no secret) and a restart action; a healthy file source
// with a completed run reads Sağlıklı with its last success and no restart; a fresh source reads Hiç çalışmadı;
// the reachability action checks the local file offline, records it and recomposes the panel.
[TestClass]
public sealed class XmlSourceHealthPanelWindowTests
{
    [TestMethod]
    public void ThePanelReadsFailedHealthyAndNeverRunAndTheCheckActionRecordsAndRecomposes()
    {
        var root = Path.Combine(Path.GetTempPath(), "srchealth-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                Directory.CreateDirectory(root);
                var now = DateTime.UtcNow;
                var store = new CatalogStore(root);
                var local = Path.Combine(root, "local.xml"); File.WriteAllText(local, "<Products><Product><Code>S1</Code></Product></Products>");
                var okId = Guid.NewGuid().ToString("N"); var downId = Guid.NewGuid().ToString("N"); var newId = Guid.NewGuid().ToString("N");
                store.SaveSource(new XmlSource { Id = downId, Name = "Düşük", Location = "https://d.example.com/feed.xml", LastHealthState = "TIMEOUT", LastHealthCheckUtc = now.AddMinutes(-3), LastHealthLatencyMs = 15000, LastHealthError = "timeout for https://u:p@d.example.com/feed.xml?token=SECRET999 Authorization: Basic dXNlcjpwYXNz", LastSuccessfulFeedUtc = now.AddDays(-2), LastSuccessfulFeedCount = 8, LastAppliedMappingRevision = 1 });
                store.SaveSource(new XmlSource { Id = okId, Name = "Yerel", Location = local, LastHealthState = "HEALTHY", LastHealthCheckUtc = now.AddHours(-2), LastHealthLatencyMs = 3, LastFeedState = "COMPLETE", LastSuccessfulFeedUtc = now.AddHours(-1), LastSuccessfulFeedCount = 3, LastAppliedMappingRevision = 1 });
                store.SaveSource(new XmlSource { Id = newId, Name = "Yeni", Location = "https://n.example.com/feed.xml" });
                var runs = new XmlRunStore(root);
                var failed = runs.Start(downId, "", null); runs.Fail(failed, "Zorunlu XML alanları bulunamadı: Sku token=SECRET999");
                var done = runs.Start(okId, "", null); runs.Complete(done, new ImportSummary(3, 0, 0));
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "xml", true });
                Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var panel = (Border)typeof(MainWindow).GetField("xmlSourceHealthPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

                SourceHealthPanelModel Select(string id)
                {
                    sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(x => x.Id == id); Drain(window);
                    var source = typeof(MainWindow).GetField("source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                    Await(window, (Task)typeof(MainWindow).GetMethod("RefreshXmlSourceHealthAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new[] { source })!);
                    Drain(window);
                    return (SourceHealthPanelModel)typeof(MainWindow).GetField("lastSourceHealth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                }
                string PanelText() => string.Join(" | ", Descendants(panel).OfType<TextBlock>().Select(t => t.Text));
                List<string> Buttons() => Descendants(panel).OfType<Button>().Select(b => (string)b.Content).ToList();

                var down = Select(downId);
                Assert.AreEqual(SourceHealthVerdict.Failed, down.Verdict);
                StringAssert.Contains(PanelText(), "Kaynak sağlığı: Başarısız"); StringAssert.Contains(PanelText(), "zaman aşımı"); StringAssert.Contains(PanelText(), "15.000 ms".Replace(".", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator));
                StringAssert.Contains(PanelText(), "2 gün önce · 8 ürün"); StringAssert.Contains(PanelText(), "son çalıştırma başarısız"); StringAssert.Contains(PanelText(), "Sku");
                Assert.IsFalse(PanelText().Contains("SECRET999") || PanelText().Contains("dXNlcjpwYXNz") || PanelText().Contains("u:p@"), PanelText());
                CollectionAssert.Contains(Buttons(), "Yeniden başlat");

                var ok = Select(okId);
                Assert.AreEqual(SourceHealthVerdict.Healthy, ok.Verdict, PanelText());
                StringAssert.Contains(PanelText(), "Kaynak sağlığı: Sağlıklı"); StringAssert.Contains(PanelText(), "1 sa önce · 3 ürün"); StringAssert.Contains(PanelText(), "gerekmiyor"); StringAssert.Contains(PanelText(), "erişilebilir");
                Assert.IsFalse(Buttons().Contains("Yeniden başlat"), "Nothing to restart on a healthy source.");
                StringAssert.Contains(PanelText(), "Havuz | ℹ 0 ürün", "The pool line counts the store's own products for this source -- none were stored, whatever the last success said.");
                Assert.IsTrue(Descendants(panel).OfType<Button>().All(b => b.Focusable), "Actions are keyboard-reachable.");

                var fresh = Select(newId);
                Assert.AreEqual(SourceHealthVerdict.NeverRun, fresh.Verdict); StringAssert.Contains(PanelText(), "Hiç çalışmadı"); StringAssert.Contains(PanelText(), "hiç kontrol edilmedi");

                // The reachability action on the local file: offline, HEALTHY, recorded, recomposed.
                Select(okId);
                var before = new CatalogStore(root).Sources().Single(x => x.Id == okId).LastHealthCheckUtc;
                Await(window, (Task)typeof(MainWindow).GetMethod("CheckSourceReachabilityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!);
                Drain(window);
                var after = new CatalogStore(root).Sources().Single(x => x.Id == okId);
                Assert.IsTrue(after.LastHealthCheckUtc > before, "The check was recorded on the persisted source.");
                Assert.AreEqual("HEALTHY", after.LastHealthState);
                StringAssert.Contains(PanelText(), "erişilebilir · az önce", PanelText());
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

    static void Await(Window window, Task task)
    {
        for (var i = 0; i < 400 && !task.IsCompleted; i++) { Drain(window); Thread.Sleep(25); }
        Assert.IsTrue(task.IsCompletedSuccessfully, "The window method must finish: " + task.Exception?.GetBaseException().Message);
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
