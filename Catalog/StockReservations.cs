using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One reservation: units of a product held for an order on a store, from a moment until an expiry (or open), with its state and how it ended.</summary>
public sealed record StockReservation(long Id, string ProductId, string Sku, string Channel, string Shop, string OrderId, int Quantity, DateTime CreatedUtc, DateTime? ExpiresUtc, string State, DateTime? ReleasedUtc, string ReleaseReason, string Note)
{
    public const string Active = "ACTIVE", Released = "RELEASED", Expired = "EXPIRED";
    public string StoreKey => SafetyBufferProfile.StoreKey(Channel, Shop);
    /// <summary>An active reservation past its expiry is expired in effect before any cleanup marks it.</summary>
    public string EffectiveState(DateTime nowUtc) => State == Active && ExpiresUtc is { } end && end <= nowUtc ? Expired : State;
    /// <summary>Whether the units are held at the moment: active and not yet expired.</summary>
    public bool Holds(DateTime nowUtc) => EffectiveState(nowUtc) == Active;
}

/// <summary>One row of the aging report: the reservation, its order's fate, its age and time to expiry, its share of the product's stock, whether it is stale and why, the words.</summary>
public sealed record ReservationAgingRow(long Id, string Sku, string StoreKey, string OrderId, string OrderState, int Quantity, string State, TimeSpan Age, string AgeBucket, TimeSpan? UntilExpiry, int ProductStock, int StockImpactPercent, bool Stale, IReadOnlyList<string> Reasons, string Words);

/// <summary>The report: the counts by state, the stale ones, the units held, every row (stale first, oldest first), the headline.</summary>
public sealed record ReservationAgingReport(int Active, int Expired, int Released, int Stale, int ReservedUnits, IReadOnlyList<ReservationAgingRow> Rows, string Headline)
{
    public IReadOnlyList<ReservationAgingRow> StaleRows => Rows.Where(r => r.Stale).ToList();
    public IReadOnlyList<ReservationAgingRow> ForStore(string channel, string shop) { var key = SafetyBufferProfile.StoreKey(channel, shop); return Rows.Where(r => r.StoreKey == key).ToList(); }
}

