namespace TrMarketplaceHubDesktop;

public sealed record ChannelFee(string Channel, string Category, decimal CommissionPercent, decimal FixedFee, string Currency, DateTimeOffset EffectiveFrom, DateTimeOffset? EffectiveTo, string Provenance, DateTimeOffset CapturedUtc);
public sealed record FeeLookupResult(ChannelFee? Fee, string Status, string Reason);

public sealed class ChannelFeeCatalog
{
    private static readonly string[] Sources = { "official-api", "official-csv", "manual-verified" };
    private readonly List<ChannelFee> fees = new();
    private readonly List<ChannelFee> history = new();
    public IReadOnlyList<ChannelFee> History => history.ToArray();
    public void Import(IEnumerable<ChannelFee> incoming)
    { foreach (var fee in incoming) { if (fee.CommissionPercent < 0 || fee.FixedFee < 0 || string.IsNullOrWhiteSpace(fee.Channel) || string.IsNullOrWhiteSpace(fee.Category) || !Sources.Contains(fee.Provenance, StringComparer.OrdinalIgnoreCase) || fee.EffectiveTo <= fee.EffectiveFrom) throw new ArgumentException("Fee kaydı doğrulanamadı."); fees.RemoveAll(x => x.Channel.Equals(fee.Channel, StringComparison.OrdinalIgnoreCase) && x.Category.Equals(fee.Category, StringComparison.OrdinalIgnoreCase) && x.EffectiveFrom == fee.EffectiveFrom); fees.Add(fee); history.Add(fee); } }
    public FeeLookupResult Lookup(string channel, string category, DateTimeOffset at, DateTimeOffset now, TimeSpan staleAfter)
    { var candidates = fees.Where(x => x.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase) && x.Category.Equals(category, StringComparison.OrdinalIgnoreCase) && x.EffectiveFrom <= at && (x.EffectiveTo is null || at <= x.EffectiveTo)).OrderByDescending(x => x.EffectiveFrom).ToArray(); if (candidates.Length == 0) return new(null, "NOT_CONFIGURED", "Doğrulanmış fee kaydı yok."); var fee = candidates[0]; if (now - fee.CapturedUtc > staleAfter || fee.EffectiveTo < now) return new(fee, "STALE", "Fee kaydı güncel değil veya süresi doldu."); return new(fee, "READY", "Doğrulanmış fee kaydı."); }
    public static decimal Apply(decimal salePrice, ChannelFee fee) { if (salePrice < 0) throw new ArgumentException("Satış fiyatı negatif olamaz."); return decimal.Round(salePrice * fee.CommissionPercent / 100m + fee.FixedFee, 4, MidpointRounding.AwayFromZero); }
    public IReadOnlyList<ChannelFee> Export() => fees.OrderBy(x => x.Channel).ThenBy(x => x.Category).ThenByDescending(x => x.EffectiveFrom).ToArray();
}
