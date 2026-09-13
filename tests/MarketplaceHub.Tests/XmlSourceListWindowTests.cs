using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #829 in the real XML page with 106 sources: bands in order with headers and counts, the running one from the
// run store's live lease, problems from the recorded health, the band chip and the text filter narrowing the
// list, the second line carrying last success / next schedule / active run, secrets unreachable by search, and the
// scan refreshing a local-file source's health without touching the network.
[TestClass]
public sealed class XmlSourceListWindowTests
{
    [TestMethod]
    public void TheListGroupsByHealthFiltersByBandAndTextAndNeverExposesASecret()
    {
        var root = Path.Combine(Path.GetTempPath(), "srcgroup-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var now = DateTime.UtcNow;
                var store = new CatalogStore(root);
                var local = Path.Combine(root, "local.xml"); File.WriteAllText(local, "<Products><Product><Code>S1</Code></Product></Products>");
                store.SaveSource(new XmlSource { Id = "run", Name = "Koşan", Location = "https://r.example.com/feed.xml" });
                store.SaveSource(new XmlSource { Id = "down", Name = "Düşük", Location = "https://d.example.com/feed.xml?token=SECRET999", LastHealthState = "TIMEOUT", LastHealthError = "timeout token=SECRET999" });
                store.SaveSource(new XmlSource { Id = "slow", Name = "Yavaş", Location = "https://s.example.com/feed.xml", LastHealthState = "HEALTHY", LastHealthLatencyMs = 7000 });
                store.SaveSource(new XmlSource { Id = "ok", Name = "Sağlam", Location = "https://ok.example.com/feed.xml", LastHealthState = "HEALTHY", LastHealthLatencyMs = 200, LastFeedState = "COMPLETE", LastSuccessfulFeedUtc = now.AddHours(-2), AutoImport = true, LastRunUtc = now.AddMinutes(-10), IntervalMinutes = 30, SlaRefreshMinutes = 120, SlaGraceMinutes = 30 });
                store.SaveSource(new XmlSource { Id = "file", Name = "Yerel", Location = local });
                store.SaveSource(new XmlSource { Id = "off", Name = "Kapalı", Location = "https://o.example.com/feed.xml", Enabled = false });
                for (var i = 1; i <= 100; i++) store.SaveSource(new XmlSource { Id = $"f{i:D3}", Name = $"Dolgu {i:D3}", Location = $"https://f{i}.example.com/feed.xml", LastHealthState = "HEALTHY", LastHealthLatencyMs = 50, LastRunUtc = now.AddMinutes(-1) });
                var runs = new XmlRunStore(root); runs.Start("run", "", TimeSpan.FromMinutes(10));
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "xml", true });
                Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var ids = sources.Items.OfType<XmlSource>().Select(x => x.Id).ToList();
                Assert.AreEqual(106, ids.Count);
                CollectionAssert.AreEqual(new[] { "run", "down", "slow" }, ids.Take(3).ToArray(), "Running, then problems (by title), before anything quiet.");
                Assert.IsTrue(ids.IndexOf("ok") > ids.IndexOf("slow") && ids.IndexOf("ok") < ids.IndexOf("file"), "Healthy sits between problems and never-run.");
                CollectionAssert.AreEqual(new[] { "file", "off" }, ids.Skip(104).ToArray(), "Never-run, then disabled, last.");

                var headers = Descendants(sources).OfType<TextBlock>().Select(t => t.Text).Where(t => t.EndsWith(")")).ToList();
                Assert.IsTrue(headers.Contains("Sürüyor (1)") && headers.Contains("Sorunlu (2)") && headers.Contains("Sağlıklı (101)") && headers.Contains("Hiç çalışmadı (1)") && headers.Contains("Pasif (1)"), string.Join(" | ", headers));

