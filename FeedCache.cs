using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

public enum FeedCacheOutcome { Success, Failed }

/// <summary>One cached feed download: whose, which file, how it ended, its hash and size, when, under which run, and the address it came from with its query string removed and its user segment masked.</summary>
public sealed record FeedCacheEntry(string SourceId, string FileName, FeedCacheOutcome Outcome, string FeedHash, long Bytes, DateTime StoredUtc, string RunId, string LocationLabel);

public sealed record FeedCacheReport(int Kept, int Evicted, int Corrupt, int Orphans, long Bytes, long QuotaBytes)
{
    public bool OverQuota => Bytes > QuotaBytes;
}

/// <summary>
/// The feed download cache (#894). Every feed a source downloads is kept on disk next to the data — the successful
/// ones so the last known good feed is always at hand, the failed ones so a bad feed can be inspected — under a
/// retention policy: a few of each per source, a size quota across sources, and a last-known-good pointer that
/// retention never evicts. A sweep removes what the manifest does not know (orphans of deleted sources, files
/// left behind), what does not verify (a corrupt entry is never served), and the oldest entries beyond the
/// quota, while never touching a source with a run in progress or a file younger than the grace. The manifest
/// records an address only as a label: the query string is dropped and the user segment masked, so a URL that
/// carried a credential never lands in the cache's metadata.
/// </summary>
public static class FeedCache
{
    public const string FolderName = "feed-cache";
    public const string ManifestName = "manifest.json";
    public const string TemporaryMarker = ".tmp-";
    public const long DefaultQuotaBytes = 200L * 1024 * 1024;
    public const int KeepSuccessPerSource = 5;
    public const int KeepFailedPerSource = 2;
    public const string DiagnosticName = "Besleme önbelleği";
    public static readonly TimeSpan ActiveGrace = TimeSpan.FromMinutes(10);
    static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    sealed class Manifest { public List<FeedCacheEntry> Entries { get; set; } = new(); }

