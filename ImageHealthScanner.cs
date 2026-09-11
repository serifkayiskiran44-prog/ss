namespace TrMarketplaceHubDesktop;

public sealed record ImageHealthInput(string ProductId, string Url, string? ContentType, long? Bytes, int? HttpStatus, string? Hash = null);
public sealed record ImageHealthFinding(string ProductId, string Url, string Status, string Fingerprint);

public static class ImageHealthScanner
{
    public static IReadOnlyList<ImageHealthFinding> Evaluate(IEnumerable<ImageHealthInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var rows = inputs.ToArray(); var duplicateUrls = rows.GroupBy(x => NormalizeUrl(x.Url)).Where(g => g.Count() > 1).SelectMany(g => g.Select(x => x.ProductId)).ToHashSet();
        return rows.Select(x =>
        {
            var safeUrl = AuditStore.Sanitize(x.Url);
            var status = string.IsNullOrWhiteSpace(x.Url) ? "MISSING" : duplicateUrls.Contains(x.ProductId) ? "DUPLICATE" : x.HttpStatus is 404 ? "NOT_FOUND" : x.HttpStatus is null ? "UNAVAILABLE" : x.HttpStatus >= 400 ? "HTTP_ERROR" : x.Bytes > 10_000_000 ? "OVERSIZE" : x.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ? "WRONG_CONTENT" : "HEALTHY";
            return new ImageHealthFinding(x.ProductId, safeUrl, status, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(x.ProductId + "|" + NormalizeUrl(x.Url)))));
        }).ToArray();
    }
    static string NormalizeUrl(string value) => (value ?? "").Trim().TrimEnd('/').ToLowerInvariant();
}
