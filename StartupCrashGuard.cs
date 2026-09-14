using System.IO;

namespace TrMarketplaceHubDesktop;

/// Tracks consecutive failed-to-render app launches so a repeating startup crash
/// surfaces as an explicit recovery state instead of retrying the same failing
/// path forever. A launch counts as failed until MarkReachedUi() is called after
/// the main window has actually rendered.
public sealed class StartupCrashGuard
{
    public const int RecoveryThreshold = 3;

    readonly string marker;

    public int ConsecutiveFailures { get; }
    public bool RecoveryModeRequired => ConsecutiveFailures >= RecoveryThreshold;

    public StartupCrashGuard(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        marker = Path.Combine(directory, "startup-crash-count.txt");
        ConsecutiveFailures = ReadCount();
        WriteCount(ConsecutiveFailures + 1);
    }

    public void MarkReachedUi() => WriteCount(0);

    int ReadCount()
    {
        try { return File.Exists(marker) && int.TryParse(File.ReadAllText(marker).Trim(), out var n) && n >= 0 ? n : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    void WriteCount(int value)
    {
        var temporary = marker + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, value.ToString(System.Globalization.CultureInfo.InvariantCulture)); File.Move(temporary, marker, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}
