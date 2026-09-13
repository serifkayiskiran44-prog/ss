using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop;

public sealed record ChannelMatrixBulkTarget(string Key, string Channel, string ShopId, string Label);

public sealed record ChannelMatrixBulkLine(string Sku, string Name, string Before, string After, string Status, string Reason)
{
    public string Marker => Status switch { "READY" => "＋", "SKIP" => "＝", _ => "✖" };
    public string Text => $"{Marker} {Sku} · {Name} · {Before} → {After}" + (Reason.Length > 0 ? " · " + Reason : "");
}

public sealed record ChannelMatrixBulkDrawerModel(string TargetLabel, int Selected, int Affected, int Skipped, int Errors, IReadOnlyList<ChannelMatrixBulkLine> Lines, int LinesTruncated, bool CanApply, string Reason, string RevisionAtPreview)
{
    public string Summary => $"{Selected} seçili ürün · {Affected} uygulanacak · {Skipped} zaten aynı · {Errors} engelli";
}

/// <summary>
/// The matrix's bulk-action preview (#845): a local channel plan for the selected products in one target store,
/// previewed through the same BulkProductOperations the bulk screen uses and shown as a drawer before anything is
/// written. The drawer says the target, how many products change, which are already identical (skipped) or
/// blocked, and the field change per product (capped to a page for large selections). Apply is refused when the
/// selection is empty, when nothing would change, when the target store is not one the shell offers (wrong
/// store), when the matrix changed since the preview (stale revision), or when this very preview was already
/// applied (idempotency) -- and the write itself is local: plans in channel_products.db, never a marketplace.
/// </summary>
public static class ChannelMatrixBulk
{
    public const int MaxLinesShown = 50;

    /// <summary>A fingerprint of what the matrix showed: product, store, state and listing per flat row, order-independent.</summary>
    public static string Revision(IEnumerable<ChannelListingMatrixRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var lines = rows.Select(r => string.Join("|", r.ProductId, r.Channel.Trim().ToLowerInvariant(), r.ShopId.Trim(), r.MappingStatus, r.ListingId, r.AuthStatus)).OrderBy(x => x, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))[..16];
    }

    public static ChannelMatrixBulkDrawerModel Compose(BulkProductPreview? preview, ChannelMatrixBulkTarget target, IReadOnlyCollection<string>? allowedStoreKeys, string revisionAtPreview, int selectedCount)
    {
        ArgumentNullException.ThrowIfNull(target);
        var lines = (preview?.Lines ?? Array.Empty<BulkProductPreviewLine>()).Select(l =>
        {
            var status = l.Status == "ERROR" ? "ERROR" : string.Equals(l.Before, l.After, StringComparison.Ordinal) ? "SKIP" : "READY";
            var reason = l.Status == "ERROR" ? AuditStore.Sanitize(l.Error) : status == "SKIP" ? "plan zaten aynı" : "";
            return new ChannelMatrixBulkLine(l.Sku, l.Name, l.Before, l.After, status, reason);
        }).ToList();
        var affected = lines.Count(l => l.Status == "READY"); var skipped = lines.Count(l => l.Status == "SKIP"); var errors = lines.Count(l => l.Status == "ERROR");
        var offered = allowedStoreKeys is null || allowedStoreKeys.Contains(target.Key, StringComparer.Ordinal);
        var reason = selectedCount == 0 ? "Seçim boş: önce matristen ürün hücreleri seçin."
            : !offered ? "Hedef mağaza bu oturumda sunulan mağazalar arasında değil; yanlış mağazaya yazılmaz."
            : preview is null ? "Önizleme hazırlanamadı."
            : affected == 0 ? "Uygulanacak değişiklik yok: her seçili ürünün planı zaten aynı veya engelli."
            : "";
        return new(target.Label, selectedCount, affected, skipped, errors, lines.Take(MaxLinesShown).ToList(), Math.Max(0, lines.Count - MaxLinesShown), reason.Length == 0, reason, revisionAtPreview);
    }

    /// <summary>The checks that run again at the moment of applying -- the drawer's own approval is not enough on its own.</summary>
    public static (bool Ok, string Reason) CheckBeforeApply(string revisionAtPreview, string revisionNow, IReadOnlyCollection<string>? allowedStoreKeys, string targetKey, ICollection<Guid> appliedPreviews, Guid previewId)
    {
        ArgumentNullException.ThrowIfNull(appliedPreviews);
        if (appliedPreviews.Contains(previewId)) return (false, "Bu önizleme zaten uygulandı; yeniden uygulamak için yeni önizleme alın.");
        if (allowedStoreKeys is not null && !allowedStoreKeys.Contains(targetKey, StringComparer.Ordinal)) return (false, "Hedef mağaza artık sunulmuyor; yanlış mağazaya yazılmaz.");
        if (!string.Equals(revisionAtPreview, revisionNow, StringComparison.Ordinal)) return (false, "Matris önizlemeden sonra değişti (bayat revizyon); yeni önizleme alın.");
        return (true, "");
    }
}
