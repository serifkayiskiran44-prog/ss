namespace TrMarketplaceHubDesktop;

public sealed record FeedRunMetrics(string Supplier, int ProductCount, int ZeroStockCount, decimal AveragePrice, int CategoryChanges, int BrandChanges, DateTimeOffset AtUtc);
public sealed record AnomalyProfile(decimal MaxCountDeltaPercent = 30, decimal MaxZeroStockPercent = 80, decimal MaxPriceDeltaPercent = 25, int MaxTaxonomyChanges = 100);
public sealed record FeedAnomalyReport(string Supplier, bool ApplyBlocked, IReadOnlyList<string> Reasons, FeedRunMetrics Current, FeedRunMetrics? Previous);
public sealed record AnomalyOverrideAudit(string Supplier, string Reason, string Actor, DateTimeOffset AtUtc);

public sealed class DropshipAnomalyGuard
{
    private readonly Dictionary<string, AnomalyProfile> profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AnomalyOverrideAudit> audits = new();
    public IReadOnlyList<AnomalyOverrideAudit> Audits => audits.ToArray();
    public void SetProfile(string supplier, AnomalyProfile profile) => profiles[supplier] = profile;
    public FeedAnomalyReport Evaluate(FeedRunMetrics current, FeedRunMetrics? previous)
    {
        var p = profiles.TryGetValue(current.Supplier, out var configured) ? configured : new AnomalyProfile(); var reasons = new List<string>();
        if (previous is not null) { var delta = previous.ProductCount == 0 ? 100 : Math.Abs((current.ProductCount - previous.ProductCount) * 100m / previous.ProductCount); if (delta > p.MaxCountDeltaPercent) reasons.Add("PRODUCT_COUNT_SPIKE"); if (current.ProductCount < previous.ProductCount && previous.ProductCount - current.ProductCount > previous.ProductCount * p.MaxCountDeltaPercent / 100m) reasons.Add("MISSING_PRODUCTS"); var zero = current.ProductCount == 0 ? 100 : current.ZeroStockCount * 100m / current.ProductCount; if (zero > p.MaxZeroStockPercent) reasons.Add("MASS_ZERO_STOCK"); if (previous.AveragePrice != 0 && Math.Abs((current.AveragePrice - previous.AveragePrice) * 100m / previous.AveragePrice) > p.MaxPriceDeltaPercent) reasons.Add("PRICE_JUMP"); }
        if (current.CategoryChanges + current.BrandChanges > p.MaxTaxonomyChanges) reasons.Add("MASS_TAXONOMY_CHANGE"); return new(current.Supplier, reasons.Count > 0, reasons, current, previous);
    }
    public AnomalyOverrideAudit ApproveOverride(FeedAnomalyReport report, string reason, string actor) { if (!report.ApplyBlocked) throw new InvalidOperationException("NO_ANOMALY_TO_OVERRIDE"); if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("Gerekçe ve actor zorunludur."); var audit = new AnomalyOverrideAudit(report.Supplier, reason, actor, DateTimeOffset.UtcNow); audits.Add(audit); return audit; }
}
