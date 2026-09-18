using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

namespace MarketplaceHub.Tests;

[TestClass]
public class TrendyolIdentityShippingTests
{
    string directory=null!;
    TrendyolWorkspaceStore store=null!;
    CatalogProduct product=null!;
    readonly TrendyolSettings account=new("10","test-key","test-secret","10 - Self Integration");
    [TestInitialize] public void Setup()
    {
        directory=Path.Combine(Path.GetTempPath(),"trendyol-identity-shipping-"+Guid.NewGuid().ToString("N"));
        store=new(directory);
        product=new(){Sku="PTD-1367",Barcode="5412087017850",Gtin="OTHER-GTIN",Name="Cup",Description="Cup description",Currency="TRY",Price=90.55m,Stock=1,ImageUrls="https://example.test/cup.jpg"};
        SaveProduct();
        var state=store.Load("10");state.ProductsUpdatedUtc=state.DictionaryUpdatedUtc=DateTime.UtcNow;
        state.Categories.Add(new(1,"Bowls","Bowls",true));state.Brands.Add(new(2,"Brand"));state.Attributes[1]=[];state.AttributesUpdatedUtc[1]=DateTime.UtcNow;
        state.Profiles.Add(new(){ProductId=product.Id,IntegrationCode="PTD-1367",CategoryId=1,BrandId=2,Origin="BE"});store.Save(state);
    }
    [TestCleanup] public void Cleanup(){SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}
    void SaveProduct()
    {
        using var c=new SqliteConnection("Data Source="+Path.Combine(directory,"catalog.db"));c.Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT OR REPLACE INTO CatalogProducts VALUES($id,$json)";cmd.Parameters.AddWithValue("$id",product.Id);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(product));cmd.ExecuteNonQuery();
    }
    JsonElement Item(TrendyolOperation operation)
    {
        var plan=store.Preview(account,[product.Id],operation);
        Assert.IsNotNull(plan.Rows.Single().ItemJson,plan.Rows.Single().Detail);
        return JsonDocument.Parse(plan.PayloadJson).RootElement.GetProperty("items")[0];
    }
    [TestMethod] public void CreateSendsProductBarcodeAndSeparateStockCodeInsteadOfLegacyIntegrationCode()
    {
        var item=Item(TrendyolOperation.Create);
        Assert.AreEqual("5412087017850",item.GetProperty("barcode").GetString());
        Assert.AreEqual("PTD-1367",item.GetProperty("stockCode").GetString());
        Assert.AreEqual("PTD-1367",item.GetProperty("productMainId").GetString());
    }
    [TestMethod] public void EmptyBarcodeNeverUsesLegacyIntegrationSkuOrGtinForCreation()
    {
        product.Barcode="";SaveProduct();var plan=store.Preview(account,[product.Id],TrendyolOperation.Create);
        Assert.AreEqual("Hatalı",plan.Rows.Single().Status);Assert.IsNull(plan.Rows.Single().ItemJson);
        Assert.ThrowsException<InvalidOperationException>(()=>store.Claim(plan.Id,account,true));Assert.AreEqual(0,store.Receipts("10").Count);
    }
    [TestMethod] public void ExplicitListingBarcodeIsIndependentFromTheExistingRemoteLink()
    {
        var state=store.Load("10");var json=JsonSerializer.Serialize(state.Profiles.Single());
        state.Profiles[0]=JsonSerializer.Deserialize<TrendyolProductProfile>(json[..^1]+",\"ListingBarcode\":\"USER-BARCODE\"}")!;store.Save(state);
        Assert.AreEqual("USER-BARCODE",Item(TrendyolOperation.Create).GetProperty("barcode").GetString());
        Assert.AreEqual("PTD-1367",store.Load("10").Profiles.Single().IntegrationCode);
        Assert.AreEqual("5412087017850",new CatalogStore(directory).Products().Single().Barcode);
    }
    [DataTestMethod][DataRow("2.5")][DataRow("2,5")]
    public void ShippingUpdateReadsEachProductsOwnDesi(string desi)
    {
        product.XmlAttributes["Desi"]=desi;SaveProduct();PrepareShipping();
        var item=Item(TrendyolOperation.ShippingDetails);Assert.AreEqual(2.5m,item.GetProperty("dimensionalWeight").GetDecimal());
        Assert.AreEqual("PTD-1367",item.GetProperty("barcode").GetString());
        Assert.IsFalse(item.TryGetProperty("quantity",out _));Assert.IsFalse(item.TryGetProperty("salePrice",out _));
        Assert.AreEqual(3,Item(TrendyolOperation.Delivery).GetProperty("deliveryOptions").GetProperty("deliveryDuration").GetInt32());
    }
    [DataTestMethod][DataRow(TrendyolOperation.Stock)][DataRow(TrendyolOperation.Price)]
    [DataRow(TrendyolOperation.PriceAndStock)][DataRow(TrendyolOperation.Delivery)][DataRow(TrendyolOperation.ShippingDetails)]
    public void ChangedListingBarcodeMustBeRematchedBeforeUpdatingAnOldRemoteLink(TrendyolOperation operation)
    {
        PrepareShipping();var state=store.Load("10");state.Profiles.Single().ListingBarcode="NEW-BARCODE";store.Save(state);
        var plan=store.Preview(account,[product.Id],operation);Assert.AreEqual("Hatalı",plan.Rows.Single().Status);StringAssert.Contains(plan.Rows.Single().Detail,"yeniden eşleştirin");
        Assert.ThrowsException<InvalidOperationException>(()=>store.Claim(plan.Id,account,true));Assert.AreEqual(0,store.Receipts("10").Count);
    }
    [DataTestMethod][DataRow("-2")][DataRow("invalid")][DataRow("1,000.5")]
    public void InvalidDesiBlocksShippingWithoutAReceipt(string desi)
    {
        product.XmlAttributes["Desi"]=desi;SaveProduct();PrepareShipping();
        var row=store.Preview(account,[product.Id],TrendyolOperation.ShippingDetails).Rows.Single();Assert.AreEqual("Hatalı",row.Status);StringAssert.Contains(row.Detail,"Desi");Assert.AreEqual(0,store.Receipts("10").Count);
    }
    void PrepareShipping()
    {
        var state=store.Load("10");state.Products.Add(new("PTD-1367","PTD-1367","Remote cup",1,1,90.55m,90.55m,true));
        state.Templates.Add(new("t","Three days","",3,null,null));state.Profiles.Single().DeliveryTemplateId="t";store.Save(state);
    }
    [TestMethod] public void BrandSafetyDefaultsFillMissingFieldsAndPreserveProductOverrides()
    {
        product.Brand="Moderna";SaveProduct();var state=PrepareSafety();
        state.Profiles.Single().Attributes.Add(new(1198,[],"Product manufacturer"));store.Save(state);
        var attrs=Item(TrendyolOperation.Create).GetProperty("attributes").EnumerateArray().ToArray();
        Assert.AreEqual("Product manufacturer",attrs.Single(a=>a.GetProperty("attributeId").GetInt64()==1198).GetProperty("customAttributeValue").GetString());
        Assert.AreEqual("manufacturer@example.test",attrs.Single(a=>a.GetProperty("attributeId").GetInt64()==1294).GetProperty("customAttributeValue").GetString());
        Assert.IsTrue(attrs.Single(a=>a.GetProperty("attributeId").GetInt64()==1296).GetProperty("customAttributeValue").GetString()!.Length>50);
        Assert.AreEqual(1,store.Load("10").Profiles.Single().Attributes.Count,"Inherited fields must not be copied into product overrides.");
    }
    [TestMethod] public void BrandSafetyOnlyAppliesToTheSameBrandAndUnsupportedCategoryFieldsBlock()
    {
        product.Brand="Another brand";SaveProduct();store.Save(PrepareSafety());
        Assert.AreEqual(0,Item(TrendyolOperation.Create).GetProperty("attributes").GetArrayLength());
        product.Brand="MODERNA";SaveProduct();var state=store.Load("10");state.Attributes[1].RemoveAll(a=>a.Id==1294);store.Save(state);
        var row=store.Preview(account,[product.Id],TrendyolOperation.Create).Rows.Single();Assert.AreEqual("Hatalı",row.Status);StringAssert.Contains(row.Detail,"Üretici Mail Adresi");
    }
    TrendyolWorkspaceState PrepareSafety()
    {
        var state=store.Load("10");state.Attributes[1]=[new(1198,"Üretici Adı",false,true,false,[]),new(1294,"Üretici Mail Adresi",false,true,false,[]),new(1296,"Üretici Adres Bilgisi",false,true,false,[])];
        var templates=JsonSerializer.Serialize(new[]{new{BrandName="Moderna",Values=new System.Collections.Generic.Dictionary<long,string>{{1198,"Brand manufacturer"},{1294,"manufacturer@example.test"},{1296,new string('A',80)}}}});
        var json=JsonSerializer.Serialize(state);return JsonSerializer.Deserialize<TrendyolWorkspaceState>(json[..^1]+",\"BrandSafetyTemplates\":"+templates+"}")!;
    }
    [TestMethod] public async Task ReviewedDesiReachesTheDocumentedEndpointWithOnlyBarcodeAndWeight()
    {
        product.XmlAttributes["Desi"]="2.5";SaveProduct();PrepareShipping();
        var plan=store.Preview(account,[product.Id],TrendyolOperation.ShippingDetails);
        using var handler=new ShippingHandler();using var http=new HttpClient(handler);using var client=new TrendyolApiClient(account,http);
        var receipt=await store.SendAsync(plan.Id,account,true,client);
        Assert.AreEqual("batch-shipping",receipt.BatchId);Assert.AreEqual(1,handler.Calls);
        StringAssert.EndsWith(handler.Path,"/products/variant-bulk-update");
        var item=JsonDocument.Parse(handler.Body).RootElement.GetProperty("items")[0];Assert.AreEqual(2,item.EnumerateObject().Count());Assert.AreEqual(2.5m,item.GetProperty("dimensionalWeight").GetDecimal());
    }
    sealed class ShippingHandler:HttpMessageHandler
    {
        public string Path="",Body="";public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;Path=request.RequestUri!.AbsolutePath;Body=await request.Content!.ReadAsStringAsync(token);
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"batchRequestId\":\"batch-shipping\"}")};
        }
    }
    [TestMethod] public void TemplateCanPreserveRemoteDesiWithoutReadingInvalidLocalValue()
    {
        product.XmlAttributes["Desi"]="invalid";SaveProduct();PrepareShipping();var state=store.Load("10");
        state.Templates[0]=state.Templates[0] with{IncludeProductDesi=false,CarrierCode="TEST"};state.Carriers.Add(new("TEST","Carrier"));state.AddressesUpdatedUtc=DateTime.UtcNow;store.Save(state);
        var item=Item(TrendyolOperation.ShippingDetails);Assert.IsFalse(item.TryGetProperty("dimensionalWeight",out _));Assert.AreEqual("TEST",item.GetProperty("cargoProviders")[0].GetString());
    }
    [TestMethod] public void ExplicitSafetyFieldRemovalPreservesOtherFieldsBrandsAndSellers()
    {
        var state=store.Load("10");store.SaveBrandSafety("10",state.Revision,["Moderna","Other"],new(){{1198,"Manufacturer"},{1294,"manufacturer@example.test"}});
        store.SaveBrandSafety("20",0,["Moderna"],new(){{1294,"otherstore@example.test"}});
        state=store.Load("10");store.ClearBrandSafetyFields("10",state.Revision,["MODERNA"],[1294]);
        Assert.ThrowsException<InvalidOperationException>(()=>store.ClearBrandSafetyFields("10",state.Revision,["Other"],[1294]));
        var templates=store.Load("10").BrandSafetyTemplates;
        Assert.IsFalse(templates.Single(t=>t.BrandName=="Moderna").Values.ContainsKey(1294));
        Assert.AreEqual("Manufacturer",templates.Single(t=>t.BrandName=="Moderna").Values[1198]);Assert.IsTrue(templates.Single(t=>t.BrandName=="Other").Values.ContainsKey(1294));
        Assert.AreEqual("otherstore@example.test",store.Load("20").BrandSafetyTemplates.Single().Values[1294]);Assert.AreEqual(0,store.Receipts("10").Count);
    }
}
