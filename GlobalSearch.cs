using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record GlobalSearchHit(
    string Type,
    string Title,
    string Detail,
    string Route,
    string TargetId,
    DateTimeOffset UpdatedUtc);

/// Bounded diagnostics only (id/type/route/target identity, a short reason,
/// detection time) - never Title/Detail - for a SearchIndex row with an
/// unparsable persisted UpdatedUtc. See CatalogStore's CorruptProductRow for the
/// same pattern. The index is a fully-derived, rebuildable cache (ReplaceAll
/// replaces it wholesale), so a corrupt row here never needs its own repair
/// flow - it simply drops out of results until the next full rebuild.
public sealed record CorruptGlobalSearchRow(string Type, string Route, string TargetId, string Reason, DateTime DetectedUtc);

public sealed record GlobalSearchIndexEntry(
    string Id,
    string Type,
    string Route,
    string TargetId,
    string Title,
    string Detail,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// Local, secret-safe index for the records users need to find across modules.
/// It intentionally stores display metadata only; credentials, URLs containing
/// credentials, customer data and message bodies never enter this index.
/// </summary>
public sealed class GlobalSearchIndexStore
{
    readonly string connectionString;

    public GlobalSearchIndexStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "search-index.db") }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS SearchIndex(Id TEXT PRIMARY KEY,Type TEXT NOT NULL,Route TEXT NOT NULL,TargetId TEXT NOT NULL,Title TEXT NOT NULL,Detail TEXT NOT NULL,SearchText TEXT NOT NULL,UpdatedUtc TEXT NOT NULL);CREATE INDEX IF NOT EXISTS IX_SearchIndex_SearchText ON SearchIndex(SearchText);CREATE INDEX IF NOT EXISTS IX_SearchIndex_Updated ON SearchIndex(UpdatedUtc DESC);CREATE TABLE IF NOT EXISTS SearchIndexMeta(Key TEXT PRIMARY KEY,Value TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    public void ReplaceAll(IReadOnlyList<GlobalSearchIndexEntry> entries, CancellationToken cancellationToken = default)
    {
        var unique = entries.Where(x => !string.IsNullOrWhiteSpace(x.Id)).GroupBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Last()).ToArray();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand()) { clear.Transaction = transaction; clear.CommandText = "DELETE FROM SearchIndex"; clear.ExecuteNonQuery(); }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO SearchIndex(Id,Type,Route,TargetId,Title,Detail,SearchText,UpdatedUtc) VALUES($id,$type,$route,$target,$title,$detail,$search,$updated)";
        var id = insert.Parameters.Add("$id", SqliteType.Text); var type = insert.Parameters.Add("$type", SqliteType.Text); var route = insert.Parameters.Add("$route", SqliteType.Text); var target = insert.Parameters.Add("$target", SqliteType.Text); var title = insert.Parameters.Add("$title", SqliteType.Text); var detail = insert.Parameters.Add("$detail", SqliteType.Text); var search = insert.Parameters.Add("$search", SqliteType.Text); var updated = insert.Parameters.Add("$updated", SqliteType.Text);
        foreach (var entry in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = Clean(entry.Id, 180); type.Value = Clean(entry.Type, 60); route.Value = Clean(entry.Route, 80); target.Value = Clean(entry.TargetId, 180); title.Value = Clean(entry.Title, 300); detail.Value = Clean(entry.Detail, 1200); search.Value = BuildSearchText(entry.Type, entry.Title, entry.Detail, entry.TargetId); updated.Value = entry.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture); insert.ExecuteNonQuery();
        }
        using (var meta = connection.CreateCommand()) { meta.Transaction = transaction; meta.CommandText = "INSERT INTO SearchIndexMeta(Key,Value) VALUES('builtUtc',$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value"; meta.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)); meta.ExecuteNonQuery(); }
        transaction.Commit();
    }

    public DateTimeOffset? BuiltUtc()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Value FROM SearchIndexMeta WHERE Key='builtUtc'"; var value = command.ExecuteScalar() as string; return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }

    public void MarkStale()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM SearchIndexMeta WHERE Key='builtUtc'"; command.ExecuteNonQuery();
    }

    /// A malformed UpdatedUtc must never crash the whole search - the row is
    /// excluded from results (this index is a fully-derived, rebuildable cache;
    /// see CorruptGlobalSearchRow) rather than throwing mid-query. No rebuild is
    /// triggered from a read failure, so the same corrupt row can never cause a
    /// rebuild/exception loop - it simply drops out until the next full rebuild.
    public IReadOnlyList<GlobalSearchHit> Search(string query, int limit = 80)
    {
        query = query.Trim(); if (query.Length < 2) return Array.Empty<GlobalSearchHit>(); if (query.Length > 200) throw new ArgumentException("Arama metni en fazla 200 karakter olabilir.", nameof(query)); limit = Math.Clamp(limit, 1, 200);
        var escaped = query.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Type,Title,Detail,Route,TargetId,UpdatedUtc FROM SearchIndex WHERE SearchText LIKE $like ESCAPE '\\' ORDER BY UpdatedUtc DESC LIMIT $limit"; command.Parameters.AddWithValue("$like", $"%{escaped}%"); command.Parameters.AddWithValue("$limit", limit); using var reader = command.ExecuteReader(); var result = new List<GlobalSearchHit>(); while (reader.Read()) if (TryRead(reader, out var hit, out _)) result.Add(hit!); return result;
    }

    /// Bounded diagnostics for every SearchIndex row whose UpdatedUtc failed to
    /// parse - never the raw Title/Detail.
    public IReadOnlyList<CorruptGlobalSearchRow> CorruptRows()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Type,Title,Detail,Route,TargetId,UpdatedUtc FROM SearchIndex";
        using var reader = command.ExecuteReader(); var result = new List<CorruptGlobalSearchRow>(); while (reader.Read()) if (!TryRead(reader, out _, out var corrupt)) result.Add(corrupt!); return result;
    }

    static bool TryRead(SqliteDataReader reader, out GlobalSearchHit? hit, out CorruptGlobalSearchRow? corrupt)
    {
        hit = null; corrupt = null; var type = reader.GetString(0); var route = reader.GetString(3); var targetId = reader.GetString(4);
        if (!TryParseUtc(reader.GetString(5), out var updated)) { corrupt = new(type, route, targetId, "Malformed UpdatedUtc timestamp", DateTime.UtcNow); return false; }
        hit = new(type, reader.GetString(1), reader.GetString(2), route, targetId, updated);
        return true;
    }

    /// Only ever a format/parse failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised by the surrounding command, not this parse.
    static bool TryParseUtc(string value, out DateTimeOffset result) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);

    static string BuildSearchText(params string[] values) => string.Join(' ', values.Select(v => Clean(v, 2000))).ToLowerInvariant();
    static string Clean(string? value, int max) { var clean = AuditStore.Sanitize(value).Replace('\r', ' ').Replace('\n', ' ').Trim(); return clean.Length > max ? clean[..max] : clean; }
}

