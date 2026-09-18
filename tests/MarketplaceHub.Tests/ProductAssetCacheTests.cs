using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;
namespace MarketplaceHub.Tests;
[TestClass]public class ProductAssetCacheTests
{
 string dir=null!;static readonly byte[] Png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6u0sAAAAASUVORK5CYII=");
 [TestInitialize]public void Setup(){dir=Path.Combine(Path.GetTempPath(),"product-assets-"+Guid.NewGuid().ToString("N"));}
 [TestCleanup]public void Cleanup(){SqliteConnection.ClearAllPools();if(Directory.Exists(dir))Directory.Delete(dir,true);}
 [TestMethod]public void DeleteRemovesOnlyOwnedImageAndMediaRecords()
 {
  var store=new CatalogStore(dir);var a=store.CreateManual(new(){Sku="A",Name="A",Currency="TRY",ImageUrls="https://example.com/shared.png"});var b=store.CreateManual(new(){Sku="B",Name="B",Currency="TRY",ImageUrls=a.ImageUrls});
  var cache=new ProductAssetCache(dir);var ap=cache.StoreBytes(a.Id,a.ImageUrls,Png);var bp=cache.StoreBytes(b.Id,b.ImageUrls,Png);Assert.AreNotEqual(ap,bp);
  var external=Path.Combine(dir,"original.png");File.WriteAllBytes(external,Png);var media=new MediaStore(dir);media.Add(a.Id,new Uri(external).AbsoluteUri);media.Add(b.Id,b.ImageUrls);
  store.DeleteProduct(a);Assert.IsFalse(File.Exists(ap));Assert.IsTrue(File.Exists(bp));Assert.IsTrue(File.Exists(external));Assert.AreEqual(0,media.List(a.Id).Count);Assert.AreEqual(1,media.List(b.Id).Count);Assert.AreEqual(1,store.Products().Count);
  Assert.ThrowsException<InvalidOperationException>(()=>cache.StoreBytes(a.Id,a.ImageUrls,Png));
 }
 [TestMethod]public void InvalidBytesAndRemovedUrlCannotBeCached()
 {
  var store=new CatalogStore(dir);var a=store.CreateManual(new(){Sku="A",Name="A",Currency="TRY",ImageUrls="https://example.com/a.png"});var cache=new ProductAssetCache(dir);
  Assert.ThrowsException<InvalidOperationException>(()=>cache.StoreBytes(a.Id,a.ImageUrls,new byte[]{1,2,3}));
  Assert.ThrowsException<InvalidOperationException>(()=>cache.StoreBytes(a.Id,"https://example.com/other.png",Png));
 }

 [TestMethod]public void UndoNewExcelProductCleansOrphanedImageOnNextSweep()
 {
  var store=new CatalogStore(dir);var file=Path.Combine(dir,"new.xlsx");using(var book=new ClosedXML.Excel.XLWorkbook()){var sheet=book.AddWorksheet("Data");sheet.Cell(1,1).Value="SKU";sheet.Cell(1,2).Value="Name";sheet.Cell(1,3).Value="Image";sheet.Cell(2,1).Value="NEW";sheet.Cell(2,2).Value="New";sheet.Cell(2,3).Value="https://example.com/new.png";book.SaveAs(file);}
  var profile=new ExcelImportProfile{ImportMode=ExcelImportMode.AddOnly,ColumnLetters=new(){{"Sku","A"},{"Name","B"},{"ImageUrls","C"}}};var plan=ExcelProductImport.Preview(store,file,profile);var receipt=ExcelProductImport.Apply(store,file,profile,plan,new[]{2});
  var p=store.Products().Single();var cache=new ProductAssetCache(dir);var path=cache.StoreBytes(p.Id,p.ImageUrls,Png);store.UndoExcelImport(receipt.Id);new ProductAssetCache(dir).CleanupDeletedProducts();Assert.IsFalse(File.Exists(path));
 }
}
