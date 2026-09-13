using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

// #832: the kind rides along so the renderer can say added / removed / changed without reading the values.
public sealed record ProductContentDiffRow(string Field, string Before, string After, bool BeforeTruncated, bool AfterTruncated, DiffKind Kind = DiffKind.Changed);

public sealed record ProductContentDiffView(IReadOnlyList<ProductContentDiffRow> Rows, string Headline, bool IsStale, string Warning)
{
    public bool HasChanges => Rows.Count > 0;
}

/// <summary>
/// A read-only before/after of the customer-facing text a save is about to change (#805). This is the one place
/// that *does* render values -- the operator opened it to see them -- so the rules are different from the
/// always-visible dirty indicator (#802), which names fields only: everything here is sanitized, flattened to a
/// single line and capped, so a 4 KB HTML description or a token pasted in from a supplier feed cannot turn the
/// dialog into a wall of text or a leak. Nothing here writes anything, locally or to a marketplace.
/// </summary>
public static class ProductContentDiff
{
    public const int MaxPreviewLength = FieldDiff.MaxLength;

    static readonly (string Field, Func<CatalogProduct, string?> Read)[] Fields =
    [
        ("Başlık", p => p.Name),
        ("Açıklama", p => p.Description),
        ("Marka", p => p.Brand),
        ("Kategori", p => p.Category),
    ];

    /// <param name="stored">The product as currently persisted, when known: if it has moved on since the
    /// baseline was taken, the diff is against a version that no longer exists and says so.</param>
    public static ProductContentDiffView Build(CatalogProduct baseline, CatalogProduct current, CatalogProduct? stored = null)
    {
        ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(current);
        var rows = new List<ProductContentDiffRow>();
        foreach (var (field, read) in Fields)
        {
            var before = read(baseline) ?? "";
            var after = read(current) ?? "";
            if (string.Equals(before, after, StringComparison.Ordinal)) continue;
            var (beforeText, beforeCut) = Present(before);
            var (afterText, afterCut) = Present(after);
            rows.Add(new(field, beforeText, afterText, beforeCut, afterCut, FieldDiff.Classify(before, after)));
        }

        var stale = stored is not null && stored.UpdatedUtc != baseline.UpdatedUtc;
        var headline = rows.Count == 0 ? "İçerik alanlarında değişiklik yok." : $"{rows.Count} alan değişecek";
        var warning = stale ? "Kayıt bu düzenleme açıldıktan sonra değişti; önizlemeyi uygulamadan önce ürünü yeniden yükleyin." : "";
        return new(rows, headline, stale, warning);
    }

    // #832: one owner for value presentation (mask, flatten, cap) -- FieldDiff -- so every diff reads the same.
    static (string Text, bool Truncated) Present(string value) => FieldDiff.PresentValue(value);
}
