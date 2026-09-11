namespace TrMarketplaceHubDesktop;

public sealed record OfferSnapshot(string Channel, string ShopId, string ProductId, string OfferId, decimal Price, string Currency, DateTimeOffset ObservedAt, string Source);
public sealed record CompetitionObservation(string OfferId, string Status, decimal Difference, decimal DifferencePercent, DateTimeOffset ObservedAt);

public static class CompetitionObserver
{
    public static IReadOnlyList<CompetitionObservation> Evaluate(IEnumerable<OfferSnapshot> offers, string channel, string shopId, string productId, decimal localPrice, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(offers);
        var scoped = offers.Where(x => x.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase) && x.ShopId.Equals(shopId, StringComparison.Ordinal) && x.ProductId.Equals(productId, StringComparison.Ordinal)).GroupBy(x => x.OfferId).Select(x => x.OrderByDescending(y => y.ObservedAt).First());
        return scoped.Select(x =>
        {
            if (x.Price <= 0 || x.Currency.Length == 0 || string.IsNullOrWhiteSpace(x.Source)) return new CompetitionObservation(x.OfferId, "UNAVAILABLE", 0, 0, x.ObservedAt);
            var delta = x.Price - localPrice;
            return new CompetitionObservation(x.OfferId, nowUtc - x.ObservedAt > TimeSpan.FromHours(24) ? "STALE" : "OBSERVED", delta, localPrice == 0 ? 0 : delta / localPrice * 100, x.ObservedAt);
        }).ToArray();
    }
}
