namespace TrMarketplaceHubDesktop;

public sealed record EtsyRemoteSnapshot(long ListingId, string ShopId, string Title, decimal Price, int Stock, string State, string Category, string MediaFingerprint, string ProfileVersion, DateTimeOffset CheckedUtc);
public sealed record EtsyLocalSnapshot(long ListingId, string ShopId, string Title, decimal Price, int Stock, string State, string Category, string MediaFingerprint, string ProfileVersion, DateTimeOffset UpdatedUtc);
public sealed record EtsyDrift(string ShopId, long ListingId, IReadOnlyList<string> Fields, string Classification, DateTimeOffset CheckedUtc);

public static class EtsyBatchDrift
{
    public static IEnumerable<IReadOnlyList<long>> Chunks(IEnumerable<long> ids, int size = 100) { if (size is < 1 or > 100) throw new ArgumentException("Etsy batch limiti 1-100 olmalıdır."); var chunk = new List<long>(size); foreach (var id in ids) { if (id <= 0) throw new ArgumentException("Listing ID pozitif olmalıdır."); chunk.Add(id); if (chunk.Count == size) { yield return chunk.ToArray(); chunk.Clear(); } } if (chunk.Count > 0) yield return chunk.ToArray(); }
    public static EtsyDrift Compare(EtsyRemoteSnapshot remote, EtsyLocalSnapshot local, DateTimeOffset now, TimeSpan staleAfter, bool expectedChange = false)
    { if (remote.ShopId != local.ShopId || remote.ListingId != local.ListingId) throw new InvalidOperationException("WRONG_SHOP_OR_LISTING"); var fields = new List<string>(); if (remote.Title != local.Title) fields.Add("title"); if (remote.Price != local.Price) fields.Add("price"); if (remote.Stock != local.Stock) fields.Add("stock"); if (remote.State != local.State) fields.Add("state"); if (remote.Category != local.Category) fields.Add("category"); if (remote.MediaFingerprint != local.MediaFingerprint) fields.Add("media"); if (remote.ProfileVersion != local.ProfileVersion) fields.Add("profile"); var stale = now - remote.CheckedUtc > staleAfter; var cls = stale ? "STALE" : fields.Count == 0 ? "IN_SYNC" : expectedChange ? "EXPECTED" : remote.State == "missing" ? "MISSING" : "MANUAL_REMOTE"; return new(remote.ShopId, remote.ListingId, fields, cls, remote.CheckedUtc); }
}
