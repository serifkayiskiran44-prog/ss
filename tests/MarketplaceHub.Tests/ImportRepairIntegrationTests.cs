using ClosedXML.Excel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class ImportRepairIntegrationTests
{
    [TestMethod]
    public void XmlMappingSnapshotBlocksNamespaceDriftUntilExplicitRemap()
    {
        var source = RequiredSource("mapping-source");
        var v1 = "<Products xmlns=\"urn:v1\"><Product><Sku>A</Sku><Name>One</Name><Cost>10.00</Cost><Stock>2</Stock></Product></Products>";
        var first = XmlCatalog.MappingSnapshot(v1, source);
        source.LastMappingShapeFingerprint = first.Fingerprint;
        source.LastAppliedMappingRevision = source.MappingRevision;

        var v2 = "<Products xmlns=\"urn:v2\"><Product><Sku>A</Sku><Name>One</Name><Cost>10.00</Cost><Stock>2</Stock></Product></Products>";
        var drift = XmlCatalog.MappingSnapshot(v2, source);
        Assert.ThrowsException<XmlMappingBlockedException>(() => XmlCatalog.EnsureMappingReady(source, drift, scheduled: true));

        source.MappingRevision++;
        XmlCatalog.EnsureMappingReady(source, drift, scheduled: false);

        var repeatDrift = "<Products xmlns=\"urn:v1\"><Product><Sku>A</Sku><Name>One</Name><Cost>10.00</Cost><Stock>2</Stock><Extra><Value>x</Value></Extra></Product></Products>";
        var repeatSnapshot = XmlCatalog.MappingSnapshot(repeatDrift, source);
        Assert.ThrowsException<XmlMappingBlockedException>(() => XmlCatalog.EnsureMappingReady(source, repeatSnapshot, scheduled: true));

        var renamedSource = RequiredSource("mapping-source-renamed");
        renamedSource.Fields["Name"] = "Title";
        var renamed = XmlCatalog.MappingSnapshot("<Products><Product><Sku>A</Sku><Title>One</Title><Cost>10</Cost><Stock>2</Stock></Product></Products>", renamedSource);
        Assert.IsTrue(renamed.IsUsable);
    }

    [TestMethod]
    public void XmlImportPersistsMissingSourceGraceAndFeedHashIdempotency()
    {
        var root = Temp("source-missing");
        try
        {
            var store = new CatalogStore(root);
            var source = RequiredSource("supplier-missing");
            source.MissingSourceGraceMinutes = 60;
            store.SaveSource(source);
            var all = Enumerable.Range(1, 10).Select(i => Product(source.Id, $"SKU-{i}", i)).ToArray();
            var first = store.Import(source, all, default, new XmlImportContext { FeedHash = "feed-a", MappingShapeFingerprint = "shape-a", CompleteFeed = true });
            Assert.AreEqual(10, first.Added);

            var partial = all.Take(9).ToArray();
            var missing = store.Import(source, partial, default, new XmlImportContext { FeedHash = "feed-b", MappingShapeFingerprint = "shape-a", CompleteFeed = true });
            Assert.AreEqual(9, missing.Updated + missing.Unchanged);
            var cases = new SourceMissingQuarantine(root).List(source.Id);
            Assert.AreEqual("WARNING", cases.Single(x => x.ProductId == "SKU-10").State);
            Assert.AreEqual(10, store.Products().Count);

            var pending = store.Import(source, partial, default, new XmlImportContext { FeedHash = "feed-b2", MappingShapeFingerprint = "shape-a", CompleteFeed = true, ObservedAtUtc = DateTimeOffset.UtcNow.AddHours(2) });
            Assert.AreEqual(9, pending.Updated + pending.Unchanged);
            Assert.AreEqual("PENDING_ACTION", new SourceMissingQuarantine(root).List(source.Id).Single(x => x.ProductId == "SKU-10").State);
            Assert.AreEqual(10, store.Products().Count);

            var recovered = store.Import(source, all, default, new XmlImportContext { FeedHash = "feed-c", MappingShapeFingerprint = "shape-a", CompleteFeed = true, ObservedAtUtc = DateTimeOffset.UtcNow.AddHours(2) });
            Assert.IsTrue(recovered.Updated + recovered.Unchanged >= 10);
            Assert.AreEqual(0, new SourceMissingQuarantine(root).List(source.Id).Count);

            var replay = store.Import(source, all, default, new XmlImportContext { FeedHash = "feed-c", MappingShapeFingerprint = "shape-a", CompleteFeed = true });
            Assert.IsTrue(replay.AlreadyApplied);
            Assert.AreEqual(0, replay.Added + replay.Updated);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void DeterministicParserUsesProfileAndReturnsSpecificRejectReasons()
    {
        var tr = DeterministicNumberParser.Decimal("1.234,56", "tr-TR", "Price");
        Assert.IsTrue(tr.Success, tr.Message);
        Assert.AreEqual(1234.56m, tr.Value);
        Assert.AreEqual(1234.56m, DeterministicNumberParser.Decimal("1,234.56", "en-US", "Price").Value);
        Assert.AreEqual(1.234m, DeterministicNumberParser.Decimal("1,234", "tr-TR", "Price").Value);
        Assert.AreEqual("AMBIGUOUS_SEPARATOR", DeterministicNumberParser.Decimal("1,234.56", "tr-TR", "Price").Code);
        Assert.AreEqual("EMPTY", DeterministicNumberParser.Decimal("", "en-US", "Price").Code);
        Assert.AreEqual("DATE_LIKE", DeterministicNumberParser.Decimal("2026-09-12", "en-US", "Price").Code);
        Assert.AreEqual("NEGATIVE", DeterministicNumberParser.Decimal("-1", "en-US", "Price").Code);
        Assert.AreEqual("OVERFLOW", DeterministicNumberParser.Decimal("999999999999999999999999999999999999999999", "en-US", "Price").Code);
        Assert.ThrowsException<InvalidOperationException>(() => ExcelProfileStore.Culture(""));
    }

    [TestMethod]
    public async Task ExcelApplyCoordinatorRejectsReentryAndPreservesPreviewMetadata()
    {
        var root = Temp("excel-coordinator");
        var path = Path.Combine(root, "products.xlsx");
        try
        {
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("Products");
                sheet.Cell(1, 1).Value = "SKU"; sheet.Cell(1, 2).Value = "Ürün"; sheet.Cell(1, 3).Value = "Alış"; sheet.Cell(1, 4).Value = "Satış"; sheet.Cell(1, 5).Value = "Stok";
                for (var i = 1; i <= 250; i++) { var row = i + 1; sheet.Cell(row, 1).Value = $"EX-{i}"; sheet.Cell(row, 2).Value = $"Product {i}"; sheet.Cell(row, 3).Value = "10.00"; sheet.Cell(row, 4).Value = "20.00"; sheet.Cell(row, 5).Value = 2; }
                book.SaveAs(path);
            }
            var store = new CatalogStore(root);
            var profile = new ExcelImportProfile { Name = "en", CultureName = "en-US" };
            var decisions = await CatalogExcel.PreviewDecisionsAsync(store, path, profile, CancellationToken.None);
            Assert.IsNotNull(decisions.Preview);
            Assert.IsFalse(string.IsNullOrWhiteSpace(decisions.Preview!.FileHash));
            using var coordinator = new ExcelApplyCoordinator();
            using var hold = new CancellationTokenSource();
            var first = coordinator.ApplyWithUndoAsync(store, new XmlSource { Id = "excel-source", Currency = "USD", DecimalSeparator = "." }, decisions.Preview, Enumerable.Range(0, decisions.Preview.Rows.Count).ToArray(), path, profile, hold.Token);
            await Task.Delay(10);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => coordinator.ApplyWithUndoAsync(store, new XmlSource { Id = "excel-source", Currency = "USD", DecimalSeparator = "." }, decisions.Preview, Enumerable.Range(0, decisions.Preview.Rows.Count).ToArray(), path, profile, hold.Token));
            hold.Cancel();
            var cancelled = false;
            try { await first; } catch (OperationCanceledException) { cancelled = true; }
            Assert.IsTrue(cancelled, "İptal edilen Excel uygulaması iptal istisnası döndürmelidir.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void XmlRunStoreRecoversExpiredLeaseAndRejectsConcurrentStart()
    {
        var root = Temp("xml-run-recovery");
        try
        {
            var first = new XmlRunStore(root);
            var id = first.Start("source-1", "feed-1", TimeSpan.FromMinutes(1));
            Assert.ThrowsException<InvalidOperationException>(() => new XmlRunStore(root).Start("source-1", "feed-2", TimeSpan.FromMinutes(1)));
            first.Heartbeat(id, TimeSpan.FromMinutes(10));
            Assert.AreEqual(1, first.RecoverAbandonedRunning(TimeSpan.FromMinutes(1), DateTime.UtcNow.AddMinutes(2)));
            var next = new XmlRunStore(root).Start("source-1", "feed-2", TimeSpan.FromMinutes(1));
            Assert.AreNotEqual(id, next);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task ProductionXmlAndXlsxChainsProduceBoundedMeasurements()
    {
        var root = Temp("production-chain");
        try
        {
            var xmlPath = Path.Combine(root, "feed.xml");
            var products = string.Join("", Enumerable.Range(1, 120).Select(i => $"<Product><Sku>P-{i}</Sku><Name>N-{i}</Name><Cost>10.25</Cost><Stock>5</Stock></Product>"));
            await File.WriteAllTextAsync(xmlPath, $"<Products>{products}</Products>");
            var source = RequiredSource("chain-source");
            source.Location = xmlPath;
            var xmlStore = new CatalogStore(root);
            var xmlMeasure = await ProductionImportMeasurement.MeasureXmlAsync(new XmlSourceReader(new HttpClient()), source, xmlStore, CancellationToken.None);
            Assert.AreEqual(120, xmlMeasure.ItemCount);
            Assert.IsTrue(xmlMeasure.ImportExecuted);
            Assert.AreEqual(120, xmlMeasure.Added);
            Assert.IsTrue(xmlMeasure.AsyncYieldObserved);
            Assert.IsTrue(xmlMeasure.Elapsed >= TimeSpan.Zero);
            Assert.IsTrue(xmlMeasure.AllocatedBytes > 0);

            var xlsxPath = Path.Combine(root, "feed.xlsx");
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("Products");
                foreach (var pair in new[] { "SKU", "Ürün", "Alış", "Satış", "Stok" }.Select((x, i) => (x, i + 1))) sheet.Cell(1, pair.Item2).Value = pair.x;
                for (var i = 1; i <= 120; i++) { var row = i + 1; sheet.Cell(row, 1).Value = $"X-{i}"; sheet.Cell(row, 2).Value = $"X-{i}"; sheet.Cell(row, 3).Value = "10.25"; sheet.Cell(row, 4).Value = "20.50"; sheet.Cell(row, 5).Value = 4; }
                book.SaveAs(xlsxPath);
            }
            var xlsxStore = new CatalogStore(Path.Combine(root, "xlsx-store"));
            var xlsxMeasure = ProductionImportMeasurement.MeasureXlsx(xlsxPath, new ExcelImportProfile { Name = "en", CultureName = "en-US" }, xlsxStore, RequiredSource("xlsx-source"));
            Assert.AreEqual(120, xlsxMeasure.ItemCount);
            Assert.IsTrue(xlsxMeasure.ImportExecuted);
            Assert.AreEqual(120, xlsxMeasure.Added);
            Assert.IsTrue(xlsxMeasure.Elapsed >= TimeSpan.Zero);
            Assert.IsTrue(xlsxMeasure.AllocatedBytes > 0);
        }
        finally { Cleanup(root); }
    }

    static XmlSource RequiredSource(string id) => new()
    {
        Id = id, Name = id, Location = "fixture.xml", Currency = "USD", CostCurrency = "USD", DecimalSeparator = ".", NumberCultureName = "en-US",
        ItemPath = "/Products/Product", Fields = new Dictionary<string, string> { ["Sku"] = "Sku", ["Name"] = "Name", ["Cost"] = "Cost", ["Stock"] = "Stock" },
        ExchangeRate = 1, MaximumStock = 1000
    };

    static CatalogProduct Product(string sourceId, string sku, int index) => new() { SourceId = sourceId, SourceKind = "xml", Sku = sku, Name = "Product " + index, Cost = 10, Price = 20, Currency = "USD", Stock = 5, Active = true };
    static string Temp(string name) { var path = Path.Combine(Path.GetTempPath(), "monobridge-" + name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    static void Cleanup(string path) { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(path)) Directory.Delete(path, true); }
}
