using System.Globalization;
using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record NumberParseResult(bool Success, decimal Value, string Code, string Message);

/// <summary>
/// Parses supplier values using the culture explicitly selected by the source/profile.
/// It deliberately does not fall back to the process/UI culture.
/// </summary>
public static class DeterministicNumberParser
{
    static readonly Regex DateLike = new(@"^\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static CultureInfo Culture(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
            throw new InvalidOperationException("Sayı kültürü açıkça belirtilmeli; geçerli profil kültürü seçin.");
        try { return CultureInfo.GetCultureInfo(cultureName.Trim()); }
        catch (CultureNotFoundException) { throw new InvalidOperationException("Sayı kültürü geçersiz."); }
    }

    public static NumberParseResult Decimal(string? raw, string cultureName, string field, bool allowNegative = false)
        => Parse(raw, cultureName, field, allowNegative, integer: false);

    public static NumberParseResult Integer(string? raw, string cultureName, string field, bool allowNegative = false)
        => Parse(raw, cultureName, field, allowNegative, integer: true);

    static NumberParseResult Parse(string? raw, string cultureName, string field, bool allowNegative, bool integer)
    {
        var value = raw?.Trim() ?? "";
        if (value.Length == 0) return Fail("EMPTY", $"{field} boş.");
        if (DateLike.IsMatch(value)) return Fail("DATE_LIKE", $"{field} tarih benzeri bir değer içeriyor.");

        var culture = Culture(cultureName);
        var number = culture.NumberFormat;
        if (!allowNegative && value.Length > 0 && value[0] == '-')
            return Fail("NEGATIVE", $"{field} negatif olamaz.");

        // A value containing both separators is valid only when the culture's decimal
        // separator occurs after the grouping separators. This avoids silently turning
        // a Turkish value (1.234,56) into a different number under en-US.
        var decimalSeparator = number.NumberDecimalSeparator;
        var groupSeparator = number.NumberGroupSeparator;
        if (value.Contains(decimalSeparator, StringComparison.Ordinal) && value.Contains(groupSeparator, StringComparison.Ordinal) &&
            value.LastIndexOf(decimalSeparator, StringComparison.Ordinal) < value.LastIndexOf(groupSeparator, StringComparison.Ordinal))
            return Fail("AMBIGUOUS_SEPARATOR", $"{field} ayraçları seçilen kültürle uyumsuz.");

        var styles = NumberStyles.Number;
        if (System.Decimal.TryParse(value, styles, culture, out var result))
        {
            if (integer && result != System.Decimal.Truncate(result)) return Fail("NOT_INTEGER", $"{field} tam sayı olmalı.");
            return new(true, result, "OK", "");
        }

        // Distinguish overflow from an ordinary malformed value for a stable UI/API error.
        var digits = value.Count(char.IsDigit);
        if (digits > 28) return Fail("OVERFLOW", $"{field} decimal sınırını aşıyor.");
        return Fail("INVALID", $"{field} seçilen kültür için geçerli bir sayı değil.");
    }

    static NumberParseResult Fail(string code, string message) => new(false, 0, code, message);
}
