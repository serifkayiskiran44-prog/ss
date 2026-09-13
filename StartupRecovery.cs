using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record StartupRecoveryState(bool UncleanExit, DateTimeOffset StartedUtc);

public sealed class StartupRecovery
{
    readonly string marker;
    public StartupRecoveryState State { get; }

    public StartupRecovery(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        marker = Path.Combine(directory, "startup.in-progress");
        State = new(File.Exists(marker), DateTimeOffset.UtcNow);
        var temporary = marker + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, State.StartedUtc.ToString("O")); File.Move(temporary, marker, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Complete()
    {
        try { if (File.Exists(marker)) File.Delete(marker); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
