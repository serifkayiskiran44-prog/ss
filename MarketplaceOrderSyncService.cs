using System.Net.Http;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum MarketplaceOrderSyncStatus { Succeeded, Failed, Backoff, Stale }

public sealed record MarketplaceOrderSyncResult(
    string ConnectionId,
    string Channel,
    string ShopId,
    string DisplayName,
    MarketplaceOrderSyncStatus Status,
    int OrdersRead,
    int OrdersApplied,
    int ReviewRequired,
    DateTime CursorUtc,
    string Error);

/// <summary>
/// Reads every operational order-capable account independently. Remote reads are
/// account scoped by the adapter; local order and stock identities remain scoped
/// by channel/shop/order, and every online deduction is protected by the inventory receipt.
/// </summary>
public sealed class MarketplaceOrderSyncService
{
    readonly string? directory;
    readonly MarketplaceConnectionStore connections;
    readonly MarketplaceAdapterRegistry adapters;
    readonly OrdersStore orders;
    readonly OrderStockDecisionService stock;
    readonly MarketplaceShopSettingsStore settings;
    readonly Func<DateTime> utcNow;
    readonly Action<MarketplaceConnection>? beforePersist;
    readonly Action<MarketplaceConnection>? beforeSyncStatePersist;

    public MarketplaceOrderSyncService(string? directory = null, MarketplaceAdapterRegistry? adapters = null, Func<DateTime>? utcNow = null,
        Action<MarketplaceConnection>? beforePersist = null, Action<MarketplaceConnection>? beforeSyncStatePersist = null)
    {
        this.directory = directory;
        connections = new(directory);
        this.adapters = adapters ?? MarketplaceAdapterRegistry.CreateDefault(directory);
        orders = new(directory);
        stock = new(directory);
        settings = new(directory, this.adapters);
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.beforePersist = beforePersist;
        this.beforeSyncStatePersist = beforeSyncStatePersist;
    }

