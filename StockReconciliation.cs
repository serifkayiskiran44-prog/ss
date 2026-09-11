namespace TrMarketplaceHubDesktop;

public sealed record StockMovement(string Id, string ShopId, string Channel, string ProductId, string Source, int Quantity, DateTimeOffset AtUtc);
public sealed record StockReconciliationRow(string ShopId, string Channel, string ProductId, int CurrentStock, int SnapshotStock, int MovementTotal, int ExpectedStock, int Difference, string Status, IReadOnlyList<string> Causes);
public sealed record StockCorrectionPreview(string Key, string ShopId, string Channel, string ProductId, int Before, int After, long Version, string Reason);
public sealed record StockAudit(string Key, DateTimeOffset AtUtc, string Action, string ShopId, string ProductId, int Before, int After, long Version);

public sealed class StockReconciliationCenter
{
    private readonly object gate = new();
    private readonly Dictionary<(string Shop, string Channel, string Product), (int Stock, int Snapshot, long Version)> balances = new();
    private readonly HashSet<string> movementIds = new(StringComparer.Ordinal);
    private readonly List<StockAudit> audit = new();

    public IReadOnlyList<StockAudit> Audit { get { lock (gate) return audit.ToArray(); } }

    public StockReconciliationRow Reconcile(string shopId, string channel, string productId, int currentStock, int snapshotStock, IEnumerable<StockMovement> movements)
    {
        if (string.IsNullOrWhiteSpace(shopId) || string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("shop/channel/product zorunludur.");
        var scoped = movements.Where(x => x.ShopId == shopId && x.Channel == channel && x.ProductId == productId).ToArray();
        var total = scoped.GroupBy(x => x.Id, StringComparer.Ordinal).Sum(g => g.First().Quantity);
        var expected = snapshotStock + total;
        var causes = new List<string>();
        if (scoped.Any(x => x.Source.Equals("order", StringComparison.OrdinalIgnoreCase))) causes.Add("order");
        if (scoped.Any(x => x.Source.Equals("xml", StringComparison.OrdinalIgnoreCase))) causes.Add("xml");
        if (scoped.Any(x => x.Source.Equals("manual", StringComparison.OrdinalIgnoreCase))) causes.Add("manual");
        if (scoped.Any(x => x.Source.Equals("channel", StringComparison.OrdinalIgnoreCase))) causes.Add("channel");
        if (scoped.Any(x => x.Source.Equals("import", StringComparison.OrdinalIgnoreCase))) causes.Add("import");
        if (currentStock < 0) causes.Add("negative-stock");
        if (scoped.GroupBy(x => x.Id).Any(g => g.Count() > 1)) causes.Add("duplicate-movement");
        if (Math.Abs(currentStock - expected) > Math.Max(10, Math.Abs(snapshotStock) / 2)) causes.Add("unexpected-jump");
        var status = currentStock < 0 || causes.Contains("duplicate-movement") ? "BLOCKED" : currentStock == expected ? "RECONCILED" : "MISMATCH";
        lock (gate) { var key = (shopId, channel, productId); if (!balances.ContainsKey(key)) balances[key] = (currentStock, snapshotStock, 0); }
        return new(shopId, channel, productId, currentStock, snapshotStock, total, expected, currentStock - expected, status, causes);
    }

    public StockCorrectionPreview PreviewCorrection(StockReconciliationRow row, string reason)
    { lock (gate) { var key = (row.ShopId, row.Channel, row.ProductId); var version = balances.TryGetValue(key, out var value) ? value.Version : 0; return new($"{row.ShopId}|{row.Channel}|{row.ProductId}|{version}|{row.ExpectedStock}", row.ShopId, row.Channel, row.ProductId, row.CurrentStock, row.ExpectedStock, version, reason); } }

    public StockAudit ApplyApproved(StockCorrectionPreview preview, long expectedVersion, bool approved)
    {
        if (!approved) throw new InvalidOperationException("EXPLICIT_APPROVAL_REQUIRED: stok write uygulanmadı.");
        lock (gate)
        {
            var key = (preview.ShopId, preview.Channel, preview.ProductId);
            if (!balances.TryGetValue(key, out var value) || value.Version != expectedVersion || preview.Version != expectedVersion) throw new InvalidOperationException("STALE_VERSION: stok düzeltme önizlemesi güncel değil.");
            if (preview.Key != $"{preview.ShopId}|{preview.Channel}|{preview.ProductId}|{preview.Version}|{preview.After}") throw new InvalidOperationException("IDEMPOTENCY_OR_SCOPE_MISMATCH: önizleme değişmiş.");
            var updated = value with { Stock = preview.After, Version = value.Version + 1 }; balances[key] = updated;
            var item = new StockAudit(preview.Key, DateTimeOffset.UtcNow, "LOCAL_CORRECTION", preview.ShopId, preview.ProductId, preview.Before, preview.After, updated.Version); audit.Add(item); return item;
        }
    }

    public async Task<IReadOnlyList<StockReconciliationRow>> PageAsync(IEnumerable<StockReconciliationRow> rows, string? filter, int page, int pageSize, CancellationToken cancellationToken = default)
    { await Task.Yield(); cancellationToken.ThrowIfCancellationRequested(); var query = rows.Where(x => string.IsNullOrWhiteSpace(filter) || x.ProductId.Contains(filter, StringComparison.OrdinalIgnoreCase) || x.Status.Contains(filter, StringComparison.OrdinalIgnoreCase)); return query.Skip(Math.Max(0, page) * pageSize).Take(pageSize).ToArray(); }
}
