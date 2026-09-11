namespace TrMarketplaceHubDesktop;

public sealed record EtsyMetadataProfile(long Version, int TaxonomyId, string TaxonomyPath, IReadOnlyDictionary<string, string> Properties, string? SectionId, string? ShippingProfileId, string? ReturnPolicyId, int? ProcessingProfileId, bool Digital, DateTimeOffset CapturedUtc, string Source);
public sealed record EtsyMetadataSelection(EtsyMetadataProfile Profile, bool Stale, IReadOnlyList<string> Missing, string Status);

public sealed class EtsyMetadataCenter
{
    private readonly Dictionary<string, EtsyMetadataProfile> cache = new(StringComparer.Ordinal);
    private readonly List<EtsyMetadataProfile> history = new();
    public IReadOnlyList<EtsyMetadataProfile> History => history.ToArray();
    public EtsyMetadataProfile Cache(string key, EtsyMetadataProfile profile) { if (profile.TaxonomyId <= 0 || string.IsNullOrWhiteSpace(profile.TaxonomyPath)) throw new ArgumentException("Taxonomy metadata geçersiz."); cache[key] = profile; history.Add(profile); return profile; }
    public EtsyMetadataSelection Select(string key, DateTimeOffset now, TimeSpan staleAfter)
    { if (!cache.TryGetValue(key, out var profile)) return new(new(0, 0, "", new Dictionary<string, string>(), null, null, null, null, false, DateTimeOffset.MinValue, ""), true, new[] { "taxonomy" }, "NOT_CONFIGURED"); var missing = new List<string>(); if (!profile.Digital && string.IsNullOrWhiteSpace(profile.ShippingProfileId)) missing.Add("shipping-profile"); if (profile.Digital && !string.IsNullOrWhiteSpace(profile.ShippingProfileId)) missing.Add("digital-shipping-must-be-empty"); if (profile.ProcessingProfileId is null) missing.Add("processing-profile"); var stale = now - profile.CapturedUtc > staleAfter; if (stale) missing.Add("STALE_METADATA"); return new(profile, stale, missing, missing.Count == 0 ? "READY" : "BLOCKED"); }
    public static void EnsureWriteApproved(EtsyMetadataSelection selection, bool approved) { if (!approved || selection.Status != "READY") throw new InvalidOperationException("METADATA_WRITE_BLOCKED: ready ve explicit approval gerekli."); }
    public static IReadOnlyList<string> MapProperties(IReadOnlyDictionary<string, string> values, IReadOnlySet<string> allowed) => values.Where(x => allowed.Contains(x.Key)).Select(x => x.Key + "=" + x.Value).ToArray();
}