    public async Task<IReadOnlyList<MarketplaceOrderSyncResult>> RefreshAllAsync(CancellationToken token = default)
    {
        var results = new List<MarketplaceOrderSyncResult>();
        foreach (var connection in MarketplaceOperationalAccounts.List(connections))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var adapter = adapters.Get(connection.Channel);
                if (!adapter.Capabilities.Supports(MarketplaceOperation.OrdersRead)) continue;
                var accountSettings = settings.Load(connection.Id);
                if (!OrdersEnabled(accountSettings)) continue;
                results.Add(await RefreshAsync(connection, adapter, accountSettings, token).ConfigureAwait(false));
            }
            catch (ArgumentException) { continue; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                // A damaged per-account sync row must remain isolated and fail closed:
                // do not call the remote reader and do not rewrite the row automatically.
                results.Add(Result(connection, MarketplaceOrderSyncStatus.Failed, 0, 0, 0, DateTime.MinValue,
                    "Sipariş senkron durumu okunamadı; kayıt onarılmalı."));
            }
        }
        return results;
    }

    async Task<MarketplaceOrderSyncResult> RefreshAsync(MarketplaceConnection connection, IMarketplaceAdapter adapter, MarketplaceShopSettings accountSettings, CancellationToken token)
    {
        var now = Utc(utcNow());
        var stored = orders.GetSyncState(connection.Id);
        var sameGeneration = stored is not null &&
            stored.Channel.Equals(connection.Channel, StringComparison.Ordinal) &&
            stored.ShopId.Equals(connection.ShopId, StringComparison.Ordinal) &&
            stored.ConnectionRevision == connection.Revision;
        var cursor = sameGeneration ? stored!.CursorUtc : DateTime.MinValue;
        var expectedStateVersion = stored?.Version ?? 0;
        if (sameGeneration && stored!.NextAttemptUtc is { } next && next > now)
            return Result(connection, MarketplaceOrderSyncStatus.Backoff, 0, 0, 0, cursor, stored.LastError);

        try
        {
            var remote = await adapter.ReadOrdersAsync(connection.Id, cursor, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!StillCurrent(connection, accountSettings.Revision)) return Result(connection, MarketplaceOrderSyncStatus.Stale, 0, 0, 0, cursor, "");
            if (remote.Count > 10_000) throw new InvalidOperationException("Sipariş okuma güvenli kayıt sınırını aştı.");

            var seen = new HashSet<(string Marketplace, string Shop, string Order)>(new OrderKeyComparer());
            var normalized = new List<OrderSnapshot>(remote.Count);
            foreach (var source in remote)
            {
                token.ThrowIfCancellationRequested();
                var copy = OrderNormalizer.Normalize(source.Copy());
                var key = (copy.Marketplace, copy.ShopId, copy.OrderId);
                if (!seen.Add(key)) throw new InvalidOperationException("Uzak sipariş yanıtında tekrarlı hesap sipariş kimliği var.");
                normalized.Add(copy);
            }

            if (!StillCurrent(connection, accountSettings.Revision)) return Result(connection, MarketplaceOrderSyncStatus.Stale, 0, 0, 0, cursor, "");
            beforePersist?.Invoke(connection);
            if (!StillCurrent(connection, accountSettings.Revision)) return Result(connection, MarketplaceOrderSyncStatus.Stale, 0, 0, 0, cursor, "");
            var applied = 0;var resolved = new List<AccountOrderStockResolution>(normalized.Count);
            foreach (var copy in normalized)
            {
                token.ThrowIfCancellationRequested();
                var decision = stock.ResolveAccountOrder(connection, copy);resolved.Add(decision);
                var result = stock.ApplyAccountOrder(connection, decision, !decision.ReviewRequired && IsStockDeductible(decision.Order), accountSettings.Revision);
                if (result.StockApplied) applied++;
            }
            var nextCursor = resolved.Count == 0 ? cursor : resolved.Max(item => item.Order.SourceUpdatedAt.UtcDateTime);
            if (nextCursor < cursor) nextCursor = cursor;
            if (!StillCurrent(connection, accountSettings.Revision)) return Result(connection, MarketplaceOrderSyncStatus.Stale, resolved.Count, applied, resolved.Count(item => item.ReviewRequired), cursor, "");
            var nextState = new OrdersStore.SyncState(connection.Id, connection.Channel, connection.ShopId, connection.Revision,
                nextCursor, 0, null, "", expectedStateVersion, now);
            beforeSyncStatePersist?.Invoke(connection);
            orders.SaveSyncState(nextState, expectedStateVersion, connection);
            return Result(connection, MarketplaceOrderSyncStatus.Succeeded, resolved.Count, applied, resolved.Count(item => item.ReviewRequired), nextCursor, "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            if (!StillCurrent(connection, accountSettings.Revision)) return Result(connection, MarketplaceOrderSyncStatus.Stale, 0, 0, 0, cursor, "");
            var failures = checked((sameGeneration ? stored!.FailureCount : 0) + 1);
            var delayMinutes = Math.Min(60, 1 << Math.Min(6, failures - 1));
            var safeError = SafeError(error);
            try
            {
                orders.SaveSyncState(new(connection.Id, connection.Channel, connection.ShopId, connection.Revision, cursor,
                    failures, now.AddMinutes(delayMinutes), safeError, expectedStateVersion, now), expectedStateVersion, connection);
            }
            catch (InvalidOperationException)
            {
                return Result(connection, MarketplaceOrderSyncStatus.Stale, 0, 0, 0, cursor, "");
            }
            return Result(connection, MarketplaceOrderSyncStatus.Failed, 0, 0, 0, cursor, safeError);
        }
    }

    bool StillCurrent(MarketplaceConnection expected, long expectedSettingsRevision)
    {
        MarketplaceConnection? current;
        try { current = connections.Get(expected.Id); }
        catch (MarketplaceConnectionCorruptException) { return false; }
        if (current is null || !MarketplaceOperationalAccounts.IsEligible(current, connections) ||
            current.Revision != expected.Revision ||
            !current.Channel.Equals(expected.Channel, StringComparison.Ordinal) ||
            !current.ShopId.Equals(expected.ShopId, StringComparison.Ordinal)) return false;
        try
        {
            var currentSettings = settings.Load(expected.Id);
            return currentSettings.Revision == expectedSettingsRevision && OrdersEnabled(currentSettings);
        }
        catch (Exception) { return false; }
    }

    static bool OrdersEnabled(MarketplaceShopSettings value) =>
        value.Active && value.OrderRules.Enabled && value.Sync.OrdersEnabled;

    static MarketplaceOrderSyncResult Result(MarketplaceConnection connection, MarketplaceOrderSyncStatus status,
        int read, int applied, int review, DateTime cursor, string error) =>
        new(connection.Id, connection.Channel, connection.ShopId, connection.DisplayName, status, read, applied, review, cursor, error);

    static string SafeError(Exception error) => error switch
    {
        HttpRequestException => "Sipariş API bağlantısı tamamlanamadı.",
        InvalidOperationException => "Sipariş yanıtı veya yerel kayıt güvenli biçimde uygulanamadı.",
        ArgumentException => "Sipariş yanıtı geçersiz.",
        _ => "Sipariş senkronizasyonu tamamlanamadı."
    };

    static bool IsStockDeductible(OrderSnapshot order) =>
        !order.RawStatus.Equals("cancelled", StringComparison.OrdinalIgnoreCase);

    static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    sealed class OrderKeyComparer : IEqualityComparer<(string Marketplace, string Shop, string Order)>
    {
        public bool Equals((string Marketplace, string Shop, string Order) x, (string Marketplace, string Shop, string Order) y) =>
            x.Marketplace.Equals(y.Marketplace, StringComparison.OrdinalIgnoreCase) && x.Shop == y.Shop && x.Order == y.Order;
        public int GetHashCode((string Marketplace, string Shop, string Order) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Marketplace), value.Shop, value.Order);
    }
}
