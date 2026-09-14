using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

/// <summary>One exception standing on an order: the kind, the store and order, the detail in safe words, open or resolved, when seen and when resolved.</summary>
public sealed record LineMappingOrderException(long Id, string Marketplace, string ShopId, string OrderId, string Kind, string Detail, string State, DateTime SeenUtc, DateTime? ResolvedUtc)
{
    public const string UnmappedLines = "UNMAPPED_LINES", MoneyMismatch = "MONEY_MISMATCH", CurrencyMismatch = "CURRENCY_MISMATCH", DuplicateConflict = "DUPLICATE_CONFLICT";
    public const string Open = "OPEN", Resolved = "RESOLVED";
    /// <summary>The kinds under which an order's stock must not be mutated: a line nobody has mapped, or a payload nobody has reviewed.</summary>
    public static bool BlocksStock(string kind) => kind is UnmappedLines or DuplicateConflict;
}

/// <summary>One row of the unmapped order queue: the order, its age, the impact (unmapped lines of all lines, the units), the open reviews to jump to, the words.</summary>
public sealed record UnmappedOrderRow(string Marketplace, string ShopId, string OrderId, TimeSpan Age, int UnmappedLines, int TotalLines, int UnmappedUnits, IReadOnlyList<long> ReviewIds, string DeepLink, string Words);

public sealed record UnmappedOrderQueue(IReadOnlyList<UnmappedOrderRow> Rows, int Orders, int Lines, int Units, string Headline)
{
    public UnmappedOrderRow? For(string marketplace, string shopId, string orderId) => Rows.FirstOrDefault(r => r.Marketplace == (marketplace ?? "").Trim().ToLowerInvariant() && r.ShopId == (shopId ?? "").Trim() && r.OrderId == (orderId ?? "").Trim());
}

/// <summary>
/// Order exceptions and the unmapped order queue (#945). An order with one or more lines the mapping queue (#944)
/// could not link is an exception of its own: it is listed apart with its age, its impact (how many of its lines and
/// units are unmapped) and a deep link to the reviews that resolve it; a cancelled order drops out, a resolved one
/// drops out on the next sync, and every store sees only its own. While the exception stands, the order's stock is
/// not mutated — the stock apply is blocked by name — so an unmapped line never moves the wrong product's stock.
/// The exception store is the one place every kind of order exception lives (unmapped lines here; money and
/// currency mismatches and duplicate conflicts belong to their own owners), keyed by store, order and kind.
/// Order ids, counts, hours and review numbers only.
/// </summary>
public static class UnmappedOrders
{
    public const string Route = "orders";

    public static UnmappedOrderQueue Build(IReadOnlyList<OrderSnapshot> orders, IReadOnlyList<OrderLineReview> openReviews, DateTime nowUtc, string? marketplace = null, string? shopId = null)
    {
        ArgumentNullException.ThrowIfNull(orders); ArgumentNullException.ThrowIfNull(openReviews);
        var m = (marketplace ?? "").Trim().ToLowerInvariant(); var s = (shopId ?? "").Trim();
        var rows = new List<UnmappedOrderRow>();
        foreach (var group in openReviews.Where(r => r.State == OrderLineReview.Open && (m.Length == 0 || (r.Marketplace == m && r.ShopId == s))).GroupBy(r => (r.Marketplace, r.ShopId, r.OrderId)))
        {
            var order = orders.FirstOrDefault(o => string.Equals((o.Marketplace ?? "").Trim(), group.Key.Marketplace, StringComparison.OrdinalIgnoreCase) && (o.ShopId ?? "").Trim() == group.Key.ShopId && (o.OrderId ?? "").Trim() == group.Key.OrderId);
            if (order is null || IsCancelled(order)) continue;
            var reviews = group.OrderBy(r => r.LineIndex).ToList();
            var units = reviews.Sum(r => r.LineIndex < order.Items.Count ? Math.Max(0, order.Items[r.LineIndex].Quantity) : 0);
            var stamp = order.SourceUpdatedAt == default ? order.UpdatedAt : order.SourceUpdatedAt;
            var age = nowUtc - stamp.UtcDateTime; if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            var ids = reviews.Select(r => r.Id).ToList();
            var words = $"{group.Key.Marketplace}/{group.Key.ShopId} · {group.Key.OrderId} · {N(reviews.Count)}/{N(order.Items.Count)} satır eşlenmemiş ({N(units)} adet) · {Ago(age)} · incelemeler: {string.Join(", ", ids.Select(i => "#" + i.ToString(CultureInfo.InvariantCulture)))} · stok düşümü engelli";
            rows.Add(new(group.Key.Marketplace, group.Key.ShopId, group.Key.OrderId, age, reviews.Count, order.Items.Count, units, ids, $"{Route}?review={ids[0].ToString(CultureInfo.InvariantCulture)}", words));
        }
        rows = rows.OrderByDescending(r => r.Age).ThenBy(r => r.OrderId, StringComparer.Ordinal).ToList();
        var lines = rows.Sum(r => r.UnmappedLines); var unitsTotal = rows.Sum(r => r.UnmappedUnits);
        var headline = rows.Count == 0 ? "Eşlenmemiş satırı olan sipariş yok." : $"{N(rows.Count)} sipariş, {N(lines)} eşlenmemiş satır ({N(unitsTotal)} adet); en eskisi {Ago(rows[0].Age)} — bu siparişlerde stok düşümü engelli.";
        return new(rows, rows.Count, lines, unitsTotal, headline);
    }

