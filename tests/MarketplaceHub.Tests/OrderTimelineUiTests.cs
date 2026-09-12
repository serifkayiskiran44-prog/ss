using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #782, on the real entry path: OrdersPanel -> select an order -> ShipmentEditor. The detail pane is never
// laid out here (no PresentationSource, so the ScrollViewer's viewport stays 0 and scroll-driven paging is
// inert); paging is driven through the "Daha fazla gözlem yükle" button, which makes the test deterministic.
[TestClass]
public sealed class OrderTimelineUiTests
{
    const string Source = "gözlem-testi";

    [TestMethod]
    public void OpeningAThousandObservationOrderRendersOnePageThenPagesOnDemandRejectsDuplicateClicksAndDropsPendingWorkOnSwitch()
    {
        Run(root =>
        {
            var store = new OrdersStore(root);
            store.SaveManual(Order("big", Events(1000)));
            store.SaveManual(Order("small", Events(3)));

            var panel = OrdersPanel.Create(root);
            var grid = Descendants(panel).OfType<DataGrid>().First();
            var orders = ((IEnumerable<OrderSnapshot>)grid.ItemsSource).ToList();
            grid.SelectedItem = orders.Single(o => o.OrderId == "big");
            Drain();

            // SaveManual appends one observation of its own when the shipment's state differs from the last
            // event's state: the big order's last seeded event is "Shipped" (odd index) against a shipment in
            // "InTransit", so its history holds 1001 and that newest, store-written line is part of page one;
            // the small order's last event already is "InTransit", so it stays at 3.
            Assert.AreEqual(50, EventLines(panel).Count, "Opening the order renders exactly one page of the observations, not the whole history.");
            var more = MoreButton(panel);
            Assert.AreEqual(Visibility.Visible, more.Visibility);
            StringAssert.Contains((string)more.Content, "951 kaldı");

            Click(more);
            Drain();
            Assert.AreEqual(100, EventLines(panel).Count, "One click appends exactly one more page.");
            StringAssert.Contains((string)more.Content, "901 kaldı");

            // Two rapid clicks: the first page parks at its mid-page dispatcher yield, the second click is a
            // duplicate request for a page already in flight and must append nothing.
            Click(more); Click(more);
            Drain();
            Assert.AreEqual(150, EventLines(panel).Count, "A duplicate click while a page is in flight must not append that page twice.");
            StringAssert.Contains((string)more.Content, "851 kaldı");

            // Switch orders while a page is in flight: the pending work is cancelled and nothing from the big
            // order's timeline leaks into the newly opened detail.
            Click(more);
            grid.SelectedItem = orders.Single(o => o.OrderId == "small");
            Drain();
            Assert.AreEqual(3, EventLines(panel).Count, "The new order shows its own 3 observations and none of the previous order's pending page.");
            Assert.AreEqual(Visibility.Collapsed, MoreButton(panel).Visibility, "Nothing is left to page in for a 3-observation history.");
        });
    }

    static List<OrderTrackingEvent> Events(int count)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count).Select(i => new OrderTrackingEvent(start.AddMinutes(i), i % 2 == 0 ? "InTransit" : "Shipped", Source)).ToList();
    }

    static OrderSnapshot Order(string id, List<OrderTrackingEvent> events) => new()
    {
        Marketplace = "Yerel", ShopId = "Mağazam", OrderId = id, RawStatus = "Açık",
        Shipments = { new OrderShipment { Id = "pkg-" + id, Carrier = "Kargo", TrackingNumber = "TN-" + id, State = "InTransit", Events = events } },
    };

    // A timeline line ends with its source: the seeded ones with the test source, the one SaveManual writes with "Yerel / manuel".
    static List<TextBlock> EventLines(DependencyObject panel) => Descendants(panel).OfType<TextBlock>().Where(t => t.Text.EndsWith(" · " + Source, StringComparison.Ordinal) || t.Text.EndsWith(" · Yerel / manuel", StringComparison.Ordinal)).ToList();

    static Button MoreButton(DependencyObject panel) => Descendants(panel).OfType<Button>().Single(b => b.Content is string text && text.StartsWith("Daha fazla gözlem yükle", StringComparison.Ordinal));

    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // Pumps every queued dispatcher operation above ApplicationIdle -- which is where the loader's
    // Background-priority yields between chunks land -- so a page that is in flight completes.
    static void Drain() { for (var i = 0; i < 4; i++) Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    static void Run(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "order-timeline-ui-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            // The real UI thread always carries a DispatcherSynchronizationContext; without it, an await that
            // completes inside a dispatcher operation resumes on the thread pool instead of the STA thread.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { test(root); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
