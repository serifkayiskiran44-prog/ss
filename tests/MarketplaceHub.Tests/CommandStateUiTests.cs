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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #871 on the real main window: every disabled command on the walked pages carries a reason of the right kind —
// Back with no trail (a store state), the XML page's row-diff button with no preview yet (a store state) and then
// with the wrong selection (a selection), the orders page's cancel with nothing running (a store state), and the
// report setup's export and cancel before a run (store states) — as a tooltip that shows while disabled and as
// help text; enabled again, the ordinary tooltip returns. No disabled button on these pages is left without a reason.
[TestClass]
public sealed class CommandStateUiTests
{
    [TestMethod]
    public void DisabledCommandsOnTheRealPagesCarryTheirReasons()
    {
        var root = Path.Combine(Path.GetTempPath(), "command-state-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var store = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" }; store.SaveSource(source);
                store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "SKU-1", Name = "Bir", Price = 10, Stock = 30, Currency = "TRY" } });
                window = new MainWindow(root); window.Show(); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");

                // Back: no trail is a store state; the ordinary hint returns once there is a trail.
                var back = (Button)window.FindName("BackButton");
                Assert.IsFalse(back.IsEnabled); Assert.AreEqual(DisabledReasonKind.StoreState, CommandState.ReasonOf(back)!.Kind); StringAssert.Contains((string)back.ToolTip, "Durum:"); StringAssert.Contains((string)back.ToolTip, "Alt+←", "the shortcut hint stays"); Assert.IsTrue(ToolTipService.GetShowOnDisabled(back)); StringAssert.Contains((string)back.ToolTip, AutomationProperties.GetHelpText(back));

                // The XML page: the row-diff button before any preview (store state), then with no row selected after the fixture's preview is not run — still the store state.
                Navigate(window, "xml"); Drain(window);
                var diff = (Button)Field(window, "previewDiffButton");
                Assert.IsFalse(diff.IsEnabled); Assert.AreEqual(DisabledReasonKind.StoreState, CommandState.ReasonOf(diff)!.Kind, (string)diff.ToolTip); StringAssert.Contains((string)diff.ToolTip, "önizleme");
                var apply = (Button)Field(window, "previewApplyButton");
                Assert.IsFalse(apply.IsEnabled); Assert.IsNotNull(CommandState.ReasonOf(apply)); StringAssert.Contains((string)apply.ToolTip, AutomationProperties.GetHelpText(apply));

                // Orders: cancel with nothing running is a store state, not a mute button.
                Navigate(window, "orders"); Drain(window);
                var cancel = Descendants(tabs).OfType<Button>().First(b => b.Content?.ToString() == "İptal");
                Assert.IsFalse(cancel.IsEnabled); Assert.AreEqual(DisabledReasonKind.StoreState, CommandState.ReasonOf(cancel)!.Kind); StringAssert.Contains((string)cancel.ToolTip, "Durum:");

                // Reports: open the orders report's setup; export and cancel before a run are store states with reasons.
                Navigate(window, "reports"); Drain(window);
                var card = Descendants(tabs).OfType<Button>().First(b => b.Tag is ReportCard c && c.Key == ReportRunner.OrdersCsvKey);
                card.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                var export = Descendants(tabs).OfType<Button>().First(b => (b.Tag as string) == "report-result-export"); var runCancel = Descendants(tabs).OfType<Button>().First(b => (b.Tag as string) == "report-run-cancel");
                Assert.IsFalse(export.IsEnabled); Assert.AreEqual(DisabledReasonKind.StoreState, CommandState.ReasonOf(export)!.Kind); StringAssert.Contains((string)export.ToolTip, "sonuç");
                Assert.AreEqual(Visibility.Collapsed, runCancel.Visibility, "cancel is hidden before a run, not mutely disabled");

                // Every disabled button on these pages carries a reason (the whole point: no mute disabled command).
                var mute = new List<string>();
                foreach (var route in new[] { "dashboard", "products", "xml", "orders", "reports" })
                {
                    Navigate(window, route); Drain(window);
                    foreach (var button in Descendants(tabs).OfType<Button>().Where(b => b.IsVisible && !b.IsEnabled && b.TemplatedParent is null))
                        if (CommandState.ReasonFor(button) is null) mute.Add($"{route}: {button.Content ?? button.Tag}");
                }
                Assert.AreEqual(0, mute.Count, "disabled without a reason: " + string.Join("; ", mute));
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

    static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
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
