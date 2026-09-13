using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>What still points at a source (blocks a delete) and what is the source's own (goes with it).</summary>
public sealed record SourceReferences(int Products, int FieldOrigins, int Sightings, int Runs, int QualityIssues, bool RunningJob, int Revisions, int CachedFeeds, bool CredentialSaved)
{
    /// <summary>References held by other records: products, their field origins, sightings, run history, quality records.</summary>
    public int Total => Products + FieldOrigins + Sightings + Runs + QualityIssues;
    public bool Any => Total > 0 || RunningJob;
}

public sealed record SourceDeleteVerdict(bool Allowed, bool Referenced, bool RunningJob, SourceReferences References, string Headline, IReadOnlyList<string> Lines, string Alternative)
{
    public string Body => string.Join(Environment.NewLine, new[] { Headline }.Concat(Lines).Concat(Alternative.Length > 0 ? [Alternative] : Array.Empty<string>()));
}

/// <summary>
/// The guard on a hard delete of a source (#901). A source that anything still points at — products linked to it,
/// field origins that name it (#895), sightings (#897), run history, data-quality records — or that is being read
/// right now cannot be deleted; the verdict lists the references and offers the alternative that keeps every
/// record and its history: turn the source off (#900). A source nothing points at can go, and its own data goes
/// with it — configuration revisions, cached feeds, a saved credential. Names only; never a feed address.
/// </summary>
public static class SourceDeleteGuard
{
    public const string AuditAction = "source-delete";
    public const string DisableAlternative = "Bunun yerine kaynak ayarlarında \"Kaynak aktif\" işaretini kaldırıp kaydedin: ürünler, kökenler ve geçmiş yerinde kalır, zamanlayıcı durur.";

    public static SourceDeleteVerdict Evaluate(XmlSource source, SourceReferences references)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(references);
        var name = string.IsNullOrWhiteSpace(source.Name) ? "adsız kaynak" : AuditStore.Redact(source.Name).Trim();
        var lines = new List<string>();
        if (references.Products > 0) lines.Add($"Ürün havuzu: {N(references.Products)} ürün bu kaynağa bağlı");
        if (references.FieldOrigins > 0) lines.Add($"Alan kökenleri: {N(references.FieldOrigins)} alan değeri bu kaynaktan geldi");
        if (references.Sightings > 0) lines.Add($"Gözlemler: {N(references.Sightings)} ürün bu kaynakta görüldü");
        if (references.Runs > 0) lines.Add($"Çalıştırma geçmişi: {N(references.Runs)} kayıt");
        if (references.QualityIssues > 0) lines.Add($"Veri kalitesi kayıtları: {N(references.QualityIssues)}");
        if (references.RunningJob) lines.Add("Çalışan aktarım: var · bitmeden silinemez");
        var own = new List<string>();
        if (references.Revisions > 0) own.Add($"{N(references.Revisions)} yapılandırma sürümü");
        if (references.CachedFeeds > 0) own.Add($"{N(references.CachedFeeds)} önbellekli besleme");
        if (references.CredentialSaved) own.Add("kayıtlı kimlik bilgisi");
        if (references.RunningJob)
            return new(false, references.Total > 0, true, references, $"{name} şu anda okunuyor; silme engellendi.", lines, references.Total > 0 ? DisableAlternative : "");
        if (references.Total > 0)
            return new(false, true, false, references, $"{name} kullanımda ({N(references.Total)} referans); silme engellendi.", lines, DisableAlternative);
        var with = own.Count > 0 ? $" Kaynakla birlikte silinecek: {string.Join(", ", own)}." : "";
        return new(true, false, false, references, $"{name} hiçbir yerde kullanılmıyor; silinebilir.{with}", lines, "");
    }

    /// <summary>The audit row: module import, action source-delete, Info for a delete and Warning for a refusal, the headline redacted.</summary>
    public static AuditEvent ToAudit(XmlSource source, SourceDeleteVerdict verdict, bool deleted)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(verdict);
        return new AuditEvent { Module = "import", Action = AuditAction, Outcome = deleted ? "Info" : "Warning", Detail = AuditStore.Redact(verdict.Headline) };
    }

    static string N(int value) => value.ToString(CultureInfo.CurrentCulture);
}
