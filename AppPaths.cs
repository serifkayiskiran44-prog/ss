using System.IO;

namespace TrMarketplaceHubDesktop;

/// <summary>One named outcome of resolving a template file: found at a real path, the templates folder itself is missing, the named file is missing, it exceeds the size limit, or its content could not be parsed.</summary>
public sealed record TemplateResolution(string State, string? Path, string Words)
{
    public const string Found = "FOUND", DirectoryMissing = "DIRECTORY_MISSING", FileMissing = "FILE_MISSING", TooLarge = "TOO_LARGE";
}

/// <summary>
/// Start-path independence (#2563). Content and resource lookups must never depend on the process's current
/// working directory — a user can launch the EXE from a Desktop shortcut, the Start Menu, a `cmd` opened in any
/// folder, or an installer, each with a different (or empty) working directory. `TemplatesDirectory` resolves next
/// to the running assembly (<see cref="AppContext.BaseDirectory"/>), which publish always populates from the
/// project's own `templates/*.json` content items, regardless of where the process was launched from.
/// `DefaultDataRoot` resolves under the user's own `LocalApplicationData`, which is itself independent of both the
/// working directory and the install location — so mutable data never lands next to a read-only install directory.
/// A missing or oversized template is a named, typed result — never a silently created empty file in whatever
/// folder happened to be current.
/// </summary>
public static class AppPaths
{
    public const long MaxTemplateBytes = 1024 * 1024;

    public static string TemplatesDirectory => Path.Combine(AppContext.BaseDirectory, "templates");
    public static string DefaultDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");

    /// <summary>Resolves a template by file name under <see cref="TemplatesDirectory"/> (or an explicit directory, for tests) without ever touching the process's current working directory.</summary>
    public static TemplateResolution ResolveTemplate(string fileName, string? templatesDirectory = null)
    {
        var directory = templatesDirectory ?? TemplatesDirectory;
        if (!Directory.Exists(directory)) return new(TemplateResolution.DirectoryMissing, null, $"Şablon klasörü bulunamadı: {directory}. Kurulum eksik olabilir; EXE'yi publish klasöründeki haliyle çalıştırın.");
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return new(TemplateResolution.FileMissing, null, $"Şablon dosyası bulunamadı: {fileName}.");
        var length = new FileInfo(path).Length;
        if (length > MaxTemplateBytes) return new(TemplateResolution.TooLarge, null, $"Şablon dosyası {MaxTemplateBytes / 1024} KB sınırını aşıyor: {fileName}.");
        return new(TemplateResolution.Found, path, $"Şablon bulundu: {fileName}.");
    }
}
