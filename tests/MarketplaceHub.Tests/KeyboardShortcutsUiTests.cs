using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #869 on the real main window: the controls that have a shortcut say so in their tooltip, the product grid's key
// bindings come from the same catalogue as the key handler, a shortcut for a disabled command does nothing, the
// number keys navigate, and F1 opens a searchable reference whose filter box already has the keyboard, filters with
// the Turkish-safe fold, and closes on Escape — all without a mouse.
[TestClass]
public sealed class KeyboardShortcutsUiTests
{
    [TestMethod]
    public void HintsBindingsDisabledCommandsAndTheReferenceAllReadTheCatalogue()
    {
        var root = Path.Combine(Path.GetTempPath(), "shortcuts-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                window = new MainWindow(root); window.Show(); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");
                var routes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

                // Hints on the real controls.
                var back = (Button)window.FindName("BackButton"); StringAssert.Contains(back.ToolTip?.ToString() ?? "", "Alt+←");
                StringAssert.Contains(((TextBox)window.FindName("GlobalSearchBox")).ToolTip?.ToString() ?? "", "Ctrl+K");
                StringAssert.Contains(((Button)window.FindName("SidebarToggle")).ToolTip?.ToString() ?? "", "Ctrl+B");
                var help = (Button)window.FindName("ShortcutsButton"); StringAssert.Contains(help.ToolTip?.ToString() ?? "", "F1"); Assert.IsTrue(AutomationProperties.GetName(help).Length > 0, "the reference button has a name");

                // The product grid's bindings are the catalogue's products-scope entries.
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                foreach (var shortcut in KeyboardShortcuts.Catalogue.Where(s => s.Scope == ShortcutScope.Products && s.CommandKey != "close-inspect"))
                    Assert.IsTrue(grid.InputBindings.OfType<KeyBinding>().Any(b => b.Key == shortcut.Key && b.Modifiers == shortcut.Modifiers), $"{shortcut.CommandKey} is bound on the product grid");

                // A disabled command does nothing: Back with an empty trail keeps the page.
                Navigate(window, "dashboard"); Drain(window); Assert.IsFalse(back.IsEnabled);
                Assert.IsFalse(window.HandleShortcut(Key.Left, ModifierKeys.Alt), "a disabled command is not handled"); Drain(window); Assert.AreSame(routes["dashboard"], tabs.SelectedItem);
                // The number keys navigate.
                Assert.IsTrue(window.HandleShortcut(Key.D2, ModifierKeys.Control)); Drain(window); Assert.AreSame(routes["products"], tabs.SelectedItem);
                Assert.IsTrue(window.HandleShortcut(Key.NumPad1, ModifierKeys.Control)); Drain(window); Assert.AreSame(routes["dashboard"], tabs.SelectedItem);
                Assert.IsFalse(window.HandleShortcut(Key.Delete, ModifierKeys.None), "no shortcut deletes anything");

                // F1 opens the reference: the filter box has the keyboard, "geri" leaves the Back entry, an ASCII I finds the dotless ı, Escape closes.
                TextBox? filter = null; ItemsControl? list = null;
                var failure = DriveModal(window, () => Assert.IsTrue(window.HandleShortcut(Key.F1, ModifierKeys.None)), new Func<Window, bool>[]
                {
                    d =>
                    {
                        Assert.AreEqual("Klavye kısayolları", d.Title);
                        filter ??= Descendants(d).OfType<TextBox>().FirstOrDefault(t => (t.Tag as string) == "shortcut-filter"); list ??= Descendants(d).OfType<ItemsControl>().FirstOrDefault(l => (l.Tag as string) == "shortcut-list");
                        return filter is not null && list is not null && filter.IsKeyboardFocused && list.Items.Count == KeyboardShortcuts.Catalogue.Count;
                    },
                    d => { filter!.Text = "geri"; d.UpdateLayout(); return list!.Items.Count == 1 && list.Items[0]!.ToString()!.Contains("Alt+←"); },
                    d => { filter!.Text = "KISAYOL"; d.UpdateLayout(); return list!.Items.Count >= 1 && list.Items.Cast<object>().Any(i => i.ToString()!.Contains("F1")); },
                    d => { filter!.Text = ""; d.UpdateLayout(); return list!.Items.Count == KeyboardShortcuts.Catalogue.Count; },
                    d =>
                    {
                        var source = PresentationSource.FromVisual(d)!;
                        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                        return true;
                    },
                });
                Assert.IsNull(failure, failure?.ToString());
                WaitUntil(window, () => !window.OwnedWindows.OfType<Window>().Any(w => w.IsVisible), "the reference to close on Escape");
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

    /// <summary>Drives the modal dialog <paramref name="open"/> shows: polls inside ShowDialog's nested loop until the owner has a visible owned window, runs the steps in order (a step returns true when done).</summary>
    static Exception? DriveModal(Window owner, Action open, IReadOnlyList<Func<Window, bool>> steps, int timeoutMs = 30000)
    {
        Exception? failure = null; Window? dialog = null; var index = 0; var deadline = Environment.TickCount64 + timeoutMs;
        void Pump()
        {
            if (failure is not null) return;
            try
            {
                if (Environment.TickCount64 > deadline) throw new TimeoutException($"Dialog step {index} did not complete in time.");
                dialog ??= owner.OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsVisible);
                if (dialog is not null)
                {
                    if (!dialog.IsVisible) { if (index < steps.Count) throw new AssertFailedException($"The dialog closed before step {index}."); return; }
                    if (index < steps.Count && steps[index](dialog)) index++;
                    if (index >= steps.Count) return;
                }
            }
            catch (Exception ex) { failure = ex; try { dialog?.Close(); } catch (InvalidOperationException) { } return; }
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { Thread.Sleep(25); Pump(); }));
        }
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Pump));
        open();
        return failure;
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
