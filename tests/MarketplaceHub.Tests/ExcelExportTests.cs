using ClosedXML.Excel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1907 (typed Excel cells) and #1908 (deterministic export
/// column schema/version).
[TestClass]
public sealed class ExcelExportTests
{
    static string TempPath() => Path.Combine(Path.GetTempPath(), "excel-export-" + Guid.NewGuid().ToString("N") + ".xlsx");

    static CatalogProduct Product() => new()
    {
        Sku = "00123", Barcode = "00045", Name = "Ürün", Brand = "Marka", Category = "Kategori",
        Cost = 5.5m, Price = 12.75m, Currency = "USD", VatRate = 18m, Stock = 7, Active = true, Gtin = "0012345678905",
    };

    [TestMethod]
    public void NumericAndBoolCellsAreTypedNotText()
    {
        var path = TempPath();
        try
        {
            CatalogExcel.Export(path, [Product()]);
            using var book = new XLWorkbook(path);
            var sheet = book.Worksheets.First();
            var header = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
            int Col(string name) => header.IndexOf(name) + 1;

            Assert.AreEqual(XLDataType.Number, sheet.Cell(2, Col("Alış")).DataType);
            Assert.AreEqual(12.75, sheet.Cell(2, Col("Satış")).GetDouble());
            Assert.AreEqual(XLDataType.Number, sheet.Cell(2, Col("Stok")).DataType);
            Assert.AreEqual(7, sheet.Cell(2, Col("Stok")).GetValue<int>());
            Assert.AreEqual(XLDataType.Boolean, sheet.Cell(2, Col("Aktif")).DataType);
            Assert.IsTrue(sheet.Cell(2, Col("Aktif")).GetBoolean());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void LeadingZeroSkuAndBarcodeSurviveReopenAsExactText()
    {
        var path = TempPath();
        try
        {
            CatalogExcel.Export(path, [Product()]);
            using var book = new XLWorkbook(path);
            var sheet = book.Worksheets.First();
            var header = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
            int Col(string name) => header.IndexOf(name) + 1;

            Assert.AreEqual("00123", sheet.Cell(2, Col("SKU")).GetString());
            Assert.AreEqual("00045", sheet.Cell(2, Col("Barkod")).GetString());
            Assert.AreEqual(XLDataType.Text, sheet.Cell(2, Col("SKU")).DataType);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void FormulaLikeTextValueIsNotStoredAsAFormula()
    {
        var path = TempPath();
        try
        {
            var product = Product(); product.Name = "=1+1"; product.Description = "@SUM(A1:A2)";
            CatalogExcel.Export(path, [product]);
            using var book = new XLWorkbook(path);
            var sheet = book.Worksheets.First();
            var header = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
            int Col(string name) => header.IndexOf(name) + 1;

            var nameCell = sheet.Cell(2, Col("Ürün"));
            Assert.IsFalse(nameCell.HasFormula, "A product name starting with '=' must never become a live formula.");
            Assert.AreEqual("=1+1", nameCell.GetString());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void EmptyValuesExportAsBlankOrZeroNotTheWordNull()
    {
        var path = TempPath();
        try
        {
            var product = Product(); product.Description = ""; product.Gtin = "";
            CatalogExcel.Export(path, [product]);
            using var book = new XLWorkbook(path);
            var sheet = book.Worksheets.First();
            var header = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
            int Col(string name) => header.IndexOf(name) + 1;
            Assert.AreEqual("", sheet.Cell(2, Col("Açıklama")).GetString());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ExportSurvivesReopenAcrossCultures()
    {
        var path = TempPath();
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            CatalogExcel.Export(path, [Product()]);
            using var book = new XLWorkbook(path);
            var sheet = book.Worksheets.First();
            var header = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
            int Col(string name) => header.IndexOf(name) + 1;
            Assert.AreEqual(12.75, sheet.Cell(2, Col("Satış")).GetDouble());
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = originalCulture; File.Delete(path); }
    }

    [TestMethod]
    public void EmptyProductListStillProducesAValidWorkbookWithHeaders()
    {
        var path = TempPath();
        try
        {
            CatalogExcel.Export(path, []);
            using var book = new XLWorkbook(path);
            var sheet = book.Worksheets.First();
            Assert.IsTrue(sheet.Row(1).CellsUsed().Any());
            Assert.IsFalse(sheet.Row(2).CellsUsed().Any());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ColumnKeyOrderIsStableAndVersionIsRecordedInTheWorkbook()
    {
        var path = TempPath();
        try
        {
            CatalogExcel.Export(path, [Product()]);
            using var book = new XLWorkbook(path);
            StringAssert.Contains(book.Properties.Comments, $"v{CatalogExcel.ExportSchemaVersion}");
            var sheet = book.Worksheets.First();
            var headers = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
            var expectedHeaders = CatalogExcel.ExportColumnKeys.Select(key => key switch
            {
                "Sku" => "SKU", "Barcode" => "Barkod", "Name" => "Ürün", "Brand" => "Marka", "Category" => "Kategori",
                "Description" => "Açıklama", "Cost" => "Alış", "Price" => "Satış", "Currency" => "Döviz", "VatRate" => "KDV %",
                "Stock" => "Stok", "Active" => "Aktif", "Gtin" => "GTIN", "ImageUrls" => "Görseller", "SourceId" => "XML Kaynağı",
                "SourceKind" => "Veri kaynağı", "PriceSource" => "Fiyat kaynağı", "StockSource" => "Stok kaynağı", "MediaSource" => "Medya kaynağı",
                _ => key,
            }).ToList();
            CollectionAssert.AreEqual(expectedHeaders, headers);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void OnlyPermittedFieldsAreWrittenWhenVisibleFieldsIsRestricted()
    {
        var path = TempPath();
        try
        {
            CatalogExcel.Export(path, [Product()], ["Sku", "Price"]);
            using var book = new XLWorkbook(path);
            var sheet = book.Worksheets.First();
            var headers = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
            CollectionAssert.AreEqual(new[] { "SKU", "Satış" }, headers);
        }
        finally { File.Delete(path); }
    }
}
