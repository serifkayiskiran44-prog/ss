namespace TrMarketplaceHubDesktop;

// #786: no copy-to-clipboard action exists anywhere in this app today (confirmed by grep -- zero
// Clipboard.* call sites), so this is the classification/masking core for whenever one is added, not a
// retrofit. Reuses AuditStore.Sanitize's existing pattern detection (Authorization headers, key=value
// secrets, email/phone PII, local user-profile paths) instead of duplicating it: if sanitizing a value
// changes it, the value contained something sensitive.
public static class ClipboardRedaction
{
    static readonly HashSet<string> SensitiveFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Token", "AccessToken", "RefreshToken", "Password", "Secret", "ApiKey", "Key", "ClientSecret", "Authorization"
    };

    public static bool IsSensitive(string fieldName, string value)
        => !string.IsNullOrEmpty(value) && (SensitiveFieldNames.Contains((fieldName ?? "").Trim()) || AuditStore.Sanitize(value) != value);

    public static string PrepareForCopy(string fieldName, string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        // The whole value IS the secret for these fields (content-based detection has nothing to anchor
        // on), so mask entirely rather than partially -- unlike Sanitize's in-place redaction of a secret
        // embedded within otherwise-useful surrounding text.
        if (SensitiveFieldNames.Contains((fieldName ?? "").Trim())) return "[gizli - kopyalanmadı]";
        return AuditStore.Sanitize(value);
    }

    public static IReadOnlyList<string> PrepareManyForCopy(IEnumerable<(string FieldName, string Value)> fields)
        => fields.Select(f => PrepareForCopy(f.FieldName, f.Value)).ToList();
}
