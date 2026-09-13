using System.Text.RegularExpressions;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record LegacyTaskPackFinding(string File, string Status, string Reason);

/// <summary>Read-only audit helper for legacy task-pack claims; it never mutates issue or repository state.</summary>
public static class LegacyTaskPackAudit
{
    public static IReadOnlyList<LegacyTaskPackFinding> Scan(string docsDirectory)
    {
        if (!Directory.Exists(docsDirectory)) throw new DirectoryNotFoundException(docsDirectory);
        return Directory.EnumerateFiles(docsDirectory, "M*.md", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(path =>
        {
            var text = File.ReadAllText(path);
            var status = text.Contains("100/100", StringComparison.OrdinalIgnoreCase) ? "PARTIAL" :
                text.Contains("REAL_WORK_COUNT=", StringComparison.OrdinalIgnoreCase) ? "REAL_DELIVERABLE" : "UNVERIFIED";
            var reason = status switch
            {
                "PARTIAL" => "Eski checklist iddiası; exact teslimat/test kanıtı yeniden doğrulanmalı.",
                "REAL_DELIVERABLE" => "Yeni gerçek-teslimat formatı bulundu; kaynak commit ve publish ayrıca doğrulanmalı.",
                _ => "Task-pack formatı kanıt sınıfı içermiyor."
            };
            return new LegacyTaskPackFinding(Path.GetRelativePath(docsDirectory, path), status, Regex.Replace(reason, "\\s+", " "));
        }).ToList();
    }
}
