using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>What turning a source off would leave behind: the linked products, whether the scheduler reads it, whether an import is running, and how many linked products have a fallback source once it is off.</summary>
public sealed record SourceDisablePreview(bool Stale, int LinkedProducts, bool SchedulerActive, bool RunningJob, int WithFallback, int WithoutFallback, string Headline, IReadOnlyList<string> Lines)
{
    /// <summary>Turning a source off always asks; a stale edit is refused instead.</summary>
    public bool RequiresConfirmation => !Stale;
    public bool Unused => LinkedProducts == 0 && !SchedulerActive && !RunningJob;
    public string Body => string.Join(Environment.NewLine, new[] { Headline }.Concat(Lines));
}

/// <summary>
/// The safety preview before a source is disabled (#900). A source is turned off through its form ("Kaynak aktif")
/// and saved; before that save the operator sees what the switch leaves behind — the products linked to the source,
/// whether the scheduler reads it (it will stop), whether an import is running (it is not stopped, a new one will not
/// start), and, product by product, whether another source could stand in (#897's verdict, computed as if the source
/// were already off) — and must confirm explicitly. An edit loaded at an older configuration revision than the
/// persisted one is stale and refused. Names only; never a feed address.
/// </summary>
public static class SourceDisable
{
    public const string AuditAction = "source-disable";

    /// <summary>True when the save would turn an enabled, persisted source off.</summary>
    public static bool IsDisable(XmlSource? persisted, XmlSource edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        return persisted is { Enabled: true } && !edited.Enabled;
    }

    public static SourceDisablePreview Preview(XmlSource persisted, XmlSource edited, IReadOnlyList<CatalogProduct> linked, IReadOnlyList<XmlSource> sources, IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTime>> sightingsByProduct, SourceRunSnapshot? latestRun, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(persisted); ArgumentNullException.ThrowIfNull(edited); ArgumentNullException.ThrowIfNull(linked); ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(sightingsByProduct);
        var name = string.IsNullOrWhiteSpace(persisted.Name) ? "adsız kaynak" : AuditStore.Redact(persisted.Name).Trim();
        if (edited.ConfigRevision != persisted.ConfigRevision)
            return new(true, 0, false, false, 0, 0, $"Bu kaynak siz düzenlerken değişti (kayıtlı rev. {N(persisted.ConfigRevision)}, yüklenen rev. {N(edited.ConfigRevision)}); devre dışı bırakmadan önce kaynağı yeniden seçin.", []);
        var scheduler = persisted.Enabled && persisted.AutoImport;
        var running = latestRun is { Status: "Running" } run && !(run.LeaseUntilUtc is { } lease && lease < nowUtc);

        // The fallback verdict as it would read once this source is off: the edited (disabled) record stands in for the persisted one.
        var hypothetical = sources.Select(x => string.Equals(x.Id, persisted.Id, StringComparison.Ordinal) ? edited : x).ToList();
        var none = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        int with = 0, without = 0;
        foreach (var product in linked)
        {
            var seen = sightingsByProduct.TryGetValue(product.Id, out var s) ? s : none;
            var verdict = LinkedSourceGraph.Evaluate(product, hypothetical, seen, nowUtc);
            if (verdict.Status is LinkedSourceGraph.FallbackAvailable or LinkedSourceGraph.EqualCandidates) with++; else without++;
        }

        var lines = new List<string>
        {
            $"Bağlı ürün: {N(linked.Count)}",
            scheduler ? "Zamanlayıcı: bu kaynağı otomatik okuyor · devre dışı kalınca duracak" : "Zamanlayıcı: bu kaynak otomatik okunmuyor",
            running ? "Çalışan aktarım: var · devre dışı bırakmak onu durdurmaz, bitince yenisi başlamaz" : "Çalışan aktarım: yok",
        };
        if (linked.Count > 0) lines.Add($"Yedek kaynak: {N(with)} ürün için uygun · {N(without)} ürün için yok" + (without > 0 ? $" — {N(without)} ürün tazelenmeden kalır (fiyat ve stok bu kaynaktan güncellenmez)" : ""));
        var headline = linked.Count == 0 && !scheduler && !running
            ? $"{name} kullanılmıyor: bağlı ürün, zamanlanmış okuma veya çalışan aktarım yok; devre dışı bırakmak hiçbir ürünü etkilemez."
            : $"{name} devre dışı bırakılacak: {N(linked.Count)} bağlı ürün" + (linked.Count > 0 ? $", yedek {N(with)} uygun / {N(without)} yok" : "") + (scheduler ? " · zamanlayıcı duracak" : "") + (running ? " · çalışan aktarım var" : "") + ".";
        return new(false, linked.Count, scheduler, running, with, without, AuditStore.Redact(headline), lines.Select(AuditStore.Redact).ToArray());
    }

    /// <summary>The audit row for a confirmed switch-off: module import, the preview's headline, redacted.</summary>
    public static AuditEvent ToAudit(XmlSource source, SourceDisablePreview preview)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(preview);
        return new AuditEvent { Module = "import", Action = AuditAction, Outcome = preview.WithoutFallback > 0 ? "Warning" : "Info", Detail = AuditStore.Redact(preview.Headline) };
    }

    static string N(int value) => value.ToString(CultureInfo.CurrentCulture);
}
