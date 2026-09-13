using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #885 on real windows: the overlay is closed while nothing is in, opens with the headline while an owner is in,
// shows its cancel only for a cancellable owner and that cancel runs the owner's cancel action alone, Escape does the
// same, and it closes when the last owner leaves. On the real shell, a guarded operation opens the overlay, a
// completion, an exception and a cancellation all close it, a second operation while one runs is refused as before,
// and rapid navigation while busy leaves the overlay's state untouched.
[TestClass]
public sealed class BusyOverlayUiTests
{
    [TestMethod]
    public void TheOverlayFollowsTheBusyStateAndTheShellAlwaysClosesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "busy-overlay-window-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            Window plain = null; MainWindow window = null;
            try
            {
                // 1. A plain window with the overlay over a button: closed, open with the headline, cancel only when cancellable and only cancelling, closed again.
                var state = new BusyState();
                var beneath = new Button { Content = "Sil", Height = 40, VerticalAlignment = VerticalAlignment.Top }; var clicks = 0; beneath.Click += (_, _) => clicks++;
                var layers = new Grid(); layers.Children.Add(beneath); var overlay = BusyOverlay.Create(state); layers.Children.Add(overlay);
                plain = new Window { Content = layers, Width = 700, Height = 300, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
                plain.Show(); plain.UpdateLayout();
                Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);
                var cancelled = 0;
                var token = state.Enter("shell", "Ürünler içe aktarılıyor… ali@example.com");
                plain.UpdateLayout();
                Assert.AreEqual(Visibility.Visible, overlay.Visibility);
                var label = Descendants(overlay).OfType<TextBlock>().First(t => t.Tag as string == BusyOverlay.LabelTag);
                StringAssert.StartsWith(label.Text, "Ürünler içe aktarılıyor…"); Assert.IsFalse(label.Text.Contains("example.com", StringComparison.Ordinal));
                var cancel = Descendants(overlay).OfType<Button>().Single(b => b.Tag as string == BusyOverlay.CancelTag);
                Assert.AreEqual(Visibility.Collapsed, cancel.Visibility, "no cancel for an owner that cannot be stopped");
                Assert.AreEqual(1, Descendants(overlay).OfType<ButtonBase>().Count(), "the overlay has exactly one action");
                var cancellable = state.Enter("import", "XML okunuyor…", () => cancelled++);
                plain.UpdateLayout(); Assert.AreEqual(Visibility.Visible, cancel.Visibility); StringAssert.StartsWith(label.Text, "2 işlem sürüyor");
                cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); plain.UpdateLayout();
                Assert.AreEqual(1, cancelled, "the cancel action ran once"); Assert.AreEqual(0, clicks, "nothing beneath the overlay was reached");
                Assert.AreEqual(Visibility.Collapsed, cancel.Visibility, "a cancelled owner offers no second cancel");
                cancellable.Dispose(); plain.UpdateLayout(); Assert.AreEqual(Visibility.Visible, overlay.Visibility, "the other owner is still in");
                token.Dispose(); plain.UpdateLayout(); Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "the last owner leaving closes the overlay");
                using (state.Enter("import", "yeniden", () => cancelled++))
                {
                    plain.UpdateLayout(); overlay.Focus();
                    overlay.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(plain), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    Assert.AreEqual(2, cancelled, "Escape cancels the cancellable owner");
                }
                plain.UpdateLayout(); Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);
                plain.Close(); plain = null;

                // 2. The real shell: a guarded operation opens the overlay; completion, an exception and a cancellation close it; a second operation is refused while one runs; navigation while busy changes nothing.
                Directory.CreateDirectory(root);
                window = new MainWindow(root); window.Show(); Drain(window);
                var shellOverlay = Descendants(window).OfType<Grid>().Single(g => g.Tag as string == BusyOverlay.Tag);
                Assert.AreEqual(Visibility.Collapsed, shellOverlay.Visibility);
                var run = typeof(MainWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var gateSource = new TaskCompletionSource<bool>();
                var running = (Task)run.Invoke(window, new object[] { new Func<Task>(() => gateSource.Task) })!;
                Drain(window);
                Assert.AreEqual(Visibility.Visible, shellOverlay.Visibility, "a guarded operation opens the overlay");
                var refused = (Task)run.Invoke(window, new object[] { new Func<Task>(() => Task.CompletedTask) })!;
                WaitUntil(window, () => refused.IsCompleted, "the refused second operation");
                Assert.AreEqual(Visibility.Visible, shellOverlay.Visibility, "the refused operation did not touch the overlay");
                Navigate(window, "orders"); Drain(window); Navigate(window, "dashboard"); Drain(window);
                Assert.AreEqual(Visibility.Visible, shellOverlay.Visibility, "navigation while busy leaves the overlay to its owner");
                gateSource.SetResult(true);
                WaitUntil(window, () => running.IsCompleted, "the operation"); Drain(window);
                Assert.AreEqual(Visibility.Collapsed, shellOverlay.Visibility, "completion closes the overlay");
                var failing = (Task)run.Invoke(window, new object[] { new Func<Task>(() => throw new InvalidOperationException("patladı")) })!;
                WaitUntil(window, () => failing.IsCompleted, "the failing operation"); Drain(window);
                Assert.AreEqual(Visibility.Collapsed, shellOverlay.Visibility, "an exception closes the overlay");
                var cancelSource = new CancellationTokenSource();
                var cancelling = (Task)run.Invoke(window, new object[] { new Func<Task>(() => Task.Delay(Timeout.Infinite, cancelSource.Token)) })!;
                Drain(window); Assert.AreEqual(Visibility.Visible, shellOverlay.Visibility);
                cancelSource.Cancel();
                WaitUntil(window, () => cancelling.IsCompleted, "the cancelled operation"); Drain(window);
                Assert.AreEqual(Visibility.Collapsed, shellOverlay.Visibility, "a cancellation closes the overlay");
            }
            finally
            {
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

    static void WaitUntil(Window window, Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++) { Drain(window); if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
    }

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
