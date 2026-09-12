namespace TrMarketplaceHubDesktop;

public sealed record ProductWorkspaceSection(string Key, string Label);
public sealed record ProductWorkspaceSectionResolution(ProductWorkspaceSection Section, bool Found);
public sealed record ProductWorkspaceRoute(string Page, string SectionKey);

/// <summary>
/// The product workspace's sections as one catalogue (#801), so a deep link, the tab strip, the keyboard and
/// the restore-on-return path all name the same things. Keys are stable identifiers -- they are the link
/// surface -- while labels are display text and may be translated freely. A link naming a section this build
/// does not have resolves to the default section *and reports that it did*, so the caller can say so rather
/// than leaving the operator on a pane that silently is not the one they asked for.
/// </summary>
public static class ProductWorkspaceSections
{
    public const string PageKey = "products";

    public static IReadOnlyList<ProductWorkspaceSection> All { get; } =
    [
        new("identity", "Kimlik"),
        new("content", "İçerik"),
        new("price-stock", "Fiyat / stok"),
        new("media", "Görseller"),
        new("channel", "Kanallar"),
        new("audit", "Geçmiş"),
    ];

    public static ProductWorkspaceSection Default => All[0];

    public static ProductWorkspaceSectionResolution Resolve(string? key)
    {
        var wanted = (key ?? "").Trim();
        var match = All.FirstOrDefault(s => s.Key.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        return match is null ? new(Default, false) : new(match, true);
    }

    /// <summary>A deep link to one section; an unknown section still yields a link that opens the workspace.</summary>
    public static string Route(string? key) => PageKey + "#" + Resolve(key).Section.Key;

    public static ProductWorkspaceRoute ParseRoute(string? route)
    {
        var text = (route ?? "").Trim();
        var hash = text.IndexOf('#');
        return hash < 0 ? new(text, "") : new(text[..hash], text[(hash + 1)..].Trim());
    }
}

/// <summary>
/// Which section the workspace should show. An explicit deep link wins and is remembered; navigating without
/// one keeps the operator where they were, which is what makes returning to the workspace feel like returning
/// rather than restarting. An unknown key never overwrites a good remembered section.
/// </summary>
public sealed class ProductWorkspaceSectionMemory
{
    public string Current { get; private set; } = ProductWorkspaceSections.Default.Key;

    public void Remember(string? key)
    {
        var resolved = ProductWorkspaceSections.Resolve(key);
        if (resolved.Found) Current = resolved.Section.Key;
    }

    /// <summary>The section to show for a navigation that may or may not name one.</summary>
    public string Resolve(string? requestedKey)
    {
        if (!string.IsNullOrWhiteSpace(requestedKey)) Remember(requestedKey);
        return Current;
    }
}
