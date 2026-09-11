namespace TrMarketplaceHubDesktop;

public enum ConnectivityMode { Online, Offline, Degraded, AuthRequired, RateLimited }

public sealed record ConnectivitySnapshot(ConnectivityMode Mode, DateTime AtUtc, DateTime? LastSuccessUtc, string Detail)
{
    public bool AllowsLiveDispatch => Mode == ConnectivityMode.Online;
}

public static class OfflineMode
{
    public static ConnectivitySnapshot Classify(Exception error, DateTime? lastSuccessUtc = null, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = AuditStore.Sanitize(error.Message);
        var mode = error is UnauthorizedAccessException || message.Contains("401", StringComparison.Ordinal) || message.Contains("403", StringComparison.Ordinal) ? ConnectivityMode.AuthRequired
            : message.Contains("429", StringComparison.Ordinal) || message.Contains("rate", StringComparison.OrdinalIgnoreCase) ? ConnectivityMode.RateLimited
            : error is HttpRequestException || message.Contains("DNS", StringComparison.OrdinalIgnoreCase) || message.Contains("ağ", StringComparison.OrdinalIgnoreCase) ? ConnectivityMode.Offline
            : ConnectivityMode.Degraded;
        return new(mode, nowUtc ?? DateTime.UtcNow, lastSuccessUtc, message);
    }

    public static void EnsureDispatchAllowed(ConnectivitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.AllowsLiveDispatch) throw new InvalidOperationException($"Canlı dispatch {snapshot.Mode} durumunda engellendi; bağlantı düzelince yeniden deneyin.");
    }
}
