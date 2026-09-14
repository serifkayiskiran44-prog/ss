using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

public static class MediaFileAccessPolicy
{
    public static IReadOnlyList<string> DefaultApprovedRoots { get; } = BuildDefaults();

    static List<string> BuildDefaults()
    {
        var roots = new List<string>();
        void Add(Environment.SpecialFolder folder)
        {
            try
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(path)) roots.Add(Canonicalize(path));
            }
            catch { }
        }
        Add(Environment.SpecialFolder.MyPictures);
        Add(Environment.SpecialFolder.MyDocuments);
        Add(Environment.SpecialFolder.DesktopDirectory);
        try
        {
            var importRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "MediaImport");
            roots.Add(Canonicalize(importRoot));
        }
        catch { }
        return roots;
    }

    public static string Canonicalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool IsUncPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    public static bool TryResolveApprovedFile(string path, IReadOnlyCollection<string> approvedRoots, out string canonicalPath, out string error)
    {
        canonicalPath = "";
        error = "";
        if (string.IsNullOrWhiteSpace(path)) { error = "Geçersiz dosya yolu."; return false; }
        if (IsUncPath(path)) { error = "Ağ paylaşımı (UNC) yolları desteklenmiyor."; return false; }

        string full;
        try { full = Canonicalize(path); }
        catch { error = "Geçersiz dosya yolu."; return false; }
        if (IsUncPath(full)) { error = "Ağ paylaşımı (UNC) yolları desteklenmiyor."; return false; }

        if (approvedRoots.Count == 0 || !approvedRoots.Any(root => IsWithinRoot(full, root)))
        {
            error = "Dosya izin verilen medya klasörlerinin dışında.";
            return false;
        }

        string resolved;
        try { resolved = ResolveFinalTarget(full); }
        catch { error = "Dosya yolu çözümlenemedi."; return false; }

        if (IsUncPath(resolved)) { error = "Ağ paylaşımı (UNC) yolları desteklenmiyor."; return false; }
        if (!approvedRoots.Any(root => IsWithinRoot(resolved, root)))
        {
            error = "Dosya izin verilen medya klasörlerinin dışına yönlendiriyor.";
            return false;
        }

        canonicalPath = resolved;
        return true;
    }

    static bool IsWithinRoot(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedRoot.Length == 0) return false;
        return candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// Resolves symlink/junction targets for the file itself and every ancestor directory,
    /// so a reparse point cannot be used to escape an approved root (string-prefix checks alone cannot catch this).
    static string ResolveFinalTarget(string fullPath)
    {
        if (File.Exists(fullPath))
        {
            var fileInfo = new FileInfo(fullPath);
            var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (target != null) fullPath = Canonicalize(target.FullName);
        }

        var directory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        var resolvedDirectory = ResolveDirectoryChain(directory);
        return resolvedDirectory.Length == 0 ? fullPath : Canonicalize(Path.Combine(resolvedDirectory, fileName));
    }

    static string ResolveDirectoryChain(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return "";
        var segments = new Stack<string>();
        var current = directory;
        while (!string.IsNullOrEmpty(current))
        {
            var isRoot = Path.GetPathRoot(current)?.Equals(current, StringComparison.OrdinalIgnoreCase) == true;
            var info = new DirectoryInfo(current);
            if (!isRoot && info.Exists)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    var resolvedBase = Canonicalize(target.FullName);
                    while (segments.Count > 0) resolvedBase = Path.Combine(resolvedBase, segments.Pop());
                    return Canonicalize(resolvedBase);
                }
            }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            segments.Push(Path.GetFileName(current));
            current = parent;
        }
        return Canonicalize(directory);
    }
}
