using System.Globalization;
using System.Text;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The one fold for every display search (#868). A query and the text it is looked for in are both normalized to
/// composed Unicode, lower-cased by Turkish rules (İ is i, I is ı), and then the dotted and the dotless i meet, so
/// an ASCII keyboard that types I for ı and i for İ still finds the word. Other diacritics stay significant (a
/// çocuk is not a cocuk). An empty query matches everything. Identity keys (a SKU, a barcode, an external key)
/// are not folded here; they keep their own normalization. A search is never logged: a query can be personal data.
/// </summary>
public static class UiSearch
{
    static readonly CultureInfo Turkish = LocalCulture.Turkish;

    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lower = text.Normalize(NormalizationForm.FormC).ToLower(Turkish);
        var builder = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (ch == '̇') continue; // a combining dot above left behind by an invariant lower-casing of İ
            builder.Append(ch == 'ı' ? 'i' : ch);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Whether the folded query occurs in the folded text; an empty or blank query matches everything.</summary>
    public static bool Matches(string? text, string? query)
    {
        var needle = Fold(query?.Trim());
        return needle.Length == 0 || Fold(text).Contains(needle, StringComparison.Ordinal);
    }

    /// <summary>The same as <see cref="Matches"/>, in the shape the display filters read: text.ContainsFolded(query).</summary>
    public static bool ContainsFolded(this string? text, string? query) => Matches(text, query);
}
