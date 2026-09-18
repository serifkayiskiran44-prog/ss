using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class ExcelWorkflowTests
{
    string directory = null!;
    CatalogStore store = null!;
    [TestInitialize] public void Setup() { directory = Path.Combine(Path.GetTempPath(), "excel-workflow-" + Guid.NewGuid().ToString("N")); store = new CatalogStore(directory); }
    [TestCleanup] public void Cleanup() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    CatalogProduct Product(string sku = "001", bool locked = false) => store.CreateManual(new CatalogProduct { Sku = sku, Name = "Original", Barcode = "BAR-" + sku, Price = 100, Stock = 10, Currency = "TRY", Cost = 50, LockStock = locked, SourceKind = "manual" });
    string Workbook(Action<IXLWorksheet> write) { var path = Path.Combine(directory, Guid.NewGuid()+".xlsx"); using var book = new XLWorkbook(); var sheet=book.AddWorksheet("Data"); write(sheet); book.SaveAs(path); return path; }
    static void Row(IXLWorksheet sheet, int row, params string[] values) { for(int i=0;i<values.Length;i++) sheet.Cell(row,i+1).Value=values[i]; }
    static ExcelImportProfile Profile(ExcelImportMode mode, params (string Field,string Column)[] columns) => new() { CultureName="tr-TR", ImportMode=mode, ColumnLetters=columns.ToDictionary(x=>x.Field,x=>x.Column) };
    static int[] Writable(ExcelProductPlan plan) => plan.Rows.Where(r=>r.Action is "CREATE" or "UPDATE").Select(r=>r.RowNumber).ToArray();

    [TestMethod] public void StockOnlyNeedsSkuAndQuantityAndUndoSurvivesRestart()
    {
        var original=Product(); var path=Workbook(s=>{Row(s,1,"Kod","Miktar"); Row(s,2,"001","15");});
        var profile=Profile(ExcelImportMode.StockOnly,("Sku","A"),("Stock","B"));
        var plan=ExcelProductImport.Preview(store,path,profile);
        Assert.AreEqual("UPDATE",plan.Rows.Single().Action); Assert.AreEqual(10,store.Products().Single().Stock);
        var receipt=ExcelProductImport.Apply(store,path,profile,plan,[2]);
        var result=store.Products().Single(); Assert.AreEqual(original.Id,result.Id); Assert.AreEqual(15,result.Stock); Assert.AreEqual(100m,result.Price); Assert.AreEqual("Original",result.Name); Assert.AreEqual("manual",result.PriceSource);
        var reopened=new CatalogStore(directory); reopened.UndoExcelImport(receipt.Id);
        Assert.AreEqual(10,reopened.Products().Single().Stock);
        Assert.ThrowsException<InvalidOperationException>(()=>reopened.UndoExcelImport(receipt.Id));
    }
    [TestMethod] public void AddStockAddsOnceAndReplayedPlanCannotWriteAgain()
    {
        Product(); var path=Workbook(s=>{Row(s,1,"Kod","Adet");Row(s,2,"001","15");});
        var profile=Profile(ExcelImportMode.StockOnly,("Sku","A"),("Stock","B"));profile.AddStock=true;
        var plan=ExcelProductImport.Preview(store,path,profile); ExcelProductImport.Apply(store,path,profile,plan,[2]);
        Assert.AreEqual(25,store.Products().Single().Stock);
        Assert.ThrowsException<InvalidOperationException>(()=>ExcelProductImport.Apply(store,path,profile,plan,[2]));
        Assert.AreEqual(25,store.Products().Single().Stock);
    }
    [TestMethod] public void PriceOnlyIgnoresUnselectedGarbageAndConvertsNetVatForMappedChannelsOnly()
    {
        var p=Product();p.ChannelPrices["Amazon"]=new(91,120,"USD");store.SaveProduct(p);
        var path=Workbook(s=>{Row(s,1,"Kod","Fiyat","Adet","Trendyol","Liste");Row(s,2,"001","125,50","bad","150","200");});
        var profile=Profile(ExcelImportMode.PriceOnly,("Sku","A"),("Price","B"),("Stock","C"),("Channel:Trendyol:Sale","D"),("Channel:Trendyol:List","E"));profile.PriceIncludesVat=false;
        var plan=ExcelProductImport.Preview(store,path,profile);Assert.AreEqual(0,plan.Errors.Count,string.Join(";",plan.Errors));
        ExcelProductImport.Apply(store,path,profile,plan,[2]);var result=store.Products().Single();
        Assert.AreEqual(150.60m,result.Price);Assert.AreEqual(180m,result.ChannelPrices["Trendyol"].SalePrice);Assert.AreEqual(240m,result.ChannelPrices["Trendyol"].ListPrice);Assert.AreEqual(91m,result.ChannelPrices["Amazon"].SalePrice);Assert.AreEqual(10,result.Stock);
    }
    [TestMethod] public void XmlLocksAndUnmappedFieldsAreRespectedInPreviewAndApply()
    {
        var p=Product();p.LockName=true;p.LockPrice=true;p.PriceSource="xml";p.StockSource="xml";p.SourceKind="xml";store.SaveProduct(p);
        var path=Workbook(s=>{Row(s,1,"SKU","Name","Price","Stock");Row(s,2,"001","Other","999","7");});
        var profile=Profile(ExcelImportMode.UpdateOnly,("Sku","A"),("Name","B"),("Price","C"),("Stock","D"));profile.SelectedFields=["Name","Price"];
        var plan=ExcelProductImport.Preview(store,path,profile);Assert.AreEqual("SKIP",plan.Rows.Single().Action);
        Assert.ThrowsException<InvalidOperationException>(()=>ExcelProductImport.Apply(store,path,profile,plan,[2]));
        Assert.AreEqual(10,store.Products().Single().Stock);Assert.AreEqual("xml",store.Products().Single().PriceSource);
    }
    [TestMethod] public void ModesSeparateCreateUpdateAndUnknownSkuSkip()
    {
        Product();var path=Workbook(s=>{Row(s,1,"Kod","İsim","Fiyat");Row(s,2,"001","Changed","125");Row(s,3,"002","New","200");});
        foreach(var (mode,first,second) in new[]{(ExcelImportMode.AddOnly,"SKIP","CREATE"),(ExcelImportMode.UpdateOnly,"UPDATE","SKIP"),(ExcelImportMode.AddAndUpdate,"UPDATE","CREATE")})
        { var profile=Profile(mode,("Sku","A"),("Name","B"),("Price","C"));var plan=ExcelProductImport.Preview(store,path,profile);Assert.AreEqual(first,plan.Rows[0].Action);Assert.AreEqual(second,plan.Rows[1].Action); }
        var both=Profile(ExcelImportMode.AddAndUpdate,("Sku","A"),("Name","B"),("Price","C"));var preview=ExcelProductImport.Preview(store,path,both);var receipt=ExcelProductImport.Apply(store,path,both,preview,[2,3]);Assert.AreEqual(2,store.Products().Count);Assert.AreEqual(200m,store.Products().Single(p=>p.Sku=="002").Price);store.UndoExcelImport(receipt.Id);Assert.AreEqual(1,store.Products().Count);Assert.AreEqual("Original",store.Products().Single().Name);
    }
    [TestMethod] public void PhysicalColumnLettersWorkWithGapsDuplicateHeadersAndAA()
    {
        Product();var path=Workbook(s=>{s.Cell(3,2).Value="same";s.Cell(3,27).Value="same";s.Cell(5,2).Value="001";s.Cell(5,27).Value=123.45;});
        var profile=Profile(ExcelImportMode.PriceOnly,("Sku","B"),("Price","AA"));profile.HeaderRow=3;
        var plan=ExcelProductImport.Preview(store,path,profile);Assert.AreEqual(5,plan.Rows.Single().RowNumber);ExcelProductImport.Apply(store,path,profile,plan,[5]);Assert.AreEqual(123.45m,store.Products().Single().Price);
    }
    [TestMethod] public void ChangedOptionsWorkbookAndCatalogInvalidatePreview()
    {
        Product();var path=Workbook(s=>{Row(s,1,"SKU","Price");Row(s,2,"001","200");});var profile=Profile(ExcelImportMode.PriceOnly,("Sku","A"),("Price","B"));
        var plan=ExcelProductImport.Preview(store,path,profile);profile.PriceIncludesVat=false;
        Assert.ThrowsException<InvalidOperationException>(()=>ExcelProductImport.Apply(store,path,profile,plan,[2]));profile.PriceIncludesVat=true;
        using(var book=new XLWorkbook(path)){book.Worksheet(1).Cell(2,2).Value="300";book.Save();}
        Assert.ThrowsException<InvalidOperationException>(()=>ExcelProductImport.Apply(store,path,profile,plan,[2]));
        plan=ExcelProductImport.Preview(store,path,profile);var p=store.Products().Single();p.Stock=8;store.SaveProduct(p);
        Assert.ThrowsException<InvalidOperationException>(()=>ExcelProductImport.Apply(store,path,profile,plan,[2]));Assert.AreEqual(100m,store.Products().Single().Price);
    }
    [TestMethod] public void ErrorsKeepActualRowNumberAndBlockEntireApply()
    {
        Product();var path=Workbook(s=>{Row(s,1,"SKU","Stock");Row(s,3,"001","12");Row(s,7,"001","-1");});var profile=Profile(ExcelImportMode.StockOnly,("Sku","A"),("Stock","B"));
        var plan=ExcelProductImport.Preview(store,path,profile);Assert.AreEqual("ERROR",plan.Rows.Single(x=>x.RowNumber==7).Action);
        Assert.ThrowsException<InvalidOperationException>(()=>ExcelProductImport.Apply(store,path,profile,plan,[3]));Assert.AreEqual(10,store.Products().Single().Stock);
    }
    [TestMethod] public void BarcodeConflictCannotRedirectSkuMatch()
    {
        Product();Product("002");var path=Workbook(s=>{Row(s,1,"SKU","Barcode","Stock");Row(s,2,"001","BAR-002","12");});var profile=Profile(ExcelImportMode.UpdateOnly,("Sku","A"),("Barcode","B"),("Stock","C"));
        var plan=ExcelProductImport.Preview(store,path,profile);Assert.AreEqual("ERROR",plan.Rows.Single().Action);Assert.AreEqual(2,store.Products().Count);
    }
    [TestMethod] public void EmptyMappedUpdateCellPreservesValueAndZeroIsApplied()
    {
        Product();var path=Workbook(s=>{Row(s,1,"SKU","Name","Stock");Row(s,2,"001","","0");});var profile=Profile(ExcelImportMode.UpdateOnly,("Sku","A"),("Name","B"),("Stock","C"));var plan=ExcelProductImport.Preview(store,path,profile);ExcelProductImport.Apply(store,path,profile,plan,[2]);Assert.AreEqual("Original",store.Products().Single().Name);Assert.AreEqual(0,store.Products().Single().Stock);
    }
    [TestMethod] public void UndoRefusesSubsequentEditsAndWrongCatalogPlan()
    {
        Product();var path=Workbook(s=>{Row(s,1,"SKU","Stock");Row(s,2,"001","15");});var profile=Profile(ExcelImportMode.StockOnly,("Sku","A"),("Stock","B"));var plan=ExcelProductImport.Preview(store,path,profile);
        var other=new CatalogStore(Path.Combine(directory,"other"));Assert.ThrowsException<InvalidOperationException>(()=>ExcelProductImport.Apply(other,path,profile,plan,[2]));
        var receipt=ExcelProductImport.Apply(store,path,profile,plan,[2]);var p=store.Products().Single();p.Name="Later";store.SaveProduct(p);Assert.ThrowsException<InvalidOperationException>(()=>store.UndoExcelImport(receipt.Id));Assert.AreEqual("Later",store.Products().Single().Name);
    }
    [TestMethod] public void DuplicateNewBarcodesAreErrorsBeforeApply()
    {
        var path=Workbook(s=>{Row(s,1,"SKU","Name","Barcode");Row(s,2,"001","One","DUP");Row(s,3,"002","Two","DUP");});var profile=Profile(ExcelImportMode.AddOnly,("Sku","A"),("Name","B"),("Barcode","C"));var plan=ExcelProductImport.Preview(store,path,profile);Assert.AreEqual("ERROR",plan.Rows[1].Action);Assert.AreEqual(0,store.Products().Count);
    }
    [TestMethod] public void ChannelOnlyPriceUpdateKeepsExistingChannelCurrencyAndListPrice()
    {
        var p=Product();p.ChannelPrices["Etsy"]=new(12,20,"USD");store.SaveProduct(p);
        var path=Workbook(s=>{Row(s,1,"SKU","Etsy");Row(s,2,"001","15");});var profile=Profile(ExcelImportMode.PriceOnly,("Sku","A"),("Channel:Etsy:Sale","B"));var plan=ExcelProductImport.Preview(store,path,profile);ExcelProductImport.Apply(store,path,profile,plan,[2]);var result=store.Products().Single();Assert.AreEqual("USD",result.ChannelPrices["Etsy"].Currency);Assert.AreEqual(20m,result.ChannelPrices["Etsy"].ListPrice);Assert.AreEqual(100m,result.Price);
    }
    [TestMethod] public void MissingPreviewAndMalformedColumnNeverWrite()
    {
        Product();var path=Workbook(s=>{Row(s,1,"SKU","Stock");Row(s,2,"001","1");});var profile=Profile(ExcelImportMode.StockOnly,("Sku","A"),("Stock","B"));Assert.ThrowsException<InvalidOperationException>(()=>ExcelProductImport.Apply(store,path,profile,null,[2]));profile.ColumnLetters["Stock"]="XFE";Assert.ThrowsException<InvalidOperationException>(()=>ExcelProductImport.Preview(store,path,profile));Assert.AreEqual(10,store.Products().Single().Stock);
    }
    [TestMethod] public void ProfileRoundTripKeepsModeLettersVatAndUnselectedFields()
    {
        var profile=Profile(ExcelImportMode.PriceOnly,("Sku","AA"),("Price","B"));profile.SelectedFields=["Price"];profile.PriceIncludesVat=false;profile.AddStock=true;new ExcelProfileStore(directory).Save(profile);var loaded=new ExcelProfileStore(directory).Find(profile.Id);Assert.AreEqual(ExcelImportMode.PriceOnly,loaded.ImportMode);Assert.AreEqual("AA",loaded.ColumnLetters["Sku"]);Assert.IsFalse(loaded.PriceIncludesVat);CollectionAssert.AreEqual(new[]{"Price"},loaded.SelectedFields);
    }


    [TestMethod] public void ExtendedFieldsUpdateAndBlankCellsPreserveValues()
    {
        var original=Product();original.XmlAttributes["Origin"]="TR";original.XmlAttributes["Weight"]="2";original.ExpiresOn=new DateTime(2027,1,1);store.SaveProduct(original);
        var path=Workbook(s=>{Row(s,1,"SKU","Desi","Origin","Weight","Expiry","CostCurrency","Subtitle2");Row(s,2,"001","3,5","","","2028-02-10","EUR","Second title");});
        var profile=Profile(ExcelImportMode.UpdateOnly,("Sku","A"),("Desi","B"),("Origin","C"),("Weight","D"),("ExpiresOn","E"),("CostCurrency","F"),("Subtitle2","G"));
        var plan=ExcelProductImport.Preview(store,path,profile);Assert.AreEqual(0,plan.Errors.Count);var receipt=ExcelProductImport.Apply(store,path,profile,plan,[2]);var result=store.Products().Single();
        Assert.AreEqual("3.5",result.XmlAttributes["Desi"]);Assert.AreEqual("TR",result.XmlAttributes["Origin"]);Assert.AreEqual("2",result.XmlAttributes["Weight"]);Assert.AreEqual(new DateTime(2028,2,10),result.ExpiresOn);Assert.AreEqual("EUR",result.CostCurrency);Assert.AreEqual("Second title",result.XmlAttributes["Subtitle2"]);
        store.UndoExcelImport(receipt.Id);Assert.AreEqual(new DateTime(2027,1,1),store.Products().Single().ExpiresOn);Assert.IsFalse(store.Products().Single().XmlAttributes.ContainsKey("Desi"));
    }
    [TestMethod] public void ExtendedFieldsRejectInvalidMeasurementsAndRespectPriceLock()
    {
        var original=Product();original.LockPrice=true;store.SaveProduct(original);
        var path=Workbook(s=>{Row(s,1,"SKU","Currency","Weight");Row(s,2,"001","INVALID","-1");});var profile=Profile(ExcelImportMode.UpdateOnly,("Sku","A"),("CostCurrency","B"),("Weight","C"));
        var plan=ExcelProductImport.Preview(store,path,profile);Assert.AreEqual("ERROR",plan.Rows.Single().Action);Assert.ThrowsException<InvalidOperationException>(()=>ExcelProductImport.Apply(store,path,profile,plan,[2]));
        var good=Workbook(s=>{Row(s,1,"SKU","Currency","Weight");Row(s,2,"001","INVALID","0");});plan=ExcelProductImport.Preview(store,good,profile);ExcelProductImport.Apply(store,good,profile,plan,[2]);Assert.AreEqual("TRY",store.Products().Single().CostCurrency);
    }

    [TestMethod] public void SeparateImageAndCategoryColumnsPreserveUnmappedValuesAndImageLock()
    {
        var original=Product();original.ImageUrls="https://example.com/1.jpg | https://example.com/2.jpg";original.Category="Clothes > Men > Shirts";store.SaveProduct(original);
        var path=Workbook(s=>{Row(s,1,"SKU","Image1","Image2","Category2");Row(s,2,"001","","https://example.com/new.jpg","Women");});
        var profile=Profile(ExcelImportMode.UpdateOnly,("Sku","A"),("Image1","B"),("Image2","C"),("Category2","D"));var plan=ExcelProductImport.Preview(store,path,profile);ExcelProductImport.Apply(store,path,profile,plan,[2]);
        var result=store.Products().Single();Assert.AreEqual("https://example.com/1.jpg | https://example.com/new.jpg",result.ImageUrls);Assert.AreEqual("Clothes > Women > Shirts",result.Category);
        result.LockImages=true;store.SaveProduct(result);var locked=Workbook(s=>{Row(s,1,"SKU","Image1");Row(s,2,"001","invalid");});profile=Profile(ExcelImportMode.UpdateOnly,("Sku","A"),("Image1","B"));plan=ExcelProductImport.Preview(store,locked,profile);Assert.AreEqual("SKIP",plan.Rows.Single().Action);
    }
}
