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

// #835 on the real OrdersPanel: the list opens sorted by urgency (score, then key), the Aciliyet column names the
// reasons without PII, the store filter keeps the sort, the band filter narrows, a cancelled order sits at 0 in
// its own band, and switching the sort off keeps the same set.
[TestClass]
public sealed class OrderUrgencyUiTests
{
    [TestMethod]
    public void TheListSortsByUrgencyFiltersByBandAndStoreAndNamesReasonsWithoutPii()
    {
        Run(root =>
        {
            var now = DateTimeOffset.UtcNow;
            var store = new OrdersStore(root);
            store.SaveManual(Order("a-late", "S1", "paid", Ship("InTransit", now.AddDays(-10), "Shipped"), Item("Kupa", "KUPA-1")));
            store.SaveManual(Order("b-exc", "S1", "paid", Ship("Exception", now.AddDays(-2), "Shipped"), Item("Ayşe Yılmaz için tabak", "NOPE")));
            store.SaveManual(Order("c-ok", "S2", "paid", Ship("Delivered", now.AddDays(-3), "Delivered"), Item("Kupa", "KUPA-1")));
            store.SaveManual(Order("d-cancel", "S2", "cancelled", Ship("Exception", now.AddDays(-2), "Shipped"), Item("Kupa", "KUPA-1")));
            store.SaveManual(Order("e-tie", "S2", "paid", Ship("InTransit", now.AddDays(-10), "Shipped"), Item("Kupa", "KUPA-1")));
            var catalog = new TrMarketplaceHubDesktop.Catalog.CatalogStore(root);
            var source = new TrMarketplaceHubDesktop.Catalog.XmlSource { Id = Guid.NewGuid().ToString("N"), Name = "seed", Location = "https://seed.example.com/f.xml", ItemPath = "/p", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" } };
            catalog.SaveSource(source);
            catalog.Import(source, new List<TrMarketplaceHubDesktop.Catalog.CatalogProduct> { new() { Sku = "KUPA-1", Name = "Kupa", Price = 5, Currency = "TRY", Stock = 30, SourceId = source.Id, SourceKind = "xml", Description = "Uzun bir açıklama metni.", ImageUrls = "https://cdn.example.com/1.jpg" } });

            var panel = OrdersPanel.Create(root);
            var grid = Descendants(panel).OfType<DataGrid>().First();
            string[] Ids() => ((IEnumerable<OrderSnapshot>)grid.ItemsSource).Select(o => o.OrderId).ToArray();
            OrderSnapshot Row(string id) => ((IEnumerable<OrderSnapshot>)grid.ItemsSource).Single(o => o.OrderId == id);
            Drain();

            CollectionAssert.AreEqual(new[] { "b-exc", "a-late", "e-tie", "c-ok", "d-cancel" }, Ids(), "Score first; equal scores by key (S1 before S2); a cancelled order at the bottom.");
            Assert.AreEqual(Row("a-late").UrgencyScore, Row("e-tie").UrgencyScore, "The tie is real.");
            StringAssert.StartsWith(Row("b-exc").UrgencyLabel, "acil"); StringAssert.Contains(Row("b-exc").UrgencyLabel, "teslimat sorunu"); StringAssert.Contains(Row("b-exc").UrgencyLabel, "1 eşlenmemiş kalem");
            Assert.IsFalse(Row("b-exc").UrgencyLabel.Contains("Ayşe") || Row("b-exc").UrgencyLabel.Contains("NOPE"), Row("b-exc").UrgencyLabel);
            StringAssert.Contains(Row("a-late").UrgencyLabel, "gecikti"); StringAssert.StartsWith(Row("d-cancel").UrgencyLabel, "iptal"); Assert.AreEqual(0, Row("d-cancel").UrgencyScore);
            // A manual order has never synced with an API: that alone is worth ten points, so a delivered one is "normal", not "düşük".
            StringAssert.StartsWith(Row("c-ok").UrgencyLabel, "normal"); StringAssert.Contains(Row("c-ok").UrgencyLabel, "senkron yok");
            Assert.IsTrue(grid.Columns.Any(c => c.Header?.ToString() == "Aciliyet"), "The urgency is a column of the list.");

            var shop = Descendants(panel).OfType<ComboBox>().Single(c => c.Width == 140);
            shop.SelectedItem = "S2"; Drain();
            CollectionAssert.AreEqual(new[] { "e-tie", "c-ok", "d-cancel" }, Ids(), "The store filter keeps the urgency order.");
            shop.SelectedItem = "Tümü"; Drain();

            var band = Descendants(panel).OfType<ComboBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Aciliyet bandı");
            band.SelectedItem = "Acil"; Drain(); CollectionAssert.AreEqual(new[] { "b-exc" }, Ids());
            band.SelectedItem = "İptal"; Drain(); CollectionAssert.AreEqual(new[] { "d-cancel" }, Ids());
            band.SelectedItem = "Yüksek"; Drain(); CollectionAssert.AreEqual(new[] { "a-late", "e-tie" }, Ids());
            band.SelectedIndex = 0; Drain();

            var sort = Descendants(panel).OfType<CheckBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Aciliyete göre sırala");
            Assert.IsTrue(sort.Focusable);
            sort.IsChecked = false; Drain();
            CollectionAssert.AreEquivalent(new[] { "a-late", "b-exc", "c-ok", "d-cancel", "e-tie" }, Ids(), "Sort off: the same set, the store's own order.");
        });
    }

    static OrderSnapshot Order(string id, string shop, string raw, OrderShipment shipment, OrderItem item) => new()
    {
        Marketplace = "etsy", ShopId = shop, OrderId = id, RawStatus = raw, Items = new List<OrderItem> { item }, Shipments = new List<OrderShipment> { shipment },
    };
    static OrderShipment Ship(string state, DateTimeOffset at, string eventState) => new() { Id = "p1", Carrier = "test", State = state, Events = new List<OrderTrackingEvent> { new(at, eventState, "test") } };
    static OrderItem Item(string title, string sku) => new() { Title = title, Sku = sku, Quantity = 1 };

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
        var root = Path.Combine(Path.GetTempPath(), "order-urgency-ui-" + Guid.NewGuid().ToString("N"));
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
