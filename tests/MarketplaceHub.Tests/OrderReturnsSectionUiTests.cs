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

// #840 on the real OrdersPanel: an order with a real stock receipt, a real applied return, a rejected and a pending
// request shows the returns section -- headline, request rows with their words, the SKU's partial-return line with
// the reconciliation's stock-impact figure, the event timeline, the refund against the total -- as focusable,
// PII-free rows; an order without returns reads "İade yok".
[TestClass]
public sealed class OrderReturnsSectionUiTests
{
    [TestMethod]
    public void TheReturnsSectionReadsRequestsLinesTimelineAndRefundFromTheRealRecords()
    {
        Run(root =>
        {
            var catalog = new CatalogStore(root);
            var source = new XmlSource { Id = Guid.NewGuid().ToString("N"), Name = "seed", Location = "https://seed.example.com/f.xml", ItemPath = "/p", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" } };
            catalog.SaveSource(source);
            catalog.Import(source, new List<CatalogProduct> { new() { Sku = "A", Name = "Kupa A", Price = 5, Currency = "TRY", Stock = 30, SourceId = source.Id, SourceKind = "xml", Description = "Uzun bir açıklama metni.", ImageUrls = "https://cdn.example.com/a.jpg" } });
            var store = new OrdersStore(root);
            store.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Total = 20m, Currency = "TRY", Items = new List<OrderItem> { new() { Title = "Ayşe Yılmaz için Kupa A", Sku = "A", Quantity = 3 } } });
            store.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-2", RawStatus = "paid", Total = 5m, Currency = "TRY", Items = new List<OrderItem> { new() { Title = "Kupa A", Sku = "A", Quantity = 1 } } });
            catalog.ApplyOrderStock("etsy", "S1", "o-1", new[] { new OrderItem { Sku = "A", Quantity = 3 } });
            var applied = catalog.ApplyOrderReturn(store.ReadAll().Single(o => o.OrderId == "o-1"), new OrderReturnEvent("etsy", "S1", "o-1", "ret-1", "A", 1, 5m, "TRY", DateTimeOffset.UtcNow), approved: true);
            Assert.IsTrue(applied.Allowed, string.Join(", ", applied.Reasons));
            var exceptions = new OrderExceptionStore(root);
            exceptions.Save(new OrderExceptionRecord { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", Type = "Return", EventKey = "req-1", Severity = "Warning", Message = "Müşteri Ayşe Yılmaz kupanın kırık geldiğini bildirdi token=SECRET77", Status = "Rejected" });
            exceptions.Save(new OrderExceptionRecord { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", Type = "Return", EventKey = "req-2", Severity = "Warning", Message = "İkinci kupa için iade talebi", Status = "Pending" });

            var panel = OrdersPanel.Create(root);
            var grid = Descendants(panel).OfType<DataGrid>().First();
            var orders = ((IEnumerable<OrderSnapshot>)grid.ItemsSource).ToList();
            Border Section() => Descendants(panel).OfType<Border>().Single(b => (string)b.Tag == "order-returns-section");
            string SectionText() => string.Join(" | ", Descendants(Section()).OfType<TextBlock>().Select(t => t.Text));

            grid.SelectedItem = orders.Single(o => o.OrderId == "o-1"); Drain();
            var text = SectionText();
            StringAssert.Contains(text, "İadeler · 2 talep (1 açık) · 1 kayıtlı iade · 1 kısmi");
            var requests = Descendants(Section()).OfType<Border>().Where(b => (string)b.Tag == "order-return-request").ToList();
            Assert.AreEqual(2, requests.Count); Assert.IsTrue(requests.All(r => r.Focusable && System.Windows.Input.KeyboardNavigation.GetIsTabStop(r)), "Request rows are keyboard-reachable.");
            StringAssert.Contains(text, "⊘ iade talebi · reddedildi"); StringAssert.Contains(text, "⏳ iade talebi · bekliyor");
            Assert.IsFalse(text.Contains("SECRET77") || text.Contains("Ayşe"), "Neither a token nor a customer's name from a request message reaches the section: " + text);
            var line = Descendants(Section()).OfType<TextBlock>().Single(t => (string)t.Tag == "order-return-line");
            StringAssert.StartsWith(line.Text, "◐ A · kısmi iade (1 / 3) · kalan iade edilebilir 2 · iade edilirse stoğa dönecek 2", "The stock-impact figure comes from the real reconciliation preview.");
            var events = Descendants(Section()).OfType<TextBlock>().Where(t => (string)t.Tag == "order-return-event").ToList();
            Assert.AreEqual(1, events.Count); StringAssert.Contains(events[0].Text, "A × 1"); StringAssert.Contains(events[0].Text, "stoğa 1");
            var refund = Descendants(Section()).OfType<TextBlock>().Single(t => (string)t.Tag == "order-return-refund");
            var sep = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            Assert.AreEqual($"İade tutarı 5{sep}00 / 20{sep}00 TRY", refund.Text);
            Assert.IsTrue(Section().Focusable, "The section itself is a tab stop.");
            Assert.AreEqual(28, catalog.Products().Single().Stock, "Nothing was written by rendering (the stock-impact figure is a preview): stock is still 30 − 3 deducted + 1 restored.");

            grid.SelectedItem = orders.Single(o => o.OrderId == "o-2"); Drain();
            StringAssert.Contains(SectionText(), "İadeler · İade yok"); StringAssert.Contains(SectionText(), "İade tutarı yok");
            Assert.AreEqual(0, Descendants(Section()).OfType<Border>().Count(b => (string)b.Tag == "order-return-request"));
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
        var root = Path.Combine(Path.GetTempPath(), "order-returns-ui-" + Guid.NewGuid().ToString("N"));
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
