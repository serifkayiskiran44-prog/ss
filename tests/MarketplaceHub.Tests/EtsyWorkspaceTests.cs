using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Etsy;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class EtsyWorkspaceTests
{
    string directory = null!;
    CatalogProduct product = null!;
    EtsyWorkspaceStore store = null!;
    readonly EtsyCredentials credentials = new("app", "secret", "88.token", "123");
    [TestInitialize] public void Init()
    {
        directory = Path.Combine(Path.GetTempPath(), "etsy-workspace-" + Guid.NewGuid().ToString("N"));
        product = new CatalogStore(directory).CreateManual(new() { Sku="ABC", Name="Item", Description="Description", Price=20, Stock=8, Currency="USD" });
        store = new(directory);
        store.Save(new() { ShopId="123", Currency="USD", Profiles=[new() { ProductId=product.Id, ListingId=456 }], Listings=[new(456,"Item","active",3,10,"USD","ABC")] });
    }
    [TestCleanup] public void Cleanup() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    [TestMethod] public void MatchingIsExactAndRejectsAmbiguityWithoutCatalogMutation()
    {
        using var http=new HttpClient(new Api()); var service=new EtsyWorkspaceService(directory,http); var state=store.Load("123");
        state.Profiles.Clear(); state.Listings=[new(1,"A","active",1,2,"USD","ABCD")];
        Assert.IsFalse(service.Match(state,[product]).Single().CanMatch);
        state.Listings=[new(1,"A","active",1,2,"USD"," abc ")];
        Assert.IsTrue(service.Match(state,[product]).Single().CanMatch);
        state.Listings.Add(new(2,"B","active",1,2,"USD","ABC"));
        Assert.IsFalse(service.Match(state,[product]).Single().CanMatch);
        Assert.AreEqual("ABC",new CatalogStore(directory).Products().Single().Sku);
    }
    [TestMethod] public void WorkspaceRevisionAndShopIsolationAreDurable()
    {
        var a=store.Load("123"); var stale=store.Load("123"); a.ShopName="New"; store.Save(a);
        Assert.ThrowsException<InvalidOperationException>(()=>store.Save(stale));
        Assert.AreEqual("New",new EtsyWorkspaceStore(directory).Load("123").ShopName);
        Assert.AreEqual(0,store.Load("999").Profiles.Count);
    }
    [TestMethod] public void JoinedRemoteSkusCannotMasqueradeAsOneExactSku()
    {
        using var http=new HttpClient(new Api()); var service=new EtsyWorkspaceService(directory,http);
        var joined=new CatalogStore(directory).CreateManual(new() { Sku="ABC, DEF",Name="Comma sku",Price=1,Stock=1,Currency="USD" });
        var state=store.Load("123"); state.Listings=[new(456,"Item","active",1,1,"USD","ABC, DEF") { Skus=["ABC","DEF"] }];
        Assert.IsFalse(service.Match(state,[joined]).Single().CanMatch);
        state.Profiles.Clear(); state.Listings=[new(456,"Item","active",1,1,"USD","ABC, DEF") { Skus=["ABC, DEF"] }];
        Assert.IsTrue(service.Match(state,[joined]).Single().CanMatch);
    }
    [TestMethod] public async Task PriceOnlyPreservesRemoteQuantityAndSkuAndReceiptBlocksReplay()
    {
        var api=new Api(); using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
        var plan=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Price);
        Assert.IsTrue(plan.Rows.Single().CanSend,plan.Rows.Single().Detail);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>service.SendAsync(credentials,plan.Id,false));
        var result=await service.SendAsync(credentials with { Token="88.refreshed" },plan.Id,true);
        Assert.AreEqual("Succeeded",result.Single().Status);
        using var payload=JsonDocument.Parse(api.Body); var item=payload.RootElement.GetProperty("products")[0];
        Assert.AreEqual("ABC",item.GetProperty("sku").GetString());
        Assert.AreEqual(3,item.GetProperty("offerings")[0].GetProperty("quantity").GetInt32());
        Assert.AreEqual(20m,item.GetProperty("offerings")[0].GetProperty("price").GetDecimal());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>new EtsyWorkspaceService(directory,http).SendAsync(credentials,plan.Id,true));
        Assert.AreEqual(1,api.Writes);
    }
    [TestMethod] public async Task StockOnlyPreservesRemotePriceAndDoesNotRequireLocalCurrencyMatch()
    {
        using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(directory,"catalog.db")))
        { connection.Open(); using var command=connection.CreateCommand(); command.CommandText="UPDATE CatalogProducts SET Json=json_set(Json,'$.Currency','TRY') WHERE Id=$id"; command.Parameters.AddWithValue("$id",product.Id); command.ExecuteNonQuery(); }
        var api=new Api(); using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
        var plan=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Stock);
        await service.SendAsync(credentials,plan.Id,true);
        using var payload=JsonDocument.Parse(api.Body); var offering=payload.RootElement.GetProperty("products")[0].GetProperty("offerings")[0];
        Assert.AreEqual(10m,offering.GetProperty("price").GetDecimal()); Assert.AreEqual(8,offering.GetProperty("quantity").GetInt32());
    }
    [TestMethod] public async Task PriceAndStockWritesDoNotIntroduceAbsentReadinessState()
    {
        foreach(var operation in new[]{EtsyOperation.Price,EtsyOperation.Stock})
        {
            var api=new Api { Mode="missing-readiness" }; using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
            var plan=await service.PreviewAsync(credentials,[product.Id],operation); Assert.IsTrue(plan.Rows.Single().CanSend,plan.Rows.Single().Detail);
            using var preview=JsonDocument.Parse(plan.Rows.Single().Steps.Single().Json);
            Assert.IsFalse(preview.RootElement.GetProperty("products")[0].GetProperty("offerings")[0].TryGetProperty("readiness_state_id",out _),operation.ToString());
            await service.SendAsync(credentials,plan.Id,true);
            using var sent=JsonDocument.Parse(api.Body);
            Assert.IsFalse(sent.RootElement.GetProperty("products")[0].GetProperty("offerings")[0].TryGetProperty("readiness_state_id",out _),operation.ToString());
        }
    }
    [TestMethod] public async Task LegacyInventoryWriteDoesNotIntroduceAbsentReadinessState()
    {
        var api=new Api { Mode="missing-readiness" }; using var http=new HttpClient(api);
        await new EtsyShopClient(http).UpdateSimpleListingAsync(credentials,456,8,20);
        using var sent=JsonDocument.Parse(api.Body);
        Assert.IsFalse(sent.RootElement.GetProperty("products")[0].GetProperty("offerings")[0].TryGetProperty("readiness_state_id",out _));
        Assert.AreEqual(1,api.Writes);
    }
    [TestMethod] public async Task WrongShopAndCurrencyAndComplexInventoryNeverWrite()
    {
        foreach(var mode in new[]{"wrong-shop","wrong-currency","complex"})
        {
            var api=new Api { Mode=mode }; using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
            var plan=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Price);
            Assert.IsFalse(plan.Rows.Single().CanSend,mode); Assert.AreEqual(0,api.Writes);
        }
    }
    [TestMethod] public async Task StaleWorkspaceAndWrongAccountAndRemoteChangeAreBlocked()
    {
        var api=new Api(); using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
        var plan=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Price);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>service.SendAsync(credentials with { Token="99.other" },plan.Id,true));
        api.Mode="changed";
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>service.SendAsync(credentials,plan.Id,true));
        api.Mode=""; var state=store.Load("123"); state.ShopName="changed"; store.Save(state);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>service.SendAsync(credentials,plan.Id,true)); Assert.AreEqual(0,api.Writes);
    }
    [TestMethod] public async Task UncertainWriteBlocksNewPreviewAndRestartReplay()
    {
        var api=new Api { Mode="timeout" }; using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
        var plan=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Price);
        var result=await service.SendAsync(credentials,plan.Id,true); Assert.AreEqual("Unknown",result.Single().Status);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>new EtsyWorkspaceService(directory,http).SendAsync(credentials,plan.Id,true));
        var next=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Price); Assert.IsFalse(next.Rows.Single().CanSend); Assert.AreEqual(1,api.Writes);
    }
    [TestMethod] public async Task SavedPreviewIgnoresCallerMutationAndConcurrentSendClaimsOnce()
    {
        var api=new Api(); using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
        var plan=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Price);
        plan.Rows.Single().Steps[0].Json="{\"unexpected\":true}";
        var one=Task.Run(async()=> { try { await service.SendAsync(credentials,plan.Id,true); return true; } catch(InvalidOperationException) { return false; } });
        var two=Task.Run(async()=> { try { await new EtsyWorkspaceService(directory,http).SendAsync(credentials,plan.Id,true); return true; } catch(InvalidOperationException) { return false; } });
        var results=await Task.WhenAll(one,two); Assert.AreEqual(1,results.Count(x=>x)); Assert.AreEqual(1,api.Writes); StringAssert.Contains(api.Body,"products");
    }
    [TestMethod] public async Task CatalogChangesInvalidatePreviewEvenWithSameTimestamp()
    {
        var api=new Api(); using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http); var plan=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Price);
        using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(directory,"catalog.db")))
        { connection.Open(); using var command=connection.CreateCommand(); command.CommandText="UPDATE CatalogProducts SET Json=json_set(Json,'$.Stock',99) WHERE Id=$id"; command.Parameters.AddWithValue("$id",product.Id); command.ExecuteNonQuery(); }
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>service.SendAsync(credentials,plan.Id,true)); Assert.AreEqual(0,api.Writes);
    }
    [TestMethod] public async Task DraftShowsAllStepsInheritsTemplateAndSavesCreatedMappingWithoutPublish()
    {
        SetupDraft(); var api=new Api(); using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
        var before=JsonSerializer.Serialize(new CatalogStore(directory).Products());
        var plan=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.CreateDraft); var row=plan.Rows.Single(); Assert.IsTrue(row.CanSend,row.Detail);
        Assert.AreEqual(2,row.Steps.Count); Assert.AreEqual("Template Item",row.Steps[0].Form["title"]); Assert.AreEqual("tag",row.Steps[0].Form["tags"]);
        Assert.IsFalse(row.Steps.Any(s=>s.Form.ContainsKey("state"))); StringAssert.Contains(row.PayloadJson,"ABC");
        var result=await service.SendAsync(credentials,plan.Id,true); Assert.AreEqual("Succeeded",result.Single().Status); Assert.AreEqual(456L,store.Load("123").Profiles.Single().ListingId);
        Assert.AreEqual(before,JsonSerializer.Serialize(new CatalogStore(directory).Products())); Assert.AreEqual(2,api.Writes);
        var second=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.CreateDraft); Assert.IsFalse(second.Rows.Single().CanSend);
    }
    [TestMethod] public async Task RequiredCategoryAttributeBlocksDraftUntilValueSelected()
    {
        SetupDraft(); var api=new Api { Mode="required" }; using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
        var blocked=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.CreateDraft); Assert.IsFalse(blocked.Rows.Single().CanSend);
        var state=store.Load("123"); state.Profiles.Single().Properties.Add(new() { PropertyId=12,ValueIds=[13],Values=["Blue"] }); store.Save(state);
        var valid=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.CreateDraft); Assert.IsTrue(valid.Rows.Single().CanSend,valid.Rows.Single().Detail); Assert.AreEqual(3,valid.Rows.Single().Steps.Count);
        Assert.AreEqual(0,api.Writes);
    }
    [TestMethod] public void ManualMappingRejectsConflictingListingAndStaleSku()
    {
        using var http=new HttpClient(new Api()); var service=new EtsyWorkspaceService(directory,http);
        var other=new CatalogStore(directory).CreateManual(new() { Sku="SECOND",Name="Second",Price=1,Stock=1,Currency="USD" });
        Assert.ThrowsException<InvalidOperationException>(()=>service.ApplyMatches(store.Load("123"),[new() { ProductId=other.Id,Sku=other.Sku,ListingId=456,CanMatch=true }]));
        Assert.ThrowsException<InvalidOperationException>(()=>service.ApplyMatches(store.Load("123"),[new() { ProductId=product.Id,Sku="old",ListingId=456,CanMatch=true }]));
        Assert.AreEqual(1,store.Load("123").Profiles.Count);
    }
    [TestMethod] public async Task PartialDraftPreservesCreatedIdAndCannotBeRepeated()
    {
        SetupDraft(); var api=new Api { Mode="reject-inventory" }; using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
        var plan=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.CreateDraft); var result=await service.SendAsync(credentials,plan.Id,true);
        Assert.AreEqual("Partial",result.Single().Status); Assert.AreEqual(456L,new EtsyWorkspaceStore(directory).Receipts("123").Single().ListingId);
        var next=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.CreateDraft); Assert.IsFalse(next.Rows.Single().CanSend);
    }
    [TestMethod] public async Task ContentUpdatesReadinessExplicitlyAndPreservesRemoteMoneyAndQuantity()
    {
        SetupDraft(); var state=store.Load("123"); state.Profiles.Single().ListingId=456; state.Profiles.Single().ReadinessStateId=9; store.Save(state);
        var api=new Api { Mode="missing-readiness" }; using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
        var plan=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Content); var row=plan.Rows.Single(); Assert.IsTrue(row.CanSend,row.Detail);
        var inventoryStep=row.Steps.Single(s=>s.Path.EndsWith("inventory")); using var body=JsonDocument.Parse(inventoryStep.Json); var offering=body.RootElement.GetProperty("products")[0].GetProperty("offerings")[0];
        Assert.AreEqual(9,offering.GetProperty("readiness_state_id").GetInt64()); Assert.AreEqual(10m,offering.GetProperty("price").GetDecimal()); Assert.AreEqual(3,offering.GetProperty("quantity").GetInt32());
        Assert.IsFalse(row.Steps[0].Form.ContainsKey("price")); Assert.IsFalse(row.Steps[0].Form.ContainsKey("quantity"));
    }
    [TestMethod] public async Task CreatedListingOwnershipIsVerifiedBeforeSkuFollowup()
    {
        SetupDraft(); var api=new Api { Mode="wrong-shop" }; using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
        var plan=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.CreateDraft); var result=await service.SendAsync(credentials,plan.Id,true);
        Assert.AreEqual("Unknown",result.Single().Status); Assert.AreEqual(1,api.Writes); Assert.IsNull(store.Load("123").Profiles.Single().ListingId);
    }
    void SetupDraft()
    {
        var state=store.Load("123"); state.Listings.Clear(); state.Profiles=[new() { ProductId=product.Id,TemplateId="template",Title=" ",Tags=" " }];
        state.Templates=[new() { Id="template",Name="Test",Listing=new() { Currency="USD",TitlePrefix="Template",Tags="tag",TaxonomyId=1,ShippingProfileId=2,ReadinessStateId=7,WhoMade="i_did",WhenMade="made_to_order" } }]; store.Save(state);
    }
    [TestMethod] public async Task ExplicitShopCurrencyPriceOverrideSupportsLocalTryWithoutGuessing()
    {
        using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(directory,"catalog.db")))
        { connection.Open(); using var command=connection.CreateCommand(); command.CommandText="UPDATE CatalogProducts SET Json=json_set(Json,'$.Currency','TRY') WHERE Id=$id"; command.Parameters.AddWithValue("$id",product.Id); command.ExecuteNonQuery(); }
        var api=new Api(); using var http=new HttpClient(api); var service=new EtsyWorkspaceService(directory,http);
        Assert.IsFalse((await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Price)).Rows.Single().CanSend);
        var state=store.Load("123"); state.Profiles.Single().Price=25; state.Profiles.Single().PriceCurrency="USD"; store.Save(state);
        Assert.IsTrue((await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Price)).Rows.Single().CanSend);
        state=store.Load("123"); state.Profiles.Single().PriceCurrency=""; store.Save(state);
        Assert.IsFalse((await service.PreviewAsync(credentials,[product.Id],EtsyOperation.Price)).Rows.Single().CanSend);
        SetupDraft(); state=store.Load("123"); state.Profiles.Single().Price=25; state.Profiles.Single().PriceCurrency="USD"; store.Save(state);
        var draft=await service.PreviewAsync(credentials,[product.Id],EtsyOperation.CreateDraft); Assert.IsTrue(draft.Rows.Single().CanSend,draft.Rows.Single().Detail);
        Assert.AreEqual("TRY",new CatalogStore(directory).Products().Single().Currency);
    }
    sealed class Api : HttpMessageHandler
    {
        public string Mode="",Body=""; public int Writes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var path=request.RequestUri!.AbsolutePath;
            if(request.Method!=HttpMethod.Get) { Interlocked.Increment(ref Writes); Body=await request.Content!.ReadAsStringAsync(ct); if(Mode=="timeout")throw new HttpRequestException("fake transport failure"); if(Mode=="reject-inventory"&&path.EndsWith("inventory"))return new(HttpStatusCode.BadRequest); return Json(path.EndsWith("inventory")?Inventory():"{\"listing_id\":456}"); }
            if(path.Contains("seller-taxonomy")) return Json(Mode=="required"?"{\"results\":[{\"property_id\":12,\"name\":\"Color\",\"is_required\":true,\"supports_attributes\":true,\"supports_variations\":false,\"possible_values\":[{\"value_id\":13,\"name\":\"Blue\"}],\"scales\":[]}]}":"{\"results\":[]}");
            if(path.EndsWith("/shops/123"))return Json("{\"shop_id\":123,\"user_id\":88,\"shop_name\":\"Shop\",\"currency_code\":\"USD\"}");
            if(path.EndsWith("/listings/456")) return Json("{\"listing_id\":456,\"shop_id\":"+(Mode=="wrong-shop"?999:123)+",\"title\":\"Item\",\"description\":\"Description\",\"state\":\"active\",\"quantity\":3,\"last_modified_timestamp\":"+(Mode=="changed"?2:1)+",\"price\":{\"amount\":1000,\"divisor\":100,\"currency_code\":\""+(Mode=="wrong-currency"?"EUR":"USD")+"\"},\"skus\":[\"ABC\"]}");
            if(path.EndsWith("/inventory"))return Json(Inventory());
            throw new InvalidOperationException("Unexpected route: "+path);
        }
        string Inventory() { var p="{\"product_id\":11,\"sku\":\"ABC\",\"property_values\":[],\"offerings\":[{\"offering_id\":22,\"price\":{\"amount\":1000,\"divisor\":100,\"currency_code\":\"USD\"},\"quantity\":3,\"is_enabled\":true,\"readiness_state_id\":7}]}"; if(Mode=="missing-readiness")p=p.Replace(",\"readiness_state_id\":7",""); return "{\"products\":["+p+(Mode=="complex"?","+p:"")+"],\"price_on_property\":[],\"quantity_on_property\":[],\"sku_on_property\":[],\"readiness_state_on_property\":[]}"; }
        static HttpResponseMessage Json(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    }
}
