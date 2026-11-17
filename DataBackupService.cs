using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

public sealed record DataBackupFile(string Path, long Length, string Sha256);
public sealed record DataBackupManifest(string Format, string ApplicationVersion, DateTime CreatedUtc, IReadOnlyList<DataBackupFile> Files);

public enum DataRestoreCheckpoint
{
    JournalPrepared,
    StagingVerified,
    ActiveDirectoryMoved,
    RestoredDirectoryVerified,
    StagingExtracted
}

/// <summary>Used only by crash-injection tests to model a process ending without catch/finally cleanup.</summary>
public sealed class DataRestoreAbruptInterruptionException : Exception;

/// <summary>Creates and restores a per-user data package without reading or decrypting credential bytes.</summary>
public sealed class DataBackupService
{
    public const string Format = "monobridge-data-v1";
    const int RestoreJournalVersion = 1;
    const int MaxJournalBytes = 1024 * 1024;
    readonly Action<DataRestoreCheckpoint>? afterRestoreCheckpoint;

    sealed record RestoreJournal(
        int Version,
        string DataDirectory,
        string BackupPath,
        string BackupSha256,
        string StagingDirectory,
        string PreRestoreDirectory,
        string PreRestoreFingerprint,
        DataRestoreCheckpoint Checkpoint);

    public string DataDirectory { get; }

    public DataBackupService(string? dataDirectory = null, Action<DataRestoreCheckpoint>? afterRestoreCheckpoint = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"));
        this.afterRestoreCheckpoint = afterRestoreCheckpoint;
    }

