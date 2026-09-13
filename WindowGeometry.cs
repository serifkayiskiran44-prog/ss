using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace TrMarketplaceHubDesktop;

/// <summary>A window rectangle in device-independent pixels.</summary>
public sealed record WindowBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>What is remembered at close: the normal (un-maximized) rectangle and whether the window was maximized. A minimized window is never remembered as such.</summary>
public sealed record WindowGeometryState(WindowBounds Bounds, bool Maximized);

/// <summary>One monitor's work area (the part outside the taskbar) in device-independent pixels.</summary>
public sealed record MonitorArea(WindowBounds WorkArea, bool Primary);

/// <summary>Default: nothing usable was saved, the start-up placement stands. Restored: the saved rectangle as it was. Fitted: moved or resized so the window is reachable on the monitors that exist now.</summary>
public enum WindowRestoreOutcome { Default, Restored, Fitted }

public sealed record WindowRestore(WindowBounds Bounds, bool Maximized, WindowRestoreOutcome Outcome);

/// <summary>
/// Window geometry safe restore (#877). The main window remembers its normal rectangle and its maximized flag in the
/// preference store and, at the next start, fits them to the monitors that exist now: a rectangle whose title strip
/// is reachable on some monitor (at least <see cref="MinVisibleWidth"/> of it inside a work area, the full strip
/// height inside) keeps its position — a window straddling two present monitors included — and only what hangs
/// beyond the monitors' bounding box is pulled in; anything else — a monitor that was removed, a title bar above or
/// beside every screen or in the gap between two monitors — is moved onto the monitor it overlaps most (the primary
/// when none) and clamped inside that work area, top-left aligned when it is bigger than the area; the size never
/// exceeds the monitors' bounding box and never drops below the window's minimum, so a DPI change that leaves a
/// smaller work area in DIPs shrinks the window to fit while keeping its title bar reachable. A maximized
/// window comes back maximized on its monitor; a close while minimized saves the normal bounds, never a minimized
/// state. A corrupt record — not a JSON object, a missing or non-finite or absurd number — is refused by
/// <see cref="TryParse"/>, so through <see cref="PreferenceSchema.TryDecode{T}"/> it is a diagnostic naming the key
/// and the start-up placement stands. All values are DIPs: the application is system-DPI-aware, so a monitor's
/// physical rectangle divides by the system scale once.
/// </summary>
public static class WindowGeometry
{
    public const string PreferenceKey = "shell:window";
    /// <summary>The height of the top edge that must be inside a work area for the window to count as reachable.</summary>
    public const double TitleStripHeight = 32;
    /// <summary>How much of that strip's width must lie on a monitor.</summary>
    public const double MinVisibleWidth = 160;
    public const double MaxCoordinate = 100_000;
    public const double MaxExtent = 32_767;

    public static string Serialize(WindowGeometryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonSerializer.Serialize(new { state.Bounds.Left, state.Bounds.Top, state.Bounds.Width, state.Bounds.Height, state.Maximized });
    }

