using System.Text.RegularExpressions;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum ImportSourceKind { XmlUrl, XmlFile, Excel, Unsupported }

/// <summary>What the import start screen knows about a source's health, from the health check the page already runs.</summary>
public sealed record SourceHealthSummary(string Status, DateTime? LastSuccessUtc, int? LatencyMs, string Error);

public sealed record ImportSourceType(ImportSourceKind Kind, string Label, string Hint);

public sealed record ImportSourceRow(string Id, string Title, ImportSourceKind Kind, string KindLabel, bool Supported, bool Enabled, bool Recent, string HealthLabel, string MaskedLocation, string Reason)
{
    public string Summary => $"{(Recent ? "★ " : "")}{Title} · {KindLabel}{(Enabled ? "" : " · pasif")}{(HealthLabel.Length > 0 ? " · " + HealthLabel : "")}{(Reason.Length > 0 ? " · " + Reason : "")}";
}

/// <summary>
/// The import start screen's view of sources (#823). Support is not a label but the reader's own rule, mirrored
/// exactly: a file that exists, or an absolute https address with no user information in it -- everything else
/// (http, ftp, "user:pass@host", a relative path, a file that is gone) is unsupported and says why. The type
/// picker offers only what this build can do: XML by address, XML by file, and Excel when the Excel screen is
/// registered. Rows carry the last-used mark, the enabled state and the health summary the page already
/// records; a location is shown masked -- user information and secret query parameters never reach the list.
/// </summary>
public static class ImportSourceCatalog
{
    public const string RecentPreferenceKey = "xml:last-source";

    static readonly Regex SecretQuery = new(@"(?i)([?&](?:access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|password|passwd|secret|token|key|sig|signature|auth)=)[^&#\s]+", RegexOptions.Compiled);

    /// <summary>The same decision <c>XmlSourceReader.ReadAsync</c> makes, without reading anything.</summary>
    public static (ImportSourceKind Kind, string Reason) Classify(string? location, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var value = (location ?? "").Trim();
        if (value.Length == 0) return (ImportSourceKind.Unsupported, "Adres veya dosya girilmedi.");
        if (fileExists(value)) return (ImportSourceKind.XmlFile, "");
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo)) return (ImportSourceKind.XmlUrl, "");
            if (!string.IsNullOrEmpty(uri.UserInfo)) return (ImportSourceKind.Unsupported, "Adres içinde kullanıcı bilgisi desteklenmiyor; Basic Auth alanlarını kullanın.");
            if (uri.Scheme == "file") return (ImportSourceKind.Unsupported, "Dosya bulunamadı.");
            if (uri.Scheme is "http" or "ftp") return (ImportSourceKind.Unsupported, $"Yalnız HTTPS desteklenir ({uri.Scheme} değil).");
        }
        if (value.Contains('\\') || value.Contains('/') && !value.Contains("://")) return (ImportSourceKind.Unsupported, "Dosya bulunamadı.");
        return (ImportSourceKind.Unsupported, "Geçerli bir HTTPS adresi veya dosya yolu değil.");
    }

    /// <summary>User information and secret query parameters are masked; host and path survive so the row is still recognisable.</summary>
    public static string MaskLocation(string? location)
    {
        var value = (location ?? "").Trim();
        if (value.Length == 0) return "";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "ftp")
        {
            var rebuilt = $"{uri.Scheme}://{(string.IsNullOrEmpty(uri.UserInfo) ? "" : "[user]@")}{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{uri.AbsolutePath}{uri.Query}";
            return SecretQuery.Replace(rebuilt, "$1[redacted]");
        }
        return AuditStore.Redact(value);
    }

    public static string KindLabel(ImportSourceKind kind) => kind switch
    {
        ImportSourceKind.XmlUrl => "XML adresi",
        ImportSourceKind.XmlFile => "XML dosyası",
        ImportSourceKind.Excel => "Excel",
        _ => "desteklenmiyor",
    };

    /// <summary>The type picker: only what this build can actually import.</summary>
    public static IReadOnlyList<ImportSourceType> SupportedTypes(Func<string, bool> routeExists)
    {
        ArgumentNullException.ThrowIfNull(routeExists);
        var types = new List<ImportSourceType>
        {
            new(ImportSourceKind.XmlUrl, "XML adresi (HTTPS)", "Tedarikçi beslemesinin HTTPS adresi; kullanıcı bilgisi adreste değil, Basic Auth alanlarında."),
            new(ImportSourceKind.XmlFile, "XML dosyası", "Bu bilgisayardaki bir XML dosyası."),
        };
        if (routeExists("excel")) types.Add(new(ImportSourceKind.Excel, "Excel (.xlsx)", "Excel ekranından profil ile içe aktarım."));
        return types;
    }

    public static string HealthLabel(SourceHealthSummary? health, DateTime nowUtc)
    {
        if (health is null) return "hiç denenmedi";
        var status = (health.Status ?? "").Trim();
        var when = health.LastSuccessUtc is { } at ? StatusTooltip.Relative(at, nowUtc) : "hiç başarılı olmadı";
        return status.Length == 0 ? when : $"{status.ToLowerInvariant()} · son başarı {when}";
    }

    public static ImportSourceRow Describe(XmlSource source, SourceHealthSummary? health, bool recent, DateTime nowUtc, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (kind, reason) = Classify(source.Location, fileExists);
        return new(source.Id, string.IsNullOrWhiteSpace(source.Name) ? "(adsız kaynak)" : source.Name.Trim(), kind, KindLabel(kind), kind != ImportSourceKind.Unsupported,
            source.Enabled, recent, HealthLabel(health, nowUtc), MaskLocation(source.Location), reason);
    }

    /// <summary>Rows in the order the operator needs: the last-used first, then usable, then disabled, then unsupported.</summary>
    public static IReadOnlyList<ImportSourceRow> Options(IEnumerable<XmlSource> sources, string? recentId, Func<string, SourceHealthSummary?> healthFor, DateTime nowUtc, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(healthFor);
        return sources.Select(s => Describe(s, healthFor(s.Id), recentId is not null && s.Id == recentId, nowUtc, fileExists))
            .OrderByDescending(r => r.Recent).ThenByDescending(r => r.Supported).ThenByDescending(r => r.Enabled).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
