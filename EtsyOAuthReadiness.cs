namespace TrMarketplaceHubDesktop;

public sealed record EtsyScopeReadiness(string AppType, IReadOnlySet<string> GrantedScopes, IReadOnlyList<string> MissingScopes, string Status, string Detail);
public static class EtsyOAuthReadiness
{
    public static EtsyScopeReadiness Evaluate(string appType, IEnumerable<string> grantedScopes, bool userMatchesShop)
    { var granted = new HashSet<string>(grantedScopes.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal); var missing = EtsyCapabilityAudit.OfficialManifest.Where(x => x.Status != "BLOCKED").Select(x => x.Scope).Distinct().Where(x => !granted.Contains(x)).ToArray(); if (!userMatchesShop) return new(appType, granted, missing, "BLOCKED", "OAuth user/shop eşleşmesi doğrulanmadı."); return missing.Length == 0 ? new(appType, granted, missing, "READY", "Gerekli scope’lar mevcut.") : new(appType, granted, missing, "REAUTHORIZE_REQUIRED", "Eksik scope için yeniden yetkilendirme gerekir."); }
    public static string ClassifyHttp(int status) => status switch { 401 => "AUTH_ERROR", 403 => "SCOPE_OR_PERMISSION_ERROR", 408 => "TIMEOUT", 429 => "RATE_LIMITED", >= 500 and <= 599 => "SERVER_ERROR", _ when status >= 400 => "CLIENT_ERROR", _ => "OK" };
}
