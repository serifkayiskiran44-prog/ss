using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductSelectionSummaryInfo(
    int Count,
    int StaleCount,
    int MatchTotal,
    bool CanActivate,
    bool CanDeactivate,
    int DeletableCount,
    int BlockedDeleteCount,
    IReadOnlyList<string> LiveIds,
    string Headline,
    string ScopeText,
    string RiskText)
{
    public bool IsVisible => Count > 0;
    public bool CanDelete => DeletableCount > 0;
}

/// <summary>
/// What the product list's selection summary bar says (#795), computed rather than guessed. Three things the
/// bar must get right: the count is of rows that are *still in the list* (a filter change leaves stale
/// selections behind, and acting on them would touch rows the operator can no longer see); the scope names the
/// selection against the full match count, so selecting a page is never mistaken for selecting the filter; and
/// a risky action is only offered when it would actually do something to this set. Only counts are rendered --
/// no product text -- so nothing from a name or a cost can travel into the bar.
/// </summary>
public static class ProductSelectionSummary
{
    public static ProductSelectionSummaryInfo Describe(IReadOnlyList<CatalogProduct> selected, IReadOnlyList<CatalogProduct> visible, int matchTotal)
    {
        ArgumentNullException.ThrowIfNull(selected); ArgumentNullException.ThrowIfNull(visible);
        var visibleIds = visible.Where(p => p is not null).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var live = selected.Where(p => p is not null && visibleIds.Contains(p.Id)).GroupBy(p => p.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var stale = selected.Count(p => p is not null && !visibleIds.Contains(p.Id));

        // Deleting is refused downstream for anything with a listing or a dispatch attempt (CatalogStore.DeleteProduct);
        // the bar states that up front instead of letting the operator discover it one error dialog at a time.
        var deletable = live.Count(p => string.IsNullOrEmpty(p.EtsyListingId) && !p.EtsyCreationAttempted);
        var blocked = live.Count - deletable;
        var canActivate = live.Any(p => !p.Active);
        var canDeactivate = live.Any(p => p.Active);

        var headline = live.Count == 0 ? "Seçim yok" : $"{Number(live.Count)} ürün seçildi";
        var scope = live.Count == 0 ? "" : $"Eşleşen {Number(matchTotal)} üründen {Number(live.Count)} tanesi seçili.";
        if (stale > 0) scope = (scope.Length > 0 ? scope + " " : "") + $"{Number(stale)} seçim listeden düştü ve işleme alınmayacak.";
        var risk = live.Count == 0 ? ""
            : blocked == 0 ? $"{Number(deletable)} ürün silinebilir."
            : deletable == 0 ? $"Seçilen {Number(blocked)} ürün silinemez (Etsy ilanı veya gönderim kaydı var)."
            : $"{Number(deletable)} ürün silinebilir, {Number(blocked)} ürün silinemez (Etsy ilanı veya gönderim kaydı var).";

        return new(live.Count, stale, matchTotal, canActivate, canDeactivate, deletable, blocked, live.Select(p => p.Id).ToArray(), headline, scope, risk);
    }

    static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
