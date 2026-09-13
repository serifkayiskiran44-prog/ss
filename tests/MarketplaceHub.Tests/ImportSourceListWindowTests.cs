using System;
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

// #823 in the real XML page: the last-used source leads and is preselected, a disabled one is labelled, an
// unsupported location is listed last and says why, the rendered row never shows a secret query parameter, and
// the type picker offers only what this build can import.
[TestClass]
public sealed class ImportSourceListWindowTests
{
    [TestMethod]
    public void TheSourceListLeadsWithTheLastUsedLabelsTheRestAndMasksSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), "srclist-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            try
            {
                var store = new CatalogStore(root);
                store.SaveSource(new XmlSource { Id = "alpha", Name = "Alpha", Location = "https://a.example.com/feed.xml?token=SECRET123&v=2" });
                store.SaveSource(new XmlSource { Id = "beta", Name = "Beta", Location = "https://b.example.com/feed.xml" });
                store.SaveSource(new XmlSource { Id = "off", Name = "Kapalı", Location = "https://c.example.com/feed.xml", Enabled = false });
                store.SaveSource(new XmlSource { Id = "old", Name = "Eski", Location = "http://d.example.com/feed.xml" });
                new UiPreferenceStore(root).Set(ImportSourceCatalog.RecentPreferenceKey, "beta");
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "xml", true });
                Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

                CollectionAssert.AreEqual(new[] { "beta", "alpha", "off", "old" }, sources.Items.OfType<XmlSource>().Select(x => x.Id).ToArray(), "Last-used, then usable, then disabled, then unsupported.");
                Assert.AreEqual("beta", ((XmlSource)sources.SelectedItem).Id, "The last-used source is preselected.");

                string RowText(int index)
                {
                    var container = (ListBoxItem)sources.ItemContainerGenerator.ContainerFromIndex(index);
                    return Descendants(container).OfType<TextBlock>().First().Text;
                }
                string RowTip(int index)
                {
                    var container = (ListBoxItem)sources.ItemContainerGenerator.ContainerFromIndex(index);
                    return Descendants(container).OfType<TextBlock>().First().ToolTip?.ToString() ?? "";
                }
                sources.ScrollIntoView(sources.Items[3]); Drain(window);
                StringAssert.StartsWith(RowText(0), "★", "The last-used row is marked.");
                StringAssert.Contains(RowText(0), "XML adresi");
                StringAssert.Contains(RowText(2), "pasif");
                StringAssert.Contains(RowText(3), "desteklenmiyor");
                StringAssert.Contains(RowTip(3), "HTTPS");
                Assert.IsFalse(RowText(1).Contains("SECRET123") || RowTip(1).Contains("SECRET123"), "The secret query parameter never reaches the list.");
                StringAssert.Contains(RowTip(1), "a.example.com/feed.xml", "…but the address stays recognisable.");

                var picker = (ComboBox)typeof(MainWindow).GetField("sourceTypePicker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                typeof(MainWindow).GetMethod("RefreshSourceTypePicker", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                var kinds = picker.Items.OfType<ImportSourceType>().Select(t => t.Kind).ToArray();
                CollectionAssert.AreEqual(new[] { ImportSourceKind.XmlUrl, ImportSourceKind.XmlFile, ImportSourceKind.Excel }, kinds, "This build has an Excel screen, so Excel is offered; nothing else is.");
                Assert.IsTrue(picker.Focusable, "The picker is keyboard-reachable.");
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

    static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject node)
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
