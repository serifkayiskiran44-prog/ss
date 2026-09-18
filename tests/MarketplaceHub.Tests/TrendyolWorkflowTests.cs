using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

namespace MarketplaceHub.Tests;
[TestClass]
public class TrendyolWorkflowTests
{
    string directory = null!;
    TrendyolWorkspaceStore store = null!;
    readonly TrendyolSettings account = new("10", "test-key", "test-secret", "10 - Self Integration");
    CatalogProduct product = null!;
    [TestInitialize] public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "trendyol-flow-" + Guid.NewGuid().ToString("N")); store = new(directory);
        product = new() { Sku="SKU",Name="Test",Description="Açıklama",Stock=5,Price=120,Currency="TRY",ImageUrls="https://example.test/a.jpg",UpdatedUtc=DateTime.UtcNow };
        using(var c=new SqliteConnection("Data Source="+Path.Combine(directory,"catalog.db"))) { c.Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO CatalogProducts VALUES($id,$json)";cmd.Parameters.AddWithValue("$id",product.Id);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(product));cmd.ExecuteNonQuery(); }
        var state=store.Load("10"); state.ProductsUpdatedUtc=DateTime.UtcNow;state.Products.Add(new("REMOTE","SKU","Remote",1,2,100m,100m,true));
        state.Profiles.Add(new(){ProductId=product.Id,IntegrationCode="REMOTE"});store.Save(state);
    }
    [TestCleanup] public void Cleanup(){SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
    [TestMethod] public void PriceOnlyNeverSendsStockContentOrImages()
    {
        var plan=store.Preview(account,[product.Id],TrendyolOperation.Price);
        var item=JsonDocument.Parse(plan.PayloadJson).RootElement.GetProperty("items")[0];
        Assert.AreEqual(120m,item.GetProperty("salePrice").GetDecimal());
        Assert.IsFalse(item.TryGetProperty("quantity",out _));Assert.IsFalse(item.TryGetProperty("description",out _));Assert.IsFalse(item.TryGetProperty("images",out _));
        Assert.AreEqual("",new CatalogStore(directory).Products().Single().Barcode);
    }
    [TestMethod] public void StockOnlyDoesNotRequirePriceOrTaxonomy()
    {
        var plan=store.Preview(account,[product.Id],TrendyolOperation.Stock);
        var item=JsonDocument.Parse(plan.PayloadJson).RootElement.GetProperty("items")[0];
        Assert.AreEqual(5,item.GetProperty("quantity").GetInt32());Assert.IsFalse(item.TryGetProperty("salePrice",out _));
    }
    [TestMethod] public void SendRequiresApprovalAndRejectsWrongAccountStaleConfigAndReplay()
    {
        var plan=store.Preview(account,[product.Id],TrendyolOperation.Stock);
        Assert.ThrowsException<InvalidOperationException>(()=>store.Claim(plan.Id,account,false));
        Assert.ThrowsException<InvalidOperationException>(()=>store.Claim(plan.Id,account with {ApiKey="other"},true));
        store.Claim(plan.Id,account,true);
        Assert.ThrowsException<InvalidOperationException>(()=>store.Claim(plan.Id,account,true));
        Assert.AreEqual("Sonuç bekleniyor",store.Receipts("10").Single().Status);
        var next=store.Preview(account,[product.Id],TrendyolOperation.Price);var state=store.Load("10");store.Save(state);
        Assert.ThrowsException<InvalidOperationException>(()=>store.Claim(next.Id,account,true));
    }
    [TestMethod] public void CatalogChangesAfterPreviewAreRejectedBeforeClaim()
    {
        var plan=store.Preview(account,[product.Id],TrendyolOperation.Stock);
        using var c=new SqliteConnection("Data Source="+Path.Combine(directory,"catalog.db"));c.Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE CatalogProducts SET Json=json_set(Json,'$.Stock',99)";cmd.ExecuteNonQuery();
        Assert.ThrowsException<InvalidOperationException>(()=>store.Claim(plan.Id,account,true));
        Assert.AreEqual(0,store.Receipts("10").Count);
    }
    [TestMethod] public void CreateRequiresRemoteBarcodeOriginAndRequiredAttributes()
    {
        var state=store.Load("10");state.Products.Clear();state.DictionaryUpdatedUtc=DateTime.UtcNow;state.Categories.Add(new(1,"Kategori","Kategori",true));state.Brands.Add(new(2,"Marka"));
        state.Attributes[1]=[new(3,"Renk",true,false,false,[new(4,"Siyah")])];state.AttributesUpdatedUtc[1]=DateTime.UtcNow;
        var profile=state.Profiles.Single();profile.CategoryId=1;profile.BrandId=2;profile.IntegrationCode="";store.Save(state);
        Assert.AreEqual("Hatalı",store.Preview(account,[product.Id],TrendyolOperation.Create).Rows.Single().Status);
        state=store.Load("10");profile=state.Profiles.Single();profile.IntegrationCode="NEW";profile.Origin="TR";profile.Attributes.Add(new(3,[4]));store.Save(state);
        var plan=store.Preview(account,[product.Id],TrendyolOperation.Create);Assert.AreEqual("Eklenecek",plan.Rows.Single().Status);
        Assert.AreEqual("NEW",JsonDocument.Parse(plan.PayloadJson).RootElement.GetProperty("items")[0].GetProperty("barcode").GetString());
    }
    [TestMethod] public void DeliveryUsesNewPluralEnvelopeAndNoDeprecatedFastType()
    {
        var state=store.Load("10");state.Templates.Add(new("t","Bugün","",0,null,null));state.Profiles.Single().DeliveryTemplateId="t";store.Save(state);
        var plan=store.Preview(account,[product.Id],TrendyolOperation.Delivery);var item=JsonDocument.Parse(plan.PayloadJson).RootElement.GetProperty("items")[0];
        Assert.AreEqual(0,item.GetProperty("deliveryOptions").GetProperty("deliveryDuration").GetInt32());Assert.IsFalse(plan.PayloadJson.Contains("fastDeliveryType"));
    }
    [TestMethod] public void CompetitionNeverRepricesAgainstOwnFirstPositionAndHonorsBounds()
    {
        Assert.IsNull(TrendyolCompetition.Suggest(new("B",1,100,null,null,false),90,200,1).Price);
        Assert.AreEqual(90m,TrendyolCompetition.Suggest(new("B",2,89,150,null,true),90,200,1).Price);
        Assert.IsNull(TrendyolCompetition.Suggest(new("B",2,100,null,null,true),200,90,1).Price);
        Assert.IsNull(TrendyolCompetition.Suggest(new("B",2,110,null,null,true),90,100.005m,1).Price);
    }
    [TestMethod] public void ApprovedContentSendsOnlyExplicitProfileFieldsAndHonorsLocks()
    {
        var state=store.Load("10");state.Profiles.Single().Description="Yeni açıklama";store.Save(state);
        var plan=store.Preview(account,[product.Id],TrendyolOperation.Content);var item=JsonDocument.Parse(plan.PayloadJson).RootElement.GetProperty("items")[0];
        Assert.AreEqual(1,item.GetProperty("contentId").GetInt32());Assert.AreEqual("Yeni açıklama",item.GetProperty("description").GetString());Assert.IsFalse(item.TryGetProperty("title",out _));Assert.IsFalse(item.TryGetProperty("attributes",out _));
        using var c=new SqliteConnection("Data Source="+Path.Combine(directory,"catalog.db"));c.Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE CatalogProducts SET Json=json_set(Json,'$.LockDescription',json('true'))";cmd.ExecuteNonQuery();
        Assert.AreEqual("Hatalı",store.Preview(account,[product.Id],TrendyolOperation.Content).Rows.Single().Status);
    }
    [TestMethod] public void RejectedProductCanBeCorrectedWithoutChangingItsPriceOrStock()
    {
        var state=store.Load("10");state.Products[0]=state.Products[0] with{Approved=false};state.DictionaryUpdatedUtc=DateTime.UtcNow;state.Categories.Add(new(1,"Kategori","Kategori",true));state.Brands.Add(new(2,"Marka"));state.Attributes[1]=[];state.AttributesUpdatedUtc[1]=DateTime.UtcNow;var profile=state.Profiles.Single();profile.CategoryId=1;profile.BrandId=2;profile.Origin="TR";store.Save(state);
        var plan=store.Preview(account,[product.Id],TrendyolOperation.UpdateUnapproved);Assert.AreEqual("Güncellenecek",plan.Rows.Single().Status);var item=JsonDocument.Parse(plan.PayloadJson).RootElement.GetProperty("items")[0];Assert.IsFalse(item.TryGetProperty("quantity",out _));Assert.IsFalse(item.TryGetProperty("salePrice",out _));Assert.AreEqual("REMOTE",item.GetProperty("barcode").GetString());
    }
    sealed class OfflineHandler:HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;throw new HttpRequestException("Offline simulated outcome");}
    }
    sealed class AcceptedHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"batchRequestId\":\"batch-test\"}")});
    }
    [DataTestMethod]
    [DataRow(TrendyolOperation.Content)]
    [DataRow(TrendyolOperation.ShippingDetails)]
    public async Task ApprovedUpdateBatchReadsNestedUpdateRequest(TrendyolOperation operation)
    {
        var state=store.Load("10");state.Profiles.Single().Description="Updated";state.Profiles.Single().DeliveryTemplateId="t";state.Templates.Add(new("t","Carrier","TEST",null,null,null));state.Carriers.Add(new("TEST","Carrier"));state.AddressesUpdatedUtc=DateTime.UtcNow;store.Save(state);
        var plan=store.Preview(account,[product.Id],operation);using var http=new HttpClient(new AcceptedHandler());using var client=new TrendyolApiClient(account,http);await store.SendAsync(plan.Id,account,true,client);
        var identity=operation==TrendyolOperation.Content?"\"contentId\":1":"\"barcode\":\"REMOTE\"";
        var reply=JsonDocument.Parse("{\"batchRequestId\":\"batch-test\",\"status\":\"COMPLETED\",\"items\":[{\"requestItem\":{\"updateRequest\":{"+identity+"}},\"status\":\"SUCCESS\"}]}");
        Assert.AreEqual("İşlem tamamlandı",store.UpdateBatch(plan.Id,"10",reply.RootElement).Status);
    }
    [TestMethod] public async Task BatchCannotCompleteWithAnotherProductOrOmittedRows()
    {
        var state=store.Load("10");var second=JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(product))!;second.Id=Guid.NewGuid().ToString("N");second.Sku="SKU2";
        using(var c=new SqliteConnection("Data Source="+Path.Combine(directory,"catalog.db"))){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO CatalogProducts VALUES($id,$json)";cmd.Parameters.AddWithValue("$id",second.Id);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(second));cmd.ExecuteNonQuery();}
        state.Products.Add(new("SECOND","SKU2","Second",2,2,100,100,true));state.Profiles.Add(new(){ProductId=second.Id,IntegrationCode="SECOND"});store.Save(state);
        var plan=store.Preview(account,[product.Id,second.Id],TrendyolOperation.Stock);using var http=new HttpClient(new AcceptedHandler());using var client=new TrendyolApiClient(account,http);await store.SendAsync(plan.Id,account,true,client);
        var incomplete=JsonDocument.Parse("{\"batchRequestId\":\"batch-test\",\"items\":[{\"requestItem\":{\"barcode\":\"REMOTE\"},\"status\":\"SUCCESS\"}]}");
        Assert.AreEqual("İşleniyor",store.UpdateBatch(plan.Id,"10",incomplete.RootElement).Status);
        var wrong=JsonDocument.Parse("{\"batchRequestId\":\"batch-test\",\"items\":[{\"requestItem\":{\"barcode\":\"WRONG\"},\"status\":\"SUCCESS\"},{\"requestItem\":{\"barcode\":\"SECOND\"},\"status\":\"SUCCESS\"}]}");
        Assert.ThrowsException<InvalidOperationException>(()=>store.UpdateBatch(plan.Id,"10",wrong.RootElement));
        var complete=JsonDocument.Parse("{\"batchRequestId\":\"batch-test\",\"items\":[{\"requestItem\":{\"barcode\":\"SECOND\"},\"status\":\"SUCCESS\"},{\"requestItem\":{\"barcode\":\"REMOTE\"},\"status\":\"SUCCESS\"}]}");
        Assert.AreEqual("İşlem tamamlandı",store.UpdateBatch(plan.Id,"10",complete.RootElement).Status);
    }
    [TestMethod] public async Task WrongClientAndUnknownOutcomeNeverSendAnotherRequest()
    {
        var handler=new OfflineHandler();using var transport=new HttpClient(handler);using var wrong=new TrendyolApiClient(account with{SupplierId="20"},transport);
        var plan=store.Preview(account,[product.Id],TrendyolOperation.Stock);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>store.SendAsync(plan.Id,account,true,wrong));Assert.AreEqual(0,handler.Calls);Assert.AreEqual(0,store.Receipts("10").Count);
        using var client=new TrendyolApiClient(account,transport);await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>store.SendAsync(plan.Id,account,true,client));Assert.AreEqual(1,handler.Calls);Assert.AreEqual("Belirsiz",store.Receipts("10").Single().Status);
        var next=store.Preview(account,[product.Id],TrendyolOperation.Price);await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>store.SendAsync(next.Id,account,true,client));Assert.AreEqual(1,handler.Calls);
    }
    [DataTestMethod]
    [DataRow(TrendyolOperation.Price)]
    [DataRow(TrendyolOperation.Stock)]
    [DataRow(TrendyolOperation.PriceAndStock)]
    [DataRow(TrendyolOperation.Content)]
    [DataRow(TrendyolOperation.Delivery)]
    [DataRow(TrendyolOperation.ShippingDetails)]
    public async Task PendingProductCannotDispatchApprovedOnlyUpdates(TrendyolOperation operation)
    {
        var state=store.Load("10");state.Products[0]=state.Products[0] with{Approved=false};store.Save(state);
        var plan=store.Preview(account,[product.Id],operation);Assert.AreEqual("Hatalı",plan.Rows.Single().Status);Assert.IsNull(plan.Rows.Single().ItemJson);
        StringAssert.Contains(plan.Rows.Single().Detail,"henüz onaylı değil");
        var handler=new OfflineHandler();using var http=new HttpClient(handler);using var client=new TrendyolApiClient(account,http);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>store.SendAsync(plan.Id,account,true,client));Assert.AreEqual(0,handler.Calls);Assert.AreEqual(0,store.Receipts("10").Count);
    }
    [TestMethod] public void ManuallyReconciledCreateNeedsFreshRemoteSnapshotThenNewPreview()
    {
        var state=store.Load("10");state.Products.Clear();state.DictionaryUpdatedUtc=DateTime.UtcNow;state.Categories.Add(new(1,"Kategori","Kategori",true));state.Brands.Add(new(2,"Marka"));state.Attributes[1]=[];state.AttributesUpdatedUtc[1]=DateTime.UtcNow;state.Profiles.Single().CategoryId=1;state.Profiles.Single().BrandId=2;state.Profiles.Single().Origin="TR";store.Save(state);
        var plan=store.Preview(account,[product.Id],TrendyolOperation.Create);store.Claim(plan.Id,account,true);store.ResolveUnknown(plan.Id,"10","Mağazada bulunmadığı kontrol edildi.");
        var stale=store.Preview(account,[product.Id],TrendyolOperation.Create);Assert.ThrowsException<InvalidOperationException>(()=>store.Claim(stale.Id,account,true));
        state=store.Load("10");state.ProductsUpdatedUtc=DateTime.UtcNow;store.Save(state);var retry=store.Preview(account,[product.Id],TrendyolOperation.Create);store.Claim(retry.Id,account,true);Assert.AreEqual(2,store.Receipts("10").Count);
    }
}
