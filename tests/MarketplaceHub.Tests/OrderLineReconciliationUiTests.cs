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
using TrMarketplaceHubDesktop.Catalog;

// #838 on the real OrdersPanel: opening an order shows its lines as a reconciliation grid fed by the real stock
// receipt and return ledger -- a partially shipped line, a partially returned line, an unshipped line -- with a
// summary; a cancelled order shows every line cancelled; the grid is keyboard-reachable and PII-free.
[TestClass]
public sealed class OrderLineReconciliationUiTests
{
    [TestMethod]
    public void TheLinesGridReadsPartialShipmentPartialReturnAndPendingFromTheRealRecords()
    {
        Run(root =>
        {
            var catalog = new CatalogStore(root);
            var source = new XmlSource { Id = Guid.NewGuid().ToString("N"), Name = "seed", Location = "https://seed.example.com/f.xml", ItemPath = "/p", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" } };
            catalog.SaveSource(source);
            catalog.Import(source, new List<CatalogProduct>
            {
                new() { Sku = "A", Name = "Kupa A", Price = 5, Currency = "TRY", Stock = 30, SourceId = source.Id, SourceKind = "xml", Description = "Uzun bir açıklama metni.", ImageUrls = "https://cdn.example.com/a.jpg" },
                new() { Sku = "B", Name = "Kupa B", Price = 5, Currency = "TRY", Stock = 30, SourceId = source.Id, SourceKind = "xml", Description = "Uzun bir açıklama metni.", ImageUrls = "https://cdn.example.com/b.jpg" },
            });
            var store = new OrdersStore(root);
            var order = new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Total = 20, Currency = "TRY",
                Items = new List<OrderItem> { new() { Title = "Ayşe Yılmaz için Kupa A", Sku = "A", Quantity = 3 }, new() { Title = "Kupa B", Sku = "B", Quantity = 2 }, new() { Title = "Kupa C", Sku = "C", Quantity = 1 } },
                Shipments = new List<OrderShipment> { new() { Id = "p1", Carrier = "test", State = "InTransit", Events = new List<OrderTrackingEvent> { new(DateTimeOffset.UtcNow.AddDays(-1), "Shipped", "test") } } } };
            store.SaveManual(order);
            store.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-2", RawStatus = "cancelled", Items = new List<OrderItem> { new() { Title = "Kupa A", Sku = "A", Quantity = 1 } } });
            // The real records: the stock receipt deducted A×2 (of 3) and B×2; the ledger then took one A back.
            catalog.ApplyOrderStock("etsy", "S1", "o-1", new[] { new OrderItem { Sku = "A", Quantity = 2 }, new OrderItem { Sku = "B", Quantity = 2 } });
            var reconciled = catalog.ApplyOrderReturn(store.ReadAll().Single(o => o.OrderId == "o-1"), new OrderReturnEvent("etsy", "S1", "o-1", "ret-1", "A", 1, 5, "TRY", DateTimeOffset.UtcNow), approved: true);
            Assert.IsTrue(reconciled.Allowed, string.Join(", ", reconciled.Reasons));

            var panel = OrdersPanel.Create(root);
            var grid = Descendants(panel).OfType<DataGrid>().First();
            var orders = ((IEnumerable<OrderSnapshot>)grid.ItemsSource).ToList();
            grid.SelectedItem = orders.Single(o => o.OrderId == "o-1"); Drain();
            var lines = Descendants(panel).OfType<DataGrid>().Single(g => (string)g.Tag == "order-lines");
            var rows = ((IEnumerable<OrderLineView>)lines.ItemsSource).ToList();
            Assert.AreEqual(3, rows.Count);
            var a = rows.Single(r => r.Sku == "A"); Assert.AreEqual(3, a.Ordered); Assert.AreEqual(2, a.Shipped); Assert.AreEqual(1, a.Returned); Assert.AreEqual(OrderLineState.PartiallyReturned, a.State);
            StringAssert.Contains(a.StateLabel, "kısmi iade"); StringAssert.Contains(a.Reason, "1 adet hiç sevk edilmedi", "A partial shipment and a partial return on one line are both said.");
            var b = rows.Single(r => r.Sku == "B"); Assert.AreEqual(OrderLineState.Shipped, b.State); Assert.AreEqual("＝ tam sevk", b.StateLabel);
            var c = rows.Single(r => r.Sku == "C"); Assert.AreEqual(OrderLineState.Pending, c.State); Assert.AreEqual("1", c.Outstanding);
            Assert.IsTrue(a.Title.Length <= OrderLineReconciliation.TitleLength && a.Title.Contains("Kupa"), "The title is the line's product text, capped.");
            Assert.IsTrue(lines.Focusable && lines.Columns.Count == 8 && lines.Columns.All(col => col.Width.IsAbsolute), "Keyboard-reachable grid with DIP column widths.");
            var summary = Descendants(panel).OfType<TextBlock>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t).StartsWith("Satır mutabakatı:"));
            StringAssert.Contains(summary.Text, "3 satır"); StringAssert.Contains(summary.Text, "1 eksik sevk"); StringAssert.Contains(summary.Text, "1 iadeli");

            grid.SelectedItem = orders.Single(o => o.OrderId == "o-2"); Drain();
            var cancelled = ((IEnumerable<OrderLineView>)Descendants(panel).OfType<DataGrid>().Single(g => (string)g.Tag == "order-lines").ItemsSource).ToList();
            Assert.IsTrue(cancelled.All(r => r.State == OrderLineState.Cancelled), "A cancelled order cancels every line.");
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
        var root = Path.Combine(Path.GetTempPath(), "order-lines-ui-" + Guid.NewGuid().ToString("N"));
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
