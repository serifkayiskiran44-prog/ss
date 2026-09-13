using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #837 on the real OrdersPanel in a window: opening an order fills a header docked above the scrolling body with
// the id, store · status, payment / SLA / exceptions; scrolling the body leaves it where it is; a long id is elided
// with the full id as tooltip; a cancelled order leads with İptal; the header carries no item or buyer text; at
// 800 DIP it wraps inside its column; closing the detail hides it.
[TestClass]
public sealed class OrderDetailHeaderUiTests
{
    [TestMethod]
    public void TheHeaderIsStickyHierarchicalElidedPiiFreeAndHidesWithTheDetail()
    {
        Run(root =>
        {
            var store = new OrdersStore(root);
            var longId = "ORD-" + new string('4', 40);
            store.SaveManual(Order("o-1", "S1", "paid", "InTransit"));
            store.SaveManual(Order(longId, "S1", "cancelled", "Exception"));
            store.SaveManual(Order("o-3", "S2", "paid", "Delivered"));
            var panel = OrdersPanel.Create(root);
            var window = new Window { Content = panel, Width = 1400, Height = 700, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            try
            {
                window.Show(); Drain(window);
                var grid = Descendants(panel).OfType<DataGrid>().First();
                var header = Descendants(panel).OfType<Border>().Single(b => (string)b.Tag == "order-detail-header");
                var host = Descendants(panel).OfType<DockPanel>().Single(d => (string)d.Tag == "order-detail-host");
                var scroll = host.Children.OfType<ScrollViewer>().Single();
                Assert.AreEqual(Visibility.Collapsed, header.Visibility, "No order open: no header.");
                Assert.AreSame(header, host.Children[0], "The header is docked above the body, not inside the scroll.");

                string HeaderText() => string.Join(" | ", Descendants(header).OfType<TextBlock>().Select(t => t.Text));
                var orders = ((IEnumerable<OrderSnapshot>)grid.ItemsSource).ToList();
                grid.SelectedItem = orders.Single(o => o.OrderId == "o-1"); Drain(window);
                Assert.AreEqual(Visibility.Visible, header.Visibility);
                StringAssert.Contains(HeaderText(), "o-1"); StringAssert.Contains(HeaderText(), "etsy · S1 · Ödendi"); StringAssert.Contains(HeaderText(), "Ödeme bilgisi yok"); StringAssert.Contains(HeaderText(), "Yolda"); StringAssert.Contains(HeaderText(), "istisna yok");
                Assert.IsFalse(HeaderText().Contains("Ayşe") || HeaderText().Contains("Kupa"), "No item or buyer text in the header: " + HeaderText());

                var before = header.TransformToAncestor(window).Transform(new Point(0, 0));
                scroll.ScrollToEnd(); Drain(window);
                Assert.AreEqual(before, header.TransformToAncestor(window).Transform(new Point(0, 0)), "Scrolling the body does not move the header.");
                Assert.IsTrue(header.ActualHeight > 0 && header.IsVisible);

                grid.SelectedItem = orders.Single(o => o.OrderId == longId); Drain(window);
                var title = Descendants(header).OfType<TextBlock>().First();
                StringAssert.Contains(title.Text, "…"); Assert.IsTrue(title.Text.Length < longId.Length, "A 44-character id is elided in the middle.");
                StringAssert.Contains(title.ToolTip?.ToString() ?? "", longId, "The full id is the tooltip.");
                StringAssert.Contains(System.Windows.Automation.AutomationProperties.GetName(header), longId);
                StringAssert.Contains(HeaderText(), "İptal"); StringAssert.Contains(HeaderText(), "SLA değerlendirilmez");
                Assert.AreEqual(TextTrimming.CharacterEllipsis, title.TextTrimming);

                window.Width = 800; Drain(window);
                var layout = Descendants(panel).OfType<Grid>().Single(g => (string)g.Tag == "order-split");
                Assert.IsTrue(header.ActualWidth <= layout.ActualWidth + 1, "The header stays inside its column at 800 DIP.");
                Assert.IsTrue(Descendants(header).OfType<TextBlock>().Skip(1).All(t => t.TextWrapping == TextWrapping.Wrap), "The lower tiers wrap instead of overflowing.");
                window.Width = 1400; Drain(window);

                var shop = Descendants(panel).OfType<ComboBox>().Single(c => c.Width == 140);
                shop.SelectedItem = "S2"; Drain(window);
                Assert.AreEqual(Visibility.Collapsed, header.Visibility, "The detail closed (wrong store): the header goes with it.");
            }
            finally { window.Close(); }
        });
    }

    static OrderSnapshot Order(string id, string shop, string raw, string state) => new()
    {
        Marketplace = "etsy", ShopId = shop, OrderId = id, RawStatus = raw, Items = new List<OrderItem> { new() { Title = "Ayşe Yılmaz için Kupa", Sku = "K1", Quantity = 1 } },
        Shipments = new List<OrderShipment> { new() { Id = "p1", Carrier = "test", State = state, Events = new List<OrderTrackingEvent> { new(DateTimeOffset.UtcNow.AddDays(-1), state == "Delivered" ? "Delivered" : "Shipped", "test") } } },
    };

    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

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
        var root = Path.Combine(Path.GetTempPath(), "order-header-ui-" + Guid.NewGuid().ToString("N"));
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
