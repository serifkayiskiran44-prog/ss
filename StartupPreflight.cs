using System.IO;
using System.Linq;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// Incomplete is the most severe value (ordinal order drives Overall's Max()
/// below) - it means the preflight pipeline itself failed to produce a real
/// result (unexpected exception, or somehow an empty check list), which must
/// never be treated as equivalent to a healthy Ready. See #2657.
public enum StartupHealthStatus { Ready, Degraded, Blocked, RecoveryRequired, Incomplete }

public sealed record StartupHealthCheck(string Key, StartupHealthStatus Status, string Detail);

public sealed record StartupHealthReport(IReadOnlyList<StartupHealthCheck> Checks)
{
    /// An empty check list is never "nothing to report, so Ready" - it can only
    /// mean the pipeline that should have populated it broke, so it fails closed
    /// to Incomplete instead of silently gating the app open. See #2657.
    public StartupHealthStatus Overall => Checks.Count == 0
        ? StartupHealthStatus.Incomplete
        : Checks.Select(x => x.Status).Max();
}

/// <summary>
/// Fast, offline, bounded preflight run once before MainWindow is created. It reuses
/// the existing store constructors (which already create their own empty schema) and
/// the existing AuditStore.Sanitize redaction; it does not introduce a second
/// integrity/checksum engine and never calls a marketplace API.
/// </summary>
public static class StartupPreflight
{
    const long LowDiskBlockedBytes = 20L * 1024 * 1024;
    const long LowDiskWarnBytes = 200L * 1024 * 1024;

    public static StartupHealthReport Run(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        var checks = new List<StartupHealthCheck>
        {
            CheckDataDirectory(directory),
            CheckDiskSpace(directory),
            CheckCoreDatabase(directory),
            CheckTemplates(),
        };
        return new(checks);
    }

    static StartupHealthCheck CheckDataDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".startup-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new("data-directory", StartupHealthStatus.Ready, "Yerel veri klasörü okunabilir ve yazılabilir.");
        }
        catch (UnauthorizedAccessException)
        {
            return new("data-directory", StartupHealthStatus.Blocked, "Yerel veri klasörüne yazma izni yok.");
        }
        catch (Exception ex)
        {
            return new("data-directory", StartupHealthStatus.Blocked, "Yerel veri klasörü kullanılamıyor: " + AuditStore.Sanitize(ex.Message));
        }
    }

    static StartupHealthCheck CheckDiskSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            // A check that could not actually run must never report as if it
            // verified a healthy disk - see #2657.
            if (string.IsNullOrEmpty(root)) return new("disk-space", StartupHealthStatus.Degraded, "Sürücü tespit edilemedi; disk alanı doğrulanamadı.");
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return new("disk-space", StartupHealthStatus.Degraded, "Sürücü hazır değil (kaldırılabilir/ağ sürücüsü olabilir); disk alanı doğrulanamadı.");
            var free = drive.AvailableFreeSpace;
            if (free < LowDiskBlockedBytes) return new("disk-space", StartupHealthStatus.Blocked, $"Disk alanı kritik seviyede düşük ({free / 1024 / 1024} MB).");
            if (free < LowDiskWarnBytes) return new("disk-space", StartupHealthStatus.Degraded, $"Disk alanı az ({free / 1024 / 1024} MB); yakında dolabilir.");
            return new("disk-space", StartupHealthStatus.Ready, $"Yeterli disk alanı var ({free / 1024 / 1024} MB).");
        }
        catch (Exception ex)
        {
            return new("disk-space", StartupHealthStatus.Degraded, "Disk alanı kontrol edilemedi: " + AuditStore.Sanitize(ex.Message));
        }
    }

    static StartupHealthCheck CheckCoreDatabase(string directory)
    {
        try
        {
            _ = new CatalogStore(directory).Products();
            return new("core-database", StartupHealthStatus.Ready, "Katalog veritabanı açılabildi ve okunabildi.");
        }
        catch (InvalidDataException ex)
        {
            return new("core-database", StartupHealthStatus.RecoveryRequired, "Yerel veritabanı bozuk görünüyor: " + AuditStore.Sanitize(ex.Message));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return new("core-database", StartupHealthStatus.RecoveryRequired, "Yerel veritabanı açılamadı: " + AuditStore.Sanitize(ex.Message));
        }
        catch (Exception ex)
        {
            return new("core-database", StartupHealthStatus.Blocked, "Yerel veritabanı kontrol edilemedi: " + AuditStore.Sanitize(ex.Message));
        }
    }

    const long MaxTemplateFileBytes = 2 * 1024 * 1024;

    /// Bounded on purpose: a per-file size budget checked before any read means a
    /// single oversized template can never force an unbounded File.ReadAllText
    /// into memory, and an unreadable/deleted-mid-scan/malformed-JSON file all
    /// fold into the same "corrupt" counter without crashing the scan. See #2657.
    static StartupHealthCheck CheckTemplates()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "templates");
        if (!Directory.Exists(dir)) return new("templates", StartupHealthStatus.Degraded, "Şablon klasörü bulunamadı; şablondan açma/kaydetme özelliği sınırlı olabilir.");
        List<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.json").ToList(); }
        catch (Exception ex) { return new("templates", StartupHealthStatus.Degraded, "Şablon klasörü okunamadı: " + AuditStore.Sanitize(ex.Message)); }
        var corrupt = 0; var total = 0;
        foreach (var file in files)
        {
            total++;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxTemplateFileBytes) { corrupt++; continue; }
                using var stream = File.OpenRead(file);
                using var document = JsonDocument.Parse(stream);
                _ = document.RootElement;
            }
            catch { corrupt++; }
        }
        if (corrupt > 0) return new("templates", StartupHealthStatus.Degraded, $"{corrupt}/{total} şablon dosyası okunamadı; diğer özellikler etkilenmez.");
        return new("templates", StartupHealthStatus.Ready, total == 0 ? "Şablon klasörü boş." : $"{total} şablon dosyası doğrulandı.");
    }
}
