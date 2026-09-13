using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

/// <summary>One event of a chain as the audit centre shows it: when, which module and action, how it ended, a safe one-line summary, the store it belongs to, and the entity it points at (a product, an order) when there is one.</summary>
public sealed record ChainEvent(DateTime AtUtc, string Module, string Action, string Outcome, string Summary, string StoreKey, DrillTarget? Entity);

/// <summary>A correlation's chain: the visible events in time order, the modules they span, the overall outcome, the span, how many events belong to stores this session may not open, and a note when there is nothing or something was hidden.</summary>
public sealed record CorrelationChainView(string Correlation, IReadOnlyList<ChainEvent> Events, IReadOnlyList<string> Modules, string Outcome, DateTime? StartedUtc, DateTime? FinishedUtc, int Hidden, string Note)
{
    public bool IsEmpty => Events.Count == 0;
    public TimeSpan? Duration => StartedUtc is { } s && FinishedUtc is { } f ? f - s : null;
}

/// <summary>
/// Correlation deep-link navigation (#883). Every audit row written while a guarded command, a shortcut or a
/// measured view load is in flight carries that operation's correlation id (#879's activity registry stamps it), so
/// an import, the jobs it queued and the orders it touched share one id across modules; a
/// <c>monobridge://correlation/&lt;id&gt;[?store=…]</c> link opens the audit centre on that chain through the same
/// trail, refusal and reveal every workspace uses (#813). The chain shows safe event summaries — the sanitized
/// detail, capped, a raw body replaced — never a log line; events of a store this session may not open are counted
/// and left out, and a chain nobody wrote is an empty view with a note, not an error. An id is an identifier only.
/// </summary>
public static class CorrelationChain
{
    public const string Kind = "correlation";
    public const string Route = "diagnostics";
    public const int MaxSummaryLength = 160;
    public const string MissingNote = "Bu korelasyon için kayıt yok (silinmiş, başka bir kurulumda yazılmış veya henüz yazılmamış).";
    public const string InvalidNote = "Korelasyon kimliği geçersiz; harf, rakam ve tire ile 4–64 karakter olmalı.";
    static readonly Regex IdShape = new("^[A-Za-z0-9-]{4,64}$", RegexOptions.Compiled);

    /// <summary>The id itself when it is an identifier (letters, digits, dashes, 4–64), otherwise empty — never text.</summary>
    public static string SafeId(string? value) => value is not null && IdShape.IsMatch(value.Trim()) ? value.Trim() : "";
    public static bool IsId(string? value) => SafeId(value).Length > 0;

    /// <summary>The deep link to a chain, scoped to a store when the chain belongs to one.</summary>
    public static string Link(string correlation, string storeKey = DashboardStoreFilter.AllStoresKey)
    {
        var id = SafeId(correlation); if (id.Length == 0) throw new ArgumentException(InvalidNote, nameof(correlation));
        return WorkspaceLinks.Format(new DrillTarget(Route, "Tanılama / audit", string.IsNullOrWhiteSpace(storeKey) ? DashboardStoreFilter.AllStoresKey : storeKey, Kind, id, id));
    }

    public static string HiddenNote(int hidden) => $"{hidden} kayıt bu oturumun açamadığı bir mağazaya ait; gösterilmedi.";

