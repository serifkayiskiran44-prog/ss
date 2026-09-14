using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TrMarketplaceHubDesktop.Catalog;

public enum MediaMatchStatus { Matched, Ambiguous, NoMatch, Unsupported, DuplicateFilename }
public sealed record MediaScanCandidate(string FileName, string FullPath, MediaMatchStatus Status, string? ProductId, string? ProductSku, DateTime? ProductUpdatedUtc);
public sealed record MediaScanPreview(string Directory, IReadOnlyList<MediaScanCandidate> Candidates)
{
    public int Matched => Candidates.Count(x => x.Status == MediaMatchStatus.Matched);
    public int Review => Candidates.Count(x => x.Status is MediaMatchStatus.Ambiguous or MediaMatchStatus.NoMatch or MediaMatchStatus.DuplicateFilename);
    public int Unsupported => Candidates.Count(x => x.Status == MediaMatchStatus.Unsupported);
}
public sealed record MediaScanApplyResult(int Committed, int AlreadyLinked, int Stale, int Failed);

/// Local folder -> product image ingest. The only mapping rule is deterministic:
/// a file's name (without extension) must exactly match a product's SKU or barcode
/// (Unicode-safe normalized comparison, see CatalogStore.NormalizeIdentityKey) -
/// no fuzzy/best-guess matching, so an ambiguous or unmatched file is never
/// silently attached to the wrong product. Extends the existing ProductMedia model
/// (MediaStore.Ensure); this is not a second, parallel media system.
public static class MediaFolderScan
{
    static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".tif", ".tiff" };

    /// Read-only: enumerates the user-selected directory (top-level only - no
    /// recursion, so a symlinked subfolder cannot pull in files outside it) and
    /// classifies each file without writing anything. The directory itself is the
    /// sole approved root for this operation; MediaFileAccessPolicy still resolves
    /// symlink/reparse targets and rejects any escape from it.
    public static MediaScanPreview Preview(string directory, IReadOnlyList<CatalogProduct> products, CancellationToken cancellationToken = default)
    {
        var canonicalRoot = MediaFileAccessPolicy.Canonicalize(directory);
        if (!Directory.Exists(canonicalRoot)) throw new InvalidOperationException("Seçilen klasör bulunamadı.");
        var byIdentity = new Dictionary<string, List<CatalogProduct>>();
        foreach (var p in products)
        {
            foreach (var key in new[] { CatalogStore.NormalizeIdentityKey(p.Sku), CatalogStore.NormalizeIdentityKey(p.Barcode) })
            {
                if (key.Length == 0) continue;
                if (!byIdentity.TryGetValue(key, out var list)) byIdentity[key] = list = new();
                list.Add(p);
            }
        }
        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(canonicalRoot, "*", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var file in files)
        {
            var nameKey = CatalogStore.NormalizeIdentityKey(Path.GetFileNameWithoutExtension(file));
            if (!seenFileNames.Add(nameKey)) duplicateFileNames.Add(nameKey);
        }
        var candidates = new List<MediaScanCandidate>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MediaFileAccessPolicy.TryResolveApprovedFile(file, [canonicalRoot], out var resolved, out _)) continue; // reparse escape - silently excluded, not a candidate
            var fileName = Path.GetFileName(resolved);
            var extension = Path.GetExtension(resolved);
            if (!SupportedExtensions.Contains(extension)) { candidates.Add(new(fileName, resolved, MediaMatchStatus.Unsupported, null, null, null)); continue; }
            var nameKey = CatalogStore.NormalizeIdentityKey(Path.GetFileNameWithoutExtension(resolved));
            if (duplicateFileNames.Contains(nameKey)) { candidates.Add(new(fileName, resolved, MediaMatchStatus.DuplicateFilename, null, null, null)); continue; }
            if (!byIdentity.TryGetValue(nameKey, out var matches) || matches.Count == 0) { candidates.Add(new(fileName, resolved, MediaMatchStatus.NoMatch, null, null, null)); continue; }
            var distinctProducts = matches.Select(x => x.Id).Distinct().ToList();
            if (distinctProducts.Count > 1) { candidates.Add(new(fileName, resolved, MediaMatchStatus.Ambiguous, null, null, null)); continue; }
            var product = matches[0];
            candidates.Add(new(fileName, resolved, MediaMatchStatus.Matched, product.Id, product.Sku, product.UpdatedUtc));
        }
        return new(canonicalRoot, candidates);
    }

    /// Applies only Matched rows. Each product is re-checked against its current
    /// UpdatedUtc (captured at preview time) immediately before linking, so a
    /// product edited after the preview was built is skipped as stale rather than
    /// linked against outdated assumptions. Re-running on the same folder is
    /// idempotent: MediaStore.Ensure links only if the exact file isn't already
    /// attached to that product. A cancelled run keeps everything already committed
    /// (Ensure calls are individually atomic) but reports Cancelled, not Succeeded.
    public static MediaScanApplyResult ApplyApproved(MediaScanPreview preview, bool approved, CatalogStore catalog, MediaStore media, MediaScanRunStore? runs = null, string? runId = null, CancellationToken cancellationToken = default)
    {
        if (!approved) throw new InvalidOperationException("Görsel eşleme için önizleme onayı gerekli.");
        var committed = 0; var already = 0; var stale = 0; var failed = 0;
        var current = catalog.Products().ToDictionary(p => p.Id);
        try
        {
            foreach (var row in preview.Candidates.Where(x => x.Status == MediaMatchStatus.Matched))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.ProductId is null || !current.TryGetValue(row.ProductId, out var product) || product.UpdatedUtc != row.ProductUpdatedUtc) { stale++; continue; }
                try
                {
                    var before = media.List(row.ProductId).Count(x => x.NormalizedUrl.Equals(MediaStore.NormalizeUrl(new Uri(row.FullPath).AbsoluteUri), StringComparison.OrdinalIgnoreCase));
                    media.Ensure(row.ProductId, new Uri(row.FullPath).AbsoluteUri, "local-folder-scan");
                    if (before > 0) already++; else committed++;
                }
                catch (Exception) { failed++; }
                if (runId is not null) runs?.UpdateProgress(runId, committed, already, stale, failed);
            }
            if (runId is not null) runs?.Complete(runId, "Succeeded");
        }
        catch (OperationCanceledException)
        {
            if (runId is not null) runs?.Complete(runId, "Cancelled");
            throw;
        }
        return new(committed, already, stale, failed);
    }
}

