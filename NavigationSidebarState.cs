using System.Globalization;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record NavigationSidebarState(bool Collapsed, double ExpandedWidth);

/// <summary>How one navigation entry is shown in the current mode: what is printed, what hovers, what is announced.</summary>
public sealed record NavigationItemPresentation(string Content, string ToolTip, string AccessibleName);

/// <summary>
/// The shell sidebar's collapse state (#812). Collapsed-or-not and the open width are the operator's choices and
/// persist per data directory; the stored value is untrusted (a hand-edited or stale preference row) and anything
/// unusable falls back to the default rather than being clamped into a width nobody chose. Widths are DIPs, the
/// unit WPF lays out in, so a DPI change re-renders the same split instead of drifting it. In icons-only mode an
/// entry prints a glyph built with Turkish casing, but its tooltip and its accessible name are the full title --
/// the label leaves the screen, not the accessibility tree.
/// </summary>
public static class NavigationSidebar
{
    public const string PreferenceKey = "shell:sidebar";
    public const double CollapsedWidth = 56;
    public const double MinExpandedWidth = 160;
    public const double MaxExpandedWidth = 360;
    public static readonly NavigationSidebarState Default = new(false, 214);
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static NavigationSidebarState Parse(string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved)) return Default;
        try
        {
            using var document = JsonDocument.Parse(saved);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Default;
            var collapsed = document.RootElement.TryGetProperty("Collapsed", out var c) && c.ValueKind == JsonValueKind.True;
            var width = document.RootElement.TryGetProperty("ExpandedWidth", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetDouble(out var value) ? value : double.NaN;
            // A width outside the usable band was never a choice the sidebar could have produced: replace, don't clamp.
            if (double.IsNaN(width) || double.IsInfinity(width) || width < MinExpandedWidth || width > MaxExpandedWidth) width = Default.ExpandedWidth;
            return new(collapsed, width);
        }
        catch (JsonException) { return Default; }
    }

    public static string Serialize(NavigationSidebarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonSerializer.Serialize(new { state.Collapsed, state.ExpandedWidth });
    }

    public static double CurrentWidth(NavigationSidebarState state) => state.Collapsed ? CollapsedWidth : state.ExpandedWidth;

    public static NavigationSidebarState Toggle(NavigationSidebarState state) => state with { Collapsed = !state.Collapsed };

    /// <summary>A drag sets the open width, clamped; while collapsed it changes nothing, because nothing was dragged open.</summary>
    public static NavigationSidebarState Resize(NavigationSidebarState state, double requestedWidth)
    {
        if (state.Collapsed || double.IsNaN(requestedWidth)) return state;
        return state with { ExpandedWidth = Math.Clamp(requestedWidth, MinExpandedWidth, MaxExpandedWidth) };
    }

    public static NavigationItemPresentation Present(string title, string description, bool collapsed)
    {
        var name = (title ?? "").Trim();
        if (!collapsed) return new(name, description ?? "", name);
        return new(Glyph(name), name, name);
    }

    static string Glyph(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return "?";
        var glyph = words[0][..1];
        if (words.Length > 1) glyph += words[1][..1];
        return glyph.ToUpper(Turkish);
    }
}