/// <summary>
/// Stock reservations and their aging (#937). Until now nothing held stock for an order: the projection subtracted a
/// buffer and stopped. A reservation now holds units of a product for an order on a store, from its creation until an
/// expiry or a release, and the projection takes the active holds off the available figure and says so. The aging
/// report judges every reservation: its state (active, expired in effect, released), its order's fate (open, cancelled,
/// completed, missing, or none for a manual hold), its age bucket, the time left to its expiry, and its share of the
/// product's stock — and calls stale what deserves a look: an expired hold still on the books, an orphan whose order
/// is gone, a hold for a cancelled or completed order, and an open-ended hold older than three days. Read-only words;
/// releasing is the cleanup's business (#938). Order ids, skus, counts and hours only — never a customer.
/// </summary>
public static class ReservationAging
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(3);
    public const string OrderOpen = "açık", OrderCancelled = "iptal", OrderCompleted = "tamamlandı", OrderMissing = "sipariş bulunamadı", OrderNone = "elle (siparişsiz)";

    public static ReservationAgingReport Build(IReadOnlyList<StockReservation> reservations, IReadOnlyList<OrderSnapshot> orders, IReadOnlyList<CatalogProduct> products, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(reservations); ArgumentNullException.ThrowIfNull(orders); ArgumentNullException.ThrowIfNull(products);
        var stockById = products.ToDictionary(p => p.Id, p => p.Stock, StringComparer.Ordinal);
        var rows = new List<ReservationAgingRow>();
        foreach (var r in reservations)
        {
            var state = r.EffectiveState(nowUtc);
            var age = nowUtc - r.CreatedUtc; if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            TimeSpan? untilExpiry = r.ExpiresUtc is { } end ? end - nowUtc : null;
            var orderState = OrderStateOf(r, orders);
            var stock = stockById.TryGetValue(r.ProductId, out var s) ? s : 0;
            var impact = stock <= 0 ? 100 : (int)Math.Round(r.Quantity * 100m / stock, MidpointRounding.AwayFromZero);
            var reasons = new List<string>();
            if (state == StockReservation.Expired) reasons.Add("süresi dolmuş, hâlâ kayıtlı");
            if (state != StockReservation.Released && orderState == OrderMissing) reasons.Add("siparişi bulunamadı (öksüz)");
            if (state == StockReservation.Active && orderState is OrderCancelled or OrderCompleted) reasons.Add($"sipariş {orderState}, rezervasyon açık");
            if (state == StockReservation.Active && r.ExpiresUtc is null && age > StaleAfter) reasons.Add($"süresiz ve {Days(age)} günden eski");
            var isStale = reasons.Count > 0;
            var words = $"{r.Sku} · {r.StoreKey} · sipariş {(r.OrderId.Length == 0 ? "yok" : r.OrderId)} ({orderState}) · {N(r.Quantity)} adet · {StateWord(state)} · {Bucket(age)} ({Ago(age)})"
                + (untilExpiry is { } left ? (left > TimeSpan.Zero ? $" · bitişe {Hours(left)}" : $" · bitişi {Ago(-left)}") : " · süresiz")
                + $" · stok etkisi %{N(impact)} (stok {N(stock)})" + (isStale ? " · BAYAT: " + string.Join("; ", reasons) : "");
            rows.Add(new(r.Id, r.Sku, r.StoreKey, r.OrderId, orderState, r.Quantity, state, age, Bucket(age), untilExpiry, stock, impact, isStale, reasons, words));
        }
        rows = rows.OrderByDescending(x => x.Stale).ThenByDescending(x => x.Age).ThenBy(x => x.Id).ToList();
        var active = rows.Count(x => x.State == StockReservation.Active); var expired = rows.Count(x => x.State == StockReservation.Expired); var released = rows.Count(x => x.State == StockReservation.Released); var stale = rows.Count(x => x.Stale);
        var units = rows.Where(x => x.State == StockReservation.Active).Sum(x => x.Quantity);
        var headline = rows.Count == 0 ? "Rezervasyon yok." : $"{N(active)} aktif rezervasyon ({N(units)} adet), {N(expired)} süresi dolmuş, {N(released)} serbest; {N(stale)} bayat" + (stale > 0 ? " — inceleyin" : "") + ".";
        return new(active, expired, released, stale, units, rows, headline);
    }

    /// <summary>The fate of a reservation's order as the orders stand now: open, cancelled, completed, missing, or none for a manual hold (shared with the cleanup, #938).</summary>
    public static string OrderStateOf(StockReservation r, IReadOnlyList<OrderSnapshot> orders)
    {
        if (r.OrderId.Length == 0) return OrderNone;
        var order = orders.FirstOrDefault(o => string.Equals(o.OrderId, r.OrderId, StringComparison.Ordinal) && string.Equals((o.Marketplace ?? "").Trim(), r.Channel, StringComparison.OrdinalIgnoreCase) && string.Equals((o.ShopId ?? "").Trim(), r.Shop, StringComparison.Ordinal));
        if (order is null) return OrderMissing;
        var status = UiSearch.Fold(order.RawStatus ?? "");
        if (status.Contains("iptal", StringComparison.Ordinal) || status.Contains("cancel", StringComparison.Ordinal)) return OrderCancelled;
        if (order.Shipments.Any(s => s.State is "Delivered" or "Returned")) return OrderCompleted;
        return OrderOpen;
    }

    public static string Bucket(TimeSpan age) => age.TotalDays < 1 ? "<1 gün" : age.TotalDays < 3 ? "1-3 gün" : age.TotalDays < 7 ? "3-7 gün" : ">7 gün";
    static string StateWord(string state) => state switch { StockReservation.Active => "aktif", StockReservation.Expired => "süresi dolmuş", _ => "serbest" };
    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static string Days(TimeSpan span) => ((int)span.TotalDays).ToString(CultureInfo.InvariantCulture);
    static string Hours(TimeSpan span) => span.TotalHours < 1 ? $"{(int)span.TotalMinutes} dk" : span.TotalDays < 1 ? $"{(int)span.TotalHours} sa" : $"{(int)span.TotalDays} gün";
    static string Ago(TimeSpan age) { if (age < TimeSpan.Zero) age = TimeSpan.Zero; return age.TotalMinutes < 1 ? "az önce" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} dk önce" : age.TotalDays < 1 ? $"{(int)age.TotalHours} sa önce" : $"{(int)age.TotalDays} gün önce"; }
}

