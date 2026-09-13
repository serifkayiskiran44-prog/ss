using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #874 on the real main window: an acknowledgement on the dashboard shows at once and the store keeps it; when the
// notification store refuses (its database made read-only for the next connection), the un-acknowledge shows at
// once, then the row returns to its stored state and the dashboard's error surface says the prior state was
// restored. The store is the same file the app uses; only the file attribute changes, and it is restored after.
[TestClass]
public sealed class OptimisticMutationUiTests
{
    [TestMethod]
    public void AcknowledgeShowsAtOnceAndRollsBackWithAVisibleErrorWhenTheStoreRefuses()
    {
        var root = Path.Combine(Path.GetTempPath(), "optimistic-" + Guid.NewGuid().ToString("N"));
        var db = Path.Combine(root, "notifications.db");
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var alerts = new NotificationStore(root);
                window = new MainWindow(root); window.Show(); Drain(window);
                Navigate(window, "dashboard"); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");
                Button? ack = null;
                // The fresh dashboard raises its own alerts (no source, no products…) and its sync resolves any alert its evaluation
                // does not produce, so a hand-seeded alert never shows; the test takes one of the real rows and follows it by id.
                WaitUntil(window, () => (ack = Descendants(tabs).OfType<Button>().FirstOrDefault(b => (b.Tag as string) == "alert-ack")) is not null, "an alert's acknowledge button");
                var id = AutomationProperties.GetAutomationId(ack!); Assert.IsFalse(string.IsNullOrEmpty(id), "the row carries its alert id");

                // Success: the row flips at once and the store keeps it; no error anywhere.
                ack!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                var afterClick = alerts.List();
                var texts = string.Join(" | ", Descendants(tabs).OfType<TextBlock>().Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text).Take(14));
                Assert.IsTrue(afterClick.Any(a => a.Id == id && a.Acknowledged), $"the store keeps the acknowledgement (rows: {afterClick.Count}, ids: {string.Join(",", afterClick.Select(a => a.Id + ":" + a.Acknowledged))}, row: {id}, ack buttons: {Descendants(tabs).OfType<Button>().Count(b => (b.Tag as string) == "alert-ack")}, unack buttons: {Descendants(tabs).OfType<Button>().Count(b => (b.Tag as string) == "alert-unack")}, in flight: {OptimisticMutation.IsInFlight("ack:" + id)}, texts: {texts})");
                var found = false; var flags = new List<string>();
                for (var i = 0; i < 120 && !found; i++) { ExpandAcknowledged(window, tabs); found = Descendants(tabs).OfType<Button>().Any(b => (b.Tag as string) == "alert-unack" && AutomationProperties.GetAutomationId(b) == id); if (i % 20 == 0) flags.Add($"t{i}:{alerts.List().FirstOrDefault(a => a.Id == id)?.Acknowledged}"); Thread.Sleep(25); }
                var withId = string.Join(",", Descendants(tabs).OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b) == id).Select(b => b.Tag as string));
                var expanders = string.Join(",", Descendants(tabs).OfType<Expander>().Select(e => $"{e.Tag}/{e.Header}/{e.IsExpanded}"));
                var section = Descendants(tabs).OfType<Panel>().FirstOrDefault(pn => (pn.Tag as string) == "alerts-acknowledged");
                Assert.IsTrue(found, $"the acknowledged row (store over time: {string.Join(" ", flags)}; buttons with the id: {withId}; expanders: {expanders}; acknowledged panel: {(section is null ? "absent" : section.Children.Count + " children, visible=" + section.IsVisible)})");
                Assert.IsFalse(Descendants(tabs).OfType<TextBlock>().Any(t => t.Text.Contains("geri alındı")), "no error on success");

                // The store refuses: the database is read-only for the next connection (the pool is cleared so the app opens it afresh).
                SqliteConnection.ClearAllPools(); File.SetAttributes(db, File.GetAttributes(db) | FileAttributes.ReadOnly);
                var unack = Descendants(tabs).OfType<Button>().First(b => (b.Tag as string) == "alert-unack" && AutomationProperties.GetAutomationId(b) == id);
                unack.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                Assert.IsTrue(alerts.List().Single(a => a.Id == id).Acknowledged, "the store still says acknowledged");
                WaitUntil(window, () => { ExpandAcknowledged(window, tabs); return Descendants(tabs).OfType<Button>().Any(b => (b.Tag as string) == "alert-unack" && AutomationProperties.GetAutomationId(b) == id); }, "the row back in its stored state");
                Assert.IsFalse(Descendants(tabs).OfType<Button>().Any(b => (b.Tag as string) == "alert-ack" && AutomationProperties.GetAutomationId(b) == id), "the optimistic un-acknowledge did not stick");
                // A banner's text block is built from inlines, so its Text stays empty: read it through a text range.
                static string TextOf(TextBlock t) => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text;
                var surface = Descendants(tabs).OfType<TextBlock>().Where(t => t.IsVisible).Select(TextOf).Where(t => t.Length > 0).ToList();
                var error = surface.FirstOrDefault(t => t.Contains("geri alındı"));
                Assert.IsNotNull(error, "the dashboard's error surface says the prior state was restored: " + string.Join(" | ", surface.Take(16)));
                Assert.IsFalse(error!.Contains(root), "no local path in the words on screen");
            }
            finally
            {
                try { if (File.Exists(db)) File.SetAttributes(db, File.GetAttributes(db) & ~FileAttributes.ReadOnly); } catch (Exception) { }
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    /// <summary>An acknowledged alert moves into a collapsed group expander; its rows enter the visual tree only once the group is expanded.</summary>
    static void ExpandAcknowledged(Window window, TabControl tabs)
    {
        foreach (var expander in Descendants(tabs).OfType<Expander>().Where(e => (e.Tag as string) is "alert-group" or "alerts-acknowledged")) expander.IsExpanded = true;
        Drain(window);
    }

    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void WaitUntil(Window window, Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++) { Drain(window); if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
    }

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
