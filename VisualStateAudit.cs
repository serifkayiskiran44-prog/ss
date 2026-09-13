using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>What a screen says, read from its tree: the empty states it shows, the error banners, whether the shell is busy, the row counts of its grids, how many actions it offers, how many elements are cut. <see cref="StateOnly"/> leaves the cut count out, because that is the one part that depends on the room the window has.</summary>
public sealed record VisualStateSnapshot(string Route, double Width, double Scale, IReadOnlyList<string> EmptyStates, IReadOnlyList<string> Errors, bool Busy, IReadOnlyList<int> GridRows, int Actions, int Overflow)
{
    public string StateOnly => string.Join(" | ", new[] { Route, "empty:" + string.Join(",", EmptyStates), "errors:" + string.Join(",", Errors), "busy:" + Busy, "rows:" + string.Join(",", GridRows), "actions:" + Actions });
    public string Semantic => StateOnly + " | overflow:" + Overflow;
    public override string ToString() => $"{Route} @ {Width:F0} DIP ×{Scale:0.##}: {Semantic}";
}

/// <summary>
/// The visual-state regression harness (#887): semantic layout and state assertions instead of pixel locks. A
/// screen's state is read from its tree — the shared empty states (#886), the error banners (#816), the shell's
/// busy overlay (#885), the grids' row counts, the offered actions, the overflow audit's cut elements (#867) — and
/// compared across widths and DPI scales as words and numbers, never as pixels. A DPI scale is a layout transform
/// on the window's content with the window's client area grown in step, so the content keeps its DIP room and the
/// semantic snapshot must not change. The desktop caps every top-level window at the virtual screen, and WPF
/// records that cap as the window's own limit; while a scale is applied the cap is raised for that window (see
/// <see cref="AllowRoom"/>), because a grown window shrunk back by the desktop would compare two different
/// screens. When the room is still refused (<see cref="HasRoom"/>), only the parts that never depend on room
/// can be compared. <see cref="VisibleTexts"/> and <see cref="PersonalDataOnScreen"/> let a fixture prove that
/// nothing on screen looks like personal data or a credential.
/// </summary>
public static class VisualStateAudit
{
    public static readonly double[] DpiScales = { 1.0, 1.25, 1.5, 2.0 };

