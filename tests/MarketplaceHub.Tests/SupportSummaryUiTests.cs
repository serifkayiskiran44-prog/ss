using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #884 on real surfaces: the error banner offers "Destek özetini kopyala" — one click puts the redacted summary on
// the clipboard and the button says so; when the clipboard is unavailable the text appears inside the banner for a
// manual copy. The diagnostics page copies the last recorded failure with its chain, or the selected audit row, the
// same way, and shows the text on the page when the clipboard is unavailable. Nothing copied carries an e-mail, a
// secret or a stack frame.
[TestClass]
public sealed class SupportSummaryUiTests
{
    [TestMethod]
    public void TheBannerAndTheDiagnosticsPageCopyRedactedSummariesAndFallBackWhenTheClipboardIsUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "support-summary-window-" + Guid.NewGuid().ToString("N"));
        var original = SafeClipboard.Setter;
        RunSta(() =>
        {
            Window plain = null; MainWindow window = null;
            try
            {
                string received = null;
                SafeClipboard.Setter = t => received = t;

                // 1. The banner: copy, the confirmation on the button, no personal data or secret in the text; then the clipboard fails and the text is shown in the banner.
                var host = new StackPanel();
                plain = new Window { Content = host, Width = 900, Height = 300, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
                plain.Show(); plain.UpdateLayout();
                var surface = new ErrorSurface(host, _ => { });
                using (UiActivity.Enter("ImportAsync", "chain-ui0001"))
                    surface.Show(new InvalidOperationException("Sipariş 1001 ali@example.com token=abc123"), sourceRoute: "xml", sourceLabel: "XML kaynağı");
                var banner = host.Children.OfType<Border>().Single();
                var copy = Descendants(banner).OfType<Button>().Single(b => AutomationProperties.GetName(b) == SupportSummary.CopyName);
                copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); plain.UpdateLayout();
                Assert.IsNotNull(received, "the summary reached the clipboard");
                StringAssert.Contains(received, SupportSummary.Header); StringAssert.Contains(received, "İşlem yapılamadı"); StringAssert.Contains(received, "InvalidOperationException"); StringAssert.Contains(received, "Korelasyon: chain-ui0001"); StringAssert.Contains(received, "Ekran: xml");
                Assert.IsFalse(received.Contains("example.com", StringComparison.Ordinal)); Assert.IsFalse(received.Contains("abc123", StringComparison.Ordinal)); Assert.IsFalse(received.Contains("   at ", StringComparison.Ordinal));
                StringAssert.Contains(copy.Content as string ?? "", "Kopyalandı");
                Assert.IsFalse(Descendants(banner).OfType<TextBox>().Any(t => t.Tag as string == "support-summary-fallback"), "no fallback while the clipboard works");
                SafeClipboard.Setter = _ => throw new COMException("OpenClipboard failed");
                copy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); plain.UpdateLayout();
                var fallback = Descendants(banner).OfType<TextBox>().Single(t => t.Tag as string == "support-summary-fallback");
                StringAssert.StartsWith(fallback.Text, SupportSummary.ClipboardUnavailable); StringAssert.Contains(fallback.Text, "İşlem yapılamadı"); Assert.IsFalse(fallback.Text.Contains("example.com", StringComparison.Ordinal)); Assert.IsTrue(fallback.IsReadOnly);
                plain.Close(); plain = null;

                // 2. The diagnostics page: the last failure with its chain, then the selected row; the fallback on the page when the clipboard is unavailable.
                Directory.CreateDirectory(root);
                var audit = new AuditStore(root);
                audit.Append(new AuditEvent { Module = "import", Action = "run", Marketplace = "etsy", ShopId = "S1", Outcome = "OK", Detail = "Kaynak okundu.", Correlation = "chain-dx0001" });
                audit.Append(new AuditEvent { Module = "order", Action = "ship", Marketplace = "etsy", ShopId = "S1", OrderId = "1001", Outcome = "Failed", Detail = "Kargo başarısız; müşteri 0532 123 45 67 password=gizli", Correlation = "chain-dx0001" });
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                received = null; SafeClipboard.Setter = t => received = t;
                window = new MainWindow(root); window.Show(); Navigate(window, "diagnostics"); Drain(window);
                var routes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var page = (DependencyObject)routes["diagnostics"].Content;
                Descendants(page).OfType<Button>().First(b => b.Tag as string == "support-copy-last").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                Assert.IsNotNull(received); StringAssert.Contains(received, "Kayıt: order/ship · Failed"); StringAssert.Contains(received, "Korelasyon: chain-dx0001"); StringAssert.Contains(received, "import/run OK");
                Assert.IsFalse(received.Contains("0532", StringComparison.Ordinal)); Assert.IsFalse(received.Contains("gizli", StringComparison.Ordinal)); StringAssert.Contains(received, "[pii-phone]");
                var status = Descendants(page).OfType<TextBlock>().First(t => t.Text == SupportSummary.Copied);
                Assert.IsNotNull(status);
                var grid = Descendants(page).OfType<DataGrid>().First(g => !g.AutoGenerateColumns);
                grid.SelectedItem = ((IEnumerable<AuditEvent>)grid.ItemsSource).First(r => r.Action == "run"); Drain(window);
                received = null;
                Descendants(page).OfType<Button>().First(b => b.Tag as string == "support-copy-selected").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                Assert.IsNotNull(received); StringAssert.Contains(received, "Kayıt: import/run · OK");
                var pageFallback = Descendants(page).OfType<TextBox>().First(t => t.Tag as string == "support-summary-fallback");
                Assert.AreEqual(Visibility.Collapsed, pageFallback.Visibility);
                SafeClipboard.Setter = _ => throw new COMException("OpenClipboard failed");
                Descendants(page).OfType<Button>().First(b => b.Tag as string == "support-copy-last").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                Assert.AreEqual(Visibility.Visible, pageFallback.Visibility); StringAssert.Contains(pageFallback.Text, "Kayıt: order/ship · Failed"); Assert.IsFalse(pageFallback.Text.Contains("gizli", StringComparison.Ordinal));
                Assert.IsTrue(Descendants(page).OfType<TextBlock>().Any(t => t.Text == SupportSummary.ClipboardUnavailable));
            }
            finally
            {
                SafeClipboard.Setter = original;
                try { plain?.Close(); } catch (Exception) { }
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