    /// <summary>Raises the UNMAPPED_LINES exception for every queued order and resolves it for every order no longer queued; returns (raised, resolved).</summary>
    public static (int Raised, int Resolved) Sync(LineMappingExceptionStore exceptions, UnmappedOrderQueue queue, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(exceptions); ArgumentNullException.ThrowIfNull(queue);
        var raised = 0; var resolved = 0;
        foreach (var row in queue.Rows) { exceptions.Raise(row.Marketplace, row.ShopId, row.OrderId, LineMappingOrderException.UnmappedLines, $"{N(row.UnmappedLines)}/{N(row.TotalLines)} satır eşlenmemiş; incelemeler: {string.Join(", ", row.ReviewIds.Select(i => "#" + i.ToString(CultureInfo.InvariantCulture)))}", nowUtc); raised++; }
        foreach (var open in exceptions.Open().Where(x => x.Kind == LineMappingOrderException.UnmappedLines && queue.For(x.Marketplace, x.ShopId, x.OrderId) is null)) { exceptions.Resolve(open.Marketplace, open.ShopId, open.OrderId, LineMappingOrderException.UnmappedLines, nowUtc); resolved++; }
        return (raised, resolved);
    }

    public static bool IsCancelled(OrderSnapshot order)
    {
        var status = UiSearch.Fold(order.RawStatus ?? "");
        return status.Contains("iptal", StringComparison.Ordinal) || status.Contains("cancel", StringComparison.Ordinal);
    }

    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static string Ago(TimeSpan age) => age.TotalMinutes < 1 ? "az önce" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} dk önce" : age.TotalDays < 1 ? $"{(int)age.TotalHours} sa önce" : $"{(int)age.TotalDays} gün önce";
}

/// <summary>The exceptions, in orders.db beside the orders: one per store, order and kind; raised, refreshed, resolved; read by kind or by order.</summary>
public sealed class LineMappingExceptionStore
{
    readonly string connectionString;

    public LineMappingExceptionStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "orders.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS order_exceptions(id INTEGER PRIMARY KEY AUTOINCREMENT, marketplace TEXT NOT NULL, shop TEXT NOT NULL, order_id TEXT NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL, state TEXT NOT NULL, seen_utc TEXT NOT NULL, resolved_utc TEXT NULL, UNIQUE(marketplace, shop, order_id, kind))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() { var c = SqliteConnectionPolicy.Open(connectionString); using var pragma = c.CreateCommand(); pragma.CommandText = "PRAGMA busy_timeout=15000"; pragma.ExecuteNonQuery(); return c; }

