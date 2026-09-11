using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductionReadinessCheck(string Key, string Status, string Detail);

public sealed record ProductionReadinessReport(
    DateTime AtUtc,
    string DataDirectory,
    IReadOnlyList<ProductionReadinessCheck> Checks)
{
    public int Passed => Checks.Count(x => x.Status == "PASS");
    public int Warnings => Checks.Count(x => x.Status == "WARN");
    public int Blocked => Checks.Count(x => x.Status == "BLOCKED");
    public int Errors => Checks.Count(x => x.Status == "ERROR");
    public bool Ready => Blocked == 0 && Errors == 0;
}

/// <summary>
/// Read-only production gate for the local desktop application. It deliberately
/// does not call marketplace APIs or reveal credential values.
/// </summary>
public sealed class ProductionReadinessService
{
    static readonly Regex SecretAssignment = new(
        "(?i)(?:access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|password|passwd|secret|token)\\s*[:=]\\s*(?!\\[redacted\\]|null|empty|\\\"\\\"|''|$)[^\\s,;&]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".log", ".txt", ".json", ".xml", ".csv", ".tsv", ".ini", ".config" };
    readonly string directory;

    public ProductionReadinessService(string? dataDirectory = null)
    {
        directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
    }

    public ProductionReadinessReport Build()
    {
        var checks = new List<ProductionReadinessCheck>();
        checks.Add(CheckDataDirectory());
        checks.Add(CheckCoreStores());
        checks.Add(CheckSecretSafety());
        checks.Add(CheckDataQuality());
        checks.Add(CheckConnectorCapabilities());
        checks.Add(CheckApiHealth());
        return new(DateTime.UtcNow, directory, checks);
    }

    ProductionReadinessCheck CheckDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".readiness-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok", Encoding.UTF8);
            File.Delete(probe);
            return new("data-directory", "PASS", "Yerel veri klasörü okunabilir ve yazılabilir.");
        }
        catch (Exception error)
        {
            return new("data-directory", "ERROR", "Yerel veri klasörü kullanılamıyor: " + Safe(error.Message));
        }
    }

    ProductionReadinessCheck CheckCoreStores()
    {
        var failures = new List<string>();
        try { _ = new CatalogStore(directory).Products(); } catch (Exception error) { failures.Add("katalog: " + Safe(error.Message)); }
        try { _ = new OrdersStore(directory).ReadAll(); } catch (Exception error) { failures.Add("sipariş: " + Safe(error.Message)); }
        try { _ = new SyncStore(directory).List(); } catch (Exception error) { failures.Add("sync: " + Safe(error.Message)); }
        try { _ = new AuditStore(directory).List(1); } catch (Exception error) { failures.Add("audit: " + Safe(error.Message)); }
        if (failures.Count > 0) return new("core-stores", "ERROR", string.Join(" · ", failures));
        return new("core-stores", "PASS", "Katalog, sipariş, sync ve audit depoları okunabildi.");
    }

    ProductionReadinessCheck CheckSecretSafety()
    {
        try
        {
            if (!Directory.Exists(directory)) return new("secret-scan", "PASS", "Taranacak metin dosyası yok.");
            var findings = 0;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                if (!TextExtensions.Contains(info.Extension) || info.Length > 2 * 1024 * 1024) continue;
                string content;
                try { content = File.ReadAllText(path, Encoding.UTF8); } catch { continue; }
                findings += SecretAssignment.Matches(content).Count;
            }
            return findings == 0
                ? new("secret-scan", "PASS", "Metin log/export/config dosyalarında açık credential değeri bulunmadı.")
                : new("secret-scan", "BLOCKED", $"Açık credential biçimi taşıyan {findings} kayıt bulundu; değerler güvenlik nedeniyle raporlanmadı.");
        }
        catch (Exception error)
        {
            return new("secret-scan", "ERROR", "Secret taraması tamamlanamadı: " + Safe(error.Message));
        }
    }

    ProductionReadinessCheck CheckDataQuality()
    {
        try
        {
            // Refresh the preflight before deciding readiness; a stale quality
            // database must never make a changed catalog appear publishable.
            _ = new DataQualityService(directory).Scan();
            var summary = new DataQualityStore(directory).Summary();
            if (summary.Critical > 0) return new("data-quality", "BLOCKED", $"{summary.Critical:N0} kritik, {summary.Error:N0} hata ve {summary.Open:N0} açık veri kalite kaydı var.");
            if (summary.Error > 0 || summary.Warning > 0) return new("data-quality", "WARN", $"{summary.Error:N0} hata ve {summary.Warning:N0} uyarı kaydı var; canlı işlem öncesi inceleyin.");
            return new("data-quality", "PASS", "Açık kritik/hata/uyarı kaydı bulunmadı.");
        }
        catch (Exception error)
        {
            return new("data-quality", "ERROR", "Veri kalite özeti okunamadı: " + Safe(error.Message));
        }
    }

    static ProductionReadinessCheck CheckConnectorCapabilities()
    {
        var blocked = MarketplaceConnectionCatalog.All.Where(x => x.LiveApiBlocked).Select(x => x.Name).ToList();
        var verified = MarketplaceConnectionCatalog.All.Count - blocked.Count;
        return blocked.Count == 0
            ? new("connector-capabilities", "PASS", $"{verified} doğrulanmış connector yetenek sözleşmesi kayıtlı.")
            : new("connector-capabilities", "BLOCKED", $"{verified} doğrulanmış; {blocked.Count} kanal LIVE_API_BLOCKED olarak korunuyor. Canlı yazma açılmadı.");
    }

    ProductionReadinessCheck CheckApiHealth()
    {
        try
        {
            var records = new ApiHealthStore(directory).List();
            var failed = records.Count(x => x.State is not ("HEALTHY" or "NOT_CONFIGURED"));
            return failed == 0
                ? new("api-health", "PASS", $"{records.Count:N0} bağlantı sağlık kaydı kontrol edildi; aktif hata yok.")
                : new("api-health", "WARN", $"{failed:N0} bağlantıda hata/backoff var; canlı işlemden önce bağlantı ekranını inceleyin.");
        }
        catch (Exception error)
        {
            return new("api-health", "ERROR", "API sağlık özeti okunamadı: " + Safe(error.Message));
        }
    }

    static string Safe(string value) => AuditStore.Sanitize(value).Replace("\r", " ").Replace("\n", " ");
}
