using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

/// <summary>Where a drill-through lands, and the context it has to carry there and back: the store, the entity, and the entity's revision as it was when the step was taken (#889), so a record that changed while you were away is noticed on the way back.</summary>
public sealed record DrillTarget(
    string Route,
    string Title,
    string StoreKey = DashboardStoreFilter.AllStoresKey,
    string EntityKind = "",
    string EntityId = "",
    string EntityLabel = "",
    string Revision = "");

/// <summary>A drill-through as the dashboard asks for it: the target plus the stores this session may open.</summary>
public sealed record DrillRequest(DrillTarget Target, IReadOnlyList<string> AllowedStoreKeys);

public sealed record DrillCrumb(string Label, bool IsCurrent, DrillTarget Target)
{
    public string Route => Target.Route;
}

public sealed record DrillOpenResult(bool Allowed, string Notice);

public sealed record DrillBackResult(DrillTarget Target, bool DroppedStaleEntity, string Notice, bool RevisionChanged = false);

/// <summary>The trail as it is kept between sessions: the steps behind the current screen and the steps in front of it.</summary>
public sealed record DrillHistoryState(IReadOnlyList<DrillTarget> Steps, IReadOnlyList<DrillTarget> Forward);

public sealed record DrillRestoreResult(int Steps, int Forward, IReadOnlyList<string> Notices);

/// <summary>
/// The drill-through trail behind a dashboard card (#810). A KPI or anomaly card is a question with a context --
/// the store the board was filtered to, and the entity the card was counting -- and following it must not throw
/// that away: the trail names every step, Back hands the whole context back (filter included, not just the
/// route), and an entity that vanished while you were away degrades to its screen instead of returning you to a
/// record that is no longer there. The security line is <see cref="Open"/>: a target naming a store outside the
/// offered list, or a different store than the one the board is filtered to, is a wrong-store deep link and
/// never opens -- the check is an ordinal comparison against the offered keys, not a parse of the target.
/// #889 scopes the history: Back keeps what it left as a forward branch, Forward walks it again with the same
/// checks, a step remembers the record's revision and says so when the record changed meanwhile, a crumb never
/// carries personal data (a label the redaction would change falls back to the screen's title), and a saved
/// trail comes back only through <see cref="Restore"/>, which re-validates every step against this session's
/// screens, stores and records.
/// </summary>
public sealed class DrillThroughStack
{
    const int VisibleCrumbs = 4;
    public const int LabelLimit = 60;
    public const int MaxSteps = 50;
    readonly List<DrillTarget> steps = new();
    readonly List<DrillTarget> forward = new();

    public DrillThroughStack(DrillTarget root) => steps.Add(root ?? throw new ArgumentNullException(nameof(root)));

    public string CurrentStoreKey => steps[0].StoreKey;
    public DrillTarget Current => steps[^1];
    public bool CanGoBack => steps.Count > 1;
    public bool CanGoForward => forward.Count > 0;

    public IReadOnlyList<DrillCrumb> Crumbs =>
        steps.Select((target, index) => new DrillCrumb(SafeLabel(target), index == steps.Count - 1, target)).ToList();

    /// <summary>What a crumb may say: the entity's label, trimmed and capped, unless the redaction would change it — then the screen's title, because a crumb is a breadcrumb, not a customer record.</summary>
    public static string SafeLabel(DrillTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var label = (target.EntityLabel ?? "").Trim();
        if (label.Length == 0) return target.Title;
        if (!string.Equals(AuditStore.Redact(label), label, StringComparison.Ordinal)) return target.Title;
        return label.Length <= LabelLimit ? label : label[..(LabelLimit - 1)] + "…";
    }

