using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #880 (EXPORT: Collision-safe filename generation). A suggested export name is a sanitized stem, a timestamp to the
// second and an extension; invalid and control characters become dashes, a reserved device name is prefixed, a very
// long name is capped, an empty or personal-data-carrying name falls back to a generic stem; two exports in the same
// second or an existing file get "-2", "-3"; committing an export onto an existing file is refused unless the
// caller asked to replace it, and the refusal names the file, never its directory.
[TestClass]
public sealed class ExportFileNamesTests
{
    [TestMethod]
    public void NamesAreSanitizedTimestampedAndDuplicateSafeAndOverwriteIsNeverSilent()
    {
        var root = Path.Combine(Path.GetTempPath(), "export-names-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);

            // Sanitizing: invalid characters, control characters and separators become dashes; a reserved name is prefixed; a very long name is capped; empty and personal data fall back.
            Assert.AreEqual("Sipariş-Raporu-Q3-2026", ExportFileNames.SafeStem("  Sipariş Raporu: Q3/2026?  "));
            Assert.AreEqual("a-b-c", ExportFileNames.SafeStem("a\tbc"));
            Assert.AreEqual(ExportFileNames.DefaultStem, ExportFileNames.SafeStem("<>:\"/\\|?*"));
            Assert.AreEqual("x-con", ExportFileNames.SafeStem("con")); Assert.AreEqual("x-LPT1", ExportFileNames.SafeStem("LPT1"));
            Assert.AreEqual(ExportFileNames.MaxStemLength, ExportFileNames.SafeStem(new string('a', 200)).Length);
            Assert.AreEqual(ExportFileNames.DefaultStem, ExportFileNames.SafeStem("   ")); Assert.AreEqual(ExportFileNames.DefaultStem, ExportFileNames.SafeStem(null));
            Assert.AreEqual(ExportFileNames.DefaultStem, ExportFileNames.SafeStem("ali@example.com raporu"), "an e-mail never reaches a file name");
            Assert.AreEqual(ExportFileNames.DefaultStem, ExportFileNames.SafeStem("password=hunter2"), "a secret never reaches a file name");
            Assert.AreEqual("csv", ExportFileNames.SafeExtension(".CSV")); Assert.AreEqual("xlsx", ExportFileNames.SafeExtension("xlsx")); Assert.AreEqual("dat", ExportFileNames.SafeExtension("?!"));

            // Timestamped to the second.
            var at = new DateTime(2026, 9, 13, 15, 4, 5);
            Assert.AreEqual("orders-csv-20260913-150405.csv", ExportFileNames.Build("orders-csv", "CSV", at));
            Assert.AreEqual("disa-aktarim-20260913-150405.xlsx", ExportFileNames.Build(null, ".Xlsx", at));

            // Same-second exports and an existing file: "-2", "-3" before the extension.
            var first = ExportFileNames.Suggest(root, "orders", "csv", at);
            Assert.AreEqual(Path.Combine(root, "orders-20260913-150405.csv"), first);
            File.WriteAllText(first, "one");
            var second = ExportFileNames.Suggest(root, "orders", "csv", at);
            Assert.AreEqual(Path.Combine(root, "orders-20260913-150405-2.csv"), second);
            File.WriteAllText(second, "two");
            Assert.AreEqual(Path.Combine(root, "orders-20260913-150405-3.csv"), ExportFileNames.Suggest(root, "orders", "csv", at));
            Assert.AreEqual(first + "x", ExportFileNames.Unique(first + "x"), "a free path is returned as it is");

            // Committing: a new file lands; an existing file is refused unless the caller asked, and the refusal names the file only; asked for, it is replaced.
            var temporary = Path.Combine(root, "work.tmp"); File.WriteAllText(temporary, "new");
            var target = Path.Combine(root, "target.csv");
            ExportFiles.Commit(temporary, target, overwrite: false);
            Assert.AreEqual("new", File.ReadAllText(target)); Assert.IsFalse(File.Exists(temporary));
            File.WriteAllText(temporary, "newer");
            var refused = Assert.ThrowsException<ExportFileExistsException>(() => ExportFiles.Commit(temporary, target, overwrite: false));
            Assert.AreEqual("new", File.ReadAllText(target), "the existing file is intact"); Assert.IsTrue(File.Exists(temporary), "the caller still owns the temporary file");
            Assert.AreEqual("target.csv", refused.FileName); StringAssert.Contains(refused.Message, "üzerine yazılmadı"); StringAssert.Contains(refused.Message, "target.csv"); Assert.IsFalse(refused.Message.Contains(root, StringComparison.OrdinalIgnoreCase), "never the directory");
            ExportFiles.Commit(temporary, target, overwrite: true);
            Assert.AreEqual("newer", File.ReadAllText(target));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheWritersRefuseToReplaceAnExistingFileUnlessAsked()
    {
        var root = Path.Combine(Path.GetTempPath(), "export-writers-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);

            // The rejected-rows CSV: the second write to the same path is a failed result naming the file, not a silent replacement; asked for, it replaces; no temporary file stays behind.
            var csv = Path.Combine(root, "rejected.csv"); var rows = new[] { RejectedRowsExport.From(1, "NEGATIVE", "Stock", "-1", "x") };
            Assert.IsTrue(RejectedRowsExport.WriteCsv(csv, rows).Success);
            var refusedCsv = RejectedRowsExport.WriteCsv(csv, rows);
            Assert.IsFalse(refusedCsv.Success); StringAssert.Contains(refusedCsv.Error, "üzerine yazılmadı"); StringAssert.Contains(refusedCsv.Error, "rejected.csv"); Assert.IsFalse(refusedCsv.Error.Contains(root, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(RejectedRowsExport.WriteCsv(csv, rows, overwrite: true).Success);
            Assert.IsFalse(Directory.GetFiles(root).Any(f => f.Contains(".tmp-", StringComparison.Ordinal)), "no temporary file is left behind");

            // The product workbook.
            var xlsx = Path.Combine(root, "urunler.xlsx"); var products = new[] { new CatalogProduct { Sku = "SKU-1", Name = "Ürün", Price = 10, Stock = 3, Currency = "TRY" } };
            CatalogExcel.Export(xlsx, products);
            Assert.ThrowsException<ExportFileExistsException>(() => CatalogExcel.Export(xlsx, products));
            CatalogExcel.Export(xlsx, products, null, overwrite: true);

            // The support package and the data backup.
            var support = Path.Combine(root, "support.zip"); SupportPackageService.Export(support, root);
            Assert.ThrowsException<ExportFileExistsException>(() => SupportPackageService.Export(support, root));
            Assert.AreEqual(support, SupportPackageService.Export(support, root, overwrite: true));
            var data = Path.Combine(root, "data"); Directory.CreateDirectory(data); _ = new CatalogStore(data).Products();
            var backup = Path.Combine(root, "backup.zip"); var service = new DataBackupService(data); service.Backup(backup);
            Assert.ThrowsException<ExportFileExistsException>(() => service.Backup(backup));
            service.Backup(backup, overwrite: true);
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
