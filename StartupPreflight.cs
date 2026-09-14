using System.IO;
using System.Linq;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum StartupHealthStatus { Ready, Degraded, Blocked, RecoveryRequired }

public sealed record StartupHealthCheck(string Key, StartupHealthStatus Status, string Detail);

public sealed record StartupHealthReport(IReadOnlyList<StartupHealthCheck> Checks)
{
    public StartupHealthStatus Overall => Checks.Count == 0
        ? StartupHealthStatus.Ready
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
            if (string.IsNullOrEmpty(root)) return new("disk-space", StartupHealthStatus.Ready, "Sürücü tespit edilemedi; kontrol atlandı.");
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return new("disk-space", StartupHealthStatus.Ready, "Sürücü durumu okunamadı; kontrol atlandı.");
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

    static StartupHealthCheck CheckTemplates()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "templates");
        if (!Directory.Exists(dir)) return new("templates", StartupHealthStatus.Degraded, "Şablon klasörü bulunamadı; şablondan açma/kaydetme özelliği sınırlı olabilir.");
        var corrupt = 0;
        var total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            total++;
            try { JsonDocument.Parse(File.ReadAllText(file)); }
            catch { corrupt++; }
        }
        if (corrupt > 0) return new("templates", StartupHealthStatus.Degraded, $"{corrupt}/{total} şablon dosyası okunamadı; diğer özellikler etkilenmez.");
        return new("templates", StartupHealthStatus.Ready, total == 0 ? "Şablon klasörü boş." : $"{total} şablon dosyası doğrulandı.");
    }
}
