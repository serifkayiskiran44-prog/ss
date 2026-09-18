using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

namespace MarketplaceHub.Tests;
[TestClass]
public class TrendyolProductMatchingReviewTests
{
    string directory=null!;CatalogStore catalog=null!;TrendyolWorkspaceStore store=null!;
    [TestInitialize] public void Setup(){directory=Path.Combine(Path.GetTempPath(),"trendyol-match-review-"+Guid.NewGuid().ToString("N"));catalog=new(directory);store=new(directory);catalog.CreateManual(new(){Sku="LOCAL-A",Name="A",Gtin="REMOTE-A",Currency="TRY"});catalog.CreateManual(new(){Sku="LOCAL-B",Name="B",Currency="TRY"});var state=store.Load("10");state.Products.Add(new("REMOTE-A","OLD-A","Remote A",1,1,10m,10m,true));state.Products.Add(new("REMOTE-B","OLD-B","Remote B",2,1,10m,10m,true));state.ProductsUpdatedUtc=DateTime.UtcNow;store.Save(state);}
    [TestCleanup] public void Cleanup(){SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
    TrendyolProductMatchChoice Choice(string sku,string barcode){var product=catalog.Products().Single(p=>p.Sku==sku);return new(product.Id,TrendyolWorkspaceStore.Hash(JsonSerializer.Serialize(product)),barcode);}
    [TestMethod] public void ReviewedMatchesPreserveLocalIdentityAndExistingProfileFields()
    {
        var state=store.Load("10");var id=catalog.Products().Single(p=>p.Sku=="LOCAL-A").Id;state.Profiles.Add(new(){ProductId=id,Title="Custom title",Origin="TR"});store.Save(state);state=store.Load("10");
        store.ApplyProductMatches("10",state.Revision,[Choice("LOCAL-A","REMOTE-A")]);
        var profile=store.Load("10").Profiles.Single();Assert.AreEqual("REMOTE-A",profile.IntegrationCode);Assert.AreEqual("Custom title",profile.Title);Assert.AreEqual("TR",profile.Origin);Assert.AreEqual("",catalog.Products().Single(p=>p.Id==id).Barcode);
    }
    [TestMethod] public void EmptyReviewDoesNotWriteARevision()
    {
        var state=store.Load("10");Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyProductMatches("10",state.Revision,[]));Assert.AreEqual(state.Revision,store.Load("10").Revision);
    }
    [TestMethod] public void DuplicateRemoteAndUnknownRemoteRejectEntireReview()
    {
        var state=store.Load("10");Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyProductMatches("10",state.Revision,[Choice("LOCAL-A","REMOTE-A"),Choice("LOCAL-B","REMOTE-A")]));
        Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyProductMatches("10",state.Revision,[Choice("LOCAL-A","REMOTE-A"),Choice("LOCAL-B","MISSING")]));Assert.AreEqual(0,store.Load("10").Profiles.Count);
        Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyProductMatches("10",state.Revision,[Choice("LOCAL-A","NEW") with{CreateIfMissing=true},Choice("LOCAL-B","NEW") with{CreateIfMissing=true}]));Assert.AreEqual(state.Revision,store.Load("10").Revision);
    }
    [TestMethod] public void ChangedCatalogOrWorkspaceCannotApplyOldReview()
    {
        var state=store.Load("10");var choice=Choice("LOCAL-A","REMOTE-A");var product=catalog.Products().Single(p=>p.Sku=="LOCAL-A");product.Gtin="CHANGED";catalog.SaveProduct(product);
        Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyProductMatches("10",state.Revision,[choice]));
        choice=Choice("LOCAL-A","REMOTE-A");store.Save(state);Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyProductMatches("10",state.Revision,[choice]));Assert.AreEqual(0,store.Load("10").Profiles.Count);
    }
    [TestMethod] public void BarcodeAssignedOutsideReviewCannotBeTaken()
    {
        var state=store.Load("10");state.Profiles.Add(new(){ProductId=catalog.Products().Single(p=>p.Sku=="LOCAL-B").Id,IntegrationCode="REMOTE-A"});store.Save(state);state=store.Load("10");
        Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyProductMatches("10",state.Revision,[Choice("LOCAL-A","REMOTE-A")]));Assert.AreEqual(1,store.Load("10").Profiles.Count);
    }
    [TestMethod] public void ExplicitMissingBarcodePreparesLocalProfileWithoutCreatingAReceipt()
    {
        var state=store.Load("10");var choice=Choice("LOCAL-A","NEW-123") with{CreateIfMissing=true};
        store.ApplyProductMatches("10",state.Revision,[choice]);
        Assert.AreEqual("NEW-123",store.Load("10").Profiles.Single().IntegrationCode);Assert.AreEqual(0,store.Receipts("10").Count);
        Assert.AreEqual("",catalog.Products().Single(p=>p.Sku=="LOCAL-A").Barcode);
        var preview=store.Preview(new("10","key","secret","10 - Self Integration"),[choice.ProductId],TrendyolOperation.Create);
        Assert.AreEqual("NEW-123",preview.Rows.Single().Barcode);Assert.AreEqual("Hatalı",preview.Rows.Single().Status,"Missing required product data must still prevent sending.");
        var product=catalog.Products().Single(p=>p.Id==choice.ProductId);product.Price=12;product.Description="Test product";product.ImageUrls="https://example.test/product.jpg";catalog.SaveProduct(product);
        state=store.Load("10");state.DictionaryUpdatedUtc=DateTime.UtcNow;state.Categories.Add(new(1,"Category","Category",true));state.Brands.Add(new(2,"Brand"));state.Attributes[1]=[];state.AttributesUpdatedUtc[1]=DateTime.UtcNow;state.Profiles.Single().CategoryId=1;state.Profiles.Single().BrandId=2;state.Profiles.Single().Origin="TR";store.Save(state);
        var account=new TrMarketplaceHubDesktop.TrendyolSettings("10","key","secret","10 - Self Integration");preview=store.Preview(account,[choice.ProductId],TrendyolOperation.Create);
        Assert.AreEqual("Eklenecek",preview.Rows.Single().Status,preview.Rows.Single().Detail);Assert.AreEqual("NEW-123",JsonDocument.Parse(preview.PayloadJson).RootElement.GetProperty("items")[0].GetProperty("barcode").GetString());
        Assert.ThrowsException<InvalidOperationException>(()=>store.Claim(preview.Id,account,false));Assert.AreEqual(0,store.Receipts("10").Count);
    }
    [TestMethod] public void NewBarcodeCannotUseExistingRemoteOrStaleCatalogCache()
    {
        var state=store.Load("10");Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyProductMatches("10",state.Revision,[Choice("LOCAL-A","REMOTE-A") with{CreateIfMissing=true}]));
        state.ProductsUpdatedUtc=DateTime.UtcNow.AddDays(-2);store.Save(state);state=store.Load("10");
        Assert.ThrowsException<InvalidOperationException>(()=>store.ApplyProductMatches("10",state.Revision,[Choice("LOCAL-A","NEW") with{CreateIfMissing=true}]));Assert.AreEqual(0,store.Load("10").Profiles.Count);
    }
}
