using System.Globalization;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>A proposal to return one channel mapping to its previous revision: the current and the previous targets, when the previous one was written, whether the proposal is blocked and why, the impact of the return (#919), the snapshot version it binds to, and the words.</summary>
public sealed record MappingRollbackProposal(TaxonomyKind Kind, string Marketplace, string ShopId, string ExternalKey, string CurrentLocalId, string CurrentName, string PreviousLocalId, string PreviousName, DateTime PreviousChangedUtc, bool Blocked, string BlockReason, int UnrelatedLaterEdits, MappingImpactPreview? Impact, string Scope, long SnapshotVersion, string Headline, IReadOnlyList<string> Lines)
{
    public string Body => string.Join(Environment.NewLine, new[] { Headline }.Concat(Lines));
}

/// <summary>
/// The mapping rollback proposal (#920). The taxonomy owner keeps every mapping write in its history (#843); a
/// rollback returns one external key of a channel and shop to the local entry it named before its latest change.
/// The proposal names both targets and the moment the previous one was written, previews the return with the
/// impact of #919 (the products whose readiness changes, their plans), binds to the channel scope's snapshot
/// version (#918), and is blocked — nothing to apply — when there is no previous revision, when the previous target
/// no longer exists or is inactive, or when the scope saw later edits of other keys after the mapping's latest
/// change (the operator reviews those first; a rollback does not silently rewind on top of them). Applying honours
/// the stale guard and writes the return through the owner as a ROLLBACK history entry, so the history says it was
/// a return and not a fresh mapping.
/// </summary>
public static class MappingRollback
{
    public const string Action = "ROLLBACK";

    public static MappingRollbackProposal Propose(string? directory, TaxonomyKind kind, string externalKey, string marketplace, string shopId, IReadOnlyList<CatalogProduct> products, IReadOnlyList<ChannelProductPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(products); ArgumentNullException.ThrowIfNull(plans);
        var market = (marketplace ?? "").Trim().ToLowerInvariant(); var shop = (shopId ?? "").Trim(); var key = (externalKey ?? "").Trim();
        var taxonomy = new TaxonomyStore(directory); var entries = taxonomy.List(kind).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var scope = TaxonomySnapshotStore.ChannelScope(market, shop); var version = new TaxonomySnapshotStore(directory).Refresh(scope, DateTime.UtcNow).Version;
        var history = taxonomy.History(kind, market, shop, 1000);
        var own = history.Where(h => h.ExternalKey == key).ToList();
        var current = own.FirstOrDefault();
        var currentName = current is null ? "" : entries.GetValueOrDefault(current.LocalId)?.Name ?? "silinmiş kayıt";
        string Name(string id) => entries.GetValueOrDefault(id)?.Name ?? "silinmiş kayıt";
        MappingRollbackProposal Blocked(string reason, string previousId = "", DateTime? previousAt = null)
            => new(kind, market, shop, key, current?.LocalId ?? "", currentName, previousId, previousId.Length > 0 ? Name(previousId) : "", previousAt ?? DateTime.MinValue, true, reason, 0, null, scope, version, $"'{key}' geri alınamaz: {reason}", Array.Empty<string>());
        if (current is null) return Blocked("bu anahtarın eşleme geçmişi yok");
        var previous = own.Skip(1).FirstOrDefault(h => h.LocalId != current.LocalId);
        if (previous is null) return Blocked("önceki bir sürüm yok; anahtar hep aynı kayda bağlıydı");
        if (!entries.TryGetValue(previous.LocalId, out var target)) return Blocked("önceki hedef silinmiş", previous.LocalId, previous.ChangedUtc);
        if (!target.Active) return Blocked($"önceki hedef pasif: {target.Name}", previous.LocalId, previous.ChangedUtc);
        var unrelated = history.Count(h => h.ExternalKey != key && h.ChangedUtc > current.ChangedUtc);
        if (unrelated > 0)
        {
            var blocked = Blocked($"bu eşlemeden sonra kapsamda {unrelated.ToString(CultureInfo.CurrentCulture)} başka düzenleme yapıldı; önce onları gözden geçirin", previous.LocalId, previous.ChangedUtc);
            return blocked with { UnrelatedLaterEdits = unrelated };
        }
        var impact = MappingImpact.Preview(directory, kind, key, previous.LocalId, market, shop, products, plans);
        var headline = $"'{key}': {currentName} → {target.Name} (önceki sürüm, {previous.ChangedUtc.ToString("g", CultureInfo.CurrentCulture)} UTC)";
        var lines = new List<string> { impact.Headline };
        lines.AddRange(impact.Lines);
        return new(kind, market, shop, key, current.LocalId, currentName, previous.LocalId, target.Name, previous.ChangedUtc, false, "", 0, impact, scope, impact.SnapshotVersion, headline, lines);
    }

    /// <summary>Applies a proposal: refused when blocked or when the taxonomy moved since the proposal; the return written through the owner as a ROLLBACK entry; the scope's snapshot refreshed.</summary>
    public static TaxonomySnapshot Apply(string? directory, MappingRollbackProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Blocked) throw new InvalidOperationException(proposal.Headline);
        var current = new TaxonomySnapshotStore(directory).Refresh(proposal.Scope, DateTime.UtcNow).Version;
        if (current != proposal.SnapshotVersion) throw new InvalidOperationException($"Taksonomi bu öneriden sonra değişti (sürüm {proposal.SnapshotVersion.ToString(CultureInfo.CurrentCulture)} → {current.ToString(CultureInfo.CurrentCulture)}); öneriyi yeniden alın.");
        new TaxonomyStore(directory).Map(proposal.Kind, proposal.ExternalKey, proposal.PreviousLocalId, proposal.Marketplace, proposal.ShopId, Action);
        return new TaxonomySnapshotStore(directory).Refresh(proposal.Scope, DateTime.UtcNow);
    }
}
