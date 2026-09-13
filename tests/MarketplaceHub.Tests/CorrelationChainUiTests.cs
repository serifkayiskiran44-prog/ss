using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #883 on the real main window: a correlation deep link opens the audit centre on that chain — the grid shows only
// the chain's events of stores this session may open, the summary line names the count, modules and outcome, an
// entity link opens the order; a link scoped to a store this session may not open is refused before any screen
// changes; a missing correlation opens the centre with the note; the correlation box does the same by hand and a
// chain of another store shows the hidden count instead of its rows.
[TestClass]
public sealed class CorrelationChainUiTests
{
    [TestMethod]
    public void ACorrelationLinkOpensTheScopedChainAndRefusesTheWrongStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "correlation-window-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                new MarketplaceConnectionStore(root).Save("etsy", "S1", "Etsy S1", enabled: true);
                var audit = new AuditStore(root);
                audit.Append(new AuditEvent { Module = "import", Action = "run", Marketplace = "etsy", ShopId = "S1", Outcome = "OK", Detail = "Kaynak okundu.", Correlation = "chain-aaaa01" });
                audit.Append(new AuditEvent { Module = "job", Action = "enqueue", Marketplace = "etsy", ShopId = "S1", Outcome = "OK", Detail = "İş kuyruğa alındı.", Correlation = "chain-aaaa01" });
                audit.Append(new AuditEvent { Module = "order", Action = "ship", Marketplace = "etsy", ShopId = "S1", OrderId = "1001", Outcome = "WARN", Detail = "Kargo bekliyor. ali@example.com", Correlation = "chain-aaaa01" });
                audit.Append(new AuditEvent { Module = "order", Action = "note", Marketplace = "ebay", ShopId = "E1", OrderId = "2001", Outcome = "OK", Detail = "Başka mağaza.", Correlation = "chain-bbbb02" });
                audit.Append(new AuditEvent { Module = "system", Action = "other", Outcome = "OK", Detail = "İlgisiz.", Correlation = "chain-cccc03" });
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show(); Drain(window);
                var routes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var tabs = (TabControl)window.FindName("ModuleTabs");
                var open = typeof(MainWindow).GetMethod("OpenWorkspaceLink", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var titleFor = (Func<string, string>)(_ => "Tanılama / audit");

                // 1. The link opens the audit centre on the chain: the grid holds the chain's three events, the summary names them, the order link is offered.
                var target = WorkspaceLinks.Parse(CorrelationChain.Link("chain-aaaa01", "etsy|S1"), titleFor)!;
                Assert.IsTrue((bool)open.Invoke(window, new object[] { target })!, "the link is allowed"); Drain(window);
                Assert.AreSame(routes["diagnostics"], tabs.SelectedItem);
                var page = (DependencyObject)routes["diagnostics"].Content;
                var grid = Descendants(page).OfType<DataGrid>().First(g => !g.AutoGenerateColumns);
                var rows = ((IEnumerable<AuditEvent>)grid.ItemsSource).ToList();
                Assert.AreEqual(3, rows.Count); Assert.IsTrue(rows.All(r => r.Correlation == "chain-aaaa01"));
                var summary = Descendants(page).OfType<TextBlock>().First(t => t.Tag as string == "correlation-summary");
                StringAssert.Contains(summary.Text, "3 olay"); StringAssert.Contains(summary.Text, "uyarı"); Assert.IsFalse(summary.Text.Contains("example.com", StringComparison.Ordinal));
                var box = Descendants(page).OfType<TextBox>().First(t => t.Tag as string == "correlation-box");
                Assert.AreEqual("chain-aaaa01", box.Text);
                var orderLink = Descendants(page).OfType<Button>().FirstOrDefault(b => b.Tag as string == "correlation-entity" && (b.Content as string ?? "").Contains("1001", StringComparison.Ordinal));
                Assert.IsNotNull(orderLink, "the order event offers its link");

                // 2. A link scoped to a store this session may not open is refused before any screen changes.
                Navigate(window, "dashboard"); Drain(window);
                var wrong = WorkspaceLinks.Parse(CorrelationChain.Link("chain-bbbb02", "ebay|E1"), titleFor)!;
                Assert.IsFalse((bool)open.Invoke(window, new object[] { wrong })!, "a store this session may not open is refused"); Drain(window);
                Assert.AreSame(routes["dashboard"], tabs.SelectedItem);

                // 3. A missing correlation opens the centre with the note and an empty grid.
                var missing = WorkspaceLinks.Parse(CorrelationChain.Link("chain-none01"), titleFor)!;
                Assert.IsTrue((bool)open.Invoke(window, new object[] { missing })!); Drain(window);
                Assert.AreSame(routes["diagnostics"], tabs.SelectedItem);
                Assert.AreEqual(0, ((IEnumerable<AuditEvent>)grid.ItemsSource).Count()); StringAssert.Contains(summary.Text, "kayıt yok");

                // 4. The box by hand: the other store's chain shows its hidden count instead of its rows; the unscoped chain shows its row; clearing restores the audit list.
                box.Text = "chain-bbbb02"; Descendants(page).OfType<Button>().First(b => b.Tag as string == "correlation-open").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                Assert.AreEqual(0, ((IEnumerable<AuditEvent>)grid.ItemsSource).Count()); StringAssert.Contains(summary.Text, "1 kayıt");
                box.Text = "chain-cccc03"; Descendants(page).OfType<Button>().First(b => b.Tag as string == "correlation-open").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                Assert.AreEqual(1, ((IEnumerable<AuditEvent>)grid.ItemsSource).Count()); StringAssert.Contains(summary.Text, "1 olay");
                Descendants(page).OfType<Button>().First(b => b.Tag as string == "correlation-clear").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                Assert.IsTrue(((IEnumerable<AuditEvent>)grid.ItemsSource).Count() >= 5, "the full audit list is back"); Assert.AreEqual("", box.Text);
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
