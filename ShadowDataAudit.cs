using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record ShadowDataAuditResult(bool ShadowDatabaseFound, string Status, string Detail);

public static class ShadowDataAudit
{
    public static ShadowDataAuditResult Inspect(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Veri yolu zorunlu.", nameof(directory));
        var path = Path.Combine(Path.GetFullPath(directory), "hub.db");
        return File.Exists(path) ? new(true, "WARNING", "Gölge hub.db bulundu; otomatik silinmedi.") : new(false, "CLEAR", "Gölge hub.db bulunamadı.");
    }
}
