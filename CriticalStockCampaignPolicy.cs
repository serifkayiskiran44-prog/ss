namespace TrMarketplaceHubDesktop;

public sealed record CampaignAllocation(string CampaignId, int ReservedUnits, DateTimeOffset StartsUtc, DateTimeOffset EndsUtc);
public sealed record CriticalStockDecision(int CurrentStock, int CriticalThreshold, int CampaignReserved, int AvailableToChannel, string Status);

public static class CriticalStockCampaignPolicy
{
    public static CriticalStockDecision Evaluate(int currentStock, int criticalThreshold, IEnumerable<CampaignAllocation> allocations, DateTimeOffset nowUtc)
    {
        if (currentStock < 0 || criticalThreshold < 0) throw new ArgumentOutOfRangeException("Stok ve kritik eşik negatif olamaz.");
        var active = allocations.Where(x => x.ReservedUnits > 0 && x.StartsUtc <= nowUtc && x.EndsUtc > nowUtc).ToArray();
        if (active.Any(x => x.EndsUtc <= x.StartsUtc)) throw new ArgumentException("Kampanya bitişi başlangıçtan sonra olmalı.", nameof(allocations));
        var reserved = active.Sum(x => x.ReservedUnits);
        if (reserved > currentStock) return new(currentStock, criticalThreshold, reserved, 0, "BLOCKED_OVERSUBSCRIBED");
        var available = Math.Max(0, currentStock - reserved - criticalThreshold);
        return new(currentStock, criticalThreshold, reserved, available, available == 0 ? "CRITICAL" : "AVAILABLE");
    }
}
