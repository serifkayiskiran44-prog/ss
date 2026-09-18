using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
namespace MarketplaceHub.Tests;
[TestClass]
public sealed class ExcelAuxiliaryWorkflowTests
{
    string directory=null!;
    [TestInitialize]public void Setup(){directory=Path.Combine(Path.GetTempPath(),"excel-aux-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);}
    [TestCleanup]public void Cleanup(){SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
    string Book(Action<IXLWorksheet> write){var path=Path.Combine(directory,"data.xlsx");using var book=new XLWorkbook();var sheet=book.AddWorksheet("Data");write(sheet);book.SaveAs(path);return path;}
    static void Row(IXLWorksheet sheet,int row,params string[] values){for(int i=0;i<values.Length;i++)sheet.Cell(row,i+1).Value=values[i];}
    [TestMethod]public void CategoryTreeCreatesParentsAtomicallyAndUndoRemovesOnlyImportedEntries()
    {
        var store=new TaxonomyStore(directory);store.Save(new TaxonomyEntry{Kind=TaxonomyKind.Category,Name="Existing"});
        var path=Book(s=>{Row(s,1,"Kategori");Row(s,2,"Giyim > Erkek > Tişört");Row(s,3,"Giyim > Erkek > Pantolon");});
        var profile=new ExcelImportProfile{ColumnLetters=new(){{"Category","A"}}};
        var plan=ExcelCategoryImport.Preview(store,path,profile);Assert.AreEqual(1,store.List(TaxonomyKind.Category).Count);
        var receipt=ExcelCategoryImport.Apply(store,path,profile,plan,[2,3]);Assert.AreEqual(5,store.List(TaxonomyKind.Category).Count);Assert.IsTrue(store.List(TaxonomyKind.Category).Any(x=>x.Name=="Giyim > Erkek"));
        store.UndoExcelCategories(receipt);Assert.AreEqual("Existing",store.List(TaxonomyKind.Category).Single().Name);
    }
    [TestMethod]public void InvalidCategoryAndChangedTaxonomyCannotPartiallyWrite()
    {
        var store=new TaxonomyStore(directory);var path=Book(s=>{Row(s,1,"Kategori");Row(s,2,"Giyim > > Tişört");Row(s,3,"Valid");});var profile=new ExcelImportProfile{ColumnLetters=new(){{"Category","A"}}};
        var plan=ExcelCategoryImport.Preview(store,path,profile);Assert.AreEqual("ERROR",plan.Rows[0].Action);Assert.ThrowsException<InvalidOperationException>(()=>ExcelCategoryImport.Apply(store,path,profile,plan,[3]));Assert.AreEqual(0,store.List(TaxonomyKind.Category).Count);
    }
    [TestMethod]public void OrderRowsGroupByOrderIdPreserveAddressAndDoNotChangeCatalogStock()
    {
        var catalog=new CatalogStore(directory);catalog.CreateManual(new CatalogProduct{Sku="001",Name="Product",Stock=10,Currency="TRY"});var store=new OrdersStore(directory);
        var path=Book(s=>{Row(s,1,"Sipariş","Kod","Ad","Adet","Fiyat","Alıcı","Adres");Row(s,2,"ORD-1","001","Product","2","100","Test Person","Test Street");Row(s,3,"ORD-1","002","Second","1","50","Test Person","Test Street");});
        var profile=new ExcelImportProfile{CultureName="tr-TR",PriceIncludesVat=false,ColumnLetters=new(){{"OrderId","A"},{"Sku","B"},{"Name","C"},{"Quantity","D"},{"UnitPrice","E"},{"CustomerName","F"},{"BillingAddress","G"}}};
        var plan=ExcelOrderImport.Preview(store,path,profile,"manual","test-shop");Assert.AreEqual(0,store.ReadAll().Count);var receipt=ExcelOrderImport.Apply(store,path,profile,plan,[2,3],"manual","test-shop");
        var order=store.ReadAll().Single();Assert.AreEqual(2,order.Items.Count);Assert.AreEqual(300m,order.Total);Assert.AreEqual("Test Street",order.BillingAddress);Assert.AreEqual(10,catalog.Products().Single().Stock);
        new OrdersStore(directory).UndoExcelOrders(receipt);Assert.AreEqual(0,store.ReadAll().Count);
    }
    [TestMethod]public void OrderImportRejectsPartialOrderSelectionAndChangedShop()
    {
        var store=new OrdersStore(directory);var path=Book(s=>{Row(s,1,"Order","SKU","Title","Qty","Price");Row(s,2,"ORD-1","001","One","1","10");Row(s,3,"ORD-1","002","Two","2","20");});
        var profile=new ExcelImportProfile{CultureName="tr-TR",ColumnLetters=new(){{"OrderId","A"},{"Sku","B"},{"Name","C"},{"Quantity","D"},{"UnitPrice","E"}}};
        var plan=ExcelOrderImport.Preview(store,path,profile,"manual","shop");Assert.ThrowsException<InvalidOperationException>(()=>ExcelOrderImport.Apply(store,path,profile,plan,[2],"manual","shop"));Assert.ThrowsException<InvalidOperationException>(()=>ExcelOrderImport.Apply(store,path,profile,plan,[2,3],"manual","other"));Assert.AreEqual(0,store.ReadAll().Count);
    }
    [TestMethod]public void UnselectedOrderFieldsAndExistingOtherItemsArePreserved()
    {
        var store=new OrdersStore(directory);store.SaveManual(new OrderSnapshot{Marketplace="manual",ShopId="shop",OrderId="ORD-1",CustomerName="Keep",Currency="TRY",Total=20,Items=new(){new(){Sku="001",Title="One",Quantity=1,UnitPrice=10},new(){Sku="002",Title="Two",Quantity=1,UnitPrice=10}}});
        var path=Book(s=>{Row(s,1,"Order","SKU","Qty","Customer");Row(s,2,"ORD-1","001","2","Overwrite");});var profile=new ExcelImportProfile{ColumnLetters=new(){{"OrderId","A"},{"Sku","B"},{"Quantity","C"},{"CustomerName","D"}},SelectedFields=new(){"Quantity"}};
        var plan=ExcelOrderImport.Preview(store,path,profile,"manual","shop");ExcelOrderImport.Apply(store,path,profile,plan,[2],"manual","shop");var order=store.ReadAll().Single();Assert.AreEqual(20m,order.Total);Assert.AreEqual("Keep",order.CustomerName);Assert.AreEqual(2,order.Items.Count);Assert.AreEqual(2,order.Items.Single(i=>i.Sku=="001").Quantity);Assert.AreEqual(1,order.Items.Single(i=>i.Sku=="002").Quantity);
    }

    [TestMethod]public void MixedVatOrderUsesAllItemRatesForNetTotal()
    {
        var store=new OrdersStore(directory);var path=Book(s=>{Row(s,1,"Order","SKU","Qty","Price","VAT","Total");Row(s,2,"MIX","001","1","100","10","200");Row(s,3,"MIX","002","1","100","20","200");});
        var profile=new ExcelImportProfile{PriceIncludesVat=false,ColumnLetters=new(){{"OrderId","A"},{"Sku","B"},{"Quantity","C"},{"UnitPrice","D"},{"VatRate","E"},{"Total","F"},{"Name","B"}}};
        var plan=ExcelOrderImport.Preview(store,path,profile,"manual","shop");Assert.IsFalse(plan.Rows.Any(r=>r.Action=="ERROR"),string.Join("; ",plan.Rows.Select(r=>r.Note)));ExcelOrderImport.Apply(store,path,profile,plan,[2,3],"manual","shop");Assert.AreEqual(230m,store.ReadAll().Single().Total);
    }

    [TestMethod]public void ExistingOrderBlankQuantityPreservesCountWhileAddressUpdates()
    {
        var store=new OrdersStore(directory);store.SaveManual(new OrderSnapshot{Marketplace="manual",ShopId="shop",OrderId="ORD-1",Currency="TRY",Total=20,Items=new(){new(){Sku="001",Title="One",Quantity=2,UnitPrice=10}}});
        var path=Book(s=>{Row(s,1,"Order","SKU","Qty","Address");Row(s,2,"ORD-1","001","","New address");});var profile=new ExcelImportProfile{ColumnLetters=new(){{"OrderId","A"},{"Sku","B"},{"Quantity","C"},{"BillingAddress","D"}}};
        var plan=ExcelOrderImport.Preview(store,path,profile,"manual","shop");Assert.AreEqual(0,plan.Errors.Count);ExcelOrderImport.Apply(store,path,profile,plan,[2],"manual","shop");Assert.AreEqual(2,store.ReadAll().Single().Items.Single().Quantity);Assert.AreEqual("New address",store.ReadAll().Single().BillingAddress);
        profile.ColumnLetters.Remove("Quantity");plan=ExcelOrderImport.Preview(store,path,profile,"manual","shop");Assert.AreEqual("SKIP",plan.Rows.Single().Action);
    }
}
