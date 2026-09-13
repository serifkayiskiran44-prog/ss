using System.Windows;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>Ordered so that "the highest wins" is a comparison, not a table.</summary>
public enum SeverityLevel { Info = 0, Success = 1, Warning = 2, Blocking = 3 }

/// <summary>Everything a surface needs to draw one severity: signals that survive without colour first, colour last.</summary>
public sealed record SeverityPresentation(SeverityLevel Level, string Glyph, string Word, string CallToAction, double BorderWeight, Color Accent, Color Surface)
{
    public string Badge => $"{Glyph} {Word}";
}

/// <summary>The roll-up of a mixed set of findings: what leads, how many of each, and the one call to action.</summary>
public sealed record SeverityAggregate(SeverityLevel Highest, int BlockingCount, int WarningCount, string Headline, string CallToAction)
{
    public bool IsMixed => BlockingCount > 0 && WarningCount > 0;
    public bool HasBlocking => BlockingCount > 0;
}

/// <summary>
/// The one place that says what a warning looks like and what a blocking error looks like (#817). The two differ
/// in every channel at once -- glyph (⚠ vs ✖), word ("Uyarı" vs "Engel"), border weight (1 vs 2), call to action
/// ("gözden geçir" vs "düzelt") and, last, colour -- so a colour-blind operator or a monochrome display still
/// tells them apart. Under high contrast the accents come from <see cref="SystemColors"/>, never from the RGB
/// table. A mixed set of findings rolls up to its highest level, counts blocking and warning separately, and
/// gives the call to action to the blocking ones: warnings are reviewed, blocking errors are fixed, and a
/// surface must never invite a "save anyway" past a blocking error. Finding text that looks like a raw payload
/// is replaced before it becomes a headline.
/// </summary>
public static class SeverityStyle
{
    public static bool IsHighContrast => SystemParameters.HighContrast;

    public static SeverityPresentation For(SeverityLevel level, bool highContrast)
    {
        var (glyph, word, cta, weight) = level switch
        {
            SeverityLevel.Blocking => ("✖", "Hata", "Düzelt", 2d),
            SeverityLevel.Warning => ("⚠", "Uyarı", "Gözden geçir", 1d),
            SeverityLevel.Success => ("✔", "Başarılı", "", 1d),
            _ => ("ℹ", "Bilgi", "", 0d),
        };
        Color accent, surface;
        if (highContrast)
        {
            accent = level switch
            {
                SeverityLevel.Blocking => SystemColors.HotTrackColor,
                SeverityLevel.Warning => SystemColors.HighlightColor,
                SeverityLevel.Success => SystemColors.WindowTextColor,
                _ => SystemColors.GrayTextColor,
            };
            surface = SystemColors.WindowColor;
        }
        else
        {
            (accent, surface) = level switch
            {
                SeverityLevel.Blocking => (Color.FromRgb(190, 52, 52), Color.FromRgb(253, 240, 240)),
                SeverityLevel.Warning => (Color.FromRgb(160, 82, 22), Color.FromRgb(253, 248, 238)),
                SeverityLevel.Success => (Color.FromRgb(31, 122, 73), Color.FromRgb(240, 250, 244)),
                _ => (Color.FromRgb(23, 107, 115), Color.FromRgb(238, 246, 248)),
            };
        }
        return new(level, glyph, word, cta, weight, accent, surface);
    }

    public static Brush AccentBrush(SeverityLevel level, bool highContrast) { var b = new SolidColorBrush(For(level, highContrast).Accent); b.Freeze(); return b; }
    public static Brush SurfaceBrush(SeverityLevel level, bool highContrast) { var b = new SolidColorBrush(For(level, highContrast).Surface); b.Freeze(); return b; }

    public static SeverityLevel FromValidation(string severity) => (severity ?? "").Trim().ToLowerInvariant() switch
    {
        ProductValidation.Blocking => SeverityLevel.Blocking,
        ProductValidation.Warning => SeverityLevel.Warning,
        _ => SeverityLevel.Info,
    };

    public static SeverityLevel FromNotification(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => SeverityLevel.Blocking,
        NotificationSeverity.Warning => SeverityLevel.Warning,
        NotificationSeverity.Success => SeverityLevel.Success,
        _ => SeverityLevel.Info,
    };

    public static SeverityLevel FromAnomaly(string severity) => string.Equals(severity, DashboardAnomalies.Critical, StringComparison.Ordinal) ? SeverityLevel.Blocking : SeverityLevel.Warning;

    public static SeverityLevel Highest(IEnumerable<SeverityLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var highest = SeverityLevel.Info;
        foreach (var level in levels) if (level > highest) highest = level;
        return highest;
    }

    /// <summary>Rolls a set of findings up: highest level, separate counts, one headline, and the call to action for the blocking ones.</summary>
    public static SeverityAggregate Aggregate(IReadOnlyList<(SeverityLevel Level, string Message)> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var blocking = findings.Count(f => f.Level == SeverityLevel.Blocking);
        var warning = findings.Count(f => f.Level == SeverityLevel.Warning);
        var highest = Highest(findings.Select(f => f.Level));
        string headline;
        if (blocking == 0 && warning == 0) headline = "Engel yok.";
        else if (blocking > 0 && warning > 0) headline = $"{blocking} engel · {warning} uyarı — önce engeller.";
        else if (blocking > 0) headline = blocking == 1 ? "1 engel: kaydetmeden önce düzeltin." : $"{blocking} engel: kaydetmeden önce düzeltin.";
        else headline = warning == 1 ? "1 uyarı: gözden geçirin, kaydedilebilir." : $"{warning} uyarı: gözden geçirin, kaydedilebilir.";
        var first = findings.FirstOrDefault(f => f.Level == highest && !string.IsNullOrWhiteSpace(f.Message)).Message;
        if (!string.IsNullOrWhiteSpace(first))
            headline += " " + (StatusTooltip.LooksLikeRawPayload(first) ? StatusTooltip.RawPayloadHidden : Trim(AuditStore.Sanitize(first), 120));
        var cta = blocking > 0 ? "İlk engele git" : warning > 0 ? "Uyarıları gözden geçir" : "";
        return new(highest, blocking, warning, headline, cta);
    }

    static string Trim(string value, int max)
    {
        var flat = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
