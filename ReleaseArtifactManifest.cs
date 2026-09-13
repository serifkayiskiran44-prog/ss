using System.Security.Cryptography;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record ReleaseArtifactFile(string Path, long Length, string Sha256);
public sealed record ReleaseArtifactManifest(string ApplicationVersion, string Runtime, DateTime CreatedUtc, IReadOnlyList<ReleaseArtifactFile> Files)
{
    public static ReleaseArtifactManifest Create(string publishDirectory)
    {
        var root = Path.GetFullPath(publishDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            using var stream = File.OpenRead(path);
            return new ReleaseArtifactFile(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
        }).ToList();
        return new(AppVersion.Current, "win-x64/self-contained", DateTime.UtcNow, files);
    }

    public void Write(string outputPath) => File.WriteAllText(outputPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