public sealed class MediaScanRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Directory { get; set; } = "";
    public string Status { get; set; } = "Running";
    public int Discovered { get; set; }
    public int Matched { get; set; }
    public int Review { get; set; }
    public int Committed { get; set; }
    public int AlreadyLinked { get; set; }
    public int Stale { get; set; }
    public int Failed { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
}

/// Durable per-run observability for media folder scans: stable id, real discovered/
/// matched/review/committed/stale/failed counts (never an estimated percentage), and
/// a terminal state that distinguishes Succeeded from Cancelled/Failed/Abandoned.
public sealed class MediaScanRunStore
{
    readonly string connectionString;
    public MediaScanRunStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS MediaScanRuns(
                Id TEXT PRIMARY KEY, Directory TEXT NOT NULL, Status TEXT NOT NULL,
                Discovered INTEGER NOT NULL, Matched INTEGER NOT NULL, Review INTEGER NOT NULL,
                Committed INTEGER NOT NULL, AlreadyLinked INTEGER NOT NULL, Stale INTEGER NOT NULL, Failed INTEGER NOT NULL,
                StartedUtc TEXT NOT NULL, CompletedUtc TEXT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }

    /// A directory already claimed by an active Running run cannot be claimed again
    /// until that run reaches a terminal state (or is recovered via AbandonedRunning).
    public MediaScanRun Start(string directory, int discovered, int matched, int review)
    {
        var canonical = MediaFileAccessPolicy.Canonicalize(directory);
        var run = new MediaScanRun { Directory = canonical, Discovered = discovered, Matched = matched, Review = review };
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var check = c.CreateCommand())
        {
            check.Transaction = tx; check.CommandText = "SELECT 1 FROM MediaScanRuns WHERE Status='Running' AND Directory=$dir LIMIT 1";
            check.Parameters.AddWithValue("$dir", canonical);
            if (check.ExecuteScalar() is not null) throw new InvalidOperationException("Bu klasör için zaten çalışan bir tarama var.");
        }
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO MediaScanRuns(Id,Directory,Status,Discovered,Matched,Review,Committed,AlreadyLinked,Stale,Failed,StartedUtc,CompletedUtc) VALUES($id,$dir,$status,$disc,$matched,$review,0,0,0,0,$started,NULL)";
        cmd.Parameters.AddWithValue("$id", run.Id); cmd.Parameters.AddWithValue("$dir", run.Directory); cmd.Parameters.AddWithValue("$status", run.Status);
        cmd.Parameters.AddWithValue("$disc", run.Discovered); cmd.Parameters.AddWithValue("$matched", run.Matched); cmd.Parameters.AddWithValue("$review", run.Review);
        cmd.Parameters.AddWithValue("$started", run.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        tx.Commit();
        return run;
    }

    public void UpdateProgress(string id, int committed, int already, int stale, int failed)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE MediaScanRuns SET Committed=$committed,AlreadyLinked=$already,Stale=$stale,Failed=$failed WHERE Id=$id AND Status='Running'";
        cmd.Parameters.AddWithValue("$committed", committed); cmd.Parameters.AddWithValue("$already", already); cmd.Parameters.AddWithValue("$stale", stale); cmd.Parameters.AddWithValue("$failed", failed); cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Complete(string id, string status)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE MediaScanRuns SET Status=$status,CompletedUtc=$completed WHERE Id=$id AND Status='Running'";
        cmd.Parameters.AddWithValue("$status", status); cmd.Parameters.AddWithValue("$completed", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// A crashed/killed run leaves Status='Running' with no CompletedUtc forever;
    /// this makes that state explicit and queryable as recoverable rather than
    /// silently misreported as still-in-progress or as a false success.
    public IReadOnlyList<MediaScanRun> AbandonedRunning()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Directory,Status,Discovered,Matched,Review,Committed,AlreadyLinked,Stale,Failed,StartedUtc,CompletedUtc FROM MediaScanRuns WHERE Status='Running'";
        using var r = cmd.ExecuteReader(); var result = new List<MediaScanRun>(); while (r.Read()) result.Add(Read(r)); return result;
    }

    public MediaScanRun Get(string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Directory,Status,Discovered,Matched,Review,Committed,AlreadyLinked,Stale,Failed,StartedUtc,CompletedUtc FROM MediaScanRuns WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : throw new InvalidOperationException("Tarama kaydı bulunamadı.");
    }

    static MediaScanRun Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), Directory = r.GetString(1), Status = r.GetString(2), Discovered = r.GetInt32(3), Matched = r.GetInt32(4), Review = r.GetInt32(5),
        Committed = r.GetInt32(6), AlreadyLinked = r.GetInt32(7), Stale = r.GetInt32(8), Failed = r.GetInt32(9),
        StartedUtc = DateTime.Parse(r.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        CompletedUtc = r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };
}
