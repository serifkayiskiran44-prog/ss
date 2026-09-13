using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClosedXML.Excel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #826 through the real export path the Excel screen calls: the xlsx carries the stable schema, only the refused
// rows, parsed from the import's own sentences; a cancellation and a disk error are results, not exceptions, and
// leave no file behind.
[TestClass]
public sealed class RejectedRowsXlsxExportTests
{
    static string Temp(string name) => Path.Combine(Path.GetTempPath(), "rejected-xlsx-" + Guid.NewGuid().ToString("N"), name);

    static ExcelPreview PreviewWithErrors(params string[] errors) => new(Array.Empty<CatalogProduct>(), errors);

    [TestMethod]
    public void TheXlsxCarriesTheSchemaAndTheParsedRows()
    {
        var path = Temp("reddedilen.xlsx");
        var preview = PreviewWithErrors("Satır 3: Stock negatif olamaz. (NEGATIVE)", "Satır 1: Ad ve kimlik boş olamaz.", "Çalışma sayfası bulunamadı.");

        var result = CatalogExcel.ExportErrors(path, preview, CancellationToken.None);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(3, result.Written);
        using var book = new XLWorkbook(path);
        var sheet = book.Worksheets.First();
        CollectionAssert.AreEqual(RejectedRowsExport.Schema.ToArray(), Enumerable.Range(1, 5).Select(c => sheet.Cell(1, c).GetString()).ToArray(), "The header row is the stable schema.");
        Assert.AreEqual(1, sheet.Cell(2, 1).GetValue<int>(), "Rows come in row order.");
        Assert.AreEqual(3, sheet.Cell(3, 1).GetValue<int>());
        Assert.AreEqual("NEGATIVE", sheet.Cell(3, 2).GetString());
        Assert.AreEqual("Stock", sheet.Cell(3, 3).GetString());
        Assert.AreEqual(RejectedRowsExport.UnknownCode, sheet.Cell(2, 2).GetString(), "No code in the sentence, none invented.");
        Assert.AreEqual(0, sheet.Cell(4, 1).GetValue<int>(), "An unparsed sentence is last, with row 0.");
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length, "The temp file was moved, not left.");
        book.Dispose();
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [TestMethod]
    public void ACancelledXlsxExportLeavesNoFile()
    {
        var path = Temp("cancelled.xlsx");
        using var cts = new CancellationTokenSource(); cts.Cancel();

        var result = CatalogExcel.ExportErrors(path, PreviewWithErrors("Satır 2: Stock negatif olamaz. (NEGATIVE)"), cts.Token);

        Assert.IsTrue(result.Cancelled); Assert.IsFalse(result.Success);
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [TestMethod]
    public void ADiskErrorOnTheXlsxPathIsAResult()
    {
        var directory = Temp("as-directory.xlsx");
        Directory.CreateDirectory(directory);

        var result = CatalogExcel.ExportErrors(directory, PreviewWithErrors("Satır 2: Stock negatif olamaz. (NEGATIVE)"), CancellationToken.None);

        Assert.IsFalse(result.Success); Assert.IsFalse(result.Cancelled);
        StringAssert.Contains(result.Error, "yazılamadı");
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(directory)!, "*.tmp-*").Length);
        Directory.Delete(Path.GetDirectoryName(directory)!, true);
    }
}
