using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop;

public sealed record EtsyListingChange(string Field, string? Before, string? After);
public sealed record EtsyListingLifecyclePreview(string ShopId, long ListingId, string CurrentState, string TargetState, IReadOnlyList<EtsyListingChange> Changes, string Receipt);

public static class EtsyListingLifecycle
{
    private static readonly HashSet<string> States = new(StringComparer.OrdinalIgnoreCase) { "draft", "active", "inactive", "expired", "sold_out" };
    public static EtsyListingLifecyclePreview CreatePreview(string shopId, long listingId, string currentState, string targetState, IEnumerable<EtsyListingChange> changes)
    { if (string.IsNullOrWhiteSpace(shopId) || listingId <= 0 || !States.Contains(currentState) || !States.Contains(targetState)) throw new ArgumentException("Etsy listing lifecycle girdisi geçersiz."); var list = changes.Where(x => !string.IsNullOrWhiteSpace(x.Field)).ToArray(); var receipt = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{shopId}|{listingId}|{currentState}|{targetState}|{string.Join(';', list.Select(x => x.Field + '=' + x.After))}"))).ToLowerInvariant(); return new(shopId, listingId, currentState, targetState, list, receipt); }
    public static void EnsureOwned(EtsyListingLifecyclePreview preview, string shopId) { if (!string.Equals(preview.ShopId, shopId, StringComparison.Ordinal)) throw new InvalidOperationException("WRONG_SHOP: Etsy listing başka mağazaya ait."); }
    public static void EnsurePublishReady(EtsyListingLifecyclePreview preview, bool hasTitle, bool hasDescription, bool hasPrice, bool hasQuantity) { if (preview.TargetState.Equals("active", StringComparison.OrdinalIgnoreCase) && (!hasTitle || !hasDescription || !hasPrice || !hasQuantity)) throw new InvalidOperationException("PUBLISH_BLOCKED: listing readiness eksik."); }
    public static string DeleteGuard(EtsyListingLifecyclePreview preview, bool destructiveConfirmed, ISet<string> receipts) { if (!destructiveConfirmed) throw new InvalidOperationException("DESTRUCTIVE_CONFIRMATION_REQUIRED"); if (!receipts.Add(preview.Receipt)) throw new InvalidOperationException("DUPLICATE_DELETE_RECEIPT"); return preview.Receipt; }
    public static string RedactDiagnostic(string message) => message.Replace("Bearer ", "Bearer [redacted]", StringComparison.OrdinalIgnoreCase).Replace("x-api-key", "x-api-key:[redacted]", StringComparison.OrdinalIgnoreCase);
}
