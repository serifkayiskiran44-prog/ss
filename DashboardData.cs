using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record DashboardConnectionRow(string Channel, string ShopId, string DisplayName, bool Enabled, string Status, DateTime? LastTestUtc, string LastError, string RouteKey);
public sealed record DashboardNotification(string Severity, string Title, string Detail, string RouteKey);
public sealed record DashboardTrendPoint(DateTime Date, int Orders, int CurrentStock);
public sealed record DashboardSnapshot(
    int TotalProducts,
    int ActiveProducts,
    int OutOfStockProducts,
    int FailedSyncs,
    int PendingSyncs,
    int OpenOrders,
    int StockWaitingOrders,
    int XmlSources,
    string LastXmlStatus,
    DateTime? LastXmlUtc,
    int ConnectionIssues,
    DateTime GeneratedUtc,
    IReadOnlyList<DashboardConnectionRow> Connections,
    IReadOnlyList<DashboardNotification> Notifications,
    IReadOnlyList<DashboardTrendPoint> OrderTrend);

public static class DashboardFreshnessEvaluator
{
    public static string Status(DateTime? lastDataUtc, DateTime nowUtc, TimeSpan maxAge)
    {
        if (!lastDataUtc.HasValue) return "NO_DATA";
        return nowUtc - lastDataUtc.Value > maxAge ? "STALE" : "FRESH";
    }
}

/// <summary>Builds a read-only, local dashboard snapshot from existing stores.</summary>
public sealed class DashboardDataService
{
    readonly string? directory;
    public DashboardDataService(string? directory = null) => this.directory = directory;

    public DashboardSnapshot Load()
    {
        var catalog = new CatalogStore(directory);
        var products = catalog.Products();
        var orders = new OrdersStore(directory).ReadAll();
        var sync = new SyncStore(directory).List();
        var xmlRuns = new XmlRunStore(directory).List(null, 100);
        var sources = catalog.Sources();
        var connectionRows = new MarketplaceConnectionStore(directory).List()
            .Select(connection =>
            {
                var definition = MarketplaceConnectionCatalog.Get(connection.Channel);
                return new DashboardConnectionRow(connection.Channel, connection.ShopId, connection.DisplayName, connection.Enabled,
                    connection.Status, connection.LastTestUtc, MarketplaceConnectionStore.Redact(connection.LastError), definition.RouteKey ?? "connections");
            }).ToList();

        var failedSync = sync.Where(x => x.Status == SyncStatus.Failed).ToList();
        var pendingSync = sync.Count(x => x.Status is SyncStatus.Pending or SyncStatus.Running);
        var openOrders = orders.Where(IsOpen).ToList();
        var stockWaiting = orders.Count(order => catalog.GetOrderStockStatus(order.Marketplace, order.ShopId, order.OrderId) is null);
        var latestXml = xmlRuns.FirstOrDefault();
        var latestSourceRun = sources.Where(x => x.LastRunUtc.HasValue).OrderByDescending(x => x.LastRunUtc).FirstOrDefault();
        var lastXmlStatus = latestXml?.Status ?? latestSourceRun?.LastStatus ?? "Henüz çalışmadı";
        var lastXmlUtc = latestXml?.FinishedUtc ?? latestXml?.StartedUtc ?? latestSourceRun?.LastRunUtc;
        var connectionIssues = connectionRows.Count(x => !string.Equals(x.Status, "CONNECTED_READ_ONLY", StringComparison.OrdinalIgnoreCase));
        var quality = new DataQualityStore(directory).Summary();
        var notifications = new List<DashboardNotification>();

        foreach (var job in failedSync.Take(20))
            notifications.Add(new("Hata", $"Sync başarısız · {job.Channel}/{job.Operation}", MarketplaceConnectionStore.Redact(job.LastError), "sync"));
        foreach (var run in xmlRuns.Where(x => x.Status == "Failed").Take(10))
            notifications.Add(new("Hata", $"XML çalışması başarısız · {run.SourceId}", MarketplaceConnectionStore.Redact(run.Error), "xml"));
        foreach (var connection in connectionRows.Where(x => !string.Equals(x.Status, "CONNECTED_READ_ONLY", StringComparison.OrdinalIgnoreCase)).Take(20))
            notifications.Add(new("Uyarı", $"Bağlantı: {connection.DisplayName}", $"Durum: {connection.Status} · {connection.LastError}", connection.RouteKey));
        if (products.Any(x => x.Active && x.Stock <= 0))
            notifications.Add(new("Uyarı", "Kritik stok", $"{products.Count(x => x.Active && x.Stock <= 0):N0} aktif ürün stokta yok.", "products"));
        if (stockWaiting > 0)
            notifications.Add(new("Bilgi", "Sipariş stoğu bekliyor", $"{stockWaiting:N0} sipariş için yerel stok kararı uygulanmamış.", "orders"));
        if (quality.Critical > 0 || quality.Error > 0)
            notifications.Add(new("Hata", "Veri kalite engeli", $"{quality.Critical:N0} kritik ve {quality.Error:N0} hatalı kayıt satış öncesi incelenmeli.", "data-quality"));
        if (notifications.Count == 0)
            notifications.Add(new("Başarılı", "Açık uyarı yok", "Yerel veri kaynaklarında gösterilecek hata bulunamadı.", "dashboard"));

        var today = DateTime.Today;
        var trend = Enumerable.Range(0, 14).Select(offset =>
        {
            var date = today.AddDays(-13 + offset);
            var count = orders.Count(x => x.UpdatedAt.LocalDateTime.Date == date.Date);
            return new DashboardTrendPoint(date, count, date.Date == today.Date ? products.Sum(x => Math.Max(0, x.Stock)) : -1);
        }).ToList();

        return new DashboardSnapshot(
            products.Count,
            products.Count(x => x.Active),
            products.Count(x => x.Active && x.Stock <= 0),
            failedSync.Count,
            pendingSync,
            openOrders.Count,
            stockWaiting,
            sources.Count,
            lastXmlStatus,
            lastXmlUtc,
            connectionIssues,
            DateTime.UtcNow,
            connectionRows,
            notifications,
            trend);
    }

    static bool IsOpen(OrderSnapshot order) => order.Shipments.Count == 0 || order.Shipments.Any(shipment => shipment.State is not ("Delivered" or "Returned"));
}
