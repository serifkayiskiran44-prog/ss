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

// #876 on the real main window: a preference store file that is not a database does not stop the start-up (it is
// set aside and recreated); corrupt records — a malformed layout, an unknown density, a route that does not exist,
// a split that is not a number — open the window on the defaults while a partial sidebar record keeps its valid
// field; the diagnostics page lists the fallbacks by key without a payload and offers a reset that, confirmed,
// removes every preference record; a restart after the reset opens on the defaults with nothing to report.
[TestClass]
public sealed class PreferenceRecoveryUiTests
{
    [TestMethod]
    public void AGarbageStoreAndCorruptRecordsOpenTheWindowOnDefaultsAndTheDiagnosticsPageResetsThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "preference-recovery-window-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null; MainWindow second = null; MainWindow third = null;
            try
            {
                Directory.CreateDirectory(root);
                PreferenceSchema.ClearDiagnostics();

                // 1. The store file is not a database: the window still opens; the file is set aside and named without its bytes.
                File.WriteAllText(Path.Combine(root, PreferenceSchema.StoreFileName), "garbage LEAKMARKER1");
                window = new MainWindow(root); window.Show(); Drain(window);
                Assert.AreEqual(1, Directory.GetFiles(root, "ui-preferences.corrupt-*.db").Length, "the unreadable file was set aside");
                Assert.IsTrue(PreferenceSchema.Diagnostics.Any(d => d.StartsWith(PreferenceSchema.StoreFileName + ":", StringComparison.Ordinal) && !d.Contains("LEAKMARKER", StringComparison.Ordinal)), string.Join(" | ", PreferenceSchema.Diagnostics));
                window.Close(); Drain(window); window = null;

                // 2. Corrupt records in a healthy store: the defaults, diagnostics without payloads, a reset from the diagnostics page.
                PreferenceSchema.ClearDiagnostics();
                var store = new UiPreferenceStore(root);
                store.Set("layout:products", PreferenceSchema.Wrap(1, "{\"Version\":1,\"Columns\":[{\"Key\":\"ali@example.com LEAKMARKER2\""));
                store.Set("density:products", PreferenceSchema.Wrap(1, "purple-haze LEAKMARKER3"));
                store.Set("last-route", PreferenceSchema.Wrap(1, "route-from-nowhere LEAKMARKER4"));
                store.Set("shell:sidebar", PreferenceSchema.Wrap(1, "{\"Collapsed\":true}"));
                store.Set(OrderWorkspaceLayout.SplitPreferenceKey, PreferenceSchema.Wrap(1, "not-a-number LEAKMARKER5"));
                second = new MainWindow(root); second.Show(); Drain(second);
                var routes = Routes(second);
                Assert.AreSame(routes["dashboard"], ((TabControl)second.FindName("ModuleTabs")).SelectedItem, "an unknown last route falls back to the default page");
                Assert.AreEqual(NavigationSidebar.CollapsedWidth, ((ColumnDefinition)second.FindName("SidebarColumn")).ActualWidth, 0.5, "the partial sidebar record keeps its valid field");
                Navigate(second, "products"); Drain(second);
                var density = (ComboBox)typeof(MainWindow).GetField("productDensityBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(second)!;
                Assert.AreEqual(ProductListDensity.Label(ProductListDensity.Comfortable), density.SelectedItem?.ToString(), "an unknown density falls back");
                foreach (var key in new[] { "layout:products", "density:products", "last-route", OrderWorkspaceLayout.SplitPreferenceKey })
                    Assert.IsTrue(PreferenceSchema.Diagnostics.Any(d => d.StartsWith(key + ":", StringComparison.Ordinal)), key + " missing in: " + string.Join(" | ", PreferenceSchema.Diagnostics));
                Assert.IsFalse(PreferenceSchema.Diagnostics.Any(d => d.Contains("LEAKMARKER", StringComparison.Ordinal)), "no payload in the diagnostics");
                Assert.IsFalse(PreferenceSchema.Diagnostics.Any(d => d.StartsWith("shell:sidebar:", StringComparison.Ordinal)), "a partial record is not a fallback");

                Navigate(second, "diagnostics"); Drain(second);
                var page = (DependencyObject)routes["diagnostics"].Content;
                var checks = Descendants(page).OfType<DataGrid>().First(g => g.AutoGenerateColumns);
                var check = ((IEnumerable<DiagnosticCheck>)checks.ItemsSource).Single(c => c.Name == PreferenceSchema.DiagnosticName);
                Assert.AreEqual("WARN", check.Status); StringAssert.Contains(check.Detail, "layout:products"); Assert.IsFalse(check.Detail.Contains("LEAKMARKER", StringComparison.Ordinal));
                var reset = Descendants(page).OfType<Button>().First(b => b.Content as string == PreferenceSchema.ResetTitle);
                var failure = DriveModal(second, () => reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), new Func<Window, bool>[]
                {
                    dialog =>
                    {
                        var confirm = Descendants(dialog).OfType<Button>().FirstOrDefault(b => b.Content as string == PreferenceSchema.ResetLabel);
                        if (confirm is null) return false;
                        confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return true;
                    }
                });
                if (failure is not null) throw failure;
                Drain(second);
                Assert.AreEqual(0, store.Keys().Count, "the reset removed every preference record");
                Assert.IsTrue(Descendants(page).OfType<TextBlock>().Any(t => t.Text.Contains("tercih kaydı silindi", StringComparison.Ordinal)), "the page reports the reset");
                Assert.AreEqual("OK", ((IEnumerable<DiagnosticCheck>)checks.ItemsSource).Single(c => c.Name == PreferenceSchema.DiagnosticName).Status, "the check is refreshed after the reset");
                second.Close(); Drain(second); second = null;

                // 3. A restart after the reset: the defaults everywhere, nothing to report.
                third = new MainWindow(root); third.Show(); Drain(third);
                Assert.AreSame(Routes(third)["dashboard"], ((TabControl)third.FindName("ModuleTabs")).SelectedItem);
                Assert.IsTrue(((ColumnDefinition)third.FindName("SidebarColumn")).ActualWidth > NavigationSidebar.CollapsedWidth + 1, "the sidebar is back to its expanded default");
                Assert.AreEqual(0, PreferenceSchema.Diagnostics.Count, string.Join(" | ", PreferenceSchema.Diagnostics));
            }
            finally
            {
                foreach (var w in new[] { window, second, third }) { try { w?.Close(); if (w is not null) Drain(w); } catch (Exception) { } }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    static Dictionary<string, TabItem> Routes(MainWindow window) => (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

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
