using System.Globalization;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum DiffKind { Added, Removed, Changed, Unchanged }

public sealed record FieldDiffRow(string Field, string Before, string After, DiffKind Kind, bool BeforeTruncated, bool AfterTruncated);

/// <summary>How a kind is told apart without colour: a glyph, a word and a border weight; the accent is extra.</summary>
public sealed record DiffPresentation(DiffKind Kind, string Glyph, string Word, double BorderWeight, Color Accent)
{
    public string Badge => $"{Glyph} {Word}";
}

/// <summary>
/// Field-level diff semantics (#832) shared by every before/after renderer: added (empty before, present after),
/// removed (present before, empty after), changed, unchanged. Each kind carries its own glyph, word and border
/// weight, so the state reads the same under high contrast, in a colour-blind simulation or on a monochrome
/// print; the accent only echoes it. Values are sanitized (tokens and credentials masked), flattened to one line
/// and capped, so a long description or a pasted secret never reaches a renderer unchecked.
/// </summary>
public static class FieldDiff
{
    public const int MaxLength = 400;
    public const string Empty = "(boş)";

    public static readonly IReadOnlyList<(string Field, Func<CatalogProduct, string?> Read)> ProductFields = new (string, Func<CatalogProduct, string?>)[]
    {
        ("Başlık", p => p.Name), ("Açıklama", p => p.Description), ("Marka", p => p.Brand), ("Kategori", p => p.Category),
        ("Barkod", p => p.Barcode), ("GTIN", p => p.Gtin),
        ("Alış", p => p.Cost > 0 ? $"{p.Cost.ToString("0.##", CultureInfo.CurrentCulture)} {p.CostCurrency}".Trim() : ""),
        ("Fiyat", p => p.Price > 0 ? $"{p.Price.ToString("0.##", CultureInfo.CurrentCulture)} {p.Currency}".Trim() : ""),
        ("Stok", p => p.Stock.ToString(CultureInfo.CurrentCulture)),
        ("Görseller", p => ImageCount(p.ImageUrls) is var n && n > 0 ? $"{n.ToString(CultureInfo.CurrentCulture)} görsel" : ""),
    };

    static int ImageCount(string? urls) => (urls ?? "").Split(new[] { '\n', '\r', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    public static DiffKind Classify(string? before, string? after)
    {
        var b = (before ?? "").Trim(); var a = (after ?? "").Trim();
        if (b.Length == 0 && a.Length == 0) return DiffKind.Unchanged;
        if (b.Length == 0) return DiffKind.Added;
        if (a.Length == 0) return DiffKind.Removed;
        return string.Equals(b, a, StringComparison.Ordinal) ? DiffKind.Unchanged : DiffKind.Changed;
    }

    /// <summary>One row: kind from the raw values, text from the sanitized, flattened and capped ones.</summary>
    public static FieldDiffRow Row(string field, string? before, string? after)
    {
        var (b, bCut) = PresentValue(before); var (a, aCut) = PresentValue(after);
        return new(field, b, a, Classify(before, after), bCut, aCut);
    }

    /// <summary>A null <paramref name="before"/> is "nothing existed": every present field is added.</summary>
    public static IReadOnlyList<FieldDiffRow> Build<T>(T? before, T after, IEnumerable<(string Field, Func<T, string?> Read)> fields, bool includeUnchanged) where T : class
    {
        ArgumentNullException.ThrowIfNull(after); ArgumentNullException.ThrowIfNull(fields);
        var rows = new List<FieldDiffRow>();
        foreach (var (field, read) in fields)
        {
            var row = Row(field, before is null ? "" : read(before), read(after));
            if (row.Kind == DiffKind.Unchanged && !includeUnchanged) continue;
            rows.Add(row);
        }
        return rows;
    }

    public static (string Text, bool Truncated) PresentValue(string? value)
    {
        var safe = AuditStore.Sanitize(value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (safe.Contains("  ", StringComparison.Ordinal)) safe = safe.Replace("  ", " ", StringComparison.Ordinal);
        if (safe.Length == 0) return (Empty, false);
        return safe.Length > MaxLength ? (safe[..MaxLength] + "…", true) : (safe, false);
    }

    public static DiffPresentation Present(DiffKind kind, bool highContrast)
    {
        var (glyph, word, weight, level) = kind switch
        {
            DiffKind.Added => ("＋", "eklendi", 2.0, SeverityLevel.Success),
            DiffKind.Removed => ("－", "kaldırıldı", 2.0, SeverityLevel.Blocking),
            DiffKind.Changed => ("△", "değişti", 1.5, SeverityLevel.Warning),
            _ => ("＝", "aynı", 0.5, SeverityLevel.Info),
        };
        return new(kind, glyph, word, weight, SeverityStyle.For(level, highContrast).Accent);
    }

    public static string Summary(IReadOnlyList<FieldDiffRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int Count(DiffKind k) => rows.Count(r => r.Kind == k);
        return $"{Count(DiffKind.Added)} eklendi · {Count(DiffKind.Changed)} değişti · {Count(DiffKind.Removed)} kaldırıldı · {Count(DiffKind.Unchanged)} aynı";
    }
}