    public string Backup(string outputPath)
    {
        outputPath = Path.GetFullPath(outputPath);
        if (IsInside(outputPath, DataDirectory)) throw new InvalidOperationException("Yedek dosyası uygulama veri klasörünün içine yazılamaz.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(DataDirectory);
        SqliteConnection.ClearAllPools();
        var files = EnumerateDataFiles(DataDirectory).Select(path => new DataBackupFile(path, new FileInfo(Path.Combine(DataDirectory, path)).Length, Hash(Path.Combine(DataDirectory, path)))).ToList();
        var manifest = new DataBackupManifest(Format, AppVersion.Current, DateTime.UtcNow, files);
        var temporary = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    var entry = archive.CreateEntry("data/" + file.Path.Replace(Path.DirectorySeparatorChar, '/'), CompressionLevel.Optimal);
                    using var source = File.OpenRead(Path.Combine(DataDirectory, file.Path));
                    using var target = entry.Open();
                    source.CopyTo(target);
                }
                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using var writer = new StreamWriter(manifestEntry.Open());
                writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            File.Move(temporary, outputPath, true);
            return outputPath;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public DataBackupManifest Validate(string backupPath)
    {
        using var archive = ZipFile.OpenRead(backupPath);
        var manifestEntries = archive.Entries.Where(entry => string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal)).ToArray();
        if (manifestEntries.Length != 1) throw new InvalidDataException("Yedek manifestosu bulunamadı veya yinelenmiş.");
        var manifestEntry = manifestEntries[0];
        DataBackupManifest? manifest;
        using (var reader = new StreamReader(manifestEntry.Open())) manifest = JsonSerializer.Deserialize<DataBackupManifest>(reader.ReadToEnd());
        if (manifest is null || manifest.Format != Format || manifest.Files is null) throw new InvalidDataException("Yedek formatı bu sürümle uyumlu değil.");
        if (manifest.Files.Count > 100_000 || manifest.Files.Sum(x => x.Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Yedek boyutu güvenli sınırı aşıyor.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            ValidateRelativePath(file.Path);
            if (!seen.Add(file.Path)) throw new InvalidDataException($"Yedekte yinelenen dosya var: {file.Path}");
            if (file.Length < 0 || file.Sha256.Length != 64 || file.Sha256.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException($"Yedek dosya özeti geçersiz: {file.Path}");
            var entry = archive.GetEntry("data/" + file.Path.Replace(Path.DirectorySeparatorChar, '/')) ?? throw new InvalidDataException($"Yedek dosyası eksik: {file.Path}");
            if (entry.Length != file.Length) throw new InvalidDataException($"Yedek dosyası boyutu değişmiş: {file.Path}");
            using var stream = entry.Open();
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Yedek dosya özeti eşleşmiyor: {file.Path}");
        }
        var archiveDataEntries = archive.Entries.Where(entry => entry.FullName.StartsWith("data/", StringComparison.Ordinal) && !entry.FullName.EndsWith('/')).ToArray();
        var archiveData = archiveDataEntries.Select(entry => entry.FullName[5..].Replace('/', Path.DirectorySeparatorChar)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (archiveDataEntries.Length != seen.Count || !archiveData.SetEquals(seen))
            throw new InvalidDataException("Yedek manifestosunda listelenmeyen veya yinelenen veri dosyası var.");
        return manifest;
    }

    public void Restore(string backupPath)
    {
        _ = RecoverInterruptedRestore();
        backupPath = Path.GetFullPath(backupPath);
        var manifest = Validate(backupPath);
        var parent = ParentDirectory();
        Directory.CreateDirectory(parent);
        var leaf = Path.GetFileName(DataDirectory);
        var staging = Path.Combine(parent, "." + leaf + ".restore-staging-" + Guid.NewGuid().ToString("N"));
        var old = DataDirectory + ".pre-restore-" + Guid.NewGuid().ToString("N");
        var journal = new RestoreJournal(RestoreJournalVersion, DataDirectory, backupPath, Hash(backupPath), staging, old,
            Directory.Exists(DataDirectory) ? DirectoryFingerprint(DataDirectory) : throw new DirectoryNotFoundException("Geri yüklenecek etkin veri klasörü bulunamadı."),
            DataRestoreCheckpoint.JournalPrepared);
        var abrupt = false;
        try
        {
            WriteJournal(journal);
            InvokeCheckpoint(journal.Checkpoint);

            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(backupPath))
            {
                foreach (var file in manifest.Files)
                {
                    ValidateRelativePath(file.Path);
                    var destination = Path.Combine(staging, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    archive.GetEntry("data/" + file.Path.Replace(Path.DirectorySeparatorChar, '/'))!.ExtractToFile(destination, false);
                }
            }
            journal = journal with { Checkpoint = DataRestoreCheckpoint.StagingExtracted };
            WriteJournal(journal);
            InvokeCheckpoint(journal.Checkpoint);
            ValidateDirectoryAgainstManifest(staging, manifest);
            journal = journal with { Checkpoint = DataRestoreCheckpoint.StagingVerified };
            WriteJournal(journal);
            InvokeCheckpoint(journal.Checkpoint);

            Backup(old + ".zip");
            Directory.Move(DataDirectory, old);
            journal = journal with { Checkpoint = DataRestoreCheckpoint.ActiveDirectoryMoved };
            WriteJournal(journal);
            InvokeCheckpoint(journal.Checkpoint);

            Directory.Move(staging, DataDirectory);
            ValidateDirectoryAgainstManifest(DataDirectory, manifest);
            journal = journal with { Checkpoint = DataRestoreCheckpoint.RestoredDirectoryVerified };
            WriteJournal(journal);
            InvokeCheckpoint(journal.Checkpoint);
            File.Delete(JournalPath());
        }
        catch (DataRestoreAbruptInterruptionException)
        {
            abrupt = true;
            throw;
        }
        catch
        {
            RollBackFailedRestore(journal);
            throw;
        }
        finally
        {
            if (!abrupt) DeleteDirectoryTree(staging);
        }
    }

    /// <summary>Repairs a journaled directory swap before any caller inspects or creates the active data directory.</summary>
    public bool RecoverInterruptedRestore()
    {
        var journalPath = JournalPath();
        if (!File.Exists(journalPath))
        {
            RejectUnjournaledMissingDirectory();
            return false;
        }

        var journal = ReadJournal(journalPath);
        ValidateJournalPaths(journal);
        if (!File.Exists(journal.BackupPath) || !string.Equals(Hash(journal.BackupPath), journal.BackupSha256, StringComparison.OrdinalIgnoreCase))
            throw RecoveryError("Geri yükleme yedeği eksik veya özeti değişmiş.");
        var manifest = Validate(journal.BackupPath);
        var activeExists = Directory.Exists(DataDirectory);
        var oldExists = Directory.Exists(journal.PreRestoreDirectory);
        var stagingExists = Directory.Exists(journal.StagingDirectory);

        if (!activeExists)
        {
            if (!oldExists || !string.Equals(DirectoryFingerprint(journal.PreRestoreDirectory), journal.PreRestoreFingerprint, StringComparison.Ordinal))
                throw RecoveryError("Önceki etkin veri klasörü doğrulanamadı.");
            if (stagingExists && DirectoryMatchesManifest(journal.StagingDirectory, manifest))
            {
                Directory.Move(journal.StagingDirectory, DataDirectory);
                ValidateDirectoryAgainstManifest(DataDirectory, manifest);
                File.Delete(journalPath);
                return true;
            }

            Directory.Move(journal.PreRestoreDirectory, DataDirectory);
            if (!string.Equals(DirectoryFingerprint(DataDirectory), journal.PreRestoreFingerprint, StringComparison.Ordinal))
                throw RecoveryError("Etkin veri klasörü byte-byte geri getirilemedi.");
            if (stagingExists) DeleteDirectoryTree(journal.StagingDirectory);
            File.Delete(journalPath);
            return true;
        }

        if (DirectoryMatchesManifest(DataDirectory, manifest))
        {
            if (stagingExists) DeleteDirectoryTree(journal.StagingDirectory);
            File.Delete(journalPath);
            return true;
        }

        if (!oldExists &&
            string.Equals(DirectoryFingerprint(DataDirectory), journal.PreRestoreFingerprint, StringComparison.Ordinal))
        {
            if (stagingExists) DeleteDirectoryTree(journal.StagingDirectory);
            File.Delete(journalPath);
            return true;
        }

        throw RecoveryError("Etkin, staging ve pre-restore klasörlerinin durumu journal ile eşleşmiyor.");
    }

    void RollBackFailedRestore(RestoreJournal journal)
    {
        try
        {
            if (Directory.Exists(journal.PreRestoreDirectory) &&
                string.Equals(DirectoryFingerprint(journal.PreRestoreDirectory), journal.PreRestoreFingerprint, StringComparison.Ordinal))
            {
                if (Directory.Exists(DataDirectory)) DeleteDirectoryTree(DataDirectory);
                Directory.Move(journal.PreRestoreDirectory, DataDirectory);
            }
            if (!Directory.Exists(DataDirectory) ||
                !string.Equals(DirectoryFingerprint(DataDirectory), journal.PreRestoreFingerprint, StringComparison.Ordinal))
                throw RecoveryError("Başarısız geri yükleme güvenle geri alınamadı.");
            if (Directory.Exists(journal.StagingDirectory)) DeleteDirectoryTree(journal.StagingDirectory);
            if (File.Exists(JournalPath())) File.Delete(JournalPath());
        }
        catch (Exception error) when (error is not OutOfMemoryException && error is not InvalidOperationException)
        {
            throw RecoveryError("Başarısız geri yükleme güvenle geri alınamadı.", error);
        }
    }

    void InvokeCheckpoint(DataRestoreCheckpoint checkpoint) => afterRestoreCheckpoint?.Invoke(checkpoint);

    RestoreJournal ReadJournal(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaxJournalBytes) throw RecoveryError("Restore journal geçersiz boyutta.");
            return JsonSerializer.Deserialize<RestoreJournal>(stream) is { } journal &&
                   journal.Version == RestoreJournalVersion && Enum.IsDefined(journal.Checkpoint)
                ? journal
                : throw RecoveryError("Restore journal geçersiz.");
        }
        catch (JsonException error) { throw RecoveryError("Restore journal okunamadı.", error); }
    }

    void ValidateJournalPaths(RestoreJournal journal)
    {
        try
        {
            var parent = ParentDirectory();
            var leaf = Path.GetFileName(DataDirectory);
            if (string.IsNullOrWhiteSpace(journal.DataDirectory) || string.IsNullOrWhiteSpace(journal.StagingDirectory) ||
                string.IsNullOrWhiteSpace(journal.PreRestoreDirectory) || string.IsNullOrWhiteSpace(journal.BackupPath) ||
                string.IsNullOrWhiteSpace(journal.BackupSha256) || string.IsNullOrWhiteSpace(journal.PreRestoreFingerprint) ||
                !Path.IsPathFullyQualified(journal.DataDirectory) || !Path.IsPathFullyQualified(journal.StagingDirectory) ||
                !Path.IsPathFullyQualified(journal.PreRestoreDirectory) || !Path.IsPathFullyQualified(journal.BackupPath) ||
                !PathEquals(journal.DataDirectory, DataDirectory) ||
                !IsDirectChildWithPrefix(journal.StagingDirectory, parent, "." + leaf + ".restore-staging-") ||
                !IsDirectChildWithPrefix(journal.PreRestoreDirectory, parent, leaf + ".pre-restore-") ||
                IsInside(Path.GetFullPath(journal.BackupPath), DataDirectory) ||
                journal.BackupSha256.Length != 64 || journal.BackupSha256.Any(character => !Uri.IsHexDigit(character)) ||
                journal.PreRestoreFingerprint.Length != 64 || journal.PreRestoreFingerprint.Any(character => !Uri.IsHexDigit(character)) ||
                IsReparseDirectory(journal.StagingDirectory) || IsReparseDirectory(journal.PreRestoreDirectory) || IsReparseDirectory(DataDirectory))
                throw RecoveryError("Restore journal yolu veya özeti güvenli değil.");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw RecoveryError("Restore journal yolu güvenli değil.", error);
        }
    }

    void RejectUnjournaledMissingDirectory()
    {
        if (Directory.Exists(DataDirectory)) return;
        var parent = ParentDirectory();
        if (!Directory.Exists(parent)) return;
        var leaf = Path.GetFileName(DataDirectory);
        var artifacts = Directory.EnumerateDirectories(parent, leaf + ".pre-restore-*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(parent, "." + leaf + ".restore-staging-*", SearchOption.TopDirectoryOnly));
        if (artifacts.Any()) throw RecoveryError("Etkin veri klasörü eksik ve journalsız geri yükleme artifaktı bulundu.");
    }

    void WriteJournal(RestoreJournal journal)
    {
        var path = JournalPath();
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(journal));
        if (bytes.Length > MaxJournalBytes) throw new InvalidDataException("Restore journal güvenli boyutu aşıyor.");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    string JournalPath() => Path.Combine(ParentDirectory(), "." + Path.GetFileName(DataDirectory) + ".restore-journal-v1.json");
    string ParentDirectory() => Directory.GetParent(DataDirectory)?.FullName ?? throw new InvalidOperationException("Veri klasörü üst yolu bulunamadı.");

    static bool DirectoryMatchesManifest(string directory, DataBackupManifest manifest)
    {
        try { ValidateDirectoryAgainstManifest(directory, manifest); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    static void ValidateDirectoryAgainstManifest(string directory, DataBackupManifest manifest)
    {
        if (!Directory.Exists(directory)) throw new InvalidDataException("Doğrulanacak geri yükleme klasörü eksik.");
        if (IsReparseDirectory(directory)) throw new InvalidDataException("Geri yükleme klasörü yeniden yönlendirilmiş bir yol olamaz.");
        var actual = EnumerateDataFiles(directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expected)) throw new InvalidDataException("Geri yüklenen dosya kümesi yedek manifestosuyla eşleşmiyor.");
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(directory, file.Path);
            if (new FileInfo(path).Length != file.Length || !string.Equals(Hash(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Geri yüklenen dosya doğrulanamadı: {file.Path}");
        }
    }

    static IEnumerable<string> EnumerateDataFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (IsReparseDirectory(current))
                throw new InvalidDataException($"Veri ağacında reparse klasör girdisi bulundu: {Path.GetRelativePath(directory, current)}");
            foreach (var full in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Veri ağacında reparse dosya girdisi bulundu: {Path.GetRelativePath(directory, full)}");
                yield return Path.GetRelativePath(directory, full);
            }
            foreach (var child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Veri ağacında reparse klasör girdisi bulundu: {Path.GetRelativePath(directory, child)}");
                pending.Push(child);
            }
        }
    }

    static void DeleteDirectoryTree(string directory)
    {
        if (!Directory.Exists(directory)) return;
        if (IsReparseDirectory(directory))
        {
            Directory.Delete(directory, false);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)) File.Delete(file);
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsReparseDirectory(child)) Directory.Delete(child, false);
            else DeleteDirectoryTree(child);
        }
        Directory.Delete(directory, false);
    }

