using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductProvenanceRow(string Field, string Origin, string Detail, bool IsOperatorOwned);

public sealed record ProductProvenanceView(
    string Headline,
    string SourceSummary,
    bool IsRevisionStale,
    IReadOnlyList<ProductProvenanceRow> Rows,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Where each product field's value came from (#800), for a summary the operator opens rather than another
/// permanent block on the card. Origins are read from the per-field source markers the import already
/// maintains (`PriceSource`, `StockSource`, `MediaSource`, `SourceKind`) and from the lock flags, which are the
/// reason a feed will not take a field back. A source is identified by its **name** only: its `Location` is the
/// feed URL and routinely carries a key in the query string, so it never enters this summary -- not even its
/// host, which a provenance line has no use for.
/// </summary>
public static class ProductProvenance
{
    public const string Unknown = "Kaynak bulunamadı";
    const string Manual = "manual";

    public static ProductProvenanceView Build(CatalogProduct product, XmlSource? source, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product);
        var warnings = new List<string>();
        var sourceName = string.IsNullOrWhiteSpace(source?.Name) ? "" : source!.Name.Trim();
        var feedBacked = !product.SourceKind.Equals(Manual, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(product.SourceId);
        var touched = product.SourceUpdatedUtc is { } when ? Ago(nowUtc - when) : "bilinmiyor";

        ProductProvenanceRow Row(string field, string marker, bool locked)
        {
            var operatorOwned = marker.Equals(Manual, StringComparison.OrdinalIgnoreCase) || sourceName.Length == 0 && !feedBacked;
            var origin = operatorOwned ? "Elle girildi" : sourceName.Length > 0 ? sourceName : Unknown;
            var detail = operatorOwned
                ? (locked ? "Alan kilitli; içe aktarma bu alanı değiştirmez." : "İçe aktarma bu alanı yeniden yazabilir.")
                : $"Son güncelleme: {touched}" + (locked ? " · Alan kilitli; içe aktarma bu alanı değiştirmez." : "");
            return new(field, origin, detail, operatorOwned);
        }

        var rows = new List<ProductProvenanceRow>
        {
            Row("Fiyat", product.PriceSource, product.LockPrice),
            Row("Stok", product.StockSource, product.LockStock),
            Row("Görseller", product.MediaSource, product.LockImages),
            Row("Başlık", product.SourceKind, product.LockName),
            Row("Açıklama", product.SourceKind, product.LockDescription),
        };

        string summary;
        var revisionStale = false;
        if (source is not null)
        {
            summary = $"{sourceName} · sürüm {source.LastAppliedMappingRevision}";
            revisionStale = source.MappingRevision > source.LastAppliedMappingRevision;
            if (revisionStale) warnings.Add($"Kaynak eşlemesi bu içe aktarmadan sonra değişti (sürüm {source.LastAppliedMappingRevision} → {source.MappingRevision}); alan kökenleri güncel olmayabilir.");
        }
        else if (!feedBacked) summary = "Elle oluşturuldu";
        else { summary = Unknown; warnings.Add("Bu ürünün kaynağı bulunamıyor; alan kökenleri doğrulanamadı."); }

        var fromSource = rows.Count(r => !r.IsOperatorOwned);
        var manual = rows.Count - fromSource;
        var headline = fromSource == 0 ? $"{manual} alan elle girildi"
            : manual == 0 ? $"{fromSource} alan kaynaktan geliyor"
            : $"{fromSource} alan kaynaktan, {manual} alan elle girildi";

        return new(headline, summary, revisionStale, rows, warnings);
    }

    static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes < 1) return "az önce";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} dakika önce";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} saat önce";
        return $"{(int)elapsed.TotalDays} gün önce";
    }
}
