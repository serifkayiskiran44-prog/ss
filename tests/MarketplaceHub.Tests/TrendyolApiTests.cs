using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Trendyol;

namespace MarketplaceHub.Tests;

[TestClass]
public class TrendyolApiTests
{
    static TrendyolSettings Settings(string seller = "42") => new(seller, "fixture-key", "fixture-secret", "MonoBridge");

    [TestMethod]
    public async Task CategoriesFlattenTreeWithLeafPathsAndMandatoryAuthentication()
    {
        using var handler = new FakeHandler(_ => Json("""{"categories":[{"id":1,"name":"Ev","subCategories":[{"id":2,"name":"Mutfak","subCategories":[]}]}]}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://wrong.invalid/") };
        using var client = new TrendyolApiClient(Settings(), http);
        var result = await client.GetCategoriesAsync();
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("Ev > Mutfak", result[1].Path);
        Assert.IsFalse(result[0].IsLeaf);
        Assert.IsTrue(result[1].IsLeaf);
        Assert.AreEqual("https://apigw.trendyol.com/integration/product/product-categories", handler.Requests[0].Url);
        Assert.AreEqual("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-key:fixture-secret")), handler.Requests[0].Authorization);
        Assert.AreEqual("42 - Self Integration", handler.Requests[0].UserAgent);
        Assert.IsNull(http.DefaultRequestHeaders.Authorization);
    }

    [TestMethod]
    public async Task BrandsReadAllPagesAndRejectRepeatedIdentifiers()
    {
        var first = JsonSerializer.Serialize(new { brands = Enumerable.Range(1, 1000).Select(x => new { id = x, name = "Brand " + x }) });
        using var handler = new FakeHandler(i => Json(i == 0 ? first : """{"brands":[{"id":1001,"name":"Last"}]}"""));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        var brands = await client.GetBrandsAsync();
        Assert.AreEqual(1001, brands.Count);
        StringAssert.EndsWith(handler.Requests[1].Url, "brands?page=1&size=1000");
        using var repeated = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json(first))));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => repeated.GetBrandsAsync());
    }

    [TestMethod]
    public async Task BrandNameSearchPreservesCaseEncodesInputAndReadsTheDirectArray()
    {
        using var handler = new FakeHandler(_ => Json("""[{"id":10,"name":"ACME\tÇelik & Ev+"},{"id":10,"name":"ACME Çelik & Ev+"},{"id":11,"name":"ACME Çelik & Ev+"}]"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://wrong.invalid/") };
        using var client = new TrendyolApiClient(Settings(), http);
        var brands = await client.GetBrandsByNameAsync("ACME Çelik & Ev+");
        Assert.AreEqual(2, brands.Count);
        Assert.AreEqual(10L, brands[0].Id);
        Assert.AreEqual(11L, brands[1].Id);
        Assert.AreEqual("ACME Çelik & Ev+", brands[0].Name);
        Assert.AreEqual("https://apigw.trendyol.com/integration/product/brands/by-name?name=ACME%20%C3%87elik%20%26%20Ev%2B", handler.Requests.Single().Url);
        Assert.AreEqual("GET", handler.Requests.Single().Method);
        Assert.AreEqual("42 - Self Integration", handler.Requests.Single().UserAgent);
        Assert.AreEqual("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-key:fixture-secret")), handler.Requests.Single().Authorization);
        Assert.AreEqual("", handler.Requests.Single().Body);
    }

    [TestMethod]
    public async Task BrandNameSearchAcceptsAnEmptyResultWithoutInventingAnId()
    {
        using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json("[]"))));
        Assert.AreEqual(0, (await client.GetBrandsByNameAsync("Unknown Brand")).Count);
    }

    [DataTestMethod]
    [DataRow("{\"brands\":[]}")]
    [DataRow("[{\"id\":0,\"name\":\"Brand\"}]")]
    [DataRow("[{\"id\":-1,\"name\":\"Brand\"}]")]
    [DataRow("[{\"id\":\"10\",\"name\":\"Brand\"}]")]
    [DataRow("[{\"id\":10,\"name\":\"Brand\\u0000Name\"}]")]
    [DataRow("[{\"id\":10,\"name\":\"Brand\"},{\"id\":10,\"name\":\"Different\"}]")]
    [DataRow("[{\"id\":10,\"name\":\"Brand\"},{\"id\":10,\"name\":\"BRAND\"}]")]
    public async Task BrandNameSearchRejectsMalformedOrConflictingRecordsAtomically(string response)
    {
        using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json(response))));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetBrandsByNameAsync("Brand"));
    }

    [TestMethod]
    public async Task BrandNameSearchRejectsUnsafeOrUnboundedInputBeforeTransport()
    {
        using var handler = new FakeHandler(_ => Json("[]"));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        foreach (var name in new[] { null, "", "   ", "Brand\0Name", "Brand\r\nName", new string('a', 513) })
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => client.GetBrandsByNameAsync(name));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task BrandNameSearchHonorsCancellationBeforeTransport()
    {
        using var handler = new FakeHandler(_ => Json("[]"));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        using var source = new CancellationTokenSource();
        source.Cancel();
        try { await client.GetBrandsByNameAsync("Brand", source.Token); Assert.Fail("Cancellation expected."); }
        catch (OperationCanceledException) { }
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task BrandDisplayWhitespaceDoesNotAbortTheCompleteDictionary()
    {
        var first = JsonSerializer.Serialize(new { brands = Enumerable.Range(1, 1000).Select(x => new { id = x, name = x == 500 ? "21\tFuji" : "Brand " + x }) });
        using var handler = new FakeHandler(i => Json(i == 0 ? first : """{"brands":[{"id":1001,"name":"24\nTRANZX"},{"id":1002,"name":"27\r\nTRUVATIV"}]}"""));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        var brands = await client.GetBrandsAsync();
        Assert.AreEqual(1002, brands.Count);
        Assert.AreEqual("21 Fuji", brands[499].Name);
        Assert.AreEqual("24 TRANZX", brands[1000].Name);
        Assert.AreEqual("27 TRUVATIV", brands[1001].Name);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task DisplayLabelsNormalizeWhitespaceAcrossReadContracts()
    {
        using var handler = new FakeHandler(i => Json(i switch
        {
            0 => """{"categories":[{"id":1,"name":"Ev\tAraç","subCategories":[]}]}""",
            1 => """{"categoryAttributes":[{"attribute":{"id":7,"name":"Kaplama\nTipi"},"required":true,"allowCustom":false,"allowMultipleAttributeValues":false}]}""",
            2 => """{"totalPages":1,"page":0,"content":[{"attributeValueId":1,"attributeValue":"Çelik\r\nMetal"}]}""",
            3 => """{"page":0,"totalPages":1,"totalElements":1,"content":[{"supplierId":42,"barcode":"B1","stockCode":"S1","title":"Yeni\tÜrün"}]}""",
            4 => """{"supplierAddresses":[{"id":9,"fullAddress":"Adres\r\nKat 2","isShipmentAddress":true,"isReturningAddress":false}]}""",
            _ => """[{"code":"ABC","name":"Kargo\tŞirketi"}]"""
        }));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        Assert.AreEqual("Ev Araç", (await client.GetCategoriesAsync()).Single().Path);
        var attribute = (await client.GetAttributesAsync(1)).Single();
        Assert.AreEqual("Kaplama Tipi", attribute.Name);
        Assert.AreEqual("Çelik Metal", attribute.Values.Single().Name);
        Assert.AreEqual("Yeni Ürün", (await client.GetProductsAsync(false)).Single().Title);
        Assert.AreEqual("Adres Kat 2", (await client.GetAddressesAsync()).Single().Name);
        Assert.AreEqual("Kargo Şirketi", (await client.GetCarriersAsync()).Single().Name);
    }

    [DataTestMethod]
    [DataRow("Brand\0Name")]
    [DataRow("Brand\u001bName")]
    public async Task DisplayLabelsStillRejectNonWhitespaceControlCharacters(string name)
    {
        var response = JsonSerializer.Serialize(new { brands = new[] { new { id = 1, name } } });
        using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json(response))));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetBrandsAsync());
    }

    [TestMethod]
    public async Task ProductBarcodesStillRejectWhitespaceControlCharacters()
    {
        var item = new Dictionary<string, object> { ["supplierId"] = 42, ["barcode"] = "B1", ["stockCode"] = "S1", ["title"] = "Product" };
        item["barcode"] = "ID\tVALUE";
        var response = JsonSerializer.Serialize(new { page = 0, totalPages = 1, totalElements = 1, content = new[] { item } });
        using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json(response))));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetProductsAsync(false));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RemoteStockCodePreservesWhitespaceExactlyWithoutChangingIdentity(bool approved)
    {
        var response = approved
            ? """{"page":0,"totalPages":1,"totalElements":1,"content":[{"contentId":8,"title":"Ürün","variants":[{"supplierId":42,"barcode":"4002064419374","stockCode":" 4002064419374\t\r\n\f","stock":{"quantity":3},"price":{"salePrice":12.5,"listPrice":15}}]}]}"""
            : """{"page":0,"totalPages":1,"totalElements":1,"content":[{"supplierId":42,"barcode":"4002064419374","stockCode":" 4002064419374\t\r\n\f","title":"Ürün","quantity":3,"salePrice":12.5,"listPrice":15}]}""";
        using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json(response))));
        var product = (await client.GetProductsAsync(approved)).Single();
        Assert.AreEqual(" 4002064419374\t\r\n\f", product.StockCode);
        Assert.AreEqual("4002064419374", product.Barcode);
        Assert.AreEqual(3, product.Quantity);
        Assert.AreEqual(12.5m, product.SalePrice);
    }

    [TestMethod]
    public async Task RemoteStockCodeRejectsNulEscapeAndUnboundedValues()
    {
        foreach (var stockCode in new[] { "SKU\0", "SKU\u001b", new string('a', 16_385) })
        {
            var response = JsonSerializer.Serialize(new { page = 0, totalPages = 1, totalElements = 1, content = new[] { new { supplierId = 42, barcode = "B1", stockCode, title = "Product" } } });
            using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json(response))));
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetProductsAsync(false));
        }
    }

    [TestMethod]
    public async Task BatchTokensAndCredentialsStillRejectWhitespaceControlCharacters()
    {
        using var http = new HttpClient(new FakeHandler(_ => Json("""{"batchRequestId":"id\tvalue"}""")));
        using var client = new TrendyolApiClient(Settings(), http);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => SendBatch(client, "inventory", """{"items":[{"barcode":"B1","quantity":1}]}"""));
        Assert.ThrowsException<ArgumentException>(() => new TrendyolApiClient(new TrendyolSettings("42", "key\tvalue", "secret", "Agent"), http));
        Assert.ThrowsException<ArgumentException>(() => new TrendyolApiClient(new TrendyolSettings("42", "key", "secret\nvalue", "Agent"), http));
    }

    [TestMethod]
    public async Task AttributesUseV2ValuesEndpointAndReadEveryPage()
    {
        var values = JsonSerializer.Serialize(new { totalPages = 2, page = 0, content = Enumerable.Range(1, 1000).Select(x => new { attributeValueId = x, attributeValue = "Value " + x }) });
        using var handler = new FakeHandler(i => Json(i switch
        {
            0 => """{"categoryAttributes":[{"attribute":{"id":7,"name":"Renk"},"required":true,"allowCustom":false,"allowMultipleAttributeValues":false}]}""",
            1 => values,
            _ => """{"totalPages":2,"page":1,"content":[{"attributeValueId":1001,"attributeValue":"Son"}]}"""
        }));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        var attributes = await client.GetAttributesAsync(12);
        Assert.AreEqual(1001, attributes.Single().Values.Count);
        Assert.IsTrue(attributes[0].Required);
        StringAssert.EndsWith(handler.Requests[0].Url, "/categories/12/attributes");
        StringAssert.EndsWith(handler.Requests[2].Url, "/categories/12/attributes/7/values?page=1&size=1000");
    }

    [TestMethod]
    public async Task ProductsMapApprovedAndUnapprovedV2Shapes()
    {
        using var handler = new FakeHandler(i => Json(i == 0
            ? """{"page":0,"totalPages":1,"totalElements":1,"content":[{"contentId":8,"title":"Onaylı","variants":[{"supplierId":42,"barcode":"B1","stockCode":"S1","stock":{"quantity":3},"price":{"salePrice":12.5,"listPrice":15}}]}]}"""
            : """{"page":0,"totalPages":1,"totalElements":1,"content":[{"supplierId":42,"barcode":"B2","stockCode":"S2","title":"Taslak","quantity":2,"salePrice":20,"listPrice":25}]}"""));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        var approved = (await client.GetProductsAsync(true)).Single();
        var draft = (await client.GetProductsAsync(false)).Single();
        Assert.AreEqual(8L, approved.ContentId);
        Assert.AreEqual(12.5m, approved.SalePrice);
        Assert.AreEqual(3, approved.Quantity);
        Assert.IsTrue(approved.Approved);
        Assert.AreEqual("B2", draft.Barcode);
        Assert.AreEqual(0L, draft.ContentId);
        Assert.IsFalse(draft.Approved);
        StringAssert.EndsWith(handler.Requests[0].Url, "/sellers/42/products/approved?size=100&page=0");
        StringAssert.EndsWith(handler.Requests[1].Url, "/sellers/42/products/unapproved?size=100&page=0");
    }

    [DataTestMethod]
    [DataRow("\"blacklisted\":true,\"archived\":true,\"locked\":true,\"docNeeded\":true,\"onSale\":true", "blacklisted")]
    [DataRow("\"blacklisted\":false,\"archived\":true,\"locked\":true,\"docNeeded\":true,\"onSale\":true", "archived")]
    [DataRow("\"locked\":true,\"docNeeded\":true,\"onSale\":true", "locked")]
    [DataRow("\"docNeeded\":true,\"onSale\":true", "documentRequired")]
    [DataRow("\"onSale\":true", "onSale")]
    [DataRow("\"onSale\":false", "notOnSale")]
    [DataRow("\"locked\":false", "approved")]
    [DataRow("\"onSale\":null", "approved")]
    [DataRow("", "approved")]
    public async Task ApprovedStatusUsesExplicitFlagsAndNeverInventsAnOnSaleState(string fields, string expectedStatus)
    {
        using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json(ProductStatusResponse(true, fields)))));
        var product = (await client.GetProductsAsync(true)).Single();
        Assert.AreEqual(expectedStatus, product.Status);
        Assert.IsTrue(product.Approved);
    }

    [DataTestMethod]
    [DataRow("\"status\":\"pendingApproval\"", "pendingApproval")]
    [DataRow("\"status\":\"rejected\"", "rejected")]
    [DataRow("\"status\":\"pendingApproval\",\"docNeeded\":true", "documentRequired")]
    [DataRow("\"status\":\"rejected\",\"docNeeded\":true", "documentRequired")]
    [DataRow("", "unapproved")]
    public async Task UnapprovedStatusPreservesTheRemoteReviewStateAndPrioritizesDocuments(string fields, string expectedStatus)
    {
        using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json(ProductStatusResponse(false, fields)))));
        var product = (await client.GetProductsAsync(false)).Single();
        Assert.AreEqual(expectedStatus, product.Status);
        Assert.IsFalse(product.Approved);
    }

    [TestMethod]
    public async Task ProductStatusDetailContainsActualRejectionAndLockReasons()
    {
        using var handler = new FakeHandler(i => Json(i == 0
            ? ProductStatusResponse(false, "\"status\":\"rejected\",\"rejectReasonDetails\":[{\"rejectReason\":\"Hatalı\\tMarka\",\"rejectReasonDetail\":\"Markayı\\r\\nyenileyin.\"},{\"rejectReason\":\"Eksik Görsel\",\"rejectReasonDetail\":\"Görsel yükleyin.\"}]")
            : ProductStatusResponse(true, "\"locked\":true,\"docNeeded\":true,\"lockReason\":\"Belge\\nyetersiz.\"")));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        var rejected = (await client.GetProductsAsync(false)).Single();
        Assert.AreEqual("rejected", rejected.Status);
        StringAssert.Contains(rejected.StatusDetail, "Hatalı Marka: Markayı yenileyin.");
        StringAssert.Contains(rejected.StatusDetail, "Eksik Görsel: Görsel yükleyin.");
        var locked = (await client.GetProductsAsync(true)).Single();
        Assert.AreEqual("locked", locked.Status);
        StringAssert.Contains(locked.StatusDetail, "Belge yetersiz.");
        StringAssert.Contains(locked.StatusDetail, "belge");
    }

    [DataTestMethod]
    [DataRow(true, "\"blacklisted\":true,\"onSale\":\"false\"")]
    [DataRow(true, "\"archived\":1")]
    [DataRow(true, "\"locked\":[]")]
    [DataRow(true, "\"docNeeded\":\"true\"")]
    [DataRow(true, "\"lockReason\":12")]
    [DataRow(true, "\"lockReason\":\"Reason\\u0000\"")]
    [DataRow(false, "\"status\":true")]
    [DataRow(false, "\"status\":\"pendingApproval\\t\"")]
    [DataRow(false, "\"rejectReasonDetails\":{}")]
    [DataRow(false, "\"rejectReasonDetails\":[{\"rejectReason\":false}]")]
    [DataRow(false, "\"rejectReasonDetails\":[{\"rejectReasonDetail\":\"Reason\\u001b\"}]")]
    public async Task ProductStatusRejectsInvalidTypesAndUnsafeControlsAtomically(bool approved, string fields)
    {
        using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json(ProductStatusResponse(approved, fields)))));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetProductsAsync(approved));
    }

    [TestMethod]
    public void OldRemoteProductCacheDefaultsNewStatusFieldsToEmpty()
    {
        var oldJson = """{"Barcode":"B1","StockCode":"S1","Title":"Product","ContentId":8,"Quantity":1,"SalePrice":5,"ListPrice":7,"Approved":true}""";
        var product = JsonSerializer.Deserialize<TrendyolRemoteProduct>(oldJson);
        Assert.IsNotNull(product);
        Assert.AreEqual("", product.Status);
        Assert.AreEqual("", product.StatusDetail);
        var positional = new TrendyolRemoteProduct("B1", "S1", "Product", 8, 1, 5, 7, true);
        Assert.AreEqual("", positional.Status);
    }

    static string ProductStatusResponse(bool approved, string fields)
    {
        var item = "\"supplierId\":42,\"barcode\":\"B1\",\"stockCode\":\"S1\"" + (fields.Length > 0 ? "," + fields : "");
        var content = approved ? "{\"contentId\":8,\"title\":\"Product\",\"variants\":[{" + item + "}]}" : "{\"title\":\"Product\"," + item + "}";
        return "{\"page\":0,\"totalPages\":1,\"totalElements\":1,\"content\":[" + content + "]}";
    }

    [TestMethod]
    public async Task ProductsRejectForeignSellerAndIncompletePagination()
    {
        using var foreign = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json("""{"page":0,"totalPages":1,"totalElements":1,"content":[{"supplierId":99,"barcode":"B","stockCode":"S","title":"Other"}]}"""))));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => foreign.GetProductsAsync(false));
        using var incomplete = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json("""{"page":0,"totalPages":2,"totalElements":101,"content":[]}"""))));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => incomplete.GetProductsAsync(false));
    }

    [TestMethod]
    public async Task ProductPaginationSwitchesToEncodedCursorAtTenThousand()
    {
        using var handler = new FakeHandler(i => Json(JsonSerializer.Serialize(new
        {
            page = i, totalPages = 102, totalElements = 10101, nextPageToken = "token/" + i + "+=",
            content = Enumerable.Range(i * 100, i == 101 ? 1 : 100).Select(x => new { supplierId = 42, barcode = "B" + x, stockCode = "S" + x, title = "Product" })
        })));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        var products = await client.GetProductsAsync(false);
        Assert.AreEqual(10101, products.Count);
        StringAssert.Contains(handler.Requests[100].Url, "nextPageToken=token%2F99%2B%3D");
        Assert.IsFalse(handler.Requests[100].Url.Contains("&page="));
    }

    [TestMethod]
    public async Task AddressesCarriersAndBuyboxReadDocumentedContracts()
    {
        using var handler = new FakeHandler(i => Json(i switch
        {
            0 => """{"supplierAddresses":[{"id":9,"fullAddress":"Depo","isShipmentAddress":true,"isReturningAddress":false}]}""",
            1 => """[{"code":"ABC","name":"Kargo"}]""",
            _ => """{"buyboxInfo":[{"barcode":"B1","buyboxOrder":2,"buyboxPrice":10.5,"secondBuyboxPrice":11,"thirdBuyboxPrice":null,"hasMultipleSeller":true}]}"""
        }));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        Assert.IsTrue((await client.GetAddressesAsync()).Single().IsShipment);
        Assert.AreEqual("ABC", (await client.GetCarriersAsync()).Single().Code);
        var buybox = (await client.GetBuyboxAsync(new[] { "B1" })).Single();
        Assert.AreEqual(2, buybox.Rank);
        Assert.AreEqual(10.5m, buybox.FirstPrice);
        Assert.IsNull(buybox.ThirdPrice);
        Assert.AreEqual("POST", handler.Requests[2].Method);
        StringAssert.EndsWith(handler.Requests[2].Url, "/products/buybox-information");
        Assert.AreEqual("{\"barcodes\":[\"B1\"]}", handler.Requests[2].Body);
    }

    [TestMethod]
    public async Task BuyboxSplitsAtTenAndRejectsUnexpectedResponseBarcode()
    {
        using var handler = new FakeHandler(i => Json("{\"buyboxInfo\":[" + string.Join(",", Enumerable.Range(i * 10, i == 0 ? 10 : 1).Select(x => "{\"barcode\":\"B" + x + "\",\"hasMultipleSeller\":false}")) + "]}"));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        Assert.AreEqual(11, (await client.GetBuyboxAsync(Enumerable.Range(0, 11).Select(x => "B" + x))).Count);
        Assert.AreEqual(2, handler.Requests.Count);
        using var wrong = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json("""{"buyboxInfo":[{"barcode":"OTHER","hasMultipleSeller":false}]}"""))));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => wrong.GetBuyboxAsync(new[] { "B1" }));
    }

    [DataTestMethod]
    [DataRow(302)]
    [DataRow(401)]
    [DataRow(429)]
    [DataRow(500)]
    public async Task HttpErrorsNeverExposeResponseBodyOrCredentialsAndDoNotRetry(int status)
    {
        using var handler = new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("fixture-key fixture-secret remote-sensitive") });
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => client.GetCategoriesAsync());
        Assert.IsFalse(error.ToString().Contains("fixture-"));
        Assert.IsFalse(error.ToString().Contains("remote-sensitive"));
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task MalformedAndOversizedResponsesHaveSafeErrors()
    {
        foreach (var body in new[] { "fixture-secret:broken", "{}", "{\"categories\":\"fixture-secret\"}", new string('x', 8 * 1024 * 1024 + 1) })
        {
            using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json(body))));
            var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetCategoriesAsync());
            Assert.IsFalse(error.ToString().Contains("fixture-secret"));
        }
    }

    [TestMethod]
    public async Task NetworkErrorsAreSanitizedAndCancellationStaysCancellation()
    {
        using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => throw new HttpRequestException("fixture-secret"))));
        var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => client.GetCategoriesAsync());
        Assert.IsFalse(error.ToString().Contains("fixture-secret"));
        using var handler = new FakeHandler(_ => Json("{\"categories\":[]}"));
        using var cancelled = new TrendyolApiClient(Settings(), new HttpClient(handler));
        using var source = new CancellationTokenSource();
        source.Cancel();
        try { await cancelled.GetCategoriesAsync(source.Token); Assert.Fail("Cancellation expected."); }
        catch (OperationCanceledException) { }
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task SharedHttpClientKeepsAccountHeadersIsolatedAndIsNotDisposed()
    {
        using var handler = new FakeHandler(_ => Json("{\"supplierAddresses\":[]}"));
        using var http = new HttpClient(handler);
        using (var first = new TrendyolApiClient(Settings("42"), http)) await first.GetAddressesAsync();
        using var second = new TrendyolApiClient(new TrendyolSettings("77", "other-key", "other-secret", "Client"), http);
        await second.GetAddressesAsync();
        StringAssert.Contains(handler.Requests[1].Url, "/sellers/77/");
        Assert.AreNotEqual(handler.Requests[0].Authorization, handler.Requests[1].Authorization);
    }

    [TestMethod]
    public void ClientExposesTheExactCredentialFingerprintForDispatcherAccountGuard()
    {
        using var client = new TrendyolApiClient(Settings(), new HttpClient(new FakeHandler(_ => Json("{}"))));
        var property = typeof(TrendyolApiClient).GetProperty("AccountFingerprint");
        Assert.IsNotNull(property);
        Assert.AreEqual(TrendyolWorkspaceStore.AccountFingerprint(Settings()), property.GetValue(client));
        Assert.AreNotEqual(TrendyolWorkspaceStore.AccountFingerprint(Settings("77")), property.GetValue(client));
    }

    [TestMethod]
    public async Task BatchIdentifierCannotNavigateToAParentEndpoint()
    {
        using var handler = new FakeHandler(_ => Json("{}"));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => client.GetBatchAsync(".."));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => client.GetBatchAsync("."));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task DetailsEndpointRejectsAttributeAndOtherDeferredVariantFields()
    {
        using var handler = new FakeHandler(_ => Json("{\"batchRequestId\":\"id\"}"));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => SendBatch(client, "details", "{\"items\":[{\"barcode\":\"B1\",\"attributes\":[]}]}"));
        await SendBatch(client, "details", "{\"items\":[{\"barcode\":\"B1\",\"cargoProviders\":[\"ABC\"],\"shipmentAddressId\":8,\"returningAddressId\":9}]}" );
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task StreamLengthLimitAlsoAppliesWithoutContentLengthHeader()
    {
        using var stream = new NonSeekingStream(Encoding.UTF8.GetBytes(new string(' ', 8 * 1024 * 1024 + 1)));
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetCategoriesAsync());
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task TimeoutBoundsResponseBodyReadsAfterHeadersArrive()
    {
        using var stream = new WaitingStream();
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        using var client = new TrendyolApiClient(Settings(), http);
        var request = client.GetCategoriesAsync();
        Assert.AreSame(request, await Task.WhenAny(request, Task.Delay(1000)), "Response body reads must respect the request deadline.");
        var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => request);
        Assert.IsFalse(error.ToString().Contains("fixture-secret"));
    }

    [TestMethod]
    public async Task RepeatedProductCursorFailsWithoutReturningPartialProducts()
    {
        using var handler = new FakeHandler(i => Json(JsonSerializer.Serialize(new
        {
            page = i, totalPages = 102, totalElements = 10200, nextPageToken = "same",
            content = Enumerable.Range(i * 100, 100).Select(x => new { supplierId = 42, barcode = "B" + x, stockCode = "S" + x, title = "Product" })
        })));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetProductsAsync(false));
        Assert.AreEqual(101, handler.Requests.Count);
    }

    [TestMethod]
    public async Task BatchReadReturnsIndependentJsonAndEncodesIdentifier()
    {
        using var handler = new FakeHandler(_ => Json("""{"batchRequestId":"batch/id","status":"COMPLETED"}"""));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        var result = await client.GetBatchAsync("batch/id");
        Assert.AreEqual("COMPLETED", result.GetProperty("status").GetString());
        StringAssert.EndsWith(handler.Requests[0].Url, "/batch-requests/batch%2Fid");
    }

    [DataTestMethod]
    [DataRow("create", "/integration/product/sellers/42/v2/products")]
    [DataRow("inventory", "/integration/inventory/sellers/42/products/price-and-inventory")]
    [DataRow("delivery", "/integration/product/sellers/42/products/delivery-info-bulk-update")]
    [DataRow("details", "/integration/product/sellers/42/products/variant-bulk-update")]
    public async Task InternalBatchSendUsesOnlyVerifiedOperationEndpoints(string operation, string path)
    {
        using var handler = new FakeHandler(_ => Json("""{"batchRequestId":"accepted-1"}"""));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        Assert.AreEqual("accepted-1", await SendBatch(client, operation, "{\"items\":[{\"barcode\":\"B1\"}]}"));
        Assert.AreEqual("POST", handler.Requests[0].Method);
        StringAssert.EndsWith(handler.Requests[0].Url, path);
        Assert.IsNull(typeof(TrendyolApiClient).GetMethod("SendBatchAsync", BindingFlags.Public | BindingFlags.Instance));
    }

    [TestMethod]
    public async Task BatchSendRejectsUnsupportedOperationAndEmptyOrOversizedItemsBeforeTransport()
    {
        using var handler = new FakeHandler(_ => Json("{}"));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => SendBatch(client, "delete", "{\"items\":[{}]}"));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => SendBatch(client, "inventory", "{\"items\":[]}"));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => SendBatch(client, "inventory", "{\"items\":[" + string.Join(",", Enumerable.Repeat("{}", 1001)) + "]}"));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task UnapprovedUpdatePreservesReviewedContentAndUsesTheRecoveryEndpoint()
    {
        const string payload = """{"items":[{"barcode":"B1","title":"Düzeltilmiş başlık","description":"Açıklama","productMainId":"S1","brandId":5,"categoryId":8,"stockCode":"S1","origin":"TR","dimensionalWeight":1.2,"vatRate":20,"deliveryOption":{"deliveryDuration":2},"cargoProviders":["ABC"],"shipmentAddressId":4,"returningAddressId":6,"images":[{"url":"https://example.com/image.jpg"}],"attributes":[{"attributeId":1,"attributeValueId":7}]}]}""";
        using var handler = new FakeHandler(_ => Json("{\"batchRequestId\":\"recovery-1\"}"));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        Assert.AreEqual("recovery-1", await SendBatch(client, "unapproved", payload));
        Assert.AreEqual("POST", handler.Requests.Single().Method);
        Assert.AreEqual("https://apigw.trendyol.com/integration/product/sellers/42/products/unapproved-bulk-update", handler.Requests.Single().Url);
        Assert.AreEqual(payload, handler.Requests.Single().Body);
    }

    [TestMethod]
    public async Task ApprovedContentUpdateSendsOnlyTheReviewedPartialContent()
    {
        const string payload = """{"items":[{"contentId":17,"title":"Yeni başlık","description":"Yeni açıklama","images":[{"url":"https://example.com/image.jpg"}]}]}""";
        using var handler = new FakeHandler(_ => Json("{\"batchRequestId\":\"content-1\"}"));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        Assert.AreEqual("content-1", await SendBatch(client, "content", payload));
        Assert.AreEqual("POST", handler.Requests.Single().Method);
        Assert.AreEqual("https://apigw.trendyol.com/integration/product/sellers/42/products/content-bulk-update", handler.Requests.Single().Url);
        Assert.AreEqual(payload, handler.Requests.Single().Body);
        await SendBatch(client, "content", "{\"items\":[{\"contentId\":17,\"description\":\"Only description\"}]}");
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [DataTestMethod]
    [DataRow("unapproved", "quantity")]
    [DataRow("unapproved", "salePrice")]
    [DataRow("unapproved", "listPrice")]
    [DataRow("unapproved", "variants")]
    [DataRow("unapproved", "channels")]
    [DataRow("content", "quantity")]
    [DataRow("content", "salePrice")]
    [DataRow("content", "listPrice")]
    [DataRow("content", "attributes")]
    [DataRow("content", "barcode")]
    [DataRow("content", "categoryId")]
    [DataRow("content", "brandId")]
    [DataRow("content", "variants")]
    public async Task RecoveryAndContentRejectUnreviewedOrUnsupportedFieldsBeforeTransport(string operation, string forbiddenField)
    {
        using var handler = new FakeHandler(_ => Json("{\"batchRequestId\":\"accepted\"}"));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        var item = operation == "content" ? "{\"contentId\":17,\"description\":\"Reviewed\"" : "{\"barcode\":\"B1\",\"title\":\"Reviewed\"";
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => SendBatch(client, operation, "{\"items\":[" + item + ",\"" + forbiddenField + "\":1}]}"));
        Assert.AreEqual(0, handler.Requests.Count);
        await SendBatch(client, operation, "{\"items\":[" + item + "}]}");
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ApprovedContentUpdateRequiresContentIdentityAndAnActualPartialUpdate()
    {
        using var handler = new FakeHandler(_ => Json("{\"batchRequestId\":\"accepted\"}"));
        using var client = new TrendyolApiClient(Settings(), new HttpClient(handler));
        foreach (var item in new[] { "{\"description\":\"A\"}", "{\"contentId\":0,\"description\":\"A\"}", "{\"contentId\":17}" })
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => SendBatch(client, "content", "{\"items\":[" + item + "]}"));
        Assert.AreEqual(0, handler.Requests.Count);
        await SendBatch(client, "content", "{\"items\":[{\"contentId\":17,\"description\":\"Reviewed\"}]}");
        Assert.AreEqual(1, handler.Requests.Count);
    }

    static async Task<string> SendBatch(TrendyolApiClient client, string operation, string payload)
    {
        var method = typeof(TrendyolApiClient).GetMethod("SendBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(method);
        try { return await (Task<string>)method.Invoke(client, new object[] { operation, payload, CancellationToken.None }); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    sealed class NonSeekingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    sealed class WaitingStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
    sealed record Request(string Url, string Method, string Authorization, string UserAgent, string Body);
    sealed class FakeHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.RequestUri.AbsoluteUri, request.Method.Method, request.Headers.Authorization?.ToString(), string.Join(" ", request.Headers.GetValues("User-Agent")), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return response(Requests.Count - 1);
        }
    }
}
