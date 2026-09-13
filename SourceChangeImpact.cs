using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum SourceChangeLevel { None, Small, Large }

/// <summary>What a pending source-settings save would touch, before it touches anything: whether the edit is stale, the level, the changed fields (addresses masked), the change groups, the affected product and job counts, and the words.</summary>
public sealed record SourceChangeImpactPreview(bool Stale, SourceChangeLevel Level, IReadOnlyList<FieldDiffRow> Changes, IReadOnlyList<string> Groups, int AffectedProducts, int AffectedJobs, bool RunningJob, string Headline, IReadOnlyList<string> Lines)
{
    /// <summary>A save that touches something asks first; a stale edit (refused) or a change with no impact does not.</summary>
    public bool RequiresConfirmation => !Stale && Level != SourceChangeLevel.None;
    public string Body => string.Join(Environment.NewLine, new[] { Headline }.Concat(Lines));
}

/// <summary>
/// The impact preview of a source-settings change (#899). Before the XML source form saves, the persisted record and
/// the edited one are compared field by field over the #893 configuration snapshot: an address, mapping, number-format,
/// ownership (priority), pricing/stock-rule or filter change touches every product linked to the source; a schedule
/// change touches the scheduled import; any meaningful change while an import is running is a large impact, as is
/// one over fifty or more products. A label-only change touches nothing. An edit loaded at an older configuration
/// revision than the persisted one is stale and refused with words (the store would refuse it too). The address is
/// masked and cut at its query before it is diffed, and credentials are never part of the snapshot, so no value that
/// could be a key appears in the words — before or after.
/// </summary>
public static class SourceChangeImpact
{
    public const int LargeProducts = 50;
    public const string Address = "adres", Mapping = "eşleme", Culture = "sayı biçimi", Schedule = "zamanlama", Ownership = "sahiplik", Rules = "fiyat/stok kuralları", Filters = "filtre", Label = "etiket";

    static readonly (string Field, string Group, Func<SourceConfig, string?> Read)[] Fields =
    {
        ("Ad", Label, c => c.Name),
        ("Adres", Address, c => SafeAddress(c.Location)),
        ("Ürün yolu", Address, c => c.ItemPath),
        ("Alan eşlemesi", Mapping, c => string.Join("; ", c.Fields.Select(kv => kv.Key + "=" + kv.Value))),
        ("Sayı kültürü", Culture, c => c.NumberCultureName),
        ("Ondalık ayracı", Culture, c => c.DecimalSeparator),
        ("Aktif", Schedule, c => Word(c.Enabled)),
        ("Otomatik içe aktarma", Schedule, c => Word(c.AutoImport)),
        ("Kontrol aralığı (dk)", Schedule, c => Number(c.IntervalMinutes)),
        ("SLA yenileme (dk)", Schedule, c => Number(c.SlaRefreshMinutes)),
        ("SLA tolerans (dk)", Schedule, c => Number(c.SlaGraceMinutes)),
        ("Kaynakta kayıp toleransı (dk)", Schedule, c => Number(c.MissingSourceGraceMinutes)),
        ("Öncelik", Ownership, c => Number(c.Priority)),
        ("Fiyat modu", Rules, c => c.PriceMode), ("Formül", Rules, c => c.Formula), ("Maliyet para birimi", Rules, c => c.CostCurrency), ("Satış para birimi", Rules, c => c.Currency),
        ("Otomatik kur", Rules, c => Word(c.AutoFx)), ("Kur türü", Rules, c => c.FxKind), ("TL / hedef birim", Rules, c => Number(c.TryPerTargetUnit)), ("Kur", Rules, c => Number(c.ExchangeRate)),
        ("Kâr yüzdesi", Rules, c => Number(c.MarkupPercent)), ("Sabit tutar", Rules, c => Number(c.FixedAmount)),
        ("Emniyet stoğu", Rules, c => Number(c.SafetyStock)), ("En az stok", Rules, c => Number(c.MinimumStock)), ("En çok stok", Rules, c => Number(c.MaximumStock)),
        ("Başlığı güncelle", Rules, c => Word(c.UpdateName)), ("Açıklamayı güncelle", Rules, c => Word(c.UpdateDescription)), ("Görselleri güncelle", Rules, c => Word(c.UpdateImages)),
        ("Marka filtresi", Filters, c => c.BrandFilter), ("Kategori filtresi", Filters, c => c.CategoryFilter),
    };

