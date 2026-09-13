namespace TrMarketplaceHubDesktop;

/// <summary>
/// One way to open an entity from anywhere (#813): a <c>monobridge://&lt;kind&gt;/&lt;id&gt;[?store=&lt;key&gt;]</c>
/// link becomes a <see cref="DrillTarget"/> on the same trail the dashboard uses (#810), so Back, the crumb text,
/// the deleted-entity fallback and the wrong-store refusal are the same in every workspace. The link is untrusted
/// input: it may name a kind and an id, and the route is always taken from the kind table, never from the link.
/// Anything malformed -- another scheme, an unknown kind, an empty or oversized id, extra path segments, control
/// characters, any query key other than <c>store</c> -- yields no target at all rather than a partial one.
/// There is deliberately no <c>report</c> kind: the shell registers no reports route, and a link must not
/// pretend to open a workspace this build does not have.
/// </summary>
public static class WorkspaceLinks
{
    public const string Scheme = "monobridge://";
    const int MaxIdLength = 200;

    public static IReadOnlyDictionary<string, string> RouteByKind { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["product"] = "products",
        ["order"] = "orders",
        ["source"] = "xml",
        ["correlation"] = CorrelationChain.Route, // #883: the audit centre's chain view
    };

    public static string Format(DrillTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var link = $"{Scheme}{target.EntityKind}/{Uri.EscapeDataString(target.EntityId)}";
        return target.StoreKey == DashboardStoreFilter.AllStoresKey ? link : $"{link}?store={Uri.EscapeDataString(target.StoreKey)}";
    }

    public static DrillTarget? Parse(string? link, Func<string, string> titleFor)
    {
        ArgumentNullException.ThrowIfNull(titleFor);
        if (string.IsNullOrWhiteSpace(link) || !link.StartsWith(Scheme, StringComparison.Ordinal)) return null;
        var rest = link[Scheme.Length..];
        var query = "";
        var cut = rest.IndexOf('?');
        if (cut >= 0) { query = rest[(cut + 1)..]; rest = rest[..cut]; }

        var segments = rest.Split('/');
        if (segments.Length != 2 || !RouteByKind.TryGetValue(segments[0], out var route)) return null;
        // Raw whitespace or control characters are malformed before any unescaping; a real link escapes them.
        if (segments[1].Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return null;
        string id;
        try { id = Uri.UnescapeDataString(segments[1]); } catch (UriFormatException) { return null; }
        if (!UsableId(id)) return null;

        var store = DashboardStoreFilter.AllStoresKey;
        if (query.Length > 0)
        {
            var pairs = query.Split('&');
            if (pairs.Length != 1 || !pairs[0].StartsWith("store=", StringComparison.Ordinal)) return null;
            try { store = Uri.UnescapeDataString(pairs[0]["store=".Length..]).Trim(); } catch (UriFormatException) { return null; }
            if (store.Length == 0) store = DashboardStoreFilter.AllStoresKey;
            if (!UsableId(store)) return null;
        }

        string title;
        try { title = titleFor(route); } catch (Exception) { return null; }
        return new DrillTarget(route, title, store, segments[0], id, id);
    }

    /// <summary>A search hit is a link only when it points at an entity a workspace can select; otherwise null, and the caller falls back to a plain screen jump.</summary>
    public static DrillTarget? FromSearchHit(GlobalSearchHit hit, Func<string, string> titleFor)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var kind = hit.Type switch { "Ürün" => "product", "XML kaynağı" => "source", "Sipariş" => "order", _ => null };
        if (kind is null || !RouteByKind.TryGetValue(kind, out var route) || route != hit.Route) return null;
        // An order is addressed by marketplace|shop|id; a bare id cannot be revealed and is not pretended to be.
        if (kind == "order" && hit.TargetId.Split('|').Length != 3) return null;
        return Parse($"{Scheme}{kind}/{Uri.EscapeDataString(hit.TargetId)}", titleFor);
    }

    static bool UsableId(string value)
    {
        // A '/' that arrived escaped is data (order ids carry them); a raw one was already a path separator and
        // failed the segment count. ".." is refused in any form.
        if (value.Length is 0 or > MaxIdLength) return false;
        if (value.Contains("..", StringComparison.Ordinal)) return false;
        return !value.Any(char.IsControl);
    }
}
