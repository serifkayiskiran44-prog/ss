namespace TrMarketplaceHubDesktop;

/// <summary>A connection the dashboard could be filtered to, as the connection store knows it.</summary>
public sealed record DashboardStoreCandidate(string Channel, string ShopId, string DisplayName, bool Enabled);

public sealed record DashboardStoreOption(string Key, string Label, string Scope);

public sealed record DashboardStoreSelection(DashboardStoreOption Selected, bool FellBack, string Notice);

/// <summary>
/// The dashboard's store filter (#809): which stores can be chosen, which one a saved preference resolves to,
/// and what to do when it names a store that is gone. Two rules do the work. The offered list contains only
/// connections that exist *and* are enabled, so a disabled connection is not something the board can be pointed
/// at. And resolution is a lookup against that list rather than a parse of the saved string -- a value that was
/// never offered, however it got into the preference file, resolves to all-stores instead of being trusted.
/// A saved store that has since been deleted or switched off falls back visibly, because a board silently
/// filtered to nothing is worse than one showing everything.
/// </summary>
public static class DashboardStoreFilter
{
    public const string AllStoresKey = "*";
    public const string PreferenceKey = "filter:dashboard-store";
    const string AllStoresScope = "tüm mağazalar";

    public static string KeyFor(string channel, string shopId) => $"{(channel ?? "").Trim().ToLowerInvariant()}|{(shopId ?? "").Trim()}";

    public static IReadOnlyList<DashboardStoreOption> Options(IReadOnlyList<DashboardStoreCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var options = new List<DashboardStoreOption> { new(AllStoresKey, "Tüm mağazalar", AllStoresScope) };
        foreach (var candidate in candidates.Where(c => c is not null && c.Enabled))
        {
            var key = KeyFor(candidate.Channel, candidate.ShopId);
            if (key == "|" || options.Any(o => o.Key == key)) continue;
            var label = string.IsNullOrWhiteSpace(candidate.DisplayName) ? $"{candidate.Channel} / {candidate.ShopId}" : candidate.DisplayName.Trim();
            options.Add(new(key, label, label));
        }
        return options;
    }

    public static DashboardStoreSelection Resolve(string? savedKey, IReadOnlyList<DashboardStoreOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var all = options.FirstOrDefault(o => o.Key == AllStoresKey) ?? new DashboardStoreOption(AllStoresKey, "Tüm mağazalar", AllStoresScope);
        var wanted = (savedKey ?? "").Trim();
        if (wanted.Length == 0 || wanted == AllStoresKey) return new(all, false, "");
        // Ordinal, exact: the saved value must be one the list actually offers, not something that merely parses.
        var match = options.FirstOrDefault(o => string.Equals(o.Key, wanted, StringComparison.Ordinal));
        return match is null
            ? new(all, true, "Seçili mağaza artık kullanılamıyor; görünüm tüm mağazalar kapsamına döndürüldü.")
            : new(match, false, "");
    }
}