    public static VisualStateSnapshot Snapshot(FrameworkElement root, string route, double width, double scale)
    {
        ArgumentNullException.ThrowIfNull(root);
        var nodes = Descendants(root).ToList();
        var empties = nodes.OfType<Border>().Where(b => b.Tag as string == EmptyState.Tag && b.IsVisible)
            .Select(b => Descendants(b).OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == EmptyState.TitleTag)?.Text ?? "").ToList();
        var errors = nodes.OfType<Border>().Where(b => b.Tag is ErrorBannerModel && b.IsVisible).Select(b => ((ErrorBannerModel)b.Tag).Title).ToList();
        // The busy overlay is a shell layer above every workspace, so it is read from the window, not from the workspace root.
        var shell = Window.GetWindow(root)?.Content as DependencyObject ?? root;
        var busy = Descendants(shell).OfType<Grid>().Any(g => g.Tag as string == BusyOverlay.Tag && g.Visibility == Visibility.Visible);
        var rows = nodes.OfType<DataGrid>().Where(g => g.IsVisible).Select(g => g.Items.Count).ToList();
        // Chrome buttons inside a control's template (a grid's select-all corner, a date picker's drop button) are not offered actions; buttons from a data template are.
        var actions = nodes.OfType<Button>().Count(b => b.IsVisible && b.IsEnabled && b.TemplatedParent is null or ContentPresenter);
        var overflow = OverflowAudit.Audit(root).Count;
        return new VisualStateSnapshot(route, width, scale, empties, errors, busy, rows, actions, overflow);
    }

    /// <summary>True while a visible command under the root is disabled for a running operation — the screen has not settled yet.</summary>
    public static bool IsSettling(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Descendants(root).OfType<ButtonBase>().Any(b => b.IsVisible && CommandState.ReasonOf(b)?.Kind == DisabledReasonKind.Busy);
    }

    /// <summary>Every visible text on screen, inline-built blocks included; editable boxes are the user's own content and stay out.</summary>
    public static IReadOnlyList<string> VisibleTexts(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Descendants(root).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => new TextRange(t.ContentStart, t.ContentEnd).Text.Trim()).Where(t => t.Length > 0).ToList();
    }

    /// <summary>The visible texts the central redaction would change — an e-mail, a phone number, a bearer value, a key=value credential, a user-profile path — none of which belongs on a screen. The words "secret" and "token" used as vocabulary (a help line, a check name) are not values; only a key/value use of them is probed.</summary>
    public static IReadOnlyList<string> PersonalDataOnScreen(FrameworkElement root)
        => VisibleTexts(root).Where(t => { var probe = Vocabulary.Replace(t, "s"); return !string.Equals(AuditStore.Redact(probe), probe, StringComparison.Ordinal); }).ToList();

    static readonly System.Text.RegularExpressions.Regex Vocabulary = new(@"(?i)\b(secret|token)\b(?!\s*[:=])", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Applies a DPI scale as a layout transform on the window's content and grows the window's client area in step, so the content keeps the DIP room it has at 1.0 in a window of the base size; 1.0 removes the transform. The frame does not scale, and the desktop's cap is raised for the window.</summary>
    public static void ApplyDpiScale(Window window, double scale, double baseWidth, double baseHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
        if (window.Content is not FrameworkElement content) throw new InvalidOperationException("Pencerenin ölçeklenecek bir içeriği yok.");
        content.LayoutTransform = scale == 1.0 ? null : new ScaleTransform(scale, scale);
        var (width, height) = OuterSize(window, scale, baseWidth, baseHeight);
        AllowRoom(window, width, height);
        window.Width = width; window.Height = height;
    }

    /// <summary>Whether the desktop gave the window the size a scale asked for; a refused size shrinks the DIP room, and a room-dependent comparison would then compare different screens.</summary>
    public static bool HasRoom(Window window, double scale, double baseWidth, double baseHeight)
    {
        ArgumentNullException.ThrowIfNull(window);
        var (width, height) = OuterSize(window, scale, baseWidth, baseHeight);
        return Math.Abs(window.ActualWidth - width) <= 2 && Math.Abs(window.ActualHeight - height) <= 2;
    }

    /// <summary>The outer size that keeps the client area at (base − frame) × scale: the frame is what the desktop draws around the client area and never scales with the content.</summary>
    static (double Width, double Height) OuterSize(Window window, double scale, double baseWidth, double baseHeight)
    {
        var (frameWidth, frameHeight) = Frame(window);
        return (Math.Max(0, baseWidth - frameWidth) * scale + frameWidth, Math.Max(0, baseHeight - frameHeight) * scale + frameHeight);
    }

    static (double Width, double Height) Frame(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source || source.Handle == IntPtr.Zero || double.IsNaN(window.ActualWidth) || window.ActualWidth <= 0) return (0, 0);
        if (!GetClientRect(source.Handle, out var client)) return (0, 0);
        var dpi = VisualTreeHelper.GetDpi(window);
        return (Math.Max(0, window.ActualWidth - (client.Right - client.Left) / dpi.DpiScaleX), Math.Max(0, window.ActualHeight - (client.Bottom - client.Top) / dpi.DpiScaleY));
    }

    // The desktop validates every top-level window's size against the virtual screen through WM_GETMINMAXINFO,
    // and WPF's Window records the limits it is handed there as the window's own (its layout clamps to them).
    // A hook added after the window's own runs before it (hooks run newest first): enlarging the limits in place,
    // without marking the message handled, makes the window record and report the enlarged ones.
    const int WmGetMinMaxInfo = 0x0024;
    static readonly ConditionalWeakTable<Window, RoomHook> hooks = new();

    sealed class RoomHook { public double WidthDip, HeightDip; public HwndSourceHook? Hook; }

    [StructLayout(LayoutKind.Sequential)] struct Point32 { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo { public Point32 Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] struct Rect32 { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect32 rect);

    /// <summary>Raises the desktop's size cap for the window to the given outer size (DIP); stays in force until the window closes.</summary>
    static void AllowRoom(Window window, double widthDip, double heightDip)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source) return;
        if (!hooks.TryGetValue(window, out var room))
        {
            room = new RoomHook();
            var self = room;
            room.Hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message != WmGetMinMaxInfo) return IntPtr.Zero;
                var dpi = VisualTreeHelper.GetDpi(window);
                var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                var wanted = new Point32 { X = Px(self.WidthDip, dpi.DpiScaleX), Y = Px(self.HeightDip, dpi.DpiScaleY) };
                info.MaxTrackSize = new Point32 { X = Math.Max(info.MaxTrackSize.X, wanted.X), Y = Math.Max(info.MaxTrackSize.Y, wanted.Y) };
                info.MaxSize = new Point32 { X = Math.Max(info.MaxSize.X, wanted.X), Y = Math.Max(info.MaxSize.Y, wanted.Y) };
                Marshal.StructureToPtr(info, lParam, false);
                return IntPtr.Zero;
            };
            source.AddHook(room.Hook);
            hooks.Add(window, room);
        }
        room.WidthDip = widthDip; room.HeightDip = heightDip;
    }

    static int Px(double dip, double scale) => (int)Math.Ceiling(dip * scale);

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }
}
