using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;

// Found while testing #826: every CatalogExcel writer saved to "<name>.xlsx.tmp-<guid>", which ClosedXML refuses
// ("Extension 'tmp-…' is not supported"), so the product export threw on every click. The temp name now keeps
// .xlsx; this pins the real product export end to end.
[TestClass]
public sealed class CatalogExcelExportTests
{
    [TestMethod]
    public void TheProductExportWritesAnXlsxWithTheHeaderAndLeavesNoTempFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "excel-export-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "urunler.xlsx");
        var products = new[]
        {
            new CatalogProduct { Sku = "A", Name = "Kupa", Price = 10, Stock = 3, Currency = "TRY" },
            new CatalogProduct { Sku = "B", Name = "Tabak", Price = 12, Stock = 5, Currency = "TRY" },
        };

        CatalogExcel.Export(path, products);

        Assert.IsTrue(File.Exists(path), "The export lands at the chosen path.");
        using (var book = new XLWorkbook(path))
        {
            var sheet = book.Worksheets.First();
            Assert.AreEqual("SKU", sheet.Cell(1, 1).GetString());
            Assert.AreEqual("A", sheet.Cell(2, 1).GetString());
            Assert.AreEqual("B", sheet.Cell(3, 1).GetString());
        }
        Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp-*").Length, "The temp file was moved, not left behind.");
        Directory.Delete(directory, true);
    }
}
