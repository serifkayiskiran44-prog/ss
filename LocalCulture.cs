using System.Globalization;

namespace TrMarketplaceHubDesktop;

public static class LocalCulture
{
    public static CultureInfo Turkish { get; } = CultureInfo.GetCultureInfo("tr-TR");
    public static string FormatAmount(decimal value, string currency) => $"{value.ToString("N2", Turkish)} {currency.Trim().ToUpperInvariant()}";

    public static bool TryParseAmount(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var candidate = text.Trim();
        if (candidate.Count(c => c is ',' or '.') == 1 && candidate.Length - candidate.IndexOfAny([',', '.']) - 1 == 3)
            return false; // A single separator with three trailing digits is ambiguous without a profile culture.
        var culture = candidate.Contains(',') && candidate.Contains('.') && candidate.LastIndexOf(',') > candidate.LastIndexOf('.')
            ? Turkish
            : candidate.Contains(',') ? Turkish : CultureInfo.InvariantCulture;
        return decimal.TryParse(candidate, NumberStyles.Number, culture, out value);
    }

    public static DateTime ToIstanbul(DateTime utc)
    {
        var normalized = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeFromUtc(normalized, TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"));
    }
}