    public static string Root(string dataDirectory) => Path.Combine(dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory)), FolderName);

    static string SourceDirectory(string dataDirectory, string sourceId)
    {
        if (!SafeId.IsMatch(sourceId ?? "")) throw new ArgumentException("Kaynak kimliği önbellek için geçersiz.", nameof(sourceId));
        return Path.Combine(Root(dataDirectory), sourceId);
    }

    /// <summary>Stores a downloaded feed under the source and applies the per-source keep policy; the newest successful entry is the last known good.</summary>
    public static FeedCacheEntry Store(string dataDirectory, string sourceId, string feedText, FeedCacheOutcome outcome, string runId = "", string location = "", DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(feedText);
        var directory = SourceDirectory(dataDirectory, sourceId); Directory.CreateDirectory(directory);
        var now = nowUtc ?? DateTime.UtcNow;
        var bytes = Encoding.UTF8.GetBytes(feedText);
        var hash = Hash(bytes);
        var fileName = $"{now:yyyyMMdd-HHmmss-fff}-{(outcome == FeedCacheOutcome.Success ? "success" : "failed")}-{hash[..12]}.xml";
        var path = Path.Combine(directory, fileName);
        var temporary = Path.Combine(directory, fileName + TemporaryMarker + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
        var entry = new FeedCacheEntry(sourceId, fileName, outcome, hash, bytes.LongLength, now, (runId ?? "").Trim(), LocationLabel(location));
        var manifest = ReadManifest(directory, out _);
        manifest.Entries.RemoveAll(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        manifest.Entries.Add(entry);
        Trim(directory, manifest);
        WriteManifest(directory, manifest);
        return entry;
    }

    /// <summary>The entries of a source, newest first, as the manifest knows them.</summary>
    public static IReadOnlyList<FeedCacheEntry> List(string dataDirectory, string sourceId)
    {
        var directory = SourceDirectory(dataDirectory, sourceId);
        if (!Directory.Exists(directory)) return Array.Empty<FeedCacheEntry>();
        return ReadManifest(directory, out _).Entries.OrderByDescending(e => e.StoredUtc).ToList();
    }

    /// <summary>The newest successful entry whose file is present and verifies; a corrupt candidate is dropped and the next one considered.</summary>
    public static FeedCacheEntry? LastKnownGood(string dataDirectory, string sourceId)
    {
        var directory = SourceDirectory(dataDirectory, sourceId);
        if (!Directory.Exists(directory)) return null;
        var manifest = ReadManifest(directory, out _); var changed = false;
        foreach (var candidate in manifest.Entries.Where(e => e.Outcome == FeedCacheOutcome.Success).OrderByDescending(e => e.StoredUtc).ToList())
        {
            if (Verifies(directory, candidate)) { if (changed) WriteManifest(directory, manifest); return candidate; }
            manifest.Entries.Remove(candidate); TryDelete(Path.Combine(directory, candidate.FileName)); changed = true;
        }
        if (changed) WriteManifest(directory, manifest);
        return null;
    }

    /// <summary>The last known good feed's text, or null when there is none that verifies.</summary>
    public static string? ReadLastKnownGood(string dataDirectory, string sourceId)
    {
        var entry = LastKnownGood(dataDirectory, sourceId);
        return entry is null ? null : File.ReadAllText(Path.Combine(SourceDirectory(dataDirectory, sourceId), entry.FileName), Encoding.UTF8);
    }

    /// <summary>
    /// Removes orphans (directories of sources that no longer exist, files the manifest does not know, entries whose
    /// file is gone), corrupt entries, and the oldest entries beyond the quota — failed ones first, then successes —
    /// never a live source's last known good, never anything of a source with a run in progress, never a temporary
    /// file younger than the grace.
    /// </summary>
    public static FeedCacheReport Sweep(string dataDirectory, IEnumerable<string> liveSourceIds, IEnumerable<string>? activeSourceIds = null, long quotaBytes = DefaultQuotaBytes, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(liveSourceIds);
        var root = Root(dataDirectory);
        if (!Directory.Exists(root)) return new(0, 0, 0, 0, 0, quotaBytes);
        var live = new HashSet<string>(liveSourceIds, StringComparer.Ordinal);
        var active = new HashSet<string>(activeSourceIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        var now = nowUtc ?? DateTime.UtcNow;
        int evicted = 0, corrupt = 0, orphans = 0;
        var kept = new List<(string Directory, FeedCacheEntry Entry)>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var sourceId = Path.GetFileName(directory);
            if (active.Contains(sourceId)) { foreach (var e in ReadManifest(directory, out _).Entries) kept.Add((directory, e)); continue; }
            if (!live.Contains(sourceId) || !SafeId.IsMatch(sourceId)) { orphans++; TryDeleteDirectory(directory); continue; }
            var manifest = ReadManifest(directory, out var manifestCorrupt); if (manifestCorrupt) corrupt++;
            var known = new HashSet<string>(manifest.Entries.Select(e => e.FileName), StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, ManifestName, StringComparison.OrdinalIgnoreCase) || known.Contains(name)) continue;
                if (name.Contains(TemporaryMarker, StringComparison.Ordinal) && now - File.GetLastWriteTimeUtc(file) < ActiveGrace) continue;
                orphans++; TryDelete(file);
            }
            foreach (var entry in manifest.Entries.ToList())
            {
                if (!File.Exists(Path.Combine(directory, entry.FileName))) { manifest.Entries.Remove(entry); orphans++; continue; }
                if (!Verifies(directory, entry)) { manifest.Entries.Remove(entry); TryDelete(Path.Combine(directory, entry.FileName)); corrupt++; }
            }
            evicted += Trim(directory, manifest);
            WriteManifest(directory, manifest);
            foreach (var e in manifest.Entries) kept.Add((directory, e));
        }
        // Quota: the oldest go first, failed before successful, a live source's last known good and an active source's entries never.
        var total = kept.Sum(k => k.Entry.Bytes);
        if (total > quotaBytes)
        {
            var protectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in kept.GroupBy(k => k.Entry.SourceId))
            {
                var lkg = group.Where(k => k.Entry.Outcome == FeedCacheOutcome.Success).OrderByDescending(k => k.Entry.StoredUtc).FirstOrDefault();
                if (lkg.Entry is not null) protectedFiles.Add(Path.Combine(lkg.Directory, lkg.Entry.FileName));
            }
            foreach (var victim in kept.Where(k => !active.Contains(k.Entry.SourceId)).OrderBy(k => k.Entry.Outcome == FeedCacheOutcome.Success ? 1 : 0).ThenBy(k => k.Entry.StoredUtc).ToList())
            {
                if (total <= quotaBytes) break;
                var path = Path.Combine(victim.Directory, victim.Entry.FileName);
                if (protectedFiles.Contains(path)) continue;
                var manifest = ReadManifest(victim.Directory, out _);
                manifest.Entries.RemoveAll(e => string.Equals(e.FileName, victim.Entry.FileName, StringComparison.OrdinalIgnoreCase));
                WriteManifest(victim.Directory, manifest); TryDelete(path);
                total -= victim.Entry.Bytes; evicted++; kept.Remove(victim);
            }
        }
        return new(kept.Count, evicted, corrupt, orphans, total, quotaBytes);
    }

    /// <summary>The diagnostics line: entries, bytes against the quota, and a warning when the quota is exceeded even after a sweep would run.</summary>
    public static DiagnosticCheck Check(string dataDirectory, long quotaBytes = DefaultQuotaBytes)
    {
        try
        {
            var root = Root(dataDirectory);
            if (!Directory.Exists(root)) return new(DiagnosticName, "OK", "Önbellek boş");
            long bytes = 0; var entries = 0; var sources = 0;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                sources++;
                foreach (var e in ReadManifest(directory, out _).Entries) { entries++; bytes += e.Bytes; }
            }
            var detail = $"{sources.ToString("N0", CultureInfo.CurrentCulture)} kaynak · {entries.ToString("N0", CultureInfo.CurrentCulture)} kayıt · {Megabytes(bytes)} / {Megabytes(quotaBytes)} MB";
            return new(DiagnosticName, bytes > quotaBytes ? "WARN" : "OK", detail);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(DiagnosticName, "ERROR", AuditStore.Sanitize(error.Message)); }
    }

    /// <summary>An address as the manifest may keep it: no query string (a token could live there), the user segment masked, capped.</summary>
    public static string LocationLabel(string? location)
    {
        var text = (location ?? "").Trim();
        var cut = text.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) text = text[..cut];
        text = AuditStore.Redact(text);
        return text.Length <= 200 ? text : text[..200];
    }

    static int Trim(string directory, Manifest manifest)
    {
        var evicted = 0;
        foreach (var outcome in new[] { FeedCacheOutcome.Success, FeedCacheOutcome.Failed })
        {
            var keep = outcome == FeedCacheOutcome.Success ? KeepSuccessPerSource : KeepFailedPerSource;
            foreach (var old in manifest.Entries.Where(e => e.Outcome == outcome).OrderByDescending(e => e.StoredUtc).Skip(keep).ToList())
            {
                manifest.Entries.Remove(old); TryDelete(Path.Combine(directory, old.FileName)); evicted++;
            }
        }
        return evicted;
    }

    static bool Verifies(string directory, FeedCacheEntry entry)
    {
        var path = Path.Combine(directory, entry.FileName);
        if (!File.Exists(path)) return false;
        try { return string.Equals(Hash(File.ReadAllBytes(path)), entry.FeedHash, StringComparison.Ordinal); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static Manifest ReadManifest(string directory, out bool corrupt)
    {
        corrupt = false;
        var path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path)) return new Manifest();
        try { return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path, Encoding.UTF8), Json) ?? new Manifest(); }
        catch (JsonException) { corrupt = true; return new Manifest(); }
        catch (IOException) { corrupt = true; return new Manifest(); }
    }

    static void WriteManifest(string directory, Manifest manifest)
    {
        var path = Path.Combine(directory, ManifestName); var temporary = path + TemporaryMarker + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Json), Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..24];
    static string Megabytes(long bytes) => (bytes / 1048576d).ToString("0.#", CultureInfo.CurrentCulture);
    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
