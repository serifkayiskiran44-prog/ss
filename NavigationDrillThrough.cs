namespace TrMarketplaceHubDesktop;

/// <summary>Where a drill-through lands, and the context it has to carry there and back.</summary>
public sealed record DrillTarget(
    string Route,
    string Title,
    string StoreKey = DashboardStoreFilter.AllStoresKey,
    string EntityKind = "",
    string EntityId = "",
    string EntityLabel = "");

/// <summary>A drill-through as the dashboard asks for it: the target plus the stores this session may open.</summary>
public sealed record DrillRequest(DrillTarget Target, IReadOnlyList<string> AllowedStoreKeys);

public sealed record DrillCrumb(string Label, bool IsCurrent, DrillTarget Target)
{
    public string Route => Target.Route;
}

public sealed record DrillOpenResult(bool Allowed, string Notice);

public sealed record DrillBackResult(DrillTarget Target, bool DroppedStaleEntity, string Notice);

/// <summary>
/// The drill-through trail behind a dashboard card (#810). A KPI or anomaly card is a question with a context --
/// the store the board was filtered to, and the entity the card was counting -- and following it must not throw
/// that away: the trail names every step, Back hands the whole context back (filter included, not just the
/// route), and an entity that vanished while you were away degrades to its screen instead of returning you to a
/// record that is no longer there. The security line is <see cref="Open"/>: a target naming a store outside the
/// offered list, or a different store than the one the board is filtered to, is a wrong-store deep link and
/// never opens -- the check is an ordinal comparison against the offered keys, not a parse of the target.
/// </summary>
public sealed class DrillThroughStack
{
    const int VisibleCrumbs = 4;
    readonly List<DrillTarget> steps = new();

    public DrillThroughStack(DrillTarget root) => steps.Add(root ?? throw new ArgumentNullException(nameof(root)));

    public string CurrentStoreKey => steps[0].StoreKey;
    public DrillTarget Current => steps[^1];
    public bool CanGoBack => steps.Count > 1;

    public IReadOnlyList<DrillCrumb> Crumbs =>
        steps.Select((target, index) => new DrillCrumb(LabelFor(target), index == steps.Count - 1, target)).ToList();

    static string LabelFor(DrillTarget target) => string.IsNullOrWhiteSpace(target.EntityLabel) ? target.Title : target.EntityLabel.Trim();

    public DrillOpenResult Open(DrillTarget target, IReadOnlyCollection<string> allowedStoreKeys)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(allowedStoreKeys);
        var wanted = target.StoreKey ?? "";
        if (wanted != DashboardStoreFilter.AllStoresKey)
        {
            // Ordinal and exact: the target must name a store this session actually offers.
            if (!allowedStoreKeys.Any(key => string.Equals(key, wanted, StringComparison.Ordinal)))
                return new(false, "Bağlantı bu oturumda kullanılamayan bir mağazayı gösteriyor; açılmadı.");
            var scope = CurrentStoreKey;
            if (scope != DashboardStoreFilter.AllStoresKey && !string.Equals(scope, wanted, StringComparison.Ordinal))
                return new(false, "Bağlantı, panonun daraltıldığı mağazanın dışına çıkıyor; açılmadı.");
        }
        if (Current.Route == target.Route && Current.EntityId == target.EntityId) steps[^1] = target;
        else steps.Add(target);
        return new(true, "");
    }

    /// <summary>Steps back one crumb and returns the context to restore; at the root it stays there.</summary>
    public DrillBackResult Back(Func<DrillTarget, bool> entityAlive)
    {
        ArgumentNullException.ThrowIfNull(entityAlive);
        if (!CanGoBack) return new(steps[0], false, "");
        steps.RemoveAt(steps.Count - 1);
        var landing = steps[^1];
        if (landing.EntityId.Length == 0 || entityAlive(landing)) return new(landing, false, "");
        // The record is gone; the screen it lived on is still worth returning to, and the crumb stops claiming it.
        var withoutEntity = landing with { EntityKind = "", EntityId = "", EntityLabel = "" };
        steps[^1] = withoutEntity;
        return new(withoutEntity, true, $"Geri dönülen kayıt artık yok; {withoutEntity.Title} ekranına dönüldü.");
    }

    /// <summary>The board changed store: a trail dug through one store's data means nothing in another's.</summary>
    public DrillTarget SwitchStore(string storeKey)
    {
        var wanted = (storeKey ?? "").Trim();
        if (wanted.Length == 0 || string.Equals(wanted, CurrentStoreKey, StringComparison.Ordinal)) return Current;
        var root = steps[0] with { StoreKey = wanted };
        steps.Clear();
        steps.Add(root);
        return root;
    }

    public string TrailText()
    {
        var labels = steps.Select(LabelFor).ToList();
        if (labels.Count > VisibleCrumbs) labels = new List<string> { labels[0], "…", labels[^2], labels[^1] };
        return string.Join("  /  ", labels);
    }
}
