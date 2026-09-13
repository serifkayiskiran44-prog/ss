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

// #839 on the real OrdersPanel: a split shipment reads as a grouped section with one focusable row per package
// (delayed → blocking marker, delivered → success), a tracking number another order also carries is flagged from
// the real anomaly detector, the overview masks the tracking number while the editor holds the full one,
// activating a row selects that package, and an order without packages reads "Paket yok".
[TestClass]
public sealed class OrderShippingSectionUiTests
{
    [TestMethod]
    public void TheShippingSectionGroupsPackagesFlagsDuplicatesMasksTrackingAndSelectsOnActivate()
    {
        Run(root =>
        {
            var now = DateTimeOffset.UtcNow;
            var store = new OrdersStore(root);
            store.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-split", RawStatus = "paid", Items = new List<OrderItem> { new() { Title = "Kupa", Sku = "K1", Quantity = 2 } }, Shipments = new List<OrderShipment>
            {
                new() { Id = "p1", Carrier = "Aras Kargo", TrackingNumber = "ARS123456789", State = "InTransit", Events = new List<OrderTrackingEvent> { new(now.AddDays(-9), "Shipped", "test") } },
                new() { Id = "p2", Carrier = "Yurtiçi Kargo", TrackingNumber = "SHARED9876543", State = "Delivered", Events = new List<OrderTrackingEvent> { new(now.AddDays(-3), "Delivered", "test") } },
            } });
            store.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-other", RawStatus = "paid", Items = new List<OrderItem> { new() { Title = "Tabak", Sku = "T1", Quantity = 1 } }, Shipments = new List<OrderShipment> { new() { Id = "q1", Carrier = "Yurtiçi Kargo", TrackingNumber = "SHARED9876543", State = "InTransit", Events = new List<OrderTrackingEvent> { new(now.AddDays(-1), "Shipped", "test") } } } });
            store.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-none", RawStatus = "paid", Items = new List<OrderItem> { new() { Title = "Kupa", Sku = "K1", Quantity = 1 } } });

            var panel = OrdersPanel.Create(root);
            var grid = Descendants(panel).OfType<DataGrid>().First();
            var orders = ((IEnumerable<OrderSnapshot>)grid.ItemsSource).ToList();
            Border Section() => Descendants(panel).OfType<Border>().Single(b => (string)b.Tag == "order-shipping-section");
            string SectionText() => string.Join(" | ", Descendants(Section()).OfType<TextBlock>().Select(t => t.Text));

            grid.SelectedItem = orders.Single(o => o.OrderId == "o-split"); Drain();
            StringAssert.Contains(SectionText(), "2 paket · bölünmüş gönderi"); StringAssert.Contains(SectionText(), "1 gecikmiş"); StringAssert.Contains(SectionText(), "1 teslim");
            var rows = Descendants(Section()).OfType<Button>().ToList();
            Assert.AreEqual(2, rows.Count, "One row per package."); Assert.IsTrue(rows.All(b => b.Focusable), "Rows are keyboard-reachable.");
            var p1 = rows.Single(b => (string)b.Tag == "p1"); var p2 = rows.Single(b => (string)b.Tag == "p2");
            string RowText(Button b) => ((TextBlock)b.Content).Text;
            StringAssert.StartsWith(RowText(p1), "✖ gecikti · Aras Kargo · AR••••••6789 · Gecikti (9 gün)");
            StringAssert.StartsWith(RowText(p2), "✔ teslim edildi · Yurtiçi Kargo · SH••••••6543");
            StringAssert.Contains(RowText(p2), "takip no 1 başka siparişte de var", "The real anomaly detector found the shared number on o-other.");
            Assert.IsFalse(SectionText().Contains("ARS123456789") || SectionText().Contains("SHARED9876543"), "The overview never shows a full tracking number.");
            StringAssert.Contains(SectionText(), "paketler karışmış olabilir");

            var picker = Descendants(panel).OfType<ComboBox>().Single(c => c.MinWidth == 260);
            Assert.AreEqual("p1", ((OrderShipment)picker.SelectedItem).Id, "The first package opens by default.");
            p2.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
            Assert.AreEqual("p2", ((OrderShipment)picker.SelectedItem).Id, "Activating a row selects that package.");
            var trackingField = Descendants(panel).OfType<TextBox>().Single(t => t.Text == "SHARED9876543");
            Assert.IsNotNull(trackingField, "The editor still holds the full tracking number for the selected package.");

            grid.SelectedItem = orders.Single(o => o.OrderId == "o-none"); Drain();
            StringAssert.Contains(SectionText(), "Paket yok"); Assert.AreEqual(0, Descendants(Section()).OfType<Button>().Count());
        });
    }

    static void Drain() { for (var i = 0; i < 4; i++) Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is not DependencyObject d) continue;
            yield return d;
            foreach (var g in Descendants(d)) yield return g;
        }
    }

    static void Run(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "order-shipping-ui-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
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