    static string DirectoryFingerprint(string directory)
    {
        if (IsReparseDirectory(directory)) throw new InvalidDataException("Yeniden yönlendirilmiş veri klasörü doğrulanamaz.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in EnumerateDataFiles(directory).OrderBy(path => path, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative.Replace(Path.DirectorySeparatorChar, '/') + "\0"));
            var full = Path.Combine(directory, relative);
            RejectReparseFile(full, relative);
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
            RejectReparseFile(full, relative);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    static string Hash(string path)
    {
        RejectReparseFile(path, Path.GetFileName(path));
        using var stream = File.OpenRead(path);
        var result = Convert.ToHexString(SHA256.HashData(stream));
        RejectReparseFile(path, Path.GetFileName(path));
        return result;
    }

    static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Replace('\\', '/').Split('/').Any(x => x == "..")) throw new InvalidDataException("Yedekte güvenli olmayan dosya yolu var.");
    }

    static bool IsInside(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    static bool IsDirectChildWithPrefix(string path, string parent, string prefix)
    {
        var full = Path.GetFullPath(path);
        return PathEquals(Path.GetDirectoryName(full) ?? "", parent) && Path.GetFileName(full).StartsWith(prefix, StringComparison.Ordinal);
    }

    static bool IsReparseDirectory(string path) => Directory.Exists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    static void RejectReparseFile(string path, string displayPath)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Veri ağacında reparse dosya girdisi bulundu: {displayPath}");
    }

    static InvalidOperationException RecoveryError(string detail, Exception? inner = null) =>
        new("Kesintiye uğrayan geri yükleme güvenle doğrulanamadı; veri yazma durduruldu. " + detail + " Doğrulanmış yedekten geri dönüş veya onarım gerekli.", inner);
}
