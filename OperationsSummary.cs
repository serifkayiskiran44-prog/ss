namespace TrMarketplaceHubDesktop;

public sealed record OperationsSummary(int OpenOrders, int FailedSyncs, int XmlFailures, int ConnectionIssues, int DataQualityErrors, string EmptyState)
{
    public bool HasAction => OpenOrders > 0 || FailedSyncs > 0 || XmlFailures > 0 || ConnectionIssues > 0 || DataQualityErrors > 0;
}

/// <summary>Normalizes supporting-screen counters from the read-only dashboard snapshot.</summary>
public static class OperationsSummaryService
{
    public static OperationsSummary From(DashboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var xmlFailures = snapshot.Notifications.Count(x => x.Title.StartsWith("XML çalışması başarısız", StringComparison.Ordinal));
        var qualityErrors = snapshot.Notifications.Count(x => x.Title == "Veri kalite engeli");
        return new(snapshot.OpenOrders, snapshot.FailedSyncs, xmlFailures, snapshot.ConnectionIssues, qualityErrors, snapshot.TotalProducts == 0 && snapshot.OpenOrders == 0 ? "Henüz yerel operasyon verisi yok." : "");
    }
}
