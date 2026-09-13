using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #814 in the real shell: an error published through the window's own Log overload becomes a toast in the host,
// a repeat shows a count instead of a second toast, the text on screen has been sanitized, the close button
// dismisses, and Escape with the keyboard inside the host dismisses the newest one.
[TestClass]
public sealed class NotificationWindowTests
{
    [TestMethod]
    public void AnErrorBecomesAToastThatDedupesSanitizesAndDismissesByMouseAndKeyboard()
    {
        var root = Path.Combine(Path.GetTempPath(), "toast-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow(root); window.Show(); Drain(window);
                var host = (StackPanel)window.FindName("ToastHost");
                Assert.AreEqual(Visibility.Collapsed, host.Visibility, "No toast, no host.");

                Log(window, "Yazma başarısız · Authorization: Bearer abc.def · ali@example.com", NotificationSeverity.Error); Drain(window);

                Assert.AreEqual(Visibility.Visible, host.Visibility);
                Assert.AreEqual(1, host.Children.Count);
                var name = AutomationProperties.GetName(host.Children[0]);
                StringAssert.Contains(name, "Hata");
                Assert.IsFalse(name.Contains("abc.def") || name.Contains("ali@example.com"), $"The screen never shows the secret or the address: {name}");

                Log(window, "Yazma başarısız · Authorization: Bearer abc.def · ali@example.com", NotificationSeverity.Error); Drain(window);
                Assert.AreEqual(1, host.Children.Count, "The same error twice is one toast.");
                StringAssert.Contains(AutomationProperties.GetName(host.Children[0]), "2 kez");

                Log(window, "Kaydedildi", NotificationSeverity.Success); Drain(window);
                Assert.AreEqual(2, host.Children.Count);

                // Keyboard: focus inside the host, Escape closes the newest (the success), the error stays.
                var newestClose = FindButtons((Border)host.Children[1]).First(b => b.Content?.ToString() == "✕");
                Keyboard.Focus(newestClose); Drain(window);
                Assert.IsTrue(host.IsKeyboardFocusWithin);
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                newestClose.RaiseEvent(escape); Drain(window);
                Assert.IsTrue(escape.Handled, "Escape inside the host is the toast's to handle.");
                Assert.AreEqual(1, host.Children.Count);
                StringAssert.Contains(AutomationProperties.GetName(host.Children[0]), "Hata", "The persistent error is what remains.");

                // Mouse: the close button on the error dismisses it and the host goes away.
                var close = FindButtons((Border)host.Children[0]).First(b => b.Content?.ToString() == "✕");
                Assert.IsTrue(close.Focusable, "The close button is reachable without a mouse.");
                close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                Assert.AreEqual(0, host.Children.Count);
                Assert.AreEqual(Visibility.Collapsed, host.Visibility);

                window.Close(); Drain(window);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    try { Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    static void Log(MainWindow window, string text, NotificationSeverity severity) =>
        typeof(MainWindow).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(string), typeof(NotificationSeverity) })!.Invoke(window, new object[] { text, severity });

    static System.Collections.Generic.IEnumerable<Button> FindButtons(DependencyObject node)
    {
        if (node is Button b) yield return b;
        var count = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++)
            foreach (var found in FindButtons(System.Windows.Media.VisualTreeHelper.GetChild(node, i))) yield return found;
    }

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
