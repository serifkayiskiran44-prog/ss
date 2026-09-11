using System.Security.Cryptography;

namespace TrMarketplaceHubDesktop;

public sealed record EtsyMediaItem(string Id, string Kind, string Name, byte[] Content, int Rank, string Source);
public sealed record EtsyMediaPreview(long ListingId, string ShopId, IReadOnlyList<EtsyMediaItem> Items, IReadOnlyList<string> Errors, string Status, string Fingerprint);
public sealed record MediaRetry(string ItemId, string Reason, int Attempt, DateTimeOffset NextAttemptUtc);

public static class EtsyMediaCenter
{
    public static EtsyMediaPreview Preview(long listingId, string shopId, IEnumerable<EtsyMediaItem> items, bool digital, bool videoContractVerified = false)
    {
        if (listingId <= 0 || string.IsNullOrWhiteSpace(shopId)) throw new ArgumentException("Listing/shop zorunludur."); var list = items.OrderBy(x => x.Rank).ToArray(); var errors = new List<string>();
        if (list.Any(x => x.Content.Length == 0)) errors.Add("EMPTY_MEDIA"); if (list.Any(x => x.Kind.Equals("video", StringComparison.OrdinalIgnoreCase)) && !videoContractVerified) errors.Add("VIDEO_LIVE_API_BLOCKED"); if (digital && list.Any(x => x.Kind.Equals("image", StringComparison.OrdinalIgnoreCase) && x.Name.Contains("physical", StringComparison.OrdinalIgnoreCase))) errors.Add("DIGITAL_MEDIA_MISMATCH");
        var fingerprint = Convert.ToHexString(SHA256.HashData(list.SelectMany(x => x.Content).ToArray())).ToLowerInvariant(); return new(listingId, shopId, list, errors, errors.Count == 0 ? "READY_READ_ONLY" : "BLOCKED", fingerprint);
    }
    public static void EnsureDestructiveApproved(EtsyMediaPreview preview, bool approved) { if (!approved || preview.Status != "READY_READ_ONLY") throw new InvalidOperationException("MEDIA_WRITE_BLOCKED: preview ve explicit approval gerekli."); }
    public static MediaRetry Retry(string itemId, string reason, int previousAttempt, DateTimeOffset now) { if (string.IsNullOrWhiteSpace(itemId) || previousAttempt < 0) throw new ArgumentException("Retry girdisi geçersiz."); var attempt = previousAttempt + 1; return new(itemId, reason, attempt, now.AddMinutes(Math.Min(60, Math.Pow(2, attempt)))); }
}
