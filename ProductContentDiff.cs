using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductContentDiffRow(string Field, string Before, string After, bool BeforeTruncated, bool AfterTruncated);

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
    public const int MaxPreviewLength = 400;
    const string Empty = "(boş)";

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
            rows.Add(new(field, beforeText, afterText, beforeCut, afterCut));
        }

        var stale = stored is not null && stored.UpdatedUtc != baseline.UpdatedUtc;
        var headline = rows.Count == 0 ? "İçerik alanlarında değişiklik yok." : $"{rows.Count} alan değişecek";
        var warning = stale ? "Kayıt bu düzenleme açıldıktan sonra değişti; önizlemeyi uygulamadan önce ürünü yeniden yükleyin." : "";
        return new(rows, headline, stale, warning);
    }

    static (string Text, bool Truncated) Present(string value)
    {
        var safe = AuditStore.Sanitize(value).Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (safe.Contains("  ", StringComparison.Ordinal)) safe = safe.Replace("  ", " ", StringComparison.Ordinal);
        if (safe.Length == 0) return (Empty, false);
        return safe.Length > MaxPreviewLength ? (safe[..MaxPreviewLength] + "…", true) : (safe, false);
    }
}
