using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #877 (UX STATE: Window geometry safe restore). A remembered window rectangle is fitted to the monitors that exist
// now: a window from a monitor that is gone comes back onto the primary one; a DPI change that shrinks the work
// area in DIPs shrinks the window to fit (never below its minimum) and keeps its title bar reachable; a window
// straddling two present monitors is left alone; a title bar above or mostly beside the screen is pulled back in;
// a maximized window comes back maximized on its monitor; a close while minimized saves the normal bounds and never
// a minimized state; nothing saved is the default, centred on the primary; corrupt values never parse and, read
// through the preference schema, are a diagnostic naming the key and not the payload.
[TestClass]
public sealed class WindowGeometryTests
{
    static readonly MonitorArea Primary = new(new WindowBounds(0, 0, 1920, 1040), true);
    static readonly MonitorArea Right = new(new WindowBounds(1920, 0, 1920, 1040), false);

    [TestMethod]
    public void SavedGeometryIsFittedToTheMonitorsThatExistAndCorruptValuesFallBack()
    {
        // Present monitor, fully visible: untouched. Straddling two present monitors: untouched.
        var visible = WindowGeometry.Fit(new WindowGeometryState(new WindowBounds(100, 50, 1440, 900), false), new[] { Primary, Right }, 1150, 760, 1440, 900);
        Assert.AreEqual(WindowRestoreOutcome.Restored, visible.Outcome); Assert.AreEqual(new WindowBounds(100, 50, 1440, 900), visible.Bounds);
        var straddling = WindowGeometry.Fit(new WindowGeometryState(new WindowBounds(1700, 50, 1440, 900), false), new[] { Primary, Right }, 1150, 760, 1440, 900);
        Assert.AreEqual(WindowRestoreOutcome.Restored, straddling.Outcome); Assert.AreEqual(1700, straddling.Bounds.Left);

        // Removed second monitor: the window that lived on the right comes back onto the primary at the same size, as far right as it fits.
        var orphaned = WindowGeometry.Fit(new WindowGeometryState(new WindowBounds(2100, 50, 1440, 900), false), new[] { Primary }, 1150, 760, 1440, 900);
        Assert.AreEqual(WindowRestoreOutcome.Fitted, orphaned.Outcome); Assert.AreEqual(new WindowBounds(480, 50, 1440, 900), orphaned.Bounds);

        // DPI change: a larger scale leaves a smaller work area in DIPs; the window shrinks to fit, never below its minimum, and its title bar stays at the top-left of the area.
        var scaled = WindowGeometry.Fit(new WindowGeometryState(new WindowBounds(100, 100, 1440, 900), false), new[] { new MonitorArea(new WindowBounds(0, 0, 1280, 680), true) }, 1150, 760, 1440, 900);
        Assert.AreEqual(WindowRestoreOutcome.Fitted, scaled.Outcome); Assert.AreEqual(new WindowBounds(0, 0, 1280, 760), scaled.Bounds);

        // A title bar above the screen is pulled down; a window mostly beyond the left edge is pulled in.
        var above = WindowGeometry.Fit(new WindowGeometryState(new WindowBounds(100, -300, 1440, 900), false), new[] { Primary }, 1150, 760, 1440, 900);
        Assert.AreEqual(WindowRestoreOutcome.Fitted, above.Outcome); Assert.AreEqual(0, above.Bounds.Top); Assert.AreEqual(100, above.Bounds.Left);
        var beside = WindowGeometry.Fit(new WindowGeometryState(new WindowBounds(-1350, 100, 1440, 900), false), new[] { Primary }, 1150, 760, 1440, 900);
        Assert.AreEqual(WindowRestoreOutcome.Fitted, beside.Outcome); Assert.AreEqual(0, beside.Bounds.Left); Assert.AreEqual(100, beside.Bounds.Top);
        // A window hanging off the right edge of the last monitor is pulled in; one whose title bar sits in the gap above a lower second monitor lies inside the monitors' bounding box yet is unreachable, so it is pulled down onto that monitor.
        var hanging = WindowGeometry.Fit(new WindowGeometryState(new WindowBounds(1760, 100, 1440, 900), false), new[] { Primary }, 1150, 760, 1440, 900);
        Assert.AreEqual(WindowRestoreOutcome.Fitted, hanging.Outcome); Assert.AreEqual(new WindowBounds(480, 100, 1440, 900), hanging.Bounds);
        var lower = new MonitorArea(new WindowBounds(1920, 500, 1920, 1040), false);
        var inGap = WindowGeometry.Fit(new WindowGeometryState(new WindowBounds(2000, 200, 1440, 900), false), new[] { Primary, lower }, 1150, 760, 1440, 900);
        Assert.AreEqual(WindowRestoreOutcome.Fitted, inGap.Outcome); Assert.AreEqual(new WindowBounds(2000, 500, 1440, 900), inGap.Bounds);

        // Maximized comes back maximized, its normal bounds placed on a monitor that exists.
        var maximized = WindowGeometry.Fit(new WindowGeometryState(new WindowBounds(2100, 50, 1440, 900), true), new[] { Primary }, 1150, 760, 1440, 900);
        Assert.IsTrue(maximized.Maximized); Assert.AreEqual(480, maximized.Bounds.Left);

        // Nothing saved, or no monitor known: the default, centred on the primary when there is one.
        var nothing = WindowGeometry.Fit(null, new[] { Primary }, 1150, 760, 1440, 900);
        Assert.AreEqual(WindowRestoreOutcome.Default, nothing.Outcome); Assert.AreEqual(new WindowBounds(240, 70, 1440, 900), nothing.Bounds); Assert.IsFalse(nothing.Maximized);
        Assert.AreEqual(WindowRestoreOutcome.Default, WindowGeometry.Fit(new WindowGeometryState(new WindowBounds(100, 50, 1440, 900), false), Array.Empty<MonitorArea>(), 1150, 760, 1440, 900).Outcome);

        // The record: a round trip, the flag optional, and corrupt values refused.
        var text = WindowGeometry.Serialize(new WindowGeometryState(new WindowBounds(100.5, 50, 1440, 900), true));
        Assert.IsTrue(WindowGeometry.TryParse(text, out var back)); Assert.AreEqual(new WindowBounds(100.5, 50, 1440, 900), back.Bounds); Assert.IsTrue(back.Maximized);
        Assert.IsTrue(WindowGeometry.TryParse("{\"Left\":100,\"Top\":50,\"Width\":1440,\"Height\":900}", out var noFlag)); Assert.IsFalse(noFlag.Maximized);
        foreach (var corrupt in new[] { "{\"Left\":\"NaN\",\"Top\":50,\"Width\":1440,\"Height\":900}", "{\"Left\":100,\"Top\":50,\"Width\":-5,\"Height\":900}", "{\"Left\":100,\"Top\":50,\"Width\":1440}", "{\"Left\":1e300,\"Top\":50,\"Width\":1440,\"Height\":900}", "{\"Left\":100,\"Top\":50,\"Width\":99999,\"Height\":900}", "[1,2,3]", "{\"Left\":", "", "compact" })
            Assert.IsFalse(WindowGeometry.TryParse(corrupt, out _), corrupt);

        // Capture: a close while minimized saves the normal bounds and no minimized state; maximized keeps its flag with the restore bounds.
        var bounds = new WindowBounds(100, 50, 1440, 900);
        Assert.IsFalse(WindowGeometry.Capture(WindowState.Minimized, bounds).Maximized); Assert.AreEqual(bounds, WindowGeometry.Capture(WindowState.Minimized, bounds).Bounds);
        Assert.IsTrue(WindowGeometry.Capture(WindowState.Maximized, bounds).Maximized); Assert.IsFalse(WindowGeometry.Capture(WindowState.Normal, bounds).Maximized);

        // Through the preference schema: a corrupt record is a diagnostic naming the key and not the payload, never an exception.
        var root = Path.Combine(Path.GetTempPath(), "window-geometry-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root); PreferenceSchema.ClearDiagnostics();
            var store = new UiPreferenceStore(root);
            store.Set(WindowGeometry.PreferenceKey, PreferenceSchema.Wrap(1, "{\"Left\":\"NaN\" LEAKMARKER"));
            Assert.IsFalse(PreferenceSchema.TryDecode<WindowGeometryState>(store, WindowGeometry.PreferenceKey, WindowGeometry.TryParse, out _));
            Assert.IsTrue(PreferenceSchema.Diagnostics.Any(d => d.StartsWith(WindowGeometry.PreferenceKey + ":", StringComparison.Ordinal) && !d.Contains("LEAKMARKER", StringComparison.Ordinal)));
            Assert.IsTrue(PreferenceSchema.Families.Any(f => f.KeyPrefix == WindowGeometry.PreferenceKey), "the family is registered");
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }
}
