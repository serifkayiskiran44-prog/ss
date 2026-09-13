using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #861 on the real main window: every shared control style names the one keyboard focus ring; a Tab key sent
// through the input pipeline moves keyboard focus and draws the ring adorner on the new element; focus given the
// way a mouse gives it draws no ring while the control still holds focus; a disabled control refuses focus; the
// ring's strokes are DIP values that survive the window's own scale.
[TestClass]
public sealed class FocusStylesUiTests
{
    [TestMethod]
    public void SharedStylesNameTheRingAndKeyboardFocusDrawsItWhileMouseFocusDoesNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "focus-styles-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                window = new MainWindow(root); window.Show(); window.Activate(); Drain(window);
                var ring = (Style)window.FindResource(FocusStyles.KeyboardFocusVisualKey);
                foreach (var type in new[] { typeof(Button), typeof(TextBox), typeof(PasswordBox), typeof(ComboBox), typeof(CheckBox), typeof(TabItem), typeof(DataGridRow), typeof(DataGridCell) })
                    Assert.AreSame(ring, Setter((Style)window.FindResource(type), FrameworkElement.FocusVisualStyleProperty), type.Name);
                Assert.AreSame(ring, Setter((Style)window.FindResource("NavigationItem"), FrameworkElement.FocusVisualStyleProperty));
                var search = (TextBox)window.FindName("GlobalSearchBox"); var searchButton = (Button)window.FindName("GlobalSearchButton");
                Assert.AreSame(ring, search.FocusVisualStyle); Assert.AreSame(ring, searchButton.FocusVisualStyle);

                // Keyboard: with the keyboard as the last input device, a Tab through the input pipeline moves focus and the ring adorner appears on the new element.
                // (A synthetic key event does not register as the last device -- only real input reports do -- so the test states it the way WPF reads it.)
                search.Focus(); Drain(window); Assert.IsTrue(search.IsKeyboardFocused);
                LastInput(Keyboard.PrimaryDevice); Tab(window); Drain(window);
                var focused = Keyboard.FocusedElement as FrameworkElement; Assert.IsNotNull(focused); Assert.AreNotSame(search, focused, "Tab moved the focus.");
                Assert.IsTrue(HasFocusRing(focused!), $"Keyboard focus draws the ring on {focused!.GetType().Name}; last device {InputManager.Current.MostRecentInputDevice?.GetType().Name ?? "none"}; adorners {string.Join(",", AdornerLayer.GetAdornerLayer(focused)?.GetAdorners(focused)?.Select(a => a.GetType().Name) ?? Array.Empty<string>())}; style {(focused.FocusVisualStyle is null ? "null" : "set")}.");

                // Mouse: with the mouse as the last input device, focus keeps the control focused but draws no ring.
                LastInput(System.Windows.Input.Mouse.PrimaryDevice);
                Assert.IsTrue(searchButton.Focus()); Drain(window);
                Assert.IsTrue(searchButton.IsKeyboardFocused); Assert.IsFalse(HasFocusRing(searchButton), "Mouse focus shows the control's own state, not the ring.");

                // Disabled: no focus at all.
                var back = (Button)window.FindName("BackButton"); Assert.IsFalse(back.IsEnabled); Assert.IsFalse(back.Focus());

                // DIP: the ring's strokes are token numbers regardless of the window's scale.
                var dpi = VisualTreeHelper.GetDpi(window); Assert.IsTrue(dpi.DpiScaleX > 0);
                Assert.AreEqual(DesignTokens.FocusRingThickness, ((System.Windows.Shapes.Rectangle)Descendants((FrameworkElement)((ControlTemplate)Setter(ring, Control.TemplateProperty)).LoadContent()).OfType<System.Windows.Shapes.Rectangle>().First()).StrokeThickness);
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

    static object Setter(Style style, DependencyProperty property) => style.Setters.OfType<Setter>().Single(s => s.Property == property).Value;

    static bool HasFocusRing(FrameworkElement element)
    {
        var layer = AdornerLayer.GetAdornerLayer(element);
        return layer?.GetAdorners(element)?.Any(a => a.GetType().Name.Contains("FocusVisualAdorner", StringComparison.Ordinal)) == true;
    }

    static void Tab(Window window)
    {
        var source = PresentationSource.FromVisual(window)!;
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Tab) { RoutedEvent = Keyboard.KeyDownEvent });
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Tab) { RoutedEvent = Keyboard.KeyUpEvent });
    }

    static void LastInput(InputDevice device) => typeof(InputManager).GetProperty("MostRecentInputDevice")!.GetSetMethod(nonPublic: true)!.Invoke(InputManager.Current, new object[] { device });

    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
        if (count == 0 && node is Panel panel) foreach (UIElement child in panel.Children) { yield return child; foreach (var d in Descendants(child)) yield return d; }
    }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
