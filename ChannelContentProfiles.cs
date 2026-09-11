namespace TrMarketplaceHubDesktop;

public sealed record ContentProfile(string ShopId, string Channel, string Locale, string ProductId, IReadOnlyDictionary<string, string> Fields, string Source, DateTimeOffset UpdatedUtc, long Version);
public sealed record ContentPreview(string ShopId, string Channel, string Locale, string ProductId, IReadOnlyDictionary<string, string> Fields, IReadOnlyList<string> Errors, string Status, long Version);

public sealed class ChannelContentProfiles
{
    private readonly object gate = new();
    private readonly Dictionary<(string Shop, string Channel, string Locale, string Product), ContentProfile> profiles = new();
    private static readonly string[] Fields = { "Title", "Subtitle", "Description", "Keywords", "Normal" };
    public ContentProfile Save(string shopId, string channel, string locale, string productId, IReadOnlyDictionary<string, string> fields, string source)
    { if (string.IsNullOrWhiteSpace(shopId) || string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(locale) || string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("Profil kapsamı zorunludur."); if (!fields.Keys.All(x => Fields.Contains(x, StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("Bilinmeyen içerik alanı."); lock (gate) { var key = (shopId, channel, locale, productId); var v = profiles.TryGetValue(key, out var old) ? old.Version + 1 : 1; var item = new ContentProfile(shopId, channel, locale, productId, new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase), source, DateTimeOffset.UtcNow, v); profiles[key] = item; return item; } }
    public ContentPreview Preview(string shopId, string channel, string locale, string productId, IReadOnlyDictionary<string, string> localFields, IReadOnlyDictionary<string, int>? officialLimits = null, DateTimeOffset? updatedAt = null, TimeSpan? maxAge = null)
    { lock (gate) { var key = (shopId, channel, locale, productId); profiles.TryGetValue(key, out var profile); var merged = new Dictionary<string, string>(localFields, StringComparer.OrdinalIgnoreCase); if (profile is not null) foreach (var x in profile.Fields) merged[x.Key] = x.Value; var errors = new List<string>(); if (officialLimits is not null) foreach (var limit in officialLimits) if (merged.TryGetValue(limit.Key, out var value) && value.Length > limit.Value) errors.Add($"{limit.Key}_TOO_LONG"); if (updatedAt is { } at && (DateTimeOffset.UtcNow - at > (maxAge ?? TimeSpan.FromDays(2)))) errors.Add("STALE_CONTENT"); return new(shopId, channel, locale, productId, merged, errors, errors.Count == 0 ? "READY" : "BLOCKED", profile?.Version ?? 0); } }
    public ContentProfile Clone(ContentProfile source, string targetShopId, string targetLocale, bool approved) { if (!approved) throw new InvalidOperationException("EXPLICIT_APPROVAL_REQUIRED"); if (source.ShopId == targetShopId && source.Locale == targetLocale) throw new InvalidOperationException("DUPLICATE_PROFILE"); return Save(targetShopId, source.Channel, targetLocale, source.ProductId, source.Fields, "clone"); }
    public void EnsureScope(ContentPreview preview, string shopId, string channel) { if (preview.ShopId != shopId || preview.Channel != channel) throw new InvalidOperationException("WRONG_SHOP_OR_CHANNEL"); }
}
