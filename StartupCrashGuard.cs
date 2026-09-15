using System.IO;

namespace TrMarketplaceHubDesktop;

/// Tracks consecutive failed-to-render app launches so a repeating startup crash
/// surfaces as an explicit recovery state instead of retrying the same failing
/// path forever. A launch counts as failed until MarkReachedUi() is called after
/// the main window has actually rendered.
public sealed class StartupCrashGuard
{
    public const int RecoveryThreshold = 3;
    /// Purely technical bound - the marker only ever holds a small
    /// non-negative integer as decimal text, so anything bigger is corruption,
    /// not a legitimate value. Never read unbounded file content. See #2647.
    const int MaxMarkerBytes = 32;

    readonly string marker;

    public int ConsecutiveFailures { get; }
    public bool RecoveryModeRequired => ConsecutiveFailures >= RecoveryThreshold;
    /// True when the persisted marker existed but was unreadable/oversized/
    /// non-numeric/negative/a symlink - a corrupt marker is never silently
    /// treated as "0 failures" (which would disable crash-loop protection);
    /// ConsecutiveFailures is instead forced to RecoveryThreshold so
    /// RecoveryModeRequired fails closed.
    public bool MarkerCorrupt { get; private set; }
    /// True when persisting the updated count failed (IO/access/disk-full) -
    /// the app must not hard-crash on this, but the failure must stay visible
    /// rather than being silently swallowed.
    public bool MarkerWriteFailed { get; private set; }

    public StartupCrashGuard(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        marker = Path.Combine(directory, "startup-crash-count.txt");
        ConsecutiveFailures = ReadCount();
        WriteCount(Math.Min(ConsecutiveFailures + 1, RecoveryThreshold));
    }

    public void MarkReachedUi() => WriteCount(0);

    int ReadCount()
    {
        try
        {
            if (!File.Exists(marker)) return 0;
            if (IsSymlinkOrReparsePoint(marker)) { MarkerCorrupt = true; return RecoveryThreshold; }
            var info = new FileInfo(marker);
            if (info.Length > MaxMarkerBytes) { MarkerCorrupt = true; return RecoveryThreshold; }
            var text = File.ReadAllText(marker).Trim();
            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) { MarkerCorrupt = true; return RecoveryThreshold; }
            return n;
        }
        catch (IOException) { MarkerCorrupt = true; return RecoveryThreshold; }
        catch (UnauthorizedAccessException) { MarkerCorrupt = true; return RecoveryThreshold; }
    }

    static bool IsSymlinkOrReparsePoint(string path)
    {
        try { return File.ResolveLinkTarget(path, false) is not null; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    void WriteCount(int value)
    {
        if (File.Exists(marker) && IsSymlinkOrReparsePoint(marker)) { MarkerWriteFailed = true; return; }
        var temporary = marker + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, value.ToString(System.Globalization.CultureInfo.InvariantCulture)); File.Move(temporary, marker, true); }
        catch (IOException) { MarkerWriteFailed = true; }
        catch (UnauthorizedAccessException) { MarkerWriteFailed = true; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}
