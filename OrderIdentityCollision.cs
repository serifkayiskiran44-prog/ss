using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

/// <summary>What an incoming order snapshot is, judged against what the store already holds for that (marketplace, shop, order id): brand new, the same order updated, or a payload that looks like a different order under the same number.</summary>
public sealed record OrderIdentityVerdict(string Outcome, string Marketplace, string ShopId, string OrderId, string Words)
{
    public const string New = "NEW", SameOrderUpdate = "SAME_ORDER_UPDATE", ConflictingPayload = "CONFLICTING_PAYLOAD";
}

/// <summary>What the save actually did: applied normally, or parked as a conflict awaiting an operator's decision.</summary>
public sealed record OrderSaveOutcome(string Outcome, OrderIdentityVerdict Verdict, long? ConflictId)
{
    public const string Applied = "APPLIED", NeedsReview = "NEEDS_REVIEW";
}

/// <summary>One parked conflict: the two payloads (as their core fingerprints, never the full snapshot with its shipments), open until an operator picks one.</summary>
public sealed record OrderIdentityConflict(long Id, string Marketplace, string ShopId, string OrderId, string ExistingSummary, string IncomingPayload, string State, DateTime SeenUtc, DateTime? ResolvedUtc, string Resolution);

/// <summary>
/// Receipt/order identity collision detector (#948). The store already scopes an order by (marketplace, shop, order
/// id), so the same number on two different stores is never a collision — it is simply two different orders, each
/// its own. Within one store, the same number arriving twice is normally the marketplace resending the same order
/// with an updated status: the core of it — the totals, the currency, the lines' SKUs and quantities — stays the
/// same or is a superset of what was there. When the core disagrees outright (different lines, a different total,
/// nothing in common) it looks like two unrelated orders were assigned the same number — a receipt id reused, or a
/// connector bug — and is never silently overwritten: it is parked as a review, both payloads kept, until an
/// operator picks which one is real. The store's own optimistic timestamp check is not enough here, because a
/// conflicting payload can carry a newer timestamp and still be wrong. Order ids, skus, counts only — never a
/// customer.
/// </summary>
public static class OrderIdentityCollision
{
    /// <summary>The core identity of an order's content, ignoring status, shipments and tracking: for comparing whether two payloads are "the same order" or a collision.</summary>
    public static string CoreFingerprint(OrderSnapshot order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var lines = order.Items.Select(i => (i.Sku ?? "").Trim().ToUpperInvariant() + ":" + i.Quantity.ToString(CultureInfo.InvariantCulture)).OrderBy(x => x, StringComparer.Ordinal);
        return string.Join(",", lines) + "|" + (order.Total?.ToString("0.00", CultureInfo.InvariantCulture) ?? "-") + "|" + (order.Currency ?? "").Trim().ToUpperInvariant();
    }

    public static OrderIdentityVerdict Evaluate(OrderSnapshot? existing, OrderSnapshot incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var m = (incoming.Marketplace ?? "").Trim().ToLowerInvariant(); var s = (incoming.ShopId ?? "").Trim(); var o = (incoming.OrderId ?? "").Trim();
        if (existing is null) return new(OrderIdentityVerdict.New, m, s, o, $"{o}: yeni sipariş");
        var existingLines = existing.Items.Select(i => (i.Sku ?? "").Trim().ToUpperInvariant()).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        var incomingLines = incoming.Items.Select(i => (i.Sku ?? "").Trim().ToUpperInvariant()).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        var sharesALine = existingLines.Count == 0 || incomingLines.Count == 0 || existingLines.Overlaps(incomingLines);
        var sameTotal = existing.Total is null || incoming.Total is null || !string.Equals((existing.Currency ?? "").Trim(), (incoming.Currency ?? "").Trim(), StringComparison.OrdinalIgnoreCase) || Math.Abs(existing.Total.Value - incoming.Total.Value) < 0.01m;
        if (sharesALine && sameTotal) return new(OrderIdentityVerdict.SameOrderUpdate, m, s, o, $"{o}: aynı siparişin güncellemesi");
        return new(OrderIdentityVerdict.ConflictingPayload, m, s, o, $"{o}: gelen veri kayıtlı siparişle örtüşmüyor (ortak SKU yok, toplam farklı) — aynı numara başka bir siparişe ait olabilir; inceleme gerekiyor, üzerine yazılmadı");
    }

    /// <summary>Saves an incoming snapshot only when it is new or a genuine update; a conflicting payload is parked in the review store and the existing order is left untouched.</summary>
    public static OrderSaveOutcome Save(OrdersStore orders, OrderIdentityConflictStore conflicts, OrderSnapshot incoming, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(orders); ArgumentNullException.ThrowIfNull(conflicts); ArgumentNullException.ThrowIfNull(incoming);
        var existing = orders.Find(incoming.Marketplace, incoming.ShopId, incoming.OrderId);
        var verdict = Evaluate(existing, incoming);
        if (verdict.Outcome != OrderIdentityVerdict.ConflictingPayload) { orders.SaveBatch(new[] { incoming }); return new(OrderSaveOutcome.Applied, verdict, null); }
        var conflict = conflicts.Raise(verdict.Marketplace, verdict.ShopId, verdict.OrderId, existing is null ? "" : CoreFingerprint(existing) + " (mevcut)", JsonSerializer.Serialize(incoming), nowUtc);
        return new(OrderSaveOutcome.NeedsReview, verdict, conflict.Id);
    }
}