/// <summary>The reservations, in catalog.db beside the products: created with a product, a store, an order and a quantity; released with a reason; listed; the active units per product and store at a moment.</summary>
public sealed class StockReservationStore
{
    public const int NoteLimit = 200;
    readonly string connectionString;

    public StockReservationStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS StockReservations(Id INTEGER PRIMARY KEY AUTOINCREMENT, ProductId TEXT NOT NULL, Sku TEXT NOT NULL, Channel TEXT NOT NULL, Shop TEXT NOT NULL, OrderId TEXT NOT NULL DEFAULT '', Quantity INTEGER NOT NULL, CreatedUtc TEXT NOT NULL, ExpiresUtc TEXT NULL, State TEXT NOT NULL, ReleasedUtc TEXT NULL, ReleaseReason TEXT NOT NULL DEFAULT '', Note TEXT NOT NULL DEFAULT ''); CREATE INDEX IF NOT EXISTS IX_StockReservations_Product ON StockReservations(ProductId, Channel, Shop, State)";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    public StockReservation Reserve(string productId, string sku, string channel, string shop, string? orderId, int quantity, DateTime nowUtc, TimeSpan? timeToLive, string? note = null)
    {
        var cleanProduct = (productId ?? "").Trim(); var cleanChannel = (channel ?? "").Trim().ToLowerInvariant(); var cleanShop = (shop ?? "").Trim(); var cleanOrder = AuditStore.Redact((orderId ?? "").Trim());
        if (cleanProduct.Length == 0) throw new ArgumentException("Rezervasyon için ürün gerekli.");
        if (cleanChannel.Length == 0 || cleanShop.Length == 0) throw new ArgumentException("Rezervasyon için pazaryeri ve mağaza gerekli.");
        if (quantity <= 0) throw new ArgumentException("Rezervasyon adedi pozitif olmalı.");
        if (timeToLive is { } ttl && ttl <= TimeSpan.Zero) throw new ArgumentException("Rezervasyon süresi pozitif olmalı ya da boş (süresiz).");
        var cleanNote = AuditStore.Redact((note ?? "").Trim()); if (cleanNote.Length > NoteLimit) cleanNote = cleanNote[..NoteLimit];
        var at = Utc(nowUtc); DateTime? expires = timeToLive is { } live ? at + live : null;
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO StockReservations(ProductId, Sku, Channel, Shop, OrderId, Quantity, CreatedUtc, ExpiresUtc, State, Note) VALUES($p, $k, $c, $s, $o, $q, $t, $e, $st, $n); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", cleanProduct); cmd.Parameters.AddWithValue("$k", (sku ?? "").Trim()); cmd.Parameters.AddWithValue("$c", cleanChannel); cmd.Parameters.AddWithValue("$s", cleanShop); cmd.Parameters.AddWithValue("$o", cleanOrder); cmd.Parameters.AddWithValue("$q", quantity);
        cmd.Parameters.AddWithValue("$t", T(at)); cmd.Parameters.AddWithValue("$e", expires is { } x ? T(x) : DBNull.Value); cmd.Parameters.AddWithValue("$st", StockReservation.Active); cmd.Parameters.AddWithValue("$n", cleanNote);
        var id = (long)cmd.ExecuteScalar()!;
        return new(id, cleanProduct, (sku ?? "").Trim(), cleanChannel, cleanShop, cleanOrder, quantity, at, expires, StockReservation.Active, null, "", cleanNote);
    }