    public DrillOpenResult Open(DrillTarget target, IReadOnlyCollection<string> allowedStoreKeys)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(allowedStoreKeys);
        var refused = Refusal(target, allowedStoreKeys);
        if (refused.Length > 0) return new(false, refused);
        if (Current.Route == target.Route && Current.EntityId == target.EntityId) steps[^1] = target;
        else steps.Add(target);
        // A new step is a new branch: whatever was in front of the old position no longer follows from here.
        forward.Clear();
        while (steps.Count > MaxSteps) steps.RemoveAt(1);
        return new(true, "");
    }

    string Refusal(DrillTarget target, IReadOnlyCollection<string> allowedStoreKeys)
    {
        var wanted = target.StoreKey ?? "";
        if (wanted == DashboardStoreFilter.AllStoresKey) return "";
        // Ordinal and exact: the target must name a store this session actually offers.
        if (!allowedStoreKeys.Any(key => string.Equals(key, wanted, StringComparison.Ordinal)))
            return "Bağlantı bu oturumda kullanılamayan bir mağazayı gösteriyor; açılmadı.";
        var scope = CurrentStoreKey;
        if (scope != DashboardStoreFilter.AllStoresKey && !string.Equals(scope, wanted, StringComparison.Ordinal))
            return "Bağlantı, panonun daraltıldığı mağazanın dışına çıkıyor; açılmadı.";
        return "";
    }

    /// <summary>Steps back one crumb and returns the context to restore; at the root it stays there. The step left behind becomes the forward branch.</summary>
    public DrillBackResult Back(Func<DrillTarget, bool> entityAlive, Func<DrillTarget, string>? revisionOf = null)
    {
        ArgumentNullException.ThrowIfNull(entityAlive);
        if (!CanGoBack) return new(steps[0], false, "");
        forward.Add(steps[^1]);
        steps.RemoveAt(steps.Count - 1);
        return Land(steps.Count - 1, entityAlive, revisionOf, "Geri dönülen");
    }

    /// <summary>Walks the forward branch one step with the same checks as Back; with nothing in front it stays where it is.</summary>
    public DrillBackResult Forward(Func<DrillTarget, bool> entityAlive, Func<DrillTarget, string>? revisionOf = null)
    {
        ArgumentNullException.ThrowIfNull(entityAlive);
        if (!CanGoForward) return new(Current, false, "");
        steps.Add(forward[^1]);
        forward.RemoveAt(forward.Count - 1);
        return Land(steps.Count - 1, entityAlive, revisionOf, "İleri gidilen");
    }

    DrillBackResult Land(int index, Func<DrillTarget, bool> entityAlive, Func<DrillTarget, string>? revisionOf, string direction)
    {
        var landing = steps[index];
        if (landing.EntityId.Length == 0) return new(landing, false, "");
        if (!entityAlive(landing))
        {
            // The record is gone; the screen it lived on is still worth returning to, and the crumb stops claiming it.
            var withoutEntity = landing with { EntityKind = "", EntityId = "", EntityLabel = "", Revision = "" };
            steps[index] = withoutEntity;
            return new(withoutEntity, true, $"{direction} kayıt artık yok; {withoutEntity.Title} ekranına dönüldü.");
        }
        if (revisionOf is not null && landing.Revision.Length > 0)
        {
            var now = (revisionOf(landing) ?? "").Trim();
            if (now.Length > 0 && !string.Equals(now, landing.Revision, StringComparison.Ordinal))
            {
                var current = landing with { Revision = now };
                steps[index] = current;
                return new(current, false, $"{direction} kayıt bu arada değişti; güncel hali gösteriliyor.", RevisionChanged: true);
            }
        }
        return new(landing, false, "");
    }

    /// <summary>The board changed store: a trail dug through one store's data means nothing in another's, forward branch included.</summary>
    public DrillTarget SwitchStore(string storeKey)
    {
        var wanted = (storeKey ?? "").Trim();
        if (wanted.Length == 0 || string.Equals(wanted, CurrentStoreKey, StringComparison.Ordinal)) return Current;
        var root = steps[0] with { StoreKey = wanted };
        steps.Clear();
        forward.Clear();
        steps.Add(root);
        return root;
    }

    public DrillHistoryState Snapshot() => new(steps.ToList(), forward.ToList());

    /// <summary>
    /// Puts a saved trail back under this session's rules. The saved root store must be one this session offers
    /// (or every store), else the whole trail is left behind; each later step must name a screen this build has
    /// and pass the same store check a live link passes, else the trail is cut there; a step whose record is gone
    /// keeps its screen and drops the record. The forward branch is validated the same way, and nothing here
    /// trusts the saved labels: they are re-sanitized on the way in.
    /// </summary>
    public DrillRestoreResult Restore(DrillHistoryState saved, Func<string, bool> routeExists, IReadOnlyCollection<string> allowedStoreKeys, Func<DrillTarget, bool> entityAlive)
    {
        ArgumentNullException.ThrowIfNull(saved); ArgumentNullException.ThrowIfNull(routeExists); ArgumentNullException.ThrowIfNull(allowedStoreKeys); ArgumentNullException.ThrowIfNull(entityAlive);
        var notices = new List<string>();
        var live = steps[0];
        steps.Clear(); forward.Clear();
        var savedRoot = saved.Steps.Count > 0 ? saved.Steps[0] : null;
        var store = savedRoot?.StoreKey ?? DashboardStoreFilter.AllStoresKey;
        if (store != DashboardStoreFilter.AllStoresKey && !allowedStoreKeys.Any(key => string.Equals(key, store, StringComparison.Ordinal)))
        {
            steps.Add(live with { StoreKey = DashboardStoreFilter.AllStoresKey });
            notices.Add("Kaydedilmiş gezinti izi bu oturumda sunulmayan bir mağazaya aitti; iz bırakıldı.");
            return new(1, 0, notices);
        }
        steps.Add(live with { StoreKey = store });
        var cut = false;
        foreach (var step in saved.Steps.Skip(1))
        {
            if (!Admit(step, routeExists, allowedStoreKeys, entityAlive, notices, steps)) { cut = true; break; }
        }
        if (!cut)
            foreach (var step in Enumerable.Reverse(saved.Forward).ToList())
                if (!Admit(step, routeExists, allowedStoreKeys, entityAlive, notices, forward)) break;
        // The forward branch is kept as a stack (the next step last), so it was admitted from the far end.
        forward.Reverse();
        while (steps.Count > MaxSteps) steps.RemoveAt(1);
        return new(steps.Count, forward.Count, notices);
    }

    bool Admit(DrillTarget step, Func<string, bool> routeExists, IReadOnlyCollection<string> allowedStoreKeys, Func<DrillTarget, bool> entityAlive, List<string> notices, List<DrillTarget> into)
    {
        var clean = step with { EntityLabel = SafeLabel(step) == step.Title ? "" : SafeLabel(step) };
        if (!routeExists(clean.Route)) { notices.Add("Gezinti izindeki bir ekran bu yapıda yok; iz orada kesildi."); return false; }
        if (Refusal(clean, allowedStoreKeys).Length > 0) { notices.Add("Gezinti izindeki bir adım bu oturumda sunulmayan bir mağazaya aitti; iz orada kesildi."); return false; }
        if (clean.EntityId.Length > 0 && !entityAlive(clean))
        {
            clean = clean with { EntityKind = "", EntityId = "", EntityLabel = "", Revision = "" };
            notices.Add($"Gezinti izindeki bir kayıt artık yok; {clean.Title} ekranı korundu.");
        }
        into.Add(clean);
        return true;
    }

    public string TrailText()
    {
        var labels = steps.Select(SafeLabel).ToList();
        if (labels.Count > VisibleCrumbs) labels = new List<string> { labels[0], "…", labels[^2], labels[^1] };
        return string.Join("  /  ", labels);
    }
}