    public LineMappingOrderException Raise(string marketplace, string shopId, string orderId, string kind, string detail, DateTime nowUtc)
    {
        var m = (marketplace ?? "").Trim().ToLowerInvariant(); var s = (shopId ?? "").Trim(); var o = (orderId ?? "").Trim(); var k = (kind ?? "").Trim();
        if (m.Length == 0 || s.Length == 0 || o.Length == 0 || k.Length == 0) throw new ArgumentException("İstisna için pazaryeri, mağaza, sipariş ve tür gerekli.");
        var at = Utc(nowUtc); var clean = AuditStore.Redact((detail ?? "").Trim());
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO order_exceptions(marketplace, shop, order_id, kind, detail, state, seen_utc, resolved_utc) VALUES($m, $s, $o, $k, $d, $st, $t, NULL) ON CONFLICT(marketplace, shop, order_id, kind) DO UPDATE SET detail=excluded.detail, state=excluded.state, seen_utc=excluded.seen_utc, resolved_utc=NULL";
        cmd.Parameters.AddWithValue("$m", m); cmd.Parameters.AddWithValue("$s", s); cmd.Parameters.AddWithValue("$o", o); cmd.Parameters.AddWithValue("$k", k); cmd.Parameters.AddWithValue("$d", clean); cmd.Parameters.AddWithValue("$st", LineMappingOrderException.Open); cmd.Parameters.AddWithValue("$t", T(at));
        cmd.ExecuteNonQuery();
        return Of(m, s, o).Single(x => x.Kind == k);
    }

    public bool Resolve(string marketplace, string shopId, string orderId, string kind, DateTime nowUtc)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE order_exceptions SET state=$st, resolved_utc=$t WHERE marketplace=$m AND shop=$s AND order_id=$o AND kind=$k AND state<>$st";
        cmd.Parameters.AddWithValue("$st", LineMappingOrderException.Resolved); cmd.Parameters.AddWithValue("$t", T(Utc(nowUtc))); cmd.Parameters.AddWithValue("$m", (marketplace ?? "").Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$s", (shopId ?? "").Trim()); cmd.Parameters.AddWithValue("$o", (orderId ?? "").Trim()); cmd.Parameters.AddWithValue("$k", (kind ?? "").Trim());
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<LineMappingOrderException> Of(string marketplace, string shopId, string orderId) { using var c = Open(); return Read(c, "WHERE marketplace=$m AND shop=$s AND order_id=$o", ("$m", (marketplace ?? "").Trim().ToLowerInvariant()), ("$s", (shopId ?? "").Trim()), ("$o", (orderId ?? "").Trim())); }
    public IReadOnlyList<LineMappingOrderException> Open(string? marketplace = null, string? shopId = null)
    {
        using var c = Open();
        return marketplace is null ? Read(c, "WHERE state=$st", ("$st", LineMappingOrderException.Open)) : Read(c, "WHERE state=$st AND marketplace=$m AND shop=$s", ("$st", LineMappingOrderException.Open), ("$m", marketplace.Trim().ToLowerInvariant()), ("$s", (shopId ?? "").Trim()));
    }
    public IReadOnlyList<LineMappingOrderException> All() { using var c = Open(); return Read(c, "", null); }

    /// <summary>Why the order's stock must not be mutated now, or null when nothing blocks it.</summary>
    public string? StockBlockReason(string marketplace, string shopId, string orderId)
    {
        var blocking = Of(marketplace, shopId, orderId).Where(x => x.State == LineMappingOrderException.Open && LineMappingOrderException.BlocksStock(x.Kind)).ToList();
        return blocking.Count == 0 ? null : "açık sipariş istisnası: " + string.Join("; ", blocking.Select(x => x.Kind + " — " + x.Detail));
    }

    static List<LineMappingOrderException> Read(SqliteConnection c, string where, params (string Name, object Value)[]? parameters)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, marketplace, shop, order_id, kind, detail, state, seen_utc, resolved_utc FROM order_exceptions " + where + " ORDER BY seen_utc DESC, id";
        foreach (var (name, value) in parameters ?? Array.Empty<(string, object)>()) cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader(); var result = new List<LineMappingOrderException>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), U(r.GetString(7)), r.IsDBNull(8) ? null : U(r.GetString(8))));
        return result;
    }
    static DateTime Utc(DateTime at) => DateTime.SpecifyKind(at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at, DateTimeKind.Utc);
    static string T(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTime U(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
