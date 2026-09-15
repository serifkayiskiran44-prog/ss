using ClosedXML.Excel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1899 (explicit worksheet/header-row selection) and #1900
/// (saveable, versioned column-mapping profiles with stale-header detection).
[TestClass]
public sealed class ExcelImportProfileTests
{
    static string NewWorkbook(Action<XLWorkbook> build)
    {
        var path = Path.Combine(Path.GetTempPath(), "excel-profile-" + Guid.NewGuid().ToString("N") + ".xlsx");
        using var book = new XLWorkbook();
        build(book);
        book.SaveAs(path);
        return path;
    }

    static void WriteRow(IXLWorksheet sheet, int row, params string[] values)
    {
        for (var i = 0; i < values.Length; i++) sheet.Cell(row, i + 1).Value = values[i];
    }

    [TestMethod]
    public void ListWorksheetsReturnsAllSheetsIncludingHiddenAndEmpty()
    {
        var path = NewWorkbook(book =>
        {
            var first = book.AddWorksheet("Boş");
            var second = book.AddWorksheet("Ürünler");
            WriteRow(second, 1, "SKU", "Ürün", "Alış", "Satış", "Stok");
            WriteRow(second, 2, "SKU-1", "Ürün 1", "5", "10", "3");
            var hidden = book.AddWorksheet("Gizli");
            hidden.Hide();
        });
        try
        {
            var sheets = CatalogExcel.ListWorksheets(path);
            CollectionAssert.AreEquivalent(new[] { "Boş", "Ürünler", "Gizli" }, sheets.ToArray());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void HeadersReadsFromExplicitlySelectedSheetNotJustTheFirst()
    {
        var path = NewWorkbook(book =>
        {
            book.AddWorksheet("Boş");
            var second = book.AddWorksheet("Ürünler");
            WriteRow(second, 1, "SKU", "Ürün");
        });
        try
        {
            var headers = CatalogExcel.Headers(path, sheetName: "Ürünler");
            CollectionAssert.AreEqual(new[] { "SKU", "Ürün" }, headers.ToArray());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ChangedHeaderRowIsRespected()
    {
        var path = NewWorkbook(book =>
        {
            var sheet = book.AddWorksheet("Sheet1");
            sheet.Cell(1, 1).Value = "Bu satır rapor başlığıdır, gerçek kolon başlığı değildir";
            WriteRow(sheet, 2, "SKU", "Ürün", "Alış", "Satış", "Stok");
            WriteRow(sheet, 3, "SKU-1", "Ürün 1", "5", "10", "3");
        });
        try
        {
            var headers = CatalogExcel.Headers(path, headerRow: 2);
            CollectionAssert.AreEqual(new[] { "SKU", "Ürün", "Alış", "Satış", "Stok" }, headers.ToArray());

            var preview = CatalogExcel.Preview(path, sheetName: null, headerRow: 2);
            Assert.AreEqual(0, preview.Errors.Count, string.Join(" | ", preview.Errors));
            Assert.AreEqual(1, preview.Rows.Count);
            Assert.AreEqual("SKU-1", preview.Rows[0].Sku);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void EmptyHeaderRowThrowsInsteadOfSilentlyMisreading()
    {
        var path = NewWorkbook(book =>
        {
            var sheet = book.AddWorksheet("Sheet1");
            WriteRow(sheet, 1, "SKU", "Ürün");
            // Row 2 is intentionally left blank.
            WriteRow(sheet, 3, "SKU-1", "Ürün 1");
        });
        try
        {
            Assert.ThrowsException<InvalidDataException>(() => CatalogExcel.Headers(path, headerRow: 2));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ProfileCreateLoadRemoveLifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "excel-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ExcelProfileStore(root);
            var profile = new ExcelImportProfile { Name = "Tedarikçi A", SheetName = "Ürünler", HeaderRow = 2 };
            store.Save(profile);

            var loaded = store.Find(profile.Id)!;
            Assert.AreEqual("Ürünler", loaded.SheetName);
            Assert.AreEqual(2, loaded.HeaderRow);

            store.Delete(profile.Id, loaded.Revision);
            Assert.IsNull(store.Find(profile.Id));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesProfileSheetAndHeaderRowChoice()
    {
        var root = Path.Combine(Path.GetTempPath(), "excel-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new ExcelImportProfile { Name = "Tedarikçi A", SheetName = "Ürünler", HeaderRow = 3, ExpectedHeaders = ["SKU", "Ürün"] };
            new ExcelProfileStore(root).Save(profile);

            var reopened = new ExcelProfileStore(root).Find(profile.Id)!;
            Assert.AreEqual("Ürünler", reopened.SheetName);
            Assert.AreEqual(3, reopened.HeaderRow);
            CollectionAssert.AreEqual(new[] { "SKU", "Ürün" }, reopened.ExpectedHeaders.ToArray());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ChangedHeadersMarkProfileStale()
    {
        var path = NewWorkbook(book =>
        {
            var sheet = book.AddWorksheet("Sheet1");
            WriteRow(sheet, 1, "SKU", "Ürün", "Alış", "Satış", "Stok", "EkstraKolon");
        });
        try
        {
            var profile = new ExcelImportProfile { ExpectedHeaders = ["SKU", "Ürün", "Alış", "Satış", "Stok"] };
            Assert.IsTrue(CatalogExcel.IsProfileStale(profile, path), "An added column must be detected as a header-set change.");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void UnchangedHeadersAreNotStale()
    {
        var path = NewWorkbook(book =>
        {
            var sheet = book.AddWorksheet("Sheet1");
            WriteRow(sheet, 1, "SKU", "Ürün", "Alış", "Satış", "Stok");
        });
        try
        {
            var profile = new ExcelImportProfile { ExpectedHeaders = ["SKU", "Ürün", "Alış", "Satış", "Stok"] };
            Assert.IsFalse(CatalogExcel.IsProfileStale(profile, path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ProfileWithoutCapturedHeadersIsNeverConsideredStale()
    {
        var path = NewWorkbook(book =>
        {
            var sheet = book.AddWorksheet("Sheet1");
            WriteRow(sheet, 1, "SKU", "Ürün");
        });
        try
        {
            var profile = new ExcelImportProfile();
            Assert.IsFalse(CatalogExcel.IsProfileStale(profile, path), "A profile that never captured expected headers has nothing to compare against.");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void DuplicateColumnNamesResolveDeterministicallyNotRandomly()
    {
        var path = NewWorkbook(book =>
        {
            var sheet = book.AddWorksheet("Sheet1");
            WriteRow(sheet, 1, "SKU", "SKU", "Ürün", "Alış", "Satış", "Stok");
            WriteRow(sheet, 2, "SKU-1", "SKU-DUP", "Ürün 1", "5", "10", "3");
        });
        try
        {
            // Two auto-mapping runs on the same duplicate-header workbook must agree -
            // whichever column wins, it wins consistently, not by chance.
            var first = CatalogExcel.Preview(path, culture: CultureInfo.InvariantCulture);
            var second = CatalogExcel.Preview(path, culture: CultureInfo.InvariantCulture);
            Assert.AreEqual(0, first.Errors.Count, string.Join(" | ", first.Errors));
            Assert.AreEqual(first.Rows.Single().Sku, second.Rows.Single().Sku);
        }
        finally { File.Delete(path); }
    }
}
