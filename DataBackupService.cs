using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record DataBackupFile(string Path, long Length, string Sha256);
public sealed record DataBackupManifest(string Format, string ApplicationVersion, DateTime CreatedUtc, IReadOnlyList<DataBackupFile> Files);

/// <summary>Creates and restores a per-user data package without reading or decrypting credential bytes.</summary>
public sealed class DataBackupService
{
    public const string Format = "monobridge-data-v1";
    public string DataDirectory { get; }

    public DataBackupService(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
    }

    public string Backup(string outputPath)
    {
        outputPath = Path.GetFullPath(outputPath);
        if (IsInside(outputPath, DataDirectory)) throw new InvalidOperationException("Yedek dosyası uygulama veri klasörünün içine yazılamaz.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Directory.CreateDirectory(DataDirectory);
        var files = EnumerateDataFiles().Select(path => new DataBackupFile(path, new FileInfo(Path.Combine(DataDirectory, path)).Length, Hash(Path.Combine(DataDirectory, path)))).ToList();
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
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Yedek manifestosu bulunamadı.");
        DataBackupManifest? manifest;
        using (var reader = new StreamReader(manifestEntry.Open())) manifest = JsonSerializer.Deserialize<DataBackupManifest>(reader.ReadToEnd());
        if (manifest is null || manifest.Format != Format) throw new InvalidDataException("Yedek formatı bu sürümle uyumlu değil.");
        if (manifest.Files.Count > 100_000 || manifest.Files.Sum(x => x.Length) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Yedek boyutu güvenli sınırı aşıyor.");
        foreach (var file in manifest.Files)
        {
            ValidateRelativePath(file.Path);
            var entry = archive.GetEntry("data/" + file.Path.Replace(Path.DirectorySeparatorChar, '/')) ?? throw new InvalidDataException($"Yedek dosyası eksik: {file.Path}");
            if (entry.Length != file.Length) throw new InvalidDataException($"Yedek dosyası boyutu değişmiş: {file.Path}");
        }
        return manifest;
    }

    public void Restore(string backupPath)
    {
        backupPath = Path.GetFullPath(backupPath);
        var manifest = Validate(backupPath);
        var parent = Directory.GetParent(DataDirectory)?.FullName ?? throw new InvalidOperationException("Veri klasörü üst yolu bulunamadı.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".monobridge-restore-" + Guid.NewGuid().ToString("N"));
        var old = DataDirectory + ".pre-restore-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        try
        {
            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(backupPath))
            {
                foreach (var file in manifest.Files)
                {
                    ValidateRelativePath(file.Path);
                    var destination = Path.Combine(staging, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    archive.GetEntry("data/" + file.Path.Replace(Path.DirectorySeparatorChar, '/'))!.ExtractToFile(destination, false);
                    if (new FileInfo(destination).Length != file.Length || Hash(destination) != file.Sha256) throw new InvalidDataException($"Yedek doğrulaması başarısız: {file.Path}");
                }
            }
            // Preserve the current data as an encrypted-byte-safe archive before the directory swap.
            var safety = old + ".zip";
            Backup(safety);
            if (Directory.Exists(DataDirectory)) Directory.Move(DataDirectory, old);
            Directory.Move(staging, DataDirectory);
            if (Directory.Exists(old)) Directory.Delete(old, true);
        }
        catch
        {
            if (!Directory.Exists(DataDirectory) && Directory.Exists(old)) Directory.Move(old, DataDirectory);
            throw;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    IEnumerable<string> EnumerateDataFiles()
    {
        if (!Directory.Exists(DataDirectory)) yield break;
        foreach (var full in Directory.EnumerateFiles(DataDirectory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (full.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            yield return Path.GetRelativePath(DataDirectory, full);
        }
    }

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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
}
