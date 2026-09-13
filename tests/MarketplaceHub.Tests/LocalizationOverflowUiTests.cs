using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #867 on the real main window: every route, and every inner tab a route carries, is walked with the overflow
// audit — at the default window size and at the window's minimum width — and no Turkish label, button, tab,
// header, hint or validation text is cut. The fixture carries the long strings a real store produces: a long product
// name, a long source name, a failed XML run, a queued sync job.
[TestClass]
public sealed class LocalizationOverflowUiTests
{
    static readonly string[] Routes =
    {
        "dashboard", "onboarding", "products", "bulk-products", "media", "listing-matrix", "xml", "excel", "migration", "taxonomy", "sync", "automation",
        "etsy", "amazon", "trendyol", "hepsiburada", "fruugo", "allegro", "wish", "channels", "connections", "api-health", "price-policies", "stock-policies",
        "policy-center", "locale-settings", "data-quality", "orders", "order-exceptions", "messages", "shipping", "readiness", "reports", "diagnostics", "settings",
    };

    [TestMethod]
    public void NoViewCutsItsTurkishTextAtTheDefaultOrTheMinimumWidth()
    {
        var root = Path.Combine(Path.GetTempPath(), "overflow-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var store = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Tedarikçi ana kataloğu — sonbahar 2026 (haftalık tam okuma)" };
                store.SaveSource(source);
                store.Import(source, new[]
                {
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-ÇĞİÖŞÜ-0001", Name = "Şüpheli işlemlerin çözümlenmesi için özel üretim, çok uzun adlı, ölçülü ürün — sonbahar koleksiyonu", Price = 1234567.89m, Stock = 1200000, Currency = "TRY", Description = "Uzun açıklama " + new string('x', 300) },
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-0002", Name = "Kısa ürün", Price = 10, Stock = 5, Currency = "EUR" },
                });
                new SyncStore(root).Enqueue(new SyncRequest("etsy", "stock", "SKU-ÇĞİÖŞÜ-0001", "v1"));
                var runs = new XmlRunStore(root); runs.Fail(runs.Start(source.Id), "Kaynak okunamadı: bağlantı zaman aşımına uğradı (30 sn) — " + new string('y', 200));
                window = new MainWindow(root); window.Show(); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");

                var report = new StringBuilder();
                foreach (var width in new[] { window.Width, window.MinWidth })
                {
                    window.Width = width; Drain(window);
                    foreach (var route in Routes)
                    {
                        Navigate(window, route); Drain(window);
                        foreach (var finding in OverflowAudit.Audit(tabs)) report.AppendLine($"{width:F0} {route}: {finding}");
                        // A route's inner tabs (a channel's connection form, the sync centre's histories) enter the tree only when selected.
                        foreach (var inner in Descendants(tabs).OfType<TabControl>().Where(t => !ReferenceEquals(t, tabs)).ToList())
                            for (var i = 0; i < inner.Items.Count; i++)
                            {
                                inner.SelectedIndex = i; Drain(window);
                                foreach (var finding in OverflowAudit.Audit(inner)) report.AppendLine($"{width:F0} {route} / tab {i}: {finding}");
                            }
                    }
                }
                Assert.AreEqual("", report.ToString(), "Cut text or controls:\n" + report);
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