public sealed class GlobalSearchIndexService
{
    readonly string? directory;
    readonly GlobalSearchIndexStore index;
    readonly SemaphoreSlim refreshGate = new(1, 1);
    DateTimeOffset? lastBuildUtc;

    public GlobalSearchIndexService(string? directory = null) { this.directory = directory; index = new GlobalSearchIndexStore(directory); }

    public void Invalidate() { lastBuildUtc = null; index.MarkStale(); }

    public async Task<IReadOnlyList<GlobalSearchHit>> SearchAsync(string query, int limit = 80, CancellationToken cancellationToken = default)
    {
        await EnsureFreshAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => index.Search(query, limit), cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (lastBuildUtc is { } built && now - built < TimeSpan.FromSeconds(30)) return;
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            var persisted = index.BuiltUtc();
            if (lastBuildUtc is { } memory && now - memory < TimeSpan.FromSeconds(30)) return;
            if (persisted is { } saved && now - saved < TimeSpan.FromSeconds(30)) { lastBuildUtc = saved; return; }
            await Task.Run(() => Rebuild(cancellationToken), cancellationToken).ConfigureAwait(false);
            lastBuildUtc = DateTimeOffset.UtcNow;
        }
        finally { refreshGate.Release(); }
    }

    public void Rebuild(CancellationToken cancellationToken = default)
    {
        var entries = new List<GlobalSearchIndexEntry>();
        void Add(string id, string type, string route, string target, string title, string detail, DateTimeOffset updated) => entries.Add(new(id, type, route, target, title, detail, updated));
        var catalog = new Catalog.CatalogStore(directory);
        foreach (var product in catalog.Products())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add("product:" + product.Id, "Ürün", "products", product.Id, product.Name, $"SKU: {product.Sku} · Barkod: {product.Barcode} · Marka: {product.Brand} · Kategori: {product.Category}", At(product.UpdatedUtc));
        }
        foreach (var order in new OrdersStore(directory).ReadAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add("order:" + order.Marketplace + ":" + order.ShopId + ":" + order.OrderId, "Sipariş", "orders", order.OrderId, $"{order.Marketplace} / {order.OrderId}", $"Mağaza: {order.ShopId} · Durum: {order.RawStatus} · SKU: {string.Join(", ", order.Items.Select(x => x.Sku).Where(x => x.Length > 0))}", order.UpdatedAt);
        }
        foreach (var plan in new ChannelProductsStore(directory).List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add("listing:" + plan.ChannelId + ":" + plan.ShopId + ":" + plan.ProductId, "İlan", Routes(plan.ChannelId), plan.ProductId, $"{plan.ChannelId} / {plan.ListingId}", $"Ürün: {plan.ProductId} · Mağaza: {plan.ShopId} · Kategori: {plan.TargetCategory}", At(plan.UpdatedUtc));
        }
        foreach (var connection in new MarketplaceConnectionStore(directory).List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add("shop:" + connection.Id, "Mağaza", "connections", connection.Id, $"{connection.Channel} / {connection.DisplayName}", $"Mağaza: {connection.ShopId} · Durum: {connection.Status}", At(connection.LastTestUtc ?? DateTime.UtcNow));
        }
        foreach (var source in catalog.Sources())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add("xml-source:" + source.Id, "XML kaynağı", "xml", source.Id, source.Name, $"Kaynak: {source.Id} · Durum: {source.LastStatus} · Alan: {source.Fields.Count}", At(source.LastRunUtc ?? DateTime.UtcNow));
        }
        foreach (var run in new XmlRunStore(directory).List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add("xml-run:" + run.Id, "XML çalışması", "xml", run.Id, $"XML {run.SourceId} · {run.Status}", $"Kaynak: {run.SourceId} · Yeni: {run.Added} · Güncel: {run.Updated} · Hata: {run.Error}", At(run.FinishedUtc ?? run.StartedUtc));
        }
        foreach (var job in new Catalog.SyncStore(directory).List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add("sync:" + job.Id, "Sync işi", "sync", job.Id, $"{job.Channel} / {job.Operation}", $"Varlık: {job.EntityId} · Sürüm: {job.Version} · Durum: {job.Status} · Hata: {job.LastError}", At(job.UpdatedUtc));
        }
        foreach (var issue in new DataQualityStore(directory).List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add("quality:" + issue.Id, "Veri kalite hatası", "data-quality", issue.Id, $"{issue.Type} · {issue.Sku}", $"{issue.Message} · Durum: {issue.Status}", At(issue.UpdatedUtc));
        }
        foreach (var health in new ApiHealthStore(directory).List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add("health:" + health.Channel + ":" + health.ShopId, "API sağlığı", "api-health", health.Channel + ":" + health.ShopId, $"{health.Channel} / {health.ShopId}", $"Durum: {health.State} · Auth: {health.AuthStatus} · Hata: {health.LastError}", health.UpdatedUtc);
        }
        foreach (var audit in new AuditStore(directory).List(AuditStore.RetentionLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add("audit:" + audit.Id, "Audit olayı", "diagnostics", audit.Id, $"{audit.Module} / {audit.Action}", $"Sonuç: {audit.Outcome} · Ürün: {audit.ProductId} · Sipariş: {audit.OrderId} · Mağaza: {audit.ShopId} · {audit.Detail}", audit.AtUtc);
        }
        index.ReplaceAll(entries, cancellationToken);
    }

    static string Routes(string channel) => channel is "etsy" or "ebay" or "amazon" or "trendyol" or "hepsiburada" or "ozon" or "joom" or "fruugo" or "allegro" or "wish" ? channel : "listing-matrix";
    static DateTimeOffset At(DateTime value) => value == default ? DateTimeOffset.UnixEpoch : new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
