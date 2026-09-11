using System.Security.Cryptography;

namespace TrMarketplaceHubDesktop;

public sealed record UpdateCheckResult(string Status, string Detail, string? Version = null);

public static class UpdateChannel
{
    public static UpdateCheckResult ValidateSource(Uri? source, string? expectedSha256, string packagePath)
    {
        if (source is null) return new("NOT_CONFIGURED", "Doğrulanmış güncelleme kaynağı tanımlı değil.");
        if (!source.IsAbsoluteUri || source.Scheme != Uri.UriSchemeHttps) return new("BLOCKED", "Güncelleme kaynağı HTTPS olmalı.");
        if (!File.Exists(packagePath)) return new("BLOCKED", "Güncelleme paketi bulunamadı.");
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64 || expectedSha256.Any(c => !Uri.IsHexDigit(c))) return new("BLOCKED", "Paket SHA-256 özeti geçersiz.");
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase) ? new("VERIFIED", "Paket SHA-256 özeti doğrulandı.") : new("BLOCKED", "Paket SHA-256 özeti eşleşmiyor.");
    }
}