                string Detail(string id)
                {
                    var item = sources.Items.OfType<XmlSource>().Single(x => x.Id == id); sources.ScrollIntoView(item); Drain(window);
                    var container = sources.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem ?? Descendants(sources).OfType<ListBoxItem>().First(c => c.DataContext == item);
                    return string.Join(" | ", Descendants(container).OfType<TextBlock>().Select(t => t.Text));
                }
                StringAssert.Contains(Detail("run"), "sürüyor");
                StringAssert.Contains(Detail("ok"), "son başarı 2 sa önce"); StringAssert.Contains(Detail("ok"), "sonraki: 20 dk sonra");
                StringAssert.Contains(Detail("down"), "zaman aşımı");
                Assert.IsFalse(Detail("down").Contains("SECRET999"), Detail("down"));

                var chips = (WrapPanel)typeof(MainWindow).GetField("sourceBandChips", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var labels = chips.Children.OfType<Button>().Select(b => (string)b.Content).ToList();
                CollectionAssert.AreEqual(new[] { "Tümü (106)", "Sürüyor (1)", "Sorunlu (2)", "Sağlıklı (101)", "Hiç çalışmadı (1)", "Pasif (1)" }, labels.ToArray());
                Assert.IsTrue(chips.Children.OfType<Button>().All(b => b.Focusable), "Chips are keyboard-reachable.");
                chips.Children.OfType<Button>().Single(b => ((string)b.Content).StartsWith("Sorunlu")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                CollectionAssert.AreEqual(new[] { "down", "slow" }, sources.Items.OfType<XmlSource>().Select(x => x.Id).ToArray(), "The band chip narrows the list.");
                chips = (WrapPanel)typeof(MainWindow).GetField("sourceBandChips", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Assert.AreEqual("Tümü (106)", (string)chips.Children.OfType<Button>().First().Content, "Counts still say what exists while the filter hides it.");
                chips.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                Assert.AreEqual(106, sources.Items.Count);

                var search = (TextBox)typeof(MainWindow).GetField("sourceSearch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                search.Text = "SECRET999"; Drain(window);
                Assert.AreEqual(0, sources.Items.Count, "A secret in an address cannot be found by searching for it.");
                search.Text = "d.example.com"; Drain(window);
                CollectionAssert.AreEqual(new[] { "down" }, sources.Items.OfType<XmlSource>().Select(x => x.Id).ToArray(), "The host still finds it.");
                search.Text = ""; Drain(window);

                // The scan checks the local file without the network: it becomes healthy and leaves never-run.
                sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(x => x.Id == "file"); Drain(window);
                var scanStore = new CatalogStore(root);
                var beforeScan = scanStore.Sources().Single(x => x.Id == "file"); Assert.AreEqual("NEVER_CHECKED", beforeScan.LastHealthState);
                var check = XmlSourceHealthChecker.CheckAsync(new System.Net.Http.HttpClient(), beforeScan).GetAwaiter().GetResult();
                Assert.AreEqual("HEALTHY", check.State, "The checker treats an existing non-empty file as healthy, so the scan can run on it offline.");
                beforeScan.LastHealthCheckUtc = check.CheckedUtc; beforeScan.LastHealthState = check.State; beforeScan.LastHealthLatencyMs = check.LatencyMs; scanStore.SaveSource(beforeScan);
                typeof(MainWindow).GetMethod("RefreshSources", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { false }); Drain(window);
                ids = sources.Items.OfType<XmlSource>().Select(x => x.Id).ToList();
                Assert.IsTrue(ids.IndexOf("file") < ids.IndexOf("off") && ids.IndexOf("file") > ids.IndexOf("slow"), "After a healthy check the file source moved into the healthy band.");
                Assert.AreEqual("file", ((XmlSource)sources.SelectedItem).Id, "A refresh keeps the selection.");
                Assert.IsNotNull(typeof(MainWindow).GetMethod("ScanSourceHealthAsync", BindingFlags.Instance | BindingFlags.NonPublic), "The scan is wired on the page.");
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
