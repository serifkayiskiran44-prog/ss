using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #877 on the real main window: a geometry remembered on a monitor that is gone opens the window exactly where the
// fit rule puts it on the monitors this machine has (never off-screen); a close while minimized saves the normal
// bounds; a corrupt record leaves the start-up placement alone and is a diagnostic naming the key; a maximized
// close comes back maximized after a restart.
[TestClass]
public sealed class WindowGeometryUiTests
{
    [TestMethod]
    public void TheWindowRestoresASavedGeometryFittedToTheScreenAndSavesOnCloseEvenWhenMinimized()
    {
        var root = Path.Combine(Path.GetTempPath(), "window-geometry-window-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null; MainWindow second = null; MainWindow third = null;
            try
            {
                Directory.CreateDirectory(root);
                PreferenceSchema.ClearDiagnostics();
                var store = new UiPreferenceStore(root);
                var monitors = WindowGeometry.CurrentMonitors();
                Assert.IsTrue(monitors.Count >= 1, "at least one monitor is known");
                var farRight = monitors.Max(m => m.WorkArea.Right) + 3000;

                // 1. A geometry saved on a monitor that no longer exists: the window opens where the fit rule puts it on the monitors that exist.
                var saved = new WindowGeometryState(new WindowBounds(farRight, 40, 1440, 900), false);
                PreferenceSchema.Write(store, WindowGeometry.PreferenceKey, WindowGeometry.Serialize(saved));
                window = new MainWindow(root); window.Show(); Drain(window);
                var expected = WindowGeometry.Fit(saved, monitors, window.MinWidth, window.MinHeight, 1440, 900);
                Assert.AreEqual(WindowRestoreOutcome.Fitted, expected.Outcome);
                Assert.AreEqual(WindowStartupLocation.Manual, window.WindowStartupLocation);
                Assert.AreEqual(expected.Bounds.Left, window.Left, 1); Assert.AreEqual(expected.Bounds.Top, window.Top, 1);
                Assert.AreEqual(expected.Bounds.Width, window.ActualWidth, 1); Assert.AreEqual(expected.Bounds.Height, window.ActualHeight, 1);
                Assert.IsTrue(window.Left < farRight - 3000, "the window is not off-screen");

                // 2. A close while minimized saves the normal bounds and never a minimized state.
                window.WindowState = WindowState.Minimized; Drain(window);
                window.Close(); Drain(window); window = null;
                Assert.IsTrue(WindowGeometry.TryParse(PreferenceSchema.Read(store, WindowGeometry.PreferenceKey), out var afterClose));
                Assert.IsFalse(afterClose.Maximized);
                Assert.AreEqual(expected.Bounds.Left, afterClose.Bounds.Left, 1); Assert.AreEqual(expected.Bounds.Top, afterClose.Bounds.Top, 1);
                Assert.AreEqual(expected.Bounds.Width, afterClose.Bounds.Width, 1); Assert.AreEqual(expected.Bounds.Height, afterClose.Bounds.Height, 1);

                // 3. A corrupt record: the start-up placement stands and the diagnostics name the key, not the payload.
                PreferenceSchema.ClearDiagnostics();
                store.Set(WindowGeometry.PreferenceKey, PreferenceSchema.Wrap(1, "{\"Left\":\"NaN\",\"Top\":-1e309 LEAKMARKER"));
                second = new MainWindow(root); second.Show(); Drain(second);
                Assert.AreEqual(WindowStartupLocation.CenterScreen, second.WindowStartupLocation, "a corrupt record leaves the default placement");
                Assert.AreEqual(WindowState.Normal, second.WindowState);
                Assert.IsTrue(PreferenceSchema.Diagnostics.Any(d => d.StartsWith(WindowGeometry.PreferenceKey + ":", StringComparison.Ordinal) && !d.Contains("LEAKMARKER", StringComparison.Ordinal)), string.Join(" | ", PreferenceSchema.Diagnostics));

                // 4. A maximized close keeps the flag with the normal bounds; a restart comes back maximized.
                second.WindowState = WindowState.Maximized; Drain(second);
                second.Close(); Drain(second); second = null;
                Assert.IsTrue(WindowGeometry.TryParse(PreferenceSchema.Read(store, WindowGeometry.PreferenceKey), out var maximized)); Assert.IsTrue(maximized.Maximized);
                third = new MainWindow(root); third.Show(); Drain(third);
                Assert.AreEqual(WindowState.Maximized, third.WindowState, "the restart comes back maximized");
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

    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
