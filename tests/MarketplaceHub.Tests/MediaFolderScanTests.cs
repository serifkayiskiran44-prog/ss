using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1847 (deterministic filename-to-product media ingest) and
/// #1848 (durable run/progress/cancel tracking for the scan).
[TestClass]
public sealed class MediaFolderScanTests
{
    static (CatalogStore Catalog, MediaStore Media, MediaScanRunStore Runs, string Root, string Folder) NewStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "media-scan-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "images");
        Directory.CreateDirectory(folder);
        return (new CatalogStore(root), new MediaStore(root), new MediaScanRunStore(root), root, folder);
    }

    static CatalogProduct Product(CatalogStore catalog, string sku) => catalog.CreateManual(new CatalogProduct { Sku = sku, Name = "Product " + sku, Price = 10, Currency = "USD" });

    [TestMethod]
    public void EmptyFolderPreviewHasNoCandidates()
    {
        var (catalog, _, _, root, folder) = NewStores();
        try
        {
            var preview = MediaFolderScan.Preview(folder, catalog.Products());
            Assert.AreEqual(0, preview.Candidates.Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MatchingFileNameLinksToTheRightProduct()
    {
        var (catalog, media, _, root, folder) = NewStores();
        try
        {
            var product = Product(catalog, "SKU-1");
            File.WriteAllBytes(Path.Combine(folder, "SKU-1.jpg"), [1, 2, 3]);
            var preview = MediaFolderScan.Preview(folder, catalog.Products());
            Assert.AreEqual(1, preview.Matched);
            var result = MediaFolderScan.ApplyApproved(preview, true, catalog, media);
            Assert.AreEqual(1, result.Committed);
            Assert.AreEqual(1, media.List(product.Id).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnsupportedExtensionIsNotACandidateForLinking()
    {
        var (catalog, _, _, root, folder) = NewStores();
        try
        {
            Product(catalog, "SKU-1");
            File.WriteAllBytes(Path.Combine(folder, "SKU-1.txt"), [1]);
            var preview = MediaFolderScan.Preview(folder, catalog.Products());
            Assert.AreEqual(1, preview.Unsupported);
            Assert.AreEqual(0, preview.Matched);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DuplicateFileNameInFolderGoesToReviewNotAutoLink()
    {
        var (catalog, _, _, root, folder) = NewStores();
        try
        {
            Product(catalog, "SKU-1");
            File.WriteAllBytes(Path.Combine(folder, "SKU-1.jpg"), [1]);
            File.WriteAllBytes(Path.Combine(folder, "SKU-1.png"), [2]);
            var preview = MediaFolderScan.Preview(folder, catalog.Products());
            Assert.AreEqual(0, preview.Matched);
            Assert.AreEqual(2, preview.Candidates.Count(c => c.Status == MediaMatchStatus.DuplicateFilename));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AmbiguousProductIdentityGoesToReview()
    {
        var (catalog, _, _, root, folder) = NewStores();
        try
        {
            // Two products sharing a normalized SKU (a pre-existing cross-source
            // duplicate, see ProductSearchTests) makes "SHARED-1.jpg" ambiguous.
            catalog.SaveSource(new XmlSource { Id = "a", Location = "https://example.test/a.xml" });
            catalog.SaveSource(new XmlSource { Id = "b", Location = "https://example.test/b.xml" });
            catalog.Import(new XmlSource { Id = "a", Location = "https://example.test/a.xml" }, [new CatalogProduct { SourceId = "a", Sku = "SHARED-1", Name = "A", Price = 10, Currency = "USD" }]);
            catalog.Import(new XmlSource { Id = "b", Location = "https://example.test/b.xml" }, [new CatalogProduct { SourceId = "b", Sku = "SHARED-1", Name = "B", Price = 10, Currency = "USD" }]);
            File.WriteAllBytes(Path.Combine(folder, "SHARED-1.jpg"), [1]);

            var preview = MediaFolderScan.Preview(folder, catalog.Products());
            Assert.AreEqual(MediaMatchStatus.Ambiguous, preview.Candidates.Single().Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NoMatchFileGoesToReview()
    {
        var (catalog, _, _, root, folder) = NewStores();
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "UNKNOWN.jpg"), [1]);
            var preview = MediaFolderScan.Preview(folder, catalog.Products());
            Assert.AreEqual(MediaMatchStatus.NoMatch, preview.Candidates.Single().Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RescanningTheSameFolderIsIdempotent()
    {
        var (catalog, media, _, root, folder) = NewStores();
        try
        {
            var product = Product(catalog, "SKU-1");
            File.WriteAllBytes(Path.Combine(folder, "SKU-1.jpg"), [1, 2, 3]);
            var first = MediaFolderScan.Preview(folder, catalog.Products());
            MediaFolderScan.ApplyApproved(first, true, catalog, media);

            var second = MediaFolderScan.Preview(folder, catalog.Products());
            var result = MediaFolderScan.ApplyApproved(second, true, catalog, media);
            Assert.AreEqual(0, result.Committed);
            Assert.AreEqual(1, result.AlreadyLinked);
            Assert.AreEqual(1, media.List(product.Id).Count, "Rescanning must not create a second media row for the same file.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CancellationMidApplyStopsFurtherWorkWithoutReportingSuccess()
    {
        var (catalog, media, runs, root, folder) = NewStores();
        try
        {
            var p1 = Product(catalog, "SKU-1"); var p2 = Product(catalog, "SKU-2");
            File.WriteAllBytes(Path.Combine(folder, "SKU-1.jpg"), [1]);
            File.WriteAllBytes(Path.Combine(folder, "SKU-2.jpg"), [2]);
            var preview = MediaFolderScan.Preview(folder, catalog.Products());
            var run = runs.Start(preview.Directory, preview.Candidates.Count, preview.Matched, preview.Review);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsException<OperationCanceledException>(() => MediaFolderScan.ApplyApproved(preview, true, catalog, media, runs, run.Id, cts.Token));
            Assert.AreEqual("Cancelled", runs.Get(run.Id).Status);
            Assert.AreEqual(0, media.List(p1.Id).Count);
            Assert.AreEqual(0, media.List(p2.Id).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StaleProductChangedAfterPreviewIsSkipped()
    {
        var (catalog, media, _, root, folder) = NewStores();
        try
        {
            var product = Product(catalog, "SKU-1");
            File.WriteAllBytes(Path.Combine(folder, "SKU-1.jpg"), [1]);
            var preview = MediaFolderScan.Preview(folder, catalog.Products());

            var live = catalog.Products().Single(); live.Name = "changed"; catalog.SaveProduct(live);

            var result = MediaFolderScan.ApplyApproved(preview, true, catalog, media);
            Assert.AreEqual(0, result.Committed);
            Assert.AreEqual(1, result.Stale);
            Assert.AreEqual(0, media.List(product.Id).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesCommittedMediaAndRunHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "media-scan-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "images");
        Directory.CreateDirectory(folder);
        try
        {
            var catalog = new CatalogStore(root);
            var product = Product(catalog, "SKU-1");
            File.WriteAllBytes(Path.Combine(folder, "SKU-1.jpg"), [1]);
            var media = new MediaStore(root);
            var runs = new MediaScanRunStore(root);
            var preview = MediaFolderScan.Preview(folder, catalog.Products());
            var run = runs.Start(preview.Directory, preview.Candidates.Count, preview.Matched, preview.Review);
            MediaFolderScan.ApplyApproved(preview, true, catalog, media, runs, run.Id);

            var reopenedMedia = new MediaStore(root);
            var reopenedRuns = new MediaScanRunStore(root);
            Assert.AreEqual(1, reopenedMedia.List(product.Id).Count);
            Assert.AreEqual("Succeeded", reopenedRuns.Get(run.Id).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SameDirectoryCannotBeClaimedByTwoActiveRuns()
    {
        var (_, _, runs, root, folder) = NewStores();
        try
        {
            runs.Start(folder, 0, 0, 0);
            Assert.ThrowsException<InvalidOperationException>(() => runs.Start(folder, 0, 0, 0));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AbandonedRunningRunIsDetectedAsRecoverable()
    {
        var (_, _, runs, root, folder) = NewStores();
        try
        {
            var run = runs.Start(folder, 5, 2, 3);
            var abandoned = runs.AbandonedRunning();
            Assert.AreEqual(1, abandoned.Count);
            Assert.AreEqual(run.Id, abandoned[0].Id);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CountsMatchBetweenPreviewAndRunRecord()
    {
        var (catalog, media, runs, root, folder) = NewStores();
        try
        {
            Product(catalog, "SKU-1");
            File.WriteAllBytes(Path.Combine(folder, "SKU-1.jpg"), [1]);
            File.WriteAllBytes(Path.Combine(folder, "UNKNOWN.jpg"), [2]);
            var preview = MediaFolderScan.Preview(folder, catalog.Products());
            var run = runs.Start(preview.Directory, preview.Candidates.Count, preview.Matched, preview.Review);
            MediaFolderScan.ApplyApproved(preview, true, catalog, media, runs, run.Id);

            var recorded = runs.Get(run.Id);
            Assert.AreEqual(2, recorded.Discovered);
            Assert.AreEqual(1, recorded.Matched);
            Assert.AreEqual(1, recorded.Review);
            Assert.AreEqual(1, recorded.Committed);
            Assert.AreEqual("Succeeded", recorded.Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
