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
using TrMarketplaceHubDesktop.Catalog;

// #841 on the real OrdersPanel: the customer section opens masked; a reveal is refused while the policy is off
// (and audited as denied); with the policy on it is refused without a reason, granted with one (audited with
// fields and reason, never a value), shows the values, re-masks itself when the policy's seconds pass, and the
// controls are keyboard-reachable; an order without customer data says so.
[TestClass]
public sealed class OrderCustomerSectionUiTests
{
    [TestMethod]
    public void MaskedByDefaultRevealDeniedThenAllowedWithReasonAuditedAndRemaskedOnTimeout()
    {
        Run(root =>
        {
            var store = new OrdersStore(root);
            store.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Items = new List<OrderItem> { new() { Title = "Kupa", Sku = "K1", Quantity = 1 } } });
            store.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-2", RawStatus = "paid", Items = new List<OrderItem> { new() { Title = "Kupa", Sku = "K1", Quantity = 1 } } });
            store.SaveCustomer(new OrderCustomer("etsy", "S1", "o-1", "Ayşe Yılmaz", "ayse.yilmaz@example.com", "+90 532 123 45 67", "Bağdat Cad. 12/3 Kadıköy"));
            var catalog = new CatalogStore(root); var audit = new AuditStore(root);

            var panel = OrdersPanel.Create(root);
            var grid = Descendants(panel).OfType<DataGrid>().First();
            var orders = ((IEnumerable<OrderSnapshot>)grid.ItemsSource).ToList();
            Border Section() => Descendants(panel).OfType<Border>().Single(b => (string)b.Tag == "order-customer-section");
            string SectionText() => string.Join(" | ", Descendants(Section()).OfType<TextBlock>().Select(t => t.Text));
            T Control<T>(string tag) where T : FrameworkElement => Descendants(Section()).OfType<T>().Single(c => (string)c.Tag == tag);
            bool NoPii(string text) => !(text.Contains("Yılmaz") || text.Contains("yilmaz") || text.Contains("532 123") || text.Contains("Kadıköy"));

            grid.SelectedItem = orders.Single(o => o.OrderId == "o-1"); Drain();
            StringAssert.Contains(SectionText(), "Müşteri · maskeli"); StringAssert.Contains(SectionText(), "E-posta: a••@e•••.com"); StringAssert.Contains(SectionText(), "Telefon: +•••••••••67");
            Assert.IsTrue(NoPii(SectionText()), SectionText());
            Assert.IsTrue(Section().Focusable && Control<Button>("order-customer-reveal").Focusable && Control<TextBox>("order-customer-reason").Focusable, "Keyboard-reachable.");

            // Policy off: refused and audited as denied, still masked.
            Control<TextBox>("order-customer-reason").Text = "kargo adresi teyidi";
            Control<Button>("order-customer-reveal").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
            StringAssert.Contains(SectionText(), "Müşteri · maskeli"); StringAssert.Contains(Control<TextBlock>("order-customer-status").Text, "izin vermiyor");
            var denied = audit.List(10).Single(a => a.Action == "pii-reveal-denied");
            Assert.AreEqual("o-1", denied.OrderId); Assert.IsTrue(NoPii(denied.Detail), denied.Detail);

            // Policy on, 5 s (the minimum): no reason → refused; a reason → revealed, audited with fields and reason only.
            catalog.SavePiiRevealPolicy(new PiiRevealPolicy(true, 5, true));
            grid.SelectedItem = orders.Single(o => o.OrderId == "o-2"); Drain(); grid.SelectedItem = orders.Single(o => o.OrderId == "o-1"); Drain();
            Control<TextBox>("order-customer-reason").Text = "ok";
            Control<Button>("order-customer-reveal").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
            StringAssert.Contains(Control<TextBlock>("order-customer-status").Text, "Gerekçe gerekli"); StringAssert.Contains(SectionText(), "maskeli");
            Control<TextBox>("order-customer-reason").Text = "kargo adresi teyidi";
            Control<Button>("order-customer-reveal").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
            StringAssert.Contains(SectionText(), "Müşteri · AÇIK");
            var edits = Descendants(Section()).OfType<TextBox>().Where(t => (string)t.Tag == "order-customer-edit").ToList();
            Assert.AreEqual(4, edits.Count); Assert.IsTrue(edits.Any(t => t.Text == "ayse.yilmaz@example.com"), "Revealed: the full value is in the editor.");
            var granted = audit.List(10).Single(a => a.Action == "pii-reveal");
            StringAssert.Contains(granted.Detail, "gerekçe: kargo adresi teyidi"); StringAssert.Contains(granted.Detail, "Alanlar: Ad, E-posta, Telefon, Adres"); Assert.IsTrue(NoPii(granted.Detail) && !granted.Detail.Contains("example.com"), granted.Detail);

            // The policy's seconds pass: re-masked without a click.
            var deadline = DateTime.UtcNow.AddSeconds(9);
            while (DateTime.UtcNow < deadline && SectionText().Contains("AÇIK")) { Drain(); Thread.Sleep(200); }
            StringAssert.Contains(SectionText(), "Müşteri · maskeli", "Auto re-mask after the policy's seconds.");
            Assert.AreEqual(0, Descendants(Section()).OfType<TextBox>().Count(t => (string)t.Tag == "order-customer-edit"));

            grid.SelectedItem = orders.Single(o => o.OrderId == "o-2"); Drain();
            StringAssert.Contains(SectionText(), "Kayıtlı müşteri iletişim verisi yok");
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
        var root = Path.Combine(Path.GetTempPath(), "order-customer-ui-" + Guid.NewGuid().ToString("N"));
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
