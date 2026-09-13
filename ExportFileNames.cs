using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

/// <summary>The final export file already exists and the caller did not ask to replace it; the message names the file, never its directory.</summary>
public sealed class ExportFileExistsException : IOException
{
    public ExportFileExistsException(string path) : base(ExportFileNames.ExistsMessage(path)) => FileName = Path.GetFileName(path);
    public string FileName { get; }
}

/// <summary>
/// Collision-safe export file names (#880). A suggested name is a sanitized stem, a timestamp to the second and an
/// extension: the stem keeps letters and digits, turns every other character (separators, control characters, the
/// characters a file system refuses) into a dash, collapses and trims the dashes, refuses to be a reserved device
/// name, is capped at <see cref="MaxStemLength"/>, and is replaced by <see cref="DefaultStem"/> when it is empty
/// or when the central redaction would change it — personal data or a secret never reaches a file name. Two exports
/// in the same second, or a name that already exists, get "-2", "-3"… before the extension.
/// </summary>
public static class ExportFileNames
{
    public const int MaxStemLength = 60;
    public const string DefaultStem = "disa-aktarim";
    public const string TimestampFormat = "yyyyMMdd-HHmmss";
    static readonly HashSet<string> Reserved = new(new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }, StringComparer.OrdinalIgnoreCase);

    public static string SafeStem(string? name)
    {
        var text = (name ?? "").Trim();
        if (text.Length == 0) return DefaultStem;
        if (!string.Equals(AuditStore.Redact(text), text, StringComparison.Ordinal)) return DefaultStem;
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) builder.Append(char.IsLetterOrDigit(c) ? c : '-');
        var cleaned = Regex.Replace(builder.ToString(), "-{2,}", "-").Trim('-');
        if (cleaned.Length == 0) return DefaultStem;
        if (cleaned.Length > MaxStemLength) cleaned = cleaned[..MaxStemLength].TrimEnd('-');
        return Reserved.Contains(cleaned) ? "x-" + cleaned : cleaned;
    }

    /// <summary>Lower-case letters and digits, at most eight; "dat" when nothing usable is left.</summary>
    public static string SafeExtension(string? extension)
    {
        var ext = Regex.Replace((extension ?? "").Trim().TrimStart('.').ToLowerInvariant(), "[^a-z0-9]", "");
        return ext.Length == 0 ? "dat" : ext.Length > 8 ? ext[..8] : ext;
    }

    /// <summary>"stem-yyyyMMdd-HHmmss.ext" for the given moment (now by default, local time as the operator reads it).</summary>
    public static string Build(string? name, string extension, DateTime? at = null)
        => $"{SafeStem(name)}-{(at ?? DateTime.Now).ToString(TimestampFormat, CultureInfo.InvariantCulture)}.{SafeExtension(extension)}";

    /// <summary>The path itself when nothing is there, otherwise the first "-2", "-3"… variant that is free.</summary>
    public static string Unique(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path) ?? ""; var stem = Path.GetFileNameWithoutExtension(path); var extension = Path.GetExtension(path);
        for (var n = 2; n < 10_000; n++)
        {
            var candidate = Path.Combine(directory, $"{stem}-{n}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Benzersiz bir dosya adı bulunamadı.");
    }

    /// <summary>A free, sanitized, timestamped path inside a directory.</summary>
    public static string Suggest(string directory, string? name, string extension, DateTime? at = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Unique(Path.Combine(directory, Build(name, extension, at)));
    }

    public static string ExistsMessage(string path) => $"Dosya zaten var; üzerine yazılmadı: {AuditStore.Redact(Path.GetFileName(path))}";
}

/// <summary>
/// The one way an export lands on its final path (#880, #881). The producer fills a temporary file that lives next
/// to the target — the same directory, so the same permissions as the final file and never a shared temp folder —
/// under the target's stem, a marker, a random tag and the target's extension (ClosedXML insists on it); only a
/// complete, uncancelled temporary is moved onto the target, and an existing target is replaced only when the caller
/// asked for it (a confirmed save dialog, a retry of the same run) — never silently. A producer that throws (a full
/// disk), is cancelled before or after it wrote, or produced nothing leaves neither a partial final file nor a
/// temporary; temporaries of the same target left by a run that crashed before its rename are swept before the next
/// write once they are older than <see cref="StaleAfter"/>.
/// </summary>
public static class ExportFiles
{
    public const string TemporaryMarker = ".tmp-";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public static void Commit(string temporary, string path, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporary); ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!overwrite && File.Exists(path)) throw new ExportFileExistsException(path);
        File.Move(temporary, path, overwrite);
    }

    /// <summary>The temporary for a target: next to it, its stem, the marker, a random tag, its extension.</summary>
    public static string TemporaryFor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        return Path.Combine(Path.GetDirectoryName(full) ?? ".", Path.GetFileNameWithoutExtension(full) + TemporaryMarker + Guid.NewGuid().ToString("N")[..12] + Path.GetExtension(full));
    }

    /// <summary>Writes an export atomically: see the type summary. The producer receives the temporary path and must leave the complete file there.</summary>
    public static void Write(string path, bool overwrite, CancellationToken cancellationToken, Action<string> produce)
    {
        ArgumentNullException.ThrowIfNull(produce);
        var full = Prepare(path);
        var temporary = TemporaryFor(full);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            produce(temporary);
            Finish(temporary, full, overwrite, cancellationToken);
        }
        finally { TryDelete(temporary); }
    }

    /// <summary>The asynchronous form of <see cref="Write"/> for a producer that awaits.</summary>
    public static async Task WriteAsync(string path, bool overwrite, CancellationToken cancellationToken, Func<string, Task> produce)
    {
        ArgumentNullException.ThrowIfNull(produce);
        var full = Prepare(path);
        var temporary = TemporaryFor(full);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await produce(temporary).ConfigureAwait(false);
            Finish(temporary, full, overwrite, cancellationToken);
        }
        finally { TryDelete(temporary); }
    }

    /// <summary>Removes temporaries of this target older than the stale window — what a run that crashed before its rename left behind; a younger one may belong to a run in progress and stays.</summary>
    public static int SweepStale(string path, TimeSpan? olderThan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path); var directory = Path.GetDirectoryName(full);
        if (directory is null || !Directory.Exists(directory)) return 0;
        var prefix = Path.GetFileNameWithoutExtension(full) + TemporaryMarker; var cutoff = DateTime.UtcNow - (olderThan ?? StaleAfter); var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            try { if (File.GetLastWriteTimeUtc(file) < cutoff) { File.Delete(file); removed++; } }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    static string Prepare(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        SweepStale(full);
        return full;
    }

    static void Finish(string temporary, string full, bool overwrite, CancellationToken cancellationToken)
    {
        // A cancellation that arrived while the producer worked discards the complete temporary: the operator asked for no file.
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(temporary)) throw new IOException("Dışa aktarım geçici dosyayı üretmedi; hedef dosya yazılmadı.");
        Commit(temporary, full, overwrite);
    }

    static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
