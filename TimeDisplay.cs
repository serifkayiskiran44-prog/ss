using System.Globalization;
using System.Windows.Data;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The one owner of every timestamp a person reads (#866). The stores keep UTC; the display shows the local wall
/// clock in the current culture's short form, its tooltip names the zone offset of that instant (so the two 02:30s
/// of an autumn DST night tell apart), the age, and the UTC instant; a time never recorded is a dash, and the tooltip
/// says so in words. A caller may name the zone (tests do; the app takes the machine's).
/// </summary>
public static class TimeDisplay
{
    public const string Missing = "—";
    public const string MissingDescription = "Zaman kaydı yok.";
    /// <summary>A grid column wide enough for the longest short date-time among the supported locales at the body size.</summary>
    public const double ColumnWidth = 155;

    public static bool IsMissing(DateTime? utc) => utc is null || utc.Value == default || utc.Value == DateTime.MinValue;
    public static bool IsMissing(DateTimeOffset? at) => at is null || at.Value == default;

    /// <summary>The instant as UTC: an unspecified kind is UTC (the stores keep UTC), a local kind is converted.</summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    static DateTime ToZone(DateTime utc, TimeZoneInfo? zone) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), zone ?? TimeZoneInfo.Local);

    public static string Format(DateTime? utc, TimeZoneInfo? zone = null, string? missing = null)
        => IsMissing(utc) ? missing ?? Missing : ToZone(utc!.Value, zone).ToString("g", CultureInfo.CurrentCulture);

    public static string Format(DateTimeOffset? at, TimeZoneInfo? zone = null, string? missing = null)
        => IsMissing(at) ? missing ?? Missing : Format(at!.Value.UtcDateTime, zone, missing);

    /// <summary>The date alone, in the zone: the day a person in that zone would name.</summary>
    public static string FormatDate(DateTime? utc, TimeZoneInfo? zone = null, string? missing = null)
        => IsMissing(utc) ? missing ?? Missing : ToZone(utc!.Value, zone).ToString("d", CultureInfo.CurrentCulture);

    /// <summary>The zone's offset at that instant: "UTC+03:00", "UTC-05:00", or "UTC" itself.</summary>
    public static string Offset(DateTime utc, TimeZoneInfo? zone)
    {
        var offset = (zone ?? TimeZoneInfo.Local).GetUtcOffset(AsUtc(utc));
        if (offset == TimeSpan.Zero) return "UTC";
        return "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>The tooltip: the local wall clock with its offset, the age, and the UTC instant; or the words for a time never recorded.</summary>
    public static string Describe(DateTime? utc, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        if (IsMissing(utc)) return MissingDescription;
        var instant = AsUtc(utc!.Value);
        return $"{Format(instant, zone)} ({Offset(instant, zone)}, yerel saat) · {StatusTooltip.Relative(instant, AsUtc(nowUtc))} · UTC {instant.ToString("g", CultureInfo.CurrentCulture)}";
    }

    public static string Describe(DateTimeOffset? at, DateTime nowUtc, TimeZoneInfo? zone = null)
        => IsMissing(at) ? MissingDescription : Describe(at!.Value.UtcDateTime, nowUtc, zone);
}

/// <summary>A binding's text for a stored UTC timestamp (DateTime or DateTimeOffset, nullable): the local short form, a dash when missing.</summary>
public sealed class TimeDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => TimeDisplay.Missing,
        DateTime d => TimeDisplay.Format(d),
        DateTimeOffset o => TimeDisplay.Format(o),
        _ => "",
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A binding's tooltip for a stored UTC timestamp: zone offset, age and UTC instant, or the words for a missing time.</summary>
public sealed class TimeTooltipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => TimeDisplay.MissingDescription,
        DateTime d => TimeDisplay.Describe(d, DateTime.UtcNow),
        DateTimeOffset o => TimeDisplay.Describe(o, DateTime.UtcNow),
        _ => "",
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
