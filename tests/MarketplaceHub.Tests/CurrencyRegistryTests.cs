using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1955 (active currency registry - reject unknown, never
/// silently coerce to TRY) and #1956 (FX snapshot provenance - rate date + fetchedAt
/// visible alongside the applied rate).
[TestClass]
public sealed class CurrencyRegistryTests
{
    static CatalogProduct Product(string currency) => new() { Sku = "SKU-" + currency + "-" + Guid.NewGuid().ToString("N")[..6], Name = "Ürün", Price = 10, Currency = currency };

    [TestMethod]
    public void KnownCurrenciesAreAccepted()
    {
        var root = Path.Combine(Path.GetTempPath(), "currency-" + Guid.NewGuid().ToString("N"));
        var store = new CatalogStore(root);
        try
        {
            foreach (var currency in new[] { "TRY", "USD", "EUR", "GBP" })
                store.CreateManual(Product(currency));
            Assert.AreEqual(4, store.Products().Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LowercaseCurrencyIsAcceptedAndNormalizedToUppercase()
    {
        var root = Path.Combine(Path.GetTempPath(), "currency-" + Guid.NewGuid().ToString("N"));
        var store = new CatalogStore(root);
        try
        {
            store.CreateManual(Product("usd"));
            Assert.AreEqual("USD", store.Products().Single().Currency);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnknownCurrencyIsRejectedNotSilentlyCoercedToTry()
    {
        var root = Path.Combine(Path.GetTempPath(), "currency-" + Guid.NewGuid().ToString("N"));
        var store = new CatalogStore(root);
        try
        {
            Assert.ThrowsException<InvalidOperationException>(() => store.CreateManual(Product("XYZ")));
            Assert.AreEqual(0, store.Products().Count, "A rejected currency must not be silently saved as TRY.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesNormalizedCurrency()
    {
        var root = Path.Combine(Path.GetTempPath(), "currency-" + Guid.NewGuid().ToString("N"));
        try
        {
            new CatalogStore(root).CreateManual(Product("eur"));
            var reopened = new CatalogStore(root);
            Assert.AreEqual("EUR", reopened.Products().Single().Currency);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ExcelImportRejectsUnknownCurrencyInsteadOfDefaultingToTry()
    {
        using var book = new ClosedXML.Excel.XLWorkbook();
        var sheet = book.AddWorksheet("Sheet1");
        sheet.Cell(1, 1).Value = "SKU"; sheet.Cell(1, 2).Value = "Ürün"; sheet.Cell(1, 3).Value = "Alış"; sheet.Cell(1, 4).Value = "Satış"; sheet.Cell(1, 5).Value = "Stok"; sheet.Cell(1, 6).Value = "Döviz";
        sheet.Cell(2, 1).Value = "SKU-1"; sheet.Cell(2, 2).Value = "Urun"; sheet.Cell(2, 3).Value = "5"; sheet.Cell(2, 4).Value = "10"; sheet.Cell(2, 5).Value = "1"; sheet.Cell(2, 6).Value = "XYZ";
        var path = Path.Combine(Path.GetTempPath(), "currency-excel-" + Guid.NewGuid().ToString("N") + ".xlsx");
        book.SaveAs(path);
        try
        {
            var preview = CatalogExcel.Preview(path, culture: System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(0, preview.Rows.Count, "Row with an unrecognized currency must not be silently imported.");
            Assert.AreEqual(1, preview.Errors.Count);
            StringAssert.Contains(preview.Errors[0], "XYZ");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ExcelImportDefaultsOnlyWhenCurrencyCellIsTrulyEmpty()
    {
        using var book = new ClosedXML.Excel.XLWorkbook();
        var sheet = book.AddWorksheet("Sheet1");
        sheet.Cell(1, 1).Value = "SKU"; sheet.Cell(1, 2).Value = "Ürün"; sheet.Cell(1, 3).Value = "Alış"; sheet.Cell(1, 4).Value = "Satış"; sheet.Cell(1, 5).Value = "Stok";
        sheet.Cell(2, 1).Value = "SKU-1"; sheet.Cell(2, 2).Value = "Urun"; sheet.Cell(2, 3).Value = "5"; sheet.Cell(2, 4).Value = "10"; sheet.Cell(2, 5).Value = "1";
        var path = Path.Combine(Path.GetTempPath(), "currency-excel-" + Guid.NewGuid().ToString("N") + ".xlsx");
        book.SaveAs(path);
        try
        {
            var preview = CatalogExcel.Preview(path, culture: System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(0, preview.Errors.Count, string.Join(" | ", preview.Errors));
            Assert.AreEqual("TRY", preview.Rows.Single().Currency);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void FormulaPriceCarriesFxRateDateAndFetchedAt()
    {
        var source = new XmlSource
        {
            ItemPath = "/Products/Product", Currency = "USD", CostCurrency = "TRY", PriceMode = "Formula", Formula = "x",
            AutoFx = true, TryPerTargetUnit = 32.5m, FxKind = "ForexSelling",
            FxRateDate = DateTime.UtcNow.Date, FxFetchedUtc = DateTimeOffset.UtcNow,
        };
        source.Fields["Sku"] = "Sku"; source.Fields["Name"] = "Name"; source.Fields["Cost"] = "Cost"; source.Fields["Stock"] = "Stock";
        var xml = "<Products><Product><Sku>SKU-1</Sku><Name>Urun</Name><Cost>10</Cost><Stock>1</Stock></Product></Products>";

        var rows = XmlCatalog.Preview(xml, source);
        var row = rows.Single();
        Assert.IsNotNull(row.FxRateDate);
        Assert.IsNotNull(row.FxFetchedUtc);
        Assert.AreEqual(source.FxRateDate, row.FxRateDate);
        Assert.AreEqual(source.FxFetchedUtc, row.FxFetchedUtc);
    }

    [TestMethod]
    public void ManualPriceModeDoesNotCarryFxProvenance()
    {
        var source = new XmlSource { ItemPath = "/Products/Product", Currency = "USD", PriceMode = "Simple", ExchangeRate = 1, MarkupPercent = 0 };
        source.Fields["Sku"] = "Sku"; source.Fields["Name"] = "Name"; source.Fields["Cost"] = "Cost"; source.Fields["Stock"] = "Stock";
        var xml = "<Products><Product><Sku>SKU-1</Sku><Name>Urun</Name><Cost>10</Cost><Stock>1</Stock></Product></Products>";

        var row = XmlCatalog.Preview(xml, source).Single();
        Assert.IsNull(row.FxRateDate);
        Assert.IsNull(row.FxFetchedUtc);
    }
}
