using System.Diagnostics;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record ImportMeasurement(TimeSpan Elapsed, long AllocatedBytes, int ItemCount, string Path,
    bool ImportExecuted = false, int Added = 0, int Updated = 0, int Unchanged = 0, bool AsyncYieldObserved = false);

/// <summary>
/// Measures the same reader and preview paths used by the desktop import flow. It is
/// intentionally backed by real XML/XLSX files rather than generated row loops.
/// </summary>
public static class ProductionImportMeasurement
{
    public static Task<ImportMeasurement> MeasureXmlAsync(XmlSourceReader reader, XmlSource source, CancellationToken cancellationToken = default)
        => MeasureXmlAsync(reader, source, null, cancellationToken);

    public static async Task<ImportMeasurement> MeasureXmlAsync(XmlSourceReader reader, XmlSource source, CatalogStore? store, CancellationToken cancellationToken = default)
    {
        var before = GC.GetAllocatedBytesForCurrentThread(); var stopwatch = Stopwatch.StartNew();
        await Task.Yield();
        var xml = await reader.ReadAsync(source.Location, null, cancellationToken).ConfigureAwait(false);
        var snapshot = XmlCatalog.MappingSnapshot(xml, source); XmlCatalog.EnsureMappingReady(source, snapshot, scheduled: false);
        var rows = XmlCatalog.Preview(xml, source);
        var summary = store is null ? null : ApplyXml(store, source, rows, xml, snapshot, cancellationToken);
        stopwatch.Stop();
        return new(stopwatch.Elapsed, Math.Max(1, GC.GetAllocatedBytesForCurrentThread() - before), rows.Count, source.Location,
            summary is not null, summary?.Added ?? 0, summary?.Updated ?? 0, summary?.Unchanged ?? 0, true);
    }

    public static ImportMeasurement MeasureXlsx(string path, ExcelImportProfile profile)
        => MeasureXlsx(path, profile, null, null);

    public static ImportMeasurement MeasureXlsx(string path, ExcelImportProfile profile, CatalogStore? store, XmlSource? source)
    {
        var before = GC.GetAllocatedBytesForCurrentThread(); var stopwatch = Stopwatch.StartNew();
        var preview = CatalogExcel.Preview(path, profile);
        ImportSummary? summary = null;
        if (store is not null && source is not null)
        {
            store.SaveSource(source);
            var rows = preview.Rows.Select(row =>
            {
                row.SourceId = source.Id; row.SourceKind = "excel"; row.PriceSource = "excel"; row.StockSource = "excel"; row.MediaSource = "excel"; row.SourceUpdatedUtc = DateTime.UtcNow;
                return row;
            }).ToList();
            var fingerprint = XmlPreviewFingerprint.FeedHash(File.ReadAllBytes(path));
            summary = store.Import(source, rows, CancellationToken.None,
                new XmlImportContext { FeedHash = fingerprint, MappingShapeFingerprint = "xlsx:" + fingerprint, CompleteFeed = true });
        }
        stopwatch.Stop();
        return new(stopwatch.Elapsed, Math.Max(1, GC.GetAllocatedBytesForCurrentThread() - before), preview.Rows.Count, path,
            summary is not null, summary?.Added ?? 0, summary?.Updated ?? 0, summary?.Unchanged ?? 0);
    }

    static ImportSummary ApplyXml(CatalogStore store, XmlSource source, IReadOnlyList<CatalogProduct> rows, string xml, XmlMappingSnapshot snapshot, CancellationToken cancellationToken)
    {
        store.SaveSource(source);
        return store.Import(source, rows, cancellationToken,
            new XmlImportContext { FeedHash = XmlPreviewFingerprint.FeedHash(xml), MappingShapeFingerprint = snapshot.Fingerprint, CompleteFeed = true, ObservedAtUtc = DateTimeOffset.UtcNow });
    }
}