/// <summary>The trail's saved form (#889): a strict shape — routes by their key pattern, bounded strings, at most <see cref="DrillThroughStack.MaxSteps"/> steps, labels that pass the redaction unchanged — so a tampered or stale preference cannot smuggle anything into the shell.</summary>
public static class DrillHistoryCodec
{
    public const string PreferenceKey = "shell:trail";
    const int TextLimit = 200;
    static readonly Regex RouteShape = new("^[a-z][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    sealed class Entry { public string Route { get; set; } = ""; public string Title { get; set; } = ""; public string StoreKey { get; set; } = DashboardStoreFilter.AllStoresKey; public string EntityKind { get; set; } = ""; public string EntityId { get; set; } = ""; public string EntityLabel { get; set; } = ""; public string Revision { get; set; } = ""; }
    sealed class Envelope { public List<Entry> Steps { get; set; } = new(); public List<Entry> Forward { get; set; } = new(); }

    public static string Serialize(DrillHistoryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonSerializer.Serialize(new Envelope { Steps = state.Steps.Take(DrillThroughStack.MaxSteps).Select(ToEntry).ToList(), Forward = state.Forward.Take(DrillThroughStack.MaxSteps).Select(ToEntry).ToList() });
    }

    static Entry ToEntry(DrillTarget t) => new() { Route = t.Route, Title = t.Title, StoreKey = t.StoreKey, EntityKind = t.EntityKind, EntityId = t.EntityId, EntityLabel = DrillThroughStack.SafeLabel(t) == t.Title ? "" : DrillThroughStack.SafeLabel(t), Revision = t.Revision };

    public static bool TryDeserialize(string payload, out DrillHistoryState state)
    {
        state = new DrillHistoryState(Array.Empty<DrillTarget>(), Array.Empty<DrillTarget>());
        if (string.IsNullOrWhiteSpace(payload)) return false;
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(payload, Options); } catch (JsonException) { return false; }
        if (envelope is null || envelope.Steps.Count == 0 || envelope.Steps.Count > DrillThroughStack.MaxSteps || envelope.Forward.Count > DrillThroughStack.MaxSteps) return false;
        var steps = new List<DrillTarget>(); var forward = new List<DrillTarget>();
        foreach (var entry in envelope.Steps) { if (!TryTarget(entry, out var target)) return false; steps.Add(target); }
        foreach (var entry in envelope.Forward) { if (!TryTarget(entry, out var target)) return false; forward.Add(target); }
        state = new DrillHistoryState(steps, forward);
        return true;
    }

    static bool TryTarget(Entry entry, out DrillTarget target)
    {
        target = null!;
        if (entry is null || !RouteShape.IsMatch(entry.Route ?? "")) return false;
        var title = Bounded(entry.Title); var store = Bounded(entry.StoreKey); var kind = Bounded(entry.EntityKind); var id = Bounded(entry.EntityId); var revision = Bounded(entry.Revision);
        if (title is null || store is null || kind is null || id is null || revision is null) return false;
        if (store.Length == 0) store = DashboardStoreFilter.AllStoresKey;
        var label = (entry.EntityLabel ?? "").Trim();
        if (label.Length > TextLimit || !string.Equals(AuditStore.Redact(label), label, StringComparison.Ordinal)) label = "";
        target = new DrillTarget(entry.Route!, AuditStore.Redact(title), store, kind, id, label, revision);
        return true;
    }

    static string? Bounded(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length > TextLimit || text.Any(char.IsControl) ? null : text;
    }
}
