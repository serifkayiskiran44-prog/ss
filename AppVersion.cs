namespace TrMarketplaceHubDesktop;

public static class AppVersion
{
    // #2562: MarketplaceHub is the single canonical user-visible product name (matching the assembly's own
    // TrMarketplaceHubDesktop / MarketplaceHub.Tests naming); the earlier working-title brand is retired from
    // every user-facing string. The %LOCALAPPDATA%\MonoBridgeDesktop app-data folder name is a separate, internal
    // storage identifier -- renaming it is real user-data migration, out of this issue's scope and owned by the
    // per-store migration issues (#2577-2582, #2601, #2603).
    public const string Product = "MarketplaceHub Desktop";
    public const string Current = "1.0.0";
    public static string Display => $"{Product} {Current}";
}
