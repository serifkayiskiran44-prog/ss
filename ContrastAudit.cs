using System.Windows;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>What a pair is used for, and so the ratio it must reach: text 4.5:1, large text or a border/icon 3:1; a disabled control and a pair the operating system owns are measured and reported, never failed.</summary>
public enum ContrastKind { Text, LargeText, NonText, Disabled, System }

public sealed record ContrastPair(string Name, Color Foreground, Color Background, ContrastKind Kind);

public sealed record ContrastFinding(string Name, double Ratio, double Required, bool Passes, ContrastKind Kind);

/// <summary>
/// The contrast audit (#862): WCAG 2.x relative luminance and contrast ratio over a catalogue of the pairs the main
/// screens draw -- text on its surface, each severity's accent as text, as a border and as an icon and the toast
/// text on its surface, the button, the navigation rail, the selected row (focused and not), the focus ring on
/// every ground -- read from the tokens and the severity table, so a colour that drifts is caught by the test that
/// runs this. A disabled control is measured and reported exempt, never hidden. Under high contrast the catalogue
/// is the system's own pairs plus the severities' system accents as emphasis.
/// </summary>
public static class ContrastAudit
{
    public const double TextMinimum = 4.5;
    public const double LargeTextMinimum = 3.0;
    public const double NonTextMinimum = 3.0;
    public const double DisabledOpacity = 0.55;

    public static double Luminance(Color color)
    {
        static double Channel(byte value) { var c = value / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    public static double Ratio(Color a, Color b)
    {
        var la = Luminance(a); var lb = Luminance(b);
        var (light, dark) = la >= lb ? (la, lb) : (lb, la);
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>The colour a viewer sees when <paramref name="top"/> is drawn at <paramref name="opacity"/> over <paramref name="under"/>.</summary>
    public static Color Blend(Color top, double opacity, Color under)
    {
        static byte Mix(byte t, byte u, double o) => (byte)Math.Round(t * o + u * (1 - o), MidpointRounding.AwayFromZero);
        return Color.FromRgb(Mix(top.R, under.R, opacity), Mix(top.G, under.G, opacity), Mix(top.B, under.B, opacity));
    }

    public static double Required(ContrastKind kind) => kind switch { ContrastKind.Text => TextMinimum, ContrastKind.LargeText => LargeTextMinimum, ContrastKind.NonText => NonTextMinimum, _ => 0 };

    public static ContrastFinding Check(ContrastPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var ratio = Ratio(pair.Foreground, pair.Background); var required = Required(pair.Kind);
        return new(pair.Name, Math.Round(ratio, 2), required, pair.Kind is ContrastKind.Disabled or ContrastKind.System || ratio >= required, pair.Kind);
    }

    public static IReadOnlyList<ContrastFinding> Audit(bool highContrast) => Pairs(highContrast).Select(Check).ToList();

    /// <summary>The catalogue: what the main screens draw, from the tokens and the severity table.</summary>
    public static IReadOnlyList<ContrastPair> Pairs(bool highContrast)
    {
        var pairs = new List<ContrastPair>();
        var levels = new[] { SeverityLevel.Blocking, SeverityLevel.Warning, SeverityLevel.Success, SeverityLevel.Info };
        if (highContrast)
        {
            // The system's own pairs: measured and reported, but the palette is the user's choice (the normal Windows highlight is 4.46:1 on purpose), so they are never failed here.
            pairs.Add(new("Metin", SystemColors.WindowTextColor, SystemColors.WindowColor, ContrastKind.System));
            pairs.Add(new("Vurgulu metin", SystemColors.HighlightTextColor, SystemColors.HighlightColor, ContrastKind.System));
            pairs.Add(new("Denetim metni", SystemColors.ControlTextColor, SystemColors.ControlColor, ContrastKind.System));
            pairs.Add(new("Devre dışı metin", SystemColors.GrayTextColor, SystemColors.WindowColor, ContrastKind.Disabled));
            foreach (var level in levels)
            {
                var style = SeverityStyle.For(level, true);
                // The system palette guarantees its highlight colours as emphasis beside its text colour; the words stay in the system text colour.
                pairs.Add(new($"{style.Word} vurgusu", style.Accent, style.Surface, ContrastKind.NonText));
            }
            return pairs;
        }
        var surface = DesignTokens.SurfaceColor; var page = DesignTokens.PageBackgroundColor; var white = DesignTokens.AccentForegroundColor;
        pairs.Add(new("Sayfa başlığı", DesignTokens.TextPrimaryColor, surface, ContrastKind.Text));
        pairs.Add(new("Gövde metni", Colors.DarkSlateGray, surface, ContrastKind.Text));
        pairs.Add(new("Soluk metin", DesignTokens.TextMutedColor, surface, ContrastKind.Text));
        pairs.Add(new("İkincil metin", DesignTokens.TextSecondaryColor, surface, ContrastKind.Text));
        pairs.Add(new("Uyarı durum metni", DesignTokens.WarningTextColor, surface, ContrastKind.Text));
        pairs.Add(new("Düğme etiketi", white, DesignTokens.AccentColor, ContrastKind.Text));
        pairs.Add(new("Düğme kenarı", DesignTokens.AccentColor, page, ContrastKind.NonText));
        pairs.Add(new("Menü öğesi", DesignTokens.RailForegroundColor, DesignTokens.RailBackgroundColor, ContrastKind.Text));
        pairs.Add(new("Seçili menü öğesi", white, DesignTokens.RailSelectedColor, ContrastKind.Text));
        pairs.Add(new("Menü alt başlığı", DesignTokens.RailSubtitleColor, DesignTokens.RailBackgroundColor, ContrastKind.Text));
        pairs.Add(new("Seçili satır metni", DesignTokens.SelectedRowForegroundColor, DesignTokens.SelectedRowColor, ContrastKind.Text));
        pairs.Add(new("Seçili satır çizgisi", DesignTokens.SelectedRowColor, surface, ContrastKind.NonText));
        pairs.Add(new("Etkin olmayan seçili satır", DesignTokens.TextPrimaryColor, DesignTokens.SelectedRowInactiveColor, ContrastKind.Text));
        pairs.Add(new("Odak halkası (açık zemin)", DesignTokens.FocusRingColor, surface, ContrastKind.NonText));
        pairs.Add(new("Odak halkası (koyu zemin)", DesignTokens.FocusRingInnerColor, DesignTokens.RailBackgroundColor, ContrastKind.NonText));
        pairs.Add(new("Odak halkası (düğme)", DesignTokens.FocusRingInnerColor, DesignTokens.AccentColor, ContrastKind.NonText));
        foreach (var level in levels)
        {
            var style = SeverityStyle.For(level, false);
            pairs.Add(new($"{style.Word} metni", style.Accent, style.Surface, ContrastKind.Text));
            pairs.Add(new($"{style.Word} kenarlığı", style.Accent, style.Surface, ContrastKind.NonText));
            pairs.Add(new($"{style.Word} simgesi", style.Accent, surface, ContrastKind.NonText));
            pairs.Add(new($"{style.Word} bildirim metni", DesignTokens.TextPrimaryColor, style.Surface, ContrastKind.Text));
        }
        pairs.Add(new("Devre dışı düğme", Blend(white, DisabledOpacity, page), Blend(DesignTokens.AccentColor, DisabledOpacity, page), ContrastKind.Disabled));
        pairs.Add(new("Devre dışı metin", SystemColors.GrayTextColor, surface, ContrastKind.Disabled));
        return pairs;
    }
}