    /// <summary>Whether a row may be shown in this session: a store-less row always, a store's row only when the store is allowed (null: every store).</summary>
    public static bool MayShow(AuditEvent audit, IReadOnlyCollection<string>? allowedStoreKeys)
    {
        var store = StoreKeyOf(audit);
        return store == DashboardStoreFilter.AllStoresKey || allowedStoreKeys is null || allowedStoreKeys.Contains(store, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The rows of one correlation this session may show, oldest first.</summary>
    public static IReadOnlyList<AuditEvent> Visible(string? correlation, IReadOnlyList<AuditEvent> events, IReadOnlyCollection<string>? allowedStoreKeys)
    {
        ArgumentNullException.ThrowIfNull(events);
        var id = SafeId(correlation);
        return id.Length == 0 ? Array.Empty<AuditEvent>() : events.Where(e => string.Equals(e.Correlation, id, StringComparison.Ordinal) && MayShow(e, allowedStoreKeys)).OrderBy(e => e.AtUtc).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The chain for an id from the events given (any correlation), scoped to the stores this session may open (null: every store).</summary>
    public static CorrelationChainView Compose(string? correlation, IReadOnlyList<AuditEvent> events, IReadOnlyCollection<string>? allowedStoreKeys, Func<string, string>? titleFor = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var id = SafeId(correlation);
        if (id.Length == 0) return new CorrelationChainView("", Array.Empty<ChainEvent>(), Array.Empty<string>(), "", null, null, 0, InvalidNote);
        var mine = events.Where(e => string.Equals(e.Correlation, id, StringComparison.Ordinal)).OrderBy(e => e.AtUtc).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        var visible = new List<ChainEvent>(); var hidden = 0;
        foreach (var e in mine)
        {
            if (!MayShow(e, allowedStoreKeys)) { hidden++; continue; }
            visible.Add(ToEvent(e, StoreKeyOf(e), titleFor));
        }
        var note = mine.Count == 0 ? MissingNote : hidden > 0 ? HiddenNote(hidden) : "";
        return new CorrelationChainView(id, visible, visible.Select(v => v.Module).Distinct(StringComparer.Ordinal).ToList(), OutcomeOf(visible), visible.FirstOrDefault()?.AtUtc, visible.LastOrDefault()?.AtUtc, hidden, note);
    }

    /// <summary>The one-line headline of a chain: count, modules, span, outcome.</summary>
    public static string Headline(CorrelationChainView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.IsEmpty) return view.Note;
        var span = view.Duration is { } d ? (d.TotalSeconds < 1 ? "1 sn altı" : d.ToString(d.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss")) : "";
        return $"{view.Events.Count} olay · {string.Join(", ", view.Modules)}{(span.Length > 0 ? " · " + span : "")} · {view.Outcome}";
    }

    /// <summary>A safe one-line summary of an audit detail: sanitized, a raw body replaced, capped.</summary>
    public static string Summarize(string? detail)
    {
        var text = Regex.Replace(AuditStore.Redact(detail ?? ""), @"\s+", " ").Trim();
        if (text.Length == 0) return "";
        if (StatusTooltip.LooksLikeRawPayload(text)) return StatusTooltip.RawPayloadHidden;
        return text.Length <= MaxSummaryLength ? text : text[..(MaxSummaryLength - 1)] + "…";
    }

    public static string StoreKeyOf(AuditEvent audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return string.IsNullOrWhiteSpace(audit.Marketplace) ? DashboardStoreFilter.AllStoresKey : DashboardStoreFilter.KeyFor(audit.Marketplace, string.IsNullOrWhiteSpace(audit.ShopId) ? "default" : audit.ShopId);
    }

    static string OutcomeOf(IReadOnlyList<ChainEvent> events)
    {
        if (events.Count == 0) return "";
        if (events.Any(e => e.Outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase) || e.Outcome.Equals("Error", StringComparison.OrdinalIgnoreCase))) return "hata";
        if (events.Any(e => e.Outcome.Equals("WARN", StringComparison.OrdinalIgnoreCase) || e.Outcome.Equals("Warning", StringComparison.OrdinalIgnoreCase))) return "uyarı";
        return "tamam";
    }

    static ChainEvent ToEvent(AuditEvent e, string store, Func<string, string>? titleFor)
    {
        DrillTarget? entity = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(e.OrderId) && store != DashboardStoreFilter.AllStoresKey)
                entity = new DrillTarget(WorkspaceLinks.RouteByKind["order"], titleFor?.Invoke("orders") ?? "Sipariş ve kargo", store, "order", $"{e.Marketplace}|{e.ShopId}|{e.OrderId}", "Sipariş " + e.OrderId);
            else if (!string.IsNullOrWhiteSpace(e.ProductId))
                entity = new DrillTarget(WorkspaceLinks.RouteByKind["product"], titleFor?.Invoke("products") ?? "Ürünler", DashboardStoreFilter.AllStoresKey, "product", e.ProductId, "Ürün");
        }
        catch (Exception) { entity = null; }
        return new ChainEvent(e.AtUtc, e.Module, e.Action, e.Outcome, Summarize(e.Detail), store, entity);
    }
}
