namespace TrMarketplaceHubDesktop;

public sealed record ProductComplianceProfile(string Manufacturer, string CountryOfOrigin, string SafetyWarning, string DocumentUrl);
public sealed record ComplianceCheck(string Key, bool IsComplete, string Status);

public static class ProductComplianceValidator
{
    public static IReadOnlyList<ComplianceCheck> Validate(ProductComplianceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return [new("manufacturer", !string.IsNullOrWhiteSpace(profile.Manufacturer), "REQUIRED"), new("country-of-origin", !string.IsNullOrWhiteSpace(profile.CountryOfOrigin), "REQUIRED"), new("safety-warning", !string.IsNullOrWhiteSpace(profile.SafetyWarning), "REQUIRED"), new("document-url", string.IsNullOrWhiteSpace(profile.DocumentUrl) || Uri.TryCreate(profile.DocumentUrl, UriKind.Absolute, out _), "OPTIONAL_URL")];
    }
}