/// <summary>The parked conflicts, in orders.db beside the orders.</summary>
public sealed class OrderIdentityConflictStore
{
    public const string OpenState = "OPEN", KeptExisting = "KEPT_EXISTING", AppliedIncoming = "APPLIED_INCOMING";
    readonly string directory; readonly string connectionString;

    public OrderIdentityConflictStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory); this.directory = directory;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "orders.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS order_identity_conflicts(id INTEGER PRIMARY KEY AUTOINCREMENT, marketplace TEXT NOT NULL, shop TEXT NOT NULL, order_id TEXT NOT NULL, existing_summary TEXT NOT NULL DEFAULT '', incoming_payload TEXT NOT NULL, state TEXT NOT NULL, seen_utc TEXT NOT NULL, resolved_utc TEXT NULL, resolution TEXT NOT NULL DEFAULT '')";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() { var c = SqliteConnectionPolicy.Open(connectionString); using var pragma = c.CreateCommand(); pragma.CommandText = "PRAGMA busy_timeout=15000"; pragma.ExecuteNonQuery(); return c; }

    public OrderIdentityConflict Raise(string marketplace, string shopId, string orderId, string existingSummary, string incomingPayload, DateTime nowUtc)
    {
        var at = Utc(nowUtc);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO order_identity_conflicts(marketplace, shop, order_id, existing_summary, incoming_payload, state, seen_utc) VALUES($m, $s, $o, $e, $p, $st, $t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$m", marketplace); cmd.Parameters.AddWithValue("$s", shopId); cmd.Parameters.AddWithValue("$o", orderId); cmd.Parameters.AddWithValue("$e", existingSummary); cmd.Parameters.AddWithValue("$p", incomingPayload); cmd.Parameters.AddWithValue("$st", OpenState); cmd.Parameters.AddWithValue("$t", T(at));
        var id = (long)cmd.ExecuteScalar()!;
        return new(id, marketplace, shopId, orderId, existingSummary, incomingPayload, OpenState, at, null, "");
    }

    /// <summary>The operator's decision: apply the parked incoming payload (overwriting whatever is stored now) or keep the existing order and discard the incoming one. Either way the conflict closes.</summary>
    public OrderIdentityConflict Resolve(long id, OrdersStore orders, bool keepIncoming, DateTime nowUtc)
    {
        var conflict = Get(id) ?? throw new InvalidOperationException("Çakışma kaydı bulunamadı.");
        if (conflict.State != OpenState) throw new InvalidOperationException("Çakışma zaten çözülmüş.");
        if (keepIncoming)
        {
            var incoming = JsonSerializer.Deserialize<OrderSnapshot>(conflict.IncomingPayload)!;
            var current = orders.Find(conflict.Marketplace, conflict.ShopId, conflict.OrderId);
            if (current is not null && incoming.UpdatedAt <= current.UpdatedAt) incoming.UpdatedAt = current.UpdatedAt.AddSeconds(1); // an operator's resolution always wins the store's own newer-wins check
            orders.SaveBatch(new[] { incoming });
        }
        var at = Utc(nowUtc); var resolution = keepIncoming ? AppliedIncoming : KeptExisting;
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE order_identity_conflicts SET state=$st, resolved_utc=$t, resolution=$r WHERE id=$id";
        cmd.Parameters.AddWithValue("$st", resolution); cmd.Parameters.AddWithValue("$t", T(at)); cmd.Parameters.AddWithValue("$r", resolution); cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        try { new AuditStore(directory).Append(new AuditEvent { Module = "orders", Action = "identity-conflict-resolve", Marketplace = conflict.Marketplace, ShopId = conflict.ShopId, OrderId = conflict.OrderId, Outcome = "Info", Detail = $"çakışma #{id.ToString(CultureInfo.InvariantCulture)}: {(keepIncoming ? "gelen veri uygulandı" : "mevcut sipariş korundu")}" }); } catch (Exception) { }
        return conflict with { State = resolution, ResolvedUtc = at, Resolution = resolution };
    }

    public OrderIdentityConflict? Get(long id) => Read("WHERE id=$id", ("$id", id)).FirstOrDefault();
    public IReadOnlyList<OrderIdentityConflict> Open_(string? marketplace = null, string? shopId = null)
        => marketplace is null ? Read("WHERE state=$st", ("$st", OpenState)) : Read("WHERE state=$st AND marketplace=$m AND shop=$s", ("$st", OpenState), ("$m", marketplace.Trim().ToLowerInvariant()), ("$s", (shopId ?? "").Trim()));
    public IReadOnlyList<OrderIdentityConflict> All() => Read("", null);

    List<OrderIdentityConflict> Read(string where, params (string Name, object Value)[]? parameters)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, marketplace, shop, order_id, existing_summary, incoming_payload, state, seen_utc, resolved_utc, resolution FROM order_identity_conflicts " + where + " ORDER BY id DESC";
        foreach (var (name, value) in parameters ?? Array.Empty<(string, object)>()) cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader(); var result = new List<OrderIdentityConflict>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), U(r.GetString(7)), r.IsDBNull(8) ? null : U(r.GetString(8)), r.GetString(9)));
        return result;
    }
    static DateTime Utc(DateTime at) => DateTime.SpecifyKind(at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at, DateTimeKind.Utc);
    static string T(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTime U(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
}
