using System.Globalization;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One line of the plan: the reservation, its order's fate, what the cleanup would do (release or keep) and why.</summary>
public sealed record ReservationCleanupRow(long Id, string Sku, string StoreKey, string OrderId, int Quantity, string State, string OrderState, string Action, string Reason)
{
    public const string Release = "RELEASE", Keep = "KEEP";
}

/// <summary>The dry-run: every reservation not yet released, judged; the counts; the words. A plan changes nothing.</summary>
public sealed record ReservationCleanupPlan(IReadOnlyList<ReservationCleanupRow> Rows, DateTime PlannedUtc, string Words)
{
    public IReadOnlyList<ReservationCleanupRow> Releasable => Rows.Where(r => r.Action == ReservationCleanupRow.Release).ToList();
    public int ReleaseCount => Rows.Count(r => r.Action == ReservationCleanupRow.Release);
    public int KeepCount => Rows.Count(r => r.Action == ReservationCleanupRow.Keep);
}

/// <summary>What an apply did: the released ids, the protected ones (planned for release, kept at apply time), a line per row, the words.</summary>
public sealed record ReservationCleanupResult(IReadOnlyList<long> Released, IReadOnlyList<long> Protected, IReadOnlyList<string> Lines, string Words);

/// <summary>
/// Stale reservation cleanup (#938). The aging report (#937) says which holds deserve a look; the cleanup releases
/// them — expired holds still on the books, holds for cancelled or completed orders, orphans whose order has been
/// missing longer than a day — and never a hold whose order is open, whatever its age or expiry, nor a fresh orphan
/// whose order may simply not have arrived yet, nor the operator's own open-ended hold. It runs as a dry-run first:
/// a plan that changes nothing and says what it would do and why. The apply takes a reason, and judges every planned
/// release again at that moment against the reservations and the orders as they are then — an order that arrived
/// or reopened since the plan protects its hold — before releasing it with the reason and putting it on the audit
/// trail. Ids, skus, stores, order ids, counts and hours only.
/// </summary>
public static class ReservationCleanup
{
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(24);
    public const string AuditAction = "reservation-cleanup";

    /// <summary>The dry-run over every reservation not yet released.</summary>
    public static ReservationCleanupPlan Plan(IReadOnlyList<StockReservation> reservations, IReadOnlyList<OrderSnapshot> orders, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(reservations); ArgumentNullException.ThrowIfNull(orders);
        var rows = reservations.Where(x => x.State != StockReservation.Released).OrderBy(x => x.Id).Select(r => Judge(r, orders, nowUtc)).ToList();
        var release = rows.Count(r => r.Action == ReservationCleanupRow.Release); var keep = rows.Count - release;
        var words = rows.Count == 0 ? "Temizlenecek rezervasyon yok; önizleme — hiçbir şey değişmedi." : $"{N(release)} rezervasyon serbest bırakılacak, {N(keep)} korunacak; önizleme — hiçbir şey değişmedi.";
        return new(rows, nowUtc, words);
    }

    /// <summary>One reservation's fate under the rules, from the orders as they stand at the moment.</summary>
    public static ReservationCleanupRow Judge(StockReservation r, IReadOnlyList<OrderSnapshot> orders, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(r); ArgumentNullException.ThrowIfNull(orders);
        var state = r.EffectiveState(nowUtc); var orderState = ReservationAging.OrderStateOf(r, orders);
        ReservationCleanupRow Row(string action, string reason) => new(r.Id, r.Sku, r.StoreKey, r.OrderId, r.Quantity, state, orderState, action, reason);
        if (state == StockReservation.Released) return Row(ReservationCleanupRow.Keep, "zaten serbest");
        if (orderState == ReservationAging.OrderOpen) return Row(ReservationCleanupRow.Keep, state == StockReservation.Expired ? "süresi dolmuş ama sipariş açık; korundu — süreyi uzatmak için yeniden rezerve edin" : "açık sipariş; asla temizlenmez");
        if (state == StockReservation.Expired) return Row(ReservationCleanupRow.Release, $"süresi dolmuş (bitiş {SafetyBufferProfile.Stamp(r.ExpiresUtc!.Value)})");
        if (orderState is ReservationAging.OrderCancelled or ReservationAging.OrderCompleted) return Row(ReservationCleanupRow.Release, $"sipariş {orderState}");
        if (orderState == ReservationAging.OrderMissing)
        {
            var age = nowUtc - r.CreatedUtc;
            return age > OrphanGrace
                ? Row(ReservationCleanupRow.Release, $"öksüz: siparişi {N((int)age.TotalHours)} saattir yok (sınır {N((int)OrphanGrace.TotalHours)} sa)")
                : Row(ReservationCleanupRow.Keep, $"öksüz ama {N((int)Math.Max(0, age.TotalHours))} saatlik; sipariş henüz gelmemiş olabilir (sınır {N((int)OrphanGrace.TotalHours)} sa)");
        }
        return Row(ReservationCleanupRow.Keep, "elle tutulmuş, süresiz; operatörün");
    }