    /// <summary>True only for a JSON object whose Left, Top, Width and Height are finite numbers within sane bounds; Maximized is optional and false unless true.</summary>
    public static bool TryParse(string? saved, out WindowGeometryState state)
    {
        state = new WindowGeometryState(new WindowBounds(0, 0, 0, 0), false);
        if (string.IsNullOrWhiteSpace(saved)) return false;
        try
        {
            using var document = JsonDocument.Parse(saved);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!Number(root, "Left", MaxCoordinate, out var left) || !Number(root, "Top", MaxCoordinate, out var top) || !Number(root, "Width", MaxExtent, out var width) || !Number(root, "Height", MaxExtent, out var height)) return false;
            if (width < 1 || height < 1) return false;
            var maximized = root.TryGetProperty("Maximized", out var flag) && flag.ValueKind == JsonValueKind.True;
            state = new WindowGeometryState(new WindowBounds(left, top, width, height), maximized);
            return true;
        }
        catch (JsonException) { return false; }
    }

    static bool Number(JsonElement root, string name, double limit, out double value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value) && double.IsFinite(value) && Math.Abs(value) <= limit;
    }

    /// <summary>What a close remembers: the normal bounds always; the maximized flag only when the window is maximized — a minimized window is saved as normal.</summary>
    public static WindowGeometryState Capture(WindowState state, WindowBounds normalBounds)
    {
        ArgumentNullException.ThrowIfNull(normalBounds);
        return new WindowGeometryState(normalBounds, state == WindowState.Maximized);
    }

    public static WindowRestore Fit(WindowGeometryState? saved, IReadOnlyList<MonitorArea> monitors, double minWidth, double minHeight, double defaultWidth, double defaultHeight)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var primary = monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
        if (saved is null || primary is null) return new WindowRestore(Default(primary, minWidth, minHeight, defaultWidth, defaultHeight), false, WindowRestoreOutcome.Default);

        var union = new WindowBounds(monitors.Min(m => m.WorkArea.Left), monitors.Min(m => m.WorkArea.Top), 0, 0);
        union = union with { Width = monitors.Max(m => m.WorkArea.Right) - union.Left, Height = monitors.Max(m => m.WorkArea.Bottom) - union.Top };
        var width = Math.Max(minWidth, Math.Min(saved.Bounds.Width, union.Width));
        var height = Math.Max(minHeight, Math.Min(saved.Bounds.Height, union.Height));
        var candidate = new WindowBounds(saved.Bounds.Left, saved.Bounds.Top, width, height);

        if (Reachable(candidate, monitors))
        {
            // Reachable, so the position is the operator's: only what hangs beyond the monitors' bounding box is pulled in — unless that pull would push the title strip into a gap between monitors.
            var clamped = ClampInto(candidate, union);
            if (Reachable(clamped, monitors))
            {
                var outcome = clamped == saved.Bounds ? WindowRestoreOutcome.Restored : WindowRestoreOutcome.Fitted;
                return new WindowRestore(clamped, saved.Maximized, outcome);
            }
        }

        var target = monitors.OrderByDescending(m => Overlap(candidate, m.WorkArea)).ThenByDescending(m => m.Primary).First();
        if (Overlap(candidate, target.WorkArea) <= 0) target = primary;
        var area = target.WorkArea;
        width = Math.Max(minWidth, Math.Min(width, area.Width));
        height = Math.Max(minHeight, Math.Min(height, area.Height));
        return new WindowRestore(ClampInto(new WindowBounds(candidate.Left, candidate.Top, width, height), area), saved.Maximized, WindowRestoreOutcome.Fitted);
    }

    /// <summary>The same size inside the area — top-left aligned when it is bigger than the area, so the title bar stays where it can be reached.</summary>
    static WindowBounds ClampInto(WindowBounds bounds, WindowBounds area)
        => bounds with { Left = Math.Max(area.Left, Math.Min(bounds.Left, area.Right - bounds.Width)), Top = Math.Max(area.Top, Math.Min(bounds.Top, area.Bottom - bounds.Height)) };

    /// <summary>The title strip is reachable when its full height lies inside work areas that together show at least <see cref="MinVisibleWidth"/> of its width.</summary>
    static bool Reachable(WindowBounds bounds, IReadOnlyList<MonitorArea> monitors)
    {
        var visible = 0d;
        foreach (var monitor in monitors)
        {
            var area = monitor.WorkArea;
            if (bounds.Top < area.Top || bounds.Top + TitleStripHeight > area.Bottom) continue;
            visible += Math.Max(0, Math.Min(bounds.Right, area.Right) - Math.Max(bounds.Left, area.Left));
        }
        return visible >= MinVisibleWidth;
    }

    static double Overlap(WindowBounds a, WindowBounds b)
        => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));

    static WindowBounds Default(MonitorArea? primary, double minWidth, double minHeight, double defaultWidth, double defaultHeight)
    {
        if (primary is null) return new WindowBounds(0, 0, defaultWidth, defaultHeight);
        var area = primary.WorkArea;
        var width = Math.Max(minWidth, Math.Min(defaultWidth, area.Width));
        var height = Math.Max(minHeight, Math.Min(defaultHeight, area.Height));
        return new WindowBounds(area.Left + Math.Max(0, (area.Width - width) / 2), area.Top + Math.Max(0, (area.Height - height) / 2), width, height);
    }

    /// <summary>Applies the remembered geometry to a window before it is shown: the start-up placement stands when nothing usable was saved.</summary>
    public static WindowRestore Restore(Window window, UiPreferenceStore store, IReadOnlyList<MonitorArea>? monitors = null)
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(store);
        var saved = PreferenceSchema.TryDecode<WindowGeometryState>(store, PreferenceKey, TryParse, out var state) ? state : null;
        var fit = Fit(saved, monitors ?? CurrentMonitors(), window.MinWidth, window.MinHeight, window.Width, window.Height);
        if (fit.Outcome == WindowRestoreOutcome.Default) return fit;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = fit.Bounds.Left; window.Top = fit.Bounds.Top; window.Width = fit.Bounds.Width; window.Height = fit.Bounds.Height;
        if (fit.Maximized) window.WindowState = WindowState.Maximized;
        return fit;
    }

    /// <summary>Remembers a window's geometry at close: the normal bounds (the restore bounds while maximized or minimized) and the maximized flag; nothing is written for bounds that are not usable.</summary>
    public static WindowGeometryState? Save(Window window, UiPreferenceStore store)
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(store);
        var rect = window.RestoreBounds;
        var bounds = rect.IsEmpty || !double.IsFinite(rect.Width) || rect.Width < 1 || rect.Height < 1
            ? new WindowBounds(window.Left, window.Top, window.ActualWidth > 0 ? window.ActualWidth : window.Width, window.ActualHeight > 0 ? window.ActualHeight : window.Height)
            : new WindowBounds(rect.Left, rect.Top, rect.Width, rect.Height);
        // A window that was never shown has no position (NaN) and nothing worth remembering.
        if (!Usable(bounds)) return null;
        var state = Capture(window.WindowState, bounds);
        PreferenceSchema.Write(store, PreferenceKey, Serialize(state));
        return state;
    }

    static bool Usable(WindowBounds bounds)
        => double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
        && Math.Abs(bounds.Left) <= MaxCoordinate && Math.Abs(bounds.Top) <= MaxCoordinate && bounds.Width >= 1 && bounds.Width <= MaxExtent && bounds.Height >= 1 && bounds.Height <= MaxExtent;

    /// <summary>The monitors' work areas in DIPs, from the system; the primary work area alone when the system cannot be asked.</summary>
    public static IReadOnlyList<MonitorArea> CurrentMonitors()
    {
        var scale = SystemScale();
        var areas = new List<MonitorArea>();
        try
        {
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
            {
                var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfoW(monitor, ref info))
                {
                    var work = info.rcWork;
                    areas.Add(new MonitorArea(new WindowBounds(work.Left / scale, work.Top / scale, (work.Right - work.Left) / scale, (work.Bottom - work.Top) / scale), (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { areas.Clear(); }
        if (areas.Count == 0)
        {
            var work = SystemParameters.WorkArea;
            areas.Add(new MonitorArea(new WindowBounds(work.Left, work.Top, work.Width, work.Height), true));
        }
        if (!areas.Any(a => a.Primary)) areas[0] = areas[0] with { Primary = true };
        return areas;
    }

    static double SystemScale()
    {
        try { var dpi = NativeMethods.GetDpiForSystem(); return dpi > 0 ? dpi / 96d : 1d; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return 1d; }
    }

    static class NativeMethods
    {
        public const uint MONITORINFOF_PRIMARY = 1;
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    }
}
