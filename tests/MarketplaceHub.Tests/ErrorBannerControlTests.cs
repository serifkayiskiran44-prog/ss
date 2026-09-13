using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #816's rendered banner: the actions are real focusable buttons, retry runs the surface's own action, Escape
// inside the banner dismisses it, a repeat shows a count on the same banner, and a narrow width wraps the text
// and the buttons instead of clipping them.
[TestClass]
public sealed class ErrorBannerControlTests
{
    [TestMethod]
    public void TheBannerRetriesDismissesByKeyboardCountsRepeatsAndWrapsWhenNarrow()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            Window window = null;
            try
            {
                // A real, owned window: a raw HwndSource left behind by a finished thread crashes the test host
                // later, when another test pumps messages into its orphaned WndProc.
                var host = new StackPanel();
                window = new Window { Content = host, Width = 900, Height = 300, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
                window.Show(); window.UpdateLayout();
                var navigated = "";
                var surface = new ErrorSurface(host, route => navigated = route);
                var retries = 0;
                Func<Task> retry = () => { retries++; return Task.CompletedTask; };

                surface.Show(new HttpRequestException("first"), retry, sourceRoute: "connections", sourceLabel: "Bağlantıya git");
                Assert.AreEqual(Visibility.Visible, host.Visibility);
                var banner = (Border)host.Children.OfType<Border>().Single();
                var buttons = Buttons(banner);
                CollectionAssert.AreEqual(new[] { "Yeniden dene", "Kaynağa git", "Tanılamayı aç", "Uyarı bildirimini kapat" }, buttons.Select(AutomationProperties.GetName).ToArray());
                Assert.IsTrue(buttons.All(b => b.Focusable), "Every action is reachable without a mouse.");
                StringAssert.Contains(AutomationProperties.GetName(banner), "Uyarı");

                surface.Show(new HttpRequestException("second"), retry, sourceRoute: "connections", sourceLabel: "Bağlantıya git");
                Assert.AreEqual(1, host.Children.Count, "The same failure again is one banner.");
                StringAssert.Contains(AutomationProperties.GetName((Border)host.Children[0]), "2 kez");

                // Narrow versus wide: the same banner needs more height when it has less width, i.e. it wraps.
                var current = (Border)host.Children[0];
                current.Measure(new Size(900, double.PositiveInfinity)); var wide = current.DesiredSize.Height;
                current.Measure(new Size(260, double.PositiveInfinity)); var narrow = current.DesiredSize.Height;
                Assert.IsTrue(narrow > wide, $"At 260 DIP the banner must wrap (narrow {narrow} vs wide {wide}).");

                Buttons(current).First(b => AutomationProperties.GetName(b) == "Kaynağa git").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.AreEqual("connections", navigated, "Go-to-source navigates where the surface said the fix lives.");

                Buttons(current).First(b => AutomationProperties.GetName(b) == "Yeniden dene").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.AreEqual(1, retries, "Retry runs the surface's own action again.");
                Assert.AreEqual(0, host.Children.Count, "A retry clears the banner; a new failure will raise a new one.");

                surface.Show(new InvalidOperationException("Önce ürün seç."), retry);
                var terminal = (Border)host.Children.OfType<Border>().Single();
                Assert.IsFalse(Buttons(terminal).Any(b => AutomationProperties.GetName(b) == "Yeniden dene"), "A terminal failure offers no retry.");
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                Buttons(terminal).First().RaiseEvent(escape);
                Assert.IsTrue(escape.Handled, "Escape inside the banner is the banner's.");
                Assert.AreEqual(0, host.Children.Count);
                Assert.AreEqual(Visibility.Collapsed, host.Visibility);
            }
            catch (Exception ex) { failure = ex; }
            finally { try { window?.Close(); window?.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); } catch (Exception) { } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    static Button[] Buttons(DependencyObject node)
    {
        var found = new System.Collections.Generic.List<Button>();
        void Walk(DependencyObject n)
        {
            if (n is Button b) found.Add(b);
            if (n is Panel p) foreach (UIElement child in p.Children) Walk(child);
            else if (n is Decorator d && d.Child is not null) Walk(d.Child);
        }
        Walk(node);
        return found.ToArray();
    }
}