    /// <summary>Applies a plan: every planned release is judged again now, against the reservation and the orders as they are; the ones still due are released with the reason and audited, the rest protected and said so.</summary>
    public static ReservationCleanupResult Apply(StockReservationStore store, IReadOnlyList<OrderSnapshot> ordersNow, ReservationCleanupPlan plan, string? reason, DateTime nowUtc, AuditStore? audit = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(ordersNow); ArgumentNullException.ThrowIfNull(plan);
        var cleanReason = AuditStore.Redact((reason ?? "").Trim()); if (cleanReason.Length == 0) throw new ArgumentException("Temizlik gerekçesi gerekli.");
        if (cleanReason.Length > StockReservationStore.NoteLimit) cleanReason = cleanReason[..StockReservationStore.NoteLimit];
        var released = new List<long>(); var kept = new List<long>(); var lines = new List<string>();
        foreach (var row in plan.Releasable)
        {
            var current = store.Get(row.Id);
            if (current is null || current.State == StockReservation.Released) { kept.Add(row.Id); lines.Add($"#{N(row.Id)}: zaten serbest ya da yok; atlandı"); continue; }
            var again = Judge(current, ordersNow, nowUtc);
            if (again.Action != ReservationCleanupRow.Release) { kept.Add(row.Id); lines.Add($"#{N(row.Id)}: korundu — {again.Reason}"); continue; }
            var done = store.Release(row.Id, $"temizlik: {cleanReason} · {again.Reason}", nowUtc);
            released.Add(row.Id); lines.Add($"#{N(row.Id)}: serbest — {again.Reason} ({N(done.Quantity)} adet, {done.Sku}, {done.StoreKey})");
            try { audit?.Append(new AuditEvent { Module = "stock", Action = AuditAction, ProductId = done.ProductId, Outcome = "Info", Detail = AuditStore.Redact($"rezervasyon #{N(row.Id)} temizlendi: {again.Reason} · gerekçe: {cleanReason}") }); } catch (Exception) { }
        }
        return new(released, kept, lines, $"{N(released.Count)} rezervasyon serbest bırakıldı, {N(kept.Count)} korundu/atlandı; gerekçe: {cleanReason}");
    }

    static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}

public partial class CatalogStore
{
    /// <summary>#938: the cleanup dry-run over the reservations and the orders as they are now; nothing changes.</summary>
    public ReservationCleanupPlan PlanReservationCleanup(DateTime nowUtc)
        => ReservationCleanup.Plan(new StockReservationStore(dataDirectory).List(), new OrdersStore(dataDirectory).ReadAll(), nowUtc);

    /// <summary>#938: applies a plan with a reason, judging every planned release again against the orders as they are now; audited per release.</summary>
    public ReservationCleanupResult ApplyReservationCleanup(ReservationCleanupPlan plan, string? reason, DateTime nowUtc)
        => ReservationCleanup.Apply(new StockReservationStore(dataDirectory), new OrdersStore(dataDirectory).ReadAll(), plan, reason, nowUtc, new AuditStore(dataDirectory));
}