    /// <summary>Releases an active (or expired-in-effect) reservation with a reason; a released one is refused by name.</summary>
    public StockReservation Release(long id, string? reason, DateTime nowUtc)
    {
        var existing = Get(id) ?? throw new InvalidOperationException("Rezervasyon bulunamadı.");
        if (existing.State == StockReservation.Released) throw new InvalidOperationException("Rezervasyon zaten serbest bırakılmış.");
        var cleanReason = AuditStore.Redact((reason ?? "").Trim()); if (cleanReason.Length == 0) throw new ArgumentException("Serbest bırakma gerekçesi gerekli.");
        if (cleanReason.Length > NoteLimit) cleanReason = cleanReason[..NoteLimit];
        var at = Utc(nowUtc);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE StockReservations SET State=$st, ReleasedUtc=$t, ReleaseReason=$r WHERE Id=$id AND State<>$st";
        cmd.Parameters.AddWithValue("$st", StockReservation.Released); cmd.Parameters.AddWithValue("$t", T(at)); cmd.Parameters.AddWithValue("$r", cleanReason); cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() == 0) throw new InvalidOperationException("Rezervasyon zaten serbest bırakılmış.");
        return existing with { State = StockReservation.Released, ReleasedUtc = at, ReleaseReason = cleanReason };
    }

    public StockReservation? Get(long id) => Read("WHERE Id=$id", ("$id", id)).FirstOrDefault();

    /// <summary>Every reservation, newest first; a store narrows them.</summary>
    public IReadOnlyList<StockReservation> List(string? channel = null, string? shop = null)
    {
        var rows = channel is null ? Read("", null) : Read("WHERE Channel=$c AND Shop=$s", ("$c", channel.Trim().ToLowerInvariant()), ("$s", (shop ?? "").Trim()));
        return rows.OrderByDescending(r => r.Id).ToList();
    }

    /// <summary>The units held for a product on a store at a moment: active reservations not yet expired.</summary>
    public int ActiveQuantity(string productId, string channel, string shop, DateTime nowUtc)
        => Read("WHERE ProductId=$p AND Channel=$c AND Shop=$s AND State=$st", ("$p", (productId ?? "").Trim()), ("$c", (channel ?? "").Trim().ToLowerInvariant()), ("$s", (shop ?? "").Trim()), ("$st", StockReservation.Active)).Where(r => r.Holds(Utc(nowUtc))).Sum(r => r.Quantity);

    List<StockReservation> Read(string where, params (string Name, object Value)[]? parameters)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductId, Sku, Channel, Shop, OrderId, Quantity, CreatedUtc, ExpiresUtc, State, ReleasedUtc, ReleaseReason, Note FROM StockReservations " + where + " ORDER BY Id";
        foreach (var (name, value) in parameters ?? Array.Empty<(string, object)>()) cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader(); var result = new List<StockReservation>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), U(r.GetString(7)), r.IsDBNull(8) ? null : U(r.GetString(8)), r.GetString(9), r.IsDBNull(10) ? null : U(r.GetString(10)), r.GetString(11), r.GetString(12)));
        return result;
    }

    static DateTime Utc(DateTime at) => DateTime.SpecifyKind(at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at, DateTimeKind.Utc);
    static string T(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);
    static DateTime U(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}

public partial class CatalogStore
{
    /// <summary>#937: the aging report over every reservation, the orders and the products as they are now.</summary>
    public ReservationAgingReport ReservationAging(DateTime nowUtc)
        => Catalog.ReservationAging.Build(new StockReservationStore(dataDirectory).List(), new OrdersStore(dataDirectory).ReadAll(), Products(), nowUtc);

    /// <summary>#937: holds units of a product for an order on a store; the product must exist. Audited by id, sku, store, order and count.</summary>
    public StockReservation Reserve(string productId, string channel, string shop, string? orderId, int quantity, DateTime nowUtc, TimeSpan? timeToLive, string? note = null)
    {
        var product = FindProduct(productId) ?? throw new InvalidOperationException("Ürün bulunamadı.");
        var reservation = new StockReservationStore(dataDirectory).Reserve(product.Id, product.Sku, channel, shop, orderId, quantity, nowUtc, timeToLive, note);
        try { new AuditStore(dataDirectory).Append(new AuditEvent { Module = "stock", Action = "reserve", ProductId = product.Id, Outcome = "Info", Detail = AuditStore.Redact($"rezervasyon #{reservation.Id.ToString(CultureInfo.InvariantCulture)}: {product.Sku} · {reservation.StoreKey} · sipariş {(reservation.OrderId.Length == 0 ? "yok" : reservation.OrderId)} · {quantity.ToString(CultureInfo.InvariantCulture)} adet") }); } catch (Exception) { }
        return reservation;
    }
}
