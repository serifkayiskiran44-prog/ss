namespace TrMarketplaceHubDesktop;

public sealed record MissingSourceCase(string Supplier, string ProductId, DateTimeOffset FirstMissingUtc, DateTimeOffset LastSeenUtc, string State, int ExistingStock, decimal ExistingPrice, bool ListingPreserved);
public sealed record MissingSourcePolicy(TimeSpan GracePeriod, int MassMissingThreshold = 100);
public sealed record DeactivatePreview(string Supplier, string ProductId, string Key, string Reason);
public sealed record QuarantineAudit(string Supplier, string ProductId, string Action, DateTimeOffset AtUtc, string Reason);

public sealed class SourceMissingQuarantine
{
    private readonly object gate = new(); private readonly Dictionary<(string Supplier, string Product), MissingSourceCase> cases = new(); private readonly List<QuarantineAudit> audits = new();
    public IReadOnlyList<QuarantineAudit> Audits => audits.ToArray();
    public MissingSourceCase Observe(string supplier, string productId, bool present, int existingStock, decimal existingPrice, DateTimeOffset now, MissingSourcePolicy policy)
    { lock (gate) { var key = (supplier, productId); if (present) { var recovered = cases.Remove(key); if (recovered) audits.Add(new(supplier, productId, "LOCAL_RECOVERY", now, "source-returned")); return new(supplier, productId, now, now, "PRESENT", existingStock, existingPrice, true); } var old = cases.TryGetValue(key, out var value) ? value : new(supplier, productId, now, now, "WARNING", existingStock, existingPrice, true); var state = now - old.FirstMissingUtc >= policy.GracePeriod ? "PENDING_ACTION" : "WARNING"; var item = old with { State = state, LastSeenUtc = old.LastSeenUtc, ListingPreserved = true }; cases[key] = item; return item; } }
    public IReadOnlyList<MissingSourceCase> List(string? supplier = null, string? state = null) { lock (gate) return cases.Values.Where(x => (supplier is null || x.Supplier.Equals(supplier, StringComparison.OrdinalIgnoreCase)) && (state is null || x.State.Equals(state, StringComparison.OrdinalIgnoreCase))).ToArray(); }
    public DeactivatePreview PreviewDeactivate(MissingSourceCase item, string reason) => new(item.Supplier, item.ProductId, $"{item.Supplier}|{item.ProductId}|{item.FirstMissingUtc:O}", reason);
    public QuarantineAudit ApplyLocalDeactivate(DeactivatePreview preview, bool approved) { if (!approved) throw new InvalidOperationException("EXPLICIT_APPROVAL_REQUIRED"); lock (gate) { var audit = new QuarantineAudit(preview.Supplier, preview.ProductId, "LOCAL_DEACTIVATE", DateTimeOffset.UtcNow, preview.Reason); audits.Add(audit); return audit; } }
    public static bool IsMassMissing(int missingCount, MissingSourcePolicy policy) => missingCount >= policy.MassMissingThreshold;
}
