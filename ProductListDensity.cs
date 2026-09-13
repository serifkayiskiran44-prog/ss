using System.Windows;

namespace TrMarketplaceHubDesktop;

public sealed record ProductListDensityMetrics(double RowHeight, double FontSize, double ThumbnailSize, Thickness CellPadding);

/// <summary>
/// The two list densities (#793) as one table of numbers, so font size, row height, thumbnail and hit target
/// always scale together instead of drifting apart in separate styles. Compact is bounded by what stays usable:
/// a 28 DIP row is still a comfortable pointer target and 12 DIP text is still legible at 100% DPI, both
/// scaled by WPF at higher DPI because these are device-independent units.
/// </summary>
public static class ProductListDensity
{
    public const string Comfortable = "comfortable";
    public const string Compact = "compact";

    public static string Normalize(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        Compact or "compact" or "sık" or "sik" => Compact,
        _ => Comfortable,
    };

    /// <summary>A stored mode is understood only as one of the two words in either language; anything else is not a density.</summary>
    public static bool TryNormalize(string? mode, out string normalized)
    {
        normalized = Normalize(mode);
        return (mode ?? "").Trim().ToLowerInvariant() is Compact or "sık" or "sik" or Comfortable or "rahat";
    }

    public static ProductListDensityMetrics Metrics(string? mode) => Normalize(mode) == Compact
        ? new(RowHeight: 28, FontSize: 12, ThumbnailSize: 24, CellPadding: new Thickness(6, 1, 6, 1))
        : new(RowHeight: 40, FontSize: 14, ThumbnailSize: 32, CellPadding: new Thickness(10, 5, 10, 5));

    /// <summary>Turkish label shown in the density selector; parsed back through <see cref="Normalize"/>.</summary>
    public static string Label(string mode) => Normalize(mode) == Compact ? "Sık" : "Rahat";
}
