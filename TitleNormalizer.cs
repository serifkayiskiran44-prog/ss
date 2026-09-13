using System.Globalization;
using System.Text;

namespace TrMarketplaceHubDesktop;

/// <summary>One pass of the title pipeline: the text as it came, as it goes, whether anything changed, and the steps that changed it.</summary>
public sealed record TitleNormalization(string Original, string Normalized, bool Changed, IReadOnlyList<string> Steps)
{
    public int Length => Normalized.Length;
}

/// <summary>
/// The product title pipeline (#905). Every write of a title — a feed row, the migration, the editor, a bulk rename —
/// goes through the same normalisation: Unicode composition (NFC, so a letter written as base + combining mark is
/// one character; Turkish letters stay themselves and no case ever changes), control and invisible format characters
/// dropped (tab and line breaks become a space), every kind of space made a plain space, runs of spaces collapsed,
/// the ends trimmed. Nothing is ever cut: a title over the limit is refused by validation with the excess counted.
/// Provenance is protected: a difference the pipeline would remove is not a change of the operator's, so a feed's
/// title stays the feed's when the operator saves it untouched.
/// </summary>
public static class TitleNormalizer
{
    public const int MaxLength = 500;

    public static TitleNormalization Normalize(string? raw)
    {
        var original = raw ?? "";
        var steps = new List<string>();
        var text = original;
        if (text.Length > 0 && !text.IsNormalized(NormalizationForm.FormC)) { text = text.Normalize(NormalizationForm.FormC); steps.Add("Unicode NFC"); }
        var sb = new StringBuilder(text.Length); var dropped = 0; var broke = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\t' or '\n' or '\r' or '\f' or '\v') { sb.Append(' '); broke++; continue; }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format) { dropped++; continue; }
            if (category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) { sb.Append(' '); continue; }
            sb.Append(rune.ToString());
        }
        if (dropped > 0) steps.Add($"{dropped} kontrol/görünmez karakter silindi");
        if (broke > 0) steps.Add($"{broke} satır sonu/sekme boşluğa çevrildi");
        text = sb.ToString();
        var collapsed = Collapse(text);
        if (collapsed != text) steps.Add("tekrarlanan boşluklar teke indirildi");
        text = collapsed;
        var trimmed = text.Trim(' ');
        if (trimmed != text) steps.Add("baş/son boşluk kırpıldı");
        text = trimmed;
        return new(original, text, !string.Equals(text, original, StringComparison.Ordinal), steps);
    }

    /// <summary>The over-limit finding for a title, with the excess counted — never a cut; null when it fits.</summary>
    public static string? OverLimit(string? title)
    {
        var normalized = Normalize(title).Normalized;
        return normalized.Length > MaxLength ? $"Başlık en fazla {MaxLength} karakter olabilir; {normalized.Length - MaxLength} karakter fazla, kısaltılmadı." : null;
    }

    public static string Describe(TitleNormalization normalization)
    {
        ArgumentNullException.ThrowIfNull(normalization);
        return normalization.Changed ? string.Join(" · ", normalization.Steps) : "değişiklik yok";
    }

    static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length); var lastSpace = false;
        foreach (var ch in text)
        {
            if (ch == ' ') { if (lastSpace) continue; lastSpace = true; }
            else lastSpace = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
