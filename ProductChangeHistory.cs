namespace TrMarketplaceHubDesktop;

public sealed record ProductChange(string ProductId, string Field, string? OldValue, string? NewValue, string Source, string Actor, string Context, DateTimeOffset AtUtc, long Version);
public sealed record ProductFieldDiff(string Field, string? Left, string? Right, bool Changed);
public sealed record ProductRollbackPreview(string ProductId, string Field, string? TargetValue, long CurrentVersion, long TargetVersion, string Key);

public sealed class ProductChangeHistory
{
    private static readonly HashSet<string> SafeFields = new(StringComparer.OrdinalIgnoreCase) { "Sku", "Barcode", "Name", "Description", "Brand", "Category", "Stock", "Price", "Normal" };
    private static readonly HashSet<string> Sources = new(StringComparer.OrdinalIgnoreCase) { "manual", "xml", "excel", "channel", "migration", "import" };
    private readonly object gate = new();
    private readonly List<ProductChange> entries = new();
    private readonly Dictionary<string, long> versions = new(StringComparer.Ordinal);

    public ProductChange Append(string productId, string field, string? oldValue, string? newValue, string source, string actor, string context)
    {
        if (string.IsNullOrWhiteSpace(productId) || !SafeFields.Contains(field)) throw new ArgumentException("Ürün ve güvenli alan zorunludur.");
        if (!Sources.Contains(source)) throw new ArgumentException("Bilinmeyen provenance kaynağı.");
        lock (gate) { var version = versions.TryGetValue(productId, out var v) ? v + 1 : 1; versions[productId] = version; var item = new ProductChange(productId, field, oldValue, newValue, source.ToLowerInvariant(), actor, Redact(context), DateTimeOffset.UtcNow, version); entries.Add(item); return item; }
    }

    public IReadOnlyList<ProductChange> Query(string productId, string? field = null, int page = 0, int pageSize = 50)
    { lock (gate) return entries.Where(x => x.ProductId == productId && (string.IsNullOrWhiteSpace(field) || x.Field.Equals(field, StringComparison.OrdinalIgnoreCase))).OrderByDescending(x => x.Version).Skip(Math.Max(0, page) * pageSize).Take(pageSize).ToArray(); }

    public IReadOnlyList<ProductFieldDiff> Diff(string productId, long leftVersion, long rightVersion)
    { lock (gate) { var all = entries.Where(x => x.ProductId == productId).ToArray(); var fields = all.Select(x => x.Field).Distinct(StringComparer.OrdinalIgnoreCase); return fields.Select(f => new ProductFieldDiff(f, ValueAt(all, f, leftVersion), ValueAt(all, f, rightVersion), ValueAt(all, f, leftVersion) != ValueAt(all, f, rightVersion))).ToArray(); } }

    public ProductRollbackPreview PreviewRollback(string productId, string field, long targetVersion)
    { lock (gate) { if (!SafeFields.Contains(field)) throw new InvalidOperationException("ROLLBACK_BLOCKED: alan güvenli değil."); var current = versions.TryGetValue(productId, out var v) ? v : 0; if (targetVersion < 1 || targetVersion > current) throw new InvalidOperationException("ROLLBACK_TARGET_INVALID"); var value = ValueAt(entries.Where(x => x.ProductId == productId).ToArray(), field, targetVersion); return new(productId, field, value, current, targetVersion, $"{productId}|{field}|{current}|{targetVersion}|{value}"); } }

    public ProductChange ApplyLocalRollback(ProductRollbackPreview preview, long expectedCurrentVersion, bool approved)
    { if (!approved) throw new InvalidOperationException("EXPLICIT_APPROVAL_REQUIRED: rollback uygulanmadı."); lock (gate) { var current = versions.TryGetValue(preview.ProductId, out var v) ? v : 0; if (current != expectedCurrentVersion || preview.CurrentVersion != current) throw new InvalidOperationException("STALE_VERSION: rollback preview güncel değil."); return Append(preview.ProductId, preview.Field, null, preview.TargetValue, "manual", "rollback", $"targetVersion={preview.TargetVersion}"); } }

    public int Retain(int keepLatestPerProduct) { lock (gate) { var remove = entries.GroupBy(x => x.ProductId).SelectMany(g => g.OrderByDescending(x => x.Version).Skip(Math.Max(1, keepLatestPerProduct))).ToHashSet(); entries.RemoveAll(remove.Contains); return remove.Count; } }
    private static string? ValueAt(IEnumerable<ProductChange> source, string field, long version) => source.Where(x => x.Field.Equals(field, StringComparison.OrdinalIgnoreCase) && x.Version <= version).OrderByDescending(x => x.Version).Select(x => x.NewValue).FirstOrDefault();
    private static string Redact(string value) => string.IsNullOrWhiteSpace(value) ? "" : value.Replace("token", "[redacted]", StringComparison.OrdinalIgnoreCase).Replace("secret", "[redacted]", StringComparison.OrdinalIgnoreCase);
}