    static readonly HashSet<string> ProductGroups = new(StringComparer.Ordinal) { Address, Mapping, Culture, Ownership, Rules, Filters };

    public static SourceChangeImpactPreview Preview(XmlSource? persisted, XmlSource edited, int linkedProducts, SourceRunSnapshot? latestRun, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(edited);
        if (persisted is not null && edited.ConfigRevision != persisted.ConfigRevision)
        {
            var refused = $"Bu kaynak siz düzenlerken değişti (kayıtlı rev. {Number(persisted.ConfigRevision)}, yüklenen rev. {Number(edited.ConfigRevision)}); kaydetmeden önce kaynağı yeniden seçin.";
            return new(true, SourceChangeLevel.None, [], [], 0, 0, false, refused, []);
        }
        var before = persisted is null ? null : SourceConfig.Of(persisted);
        var after = SourceConfig.Of(edited);
        var changes = FieldDiff.Build(before, after, Fields.Select(f => (f.Field, f.Read)), includeUnchanged: false).Where(r => r.Kind != DiffKind.Unchanged).ToArray();
        var groups = Fields.Where(f => changes.Any(c => c.Field == f.Field)).Select(f => f.Group).Distinct().ToArray();
        var meaningful = groups.Any(g => g != Label);
        var running = meaningful && latestRun is { Status: "Running" } run && !(run.LeaseUntilUtc is { } lease && lease < nowUtc);
        // A record that does not exist yet has no scheduled import and no linked products to affect.
        var scheduled = meaningful && persisted is not null && edited.Enabled && edited.AutoImport;
        var products = meaningful && persisted is not null && groups.Any(ProductGroups.Contains) ? Math.Max(0, linkedProducts) : 0;
        var jobs = (running ? 1 : 0) + (scheduled ? 1 : 0);
        var level = changes.Length == 0 || (products == 0 && jobs == 0) ? SourceChangeLevel.None
            : products >= LargeProducts || running ? SourceChangeLevel.Large
            : SourceChangeLevel.Small;
        var lines = changes.Select(c => $"{c.Field}: {Show(c.Before)} → {Show(c.After)}").ToList();
        if (level != SourceChangeLevel.None) lines.Add($"Etki: {Number(products)} ürün (kaynağa bağlı) · {Number(jobs)} iş" + (running ? " · çalışan aktarım var" : "") + (scheduled ? " · zamanlanmış içe aktarma" : ""));
        var headline = changes.Length == 0 ? "Değişiklik yok."
            : level == SourceChangeLevel.None ? $"Etkilenen ürün veya iş yok ({string.Join(", ", groups)})."
            : $"{Number(products)} ürün ve {Number(jobs)} iş etkilenecek · {string.Join(", ", groups)}" + (level == SourceChangeLevel.Large ? " · büyük etki" : "") + (running ? " · çalışan aktarım var" : "");
        return new(false, level, changes, groups, products, jobs, running, AuditStore.Redact(headline), lines.Select(AuditStore.Redact).ToArray());
    }

    /// <summary>The address as the words may show it: masked by the source catalogue's rule, then cut at its query — a key lives there.</summary>
    static string SafeAddress(string? location)
    {
        var masked = ImportSourceCatalog.MaskLocation(location);
        var cut = masked.IndexOfAny(['?', '#']);
        return cut >= 0 ? masked[..cut] + "…" : masked;
    }

    static string Show(string? value) => string.IsNullOrEmpty(value) ? "(boş)" : value.Length > 60 ? value[..60] + "…" : value;
    static string Word(bool value) => value ? "evet" : "hayır";
    static string Number(int value) => value.ToString(CultureInfo.CurrentCulture);
    static string Number(decimal value) => value.ToString(CultureInfo.CurrentCulture);
}
