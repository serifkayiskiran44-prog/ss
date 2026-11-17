using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Hepsiburada;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class HepsiburadaDispatchTests
{
    string root = null!;
    MarketplaceConnection account = null!;
    CatalogProduct product = null!;

    [TestInitialize]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "hb-dispatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var connections = new MarketplaceConnectionStore(root);
        account = connections.Save("hepsiburada", "merchant-a", "HB A", true);
        var catalog = new CatalogStore(root);
        catalog.Import(new XmlSource { Id = "src", Location = "https://example.test/feed.xml" },
            [new CatalogProduct { SourceId = "src", Sku = "SKU-1", Barcode = "8690001", Name = "Ürün", Price = 123.45m, Stock = 7, Currency = "TRY", Category = "Ev", Brand = "Marka" }]);
        product = catalog.Products().Single();
        new ProductChannelBindingStore(root).Save(new(product.Id, account.Id, "HB-1", product.Sku, product.Barcode, true, true, true, "", "", "Active", 0, default), 0);
        new HepsiburadaWorkspaceStore(root).ReplaceProducts(account.Id, account.ShopId, [new(account.ShopId, product.Sku, product.Barcode, "HB-1")]);
    }

    [TestCleanup] public void Cleanup() { try { Directory.Delete(root, true); } catch { } }

    [TestMethod]
    public void StockPreviewContainsOnlyStockIdentityAndQuantity()
    {
        var plan = new HepsiburadaDispatchStore(root).Preview(account.Id, [product.Id], HepsiburadaOperation.Stock);
        using var json = JsonDocument.Parse(plan.PayloadJson);
        var row = json.RootElement.GetProperty("listings")[0];
        Assert.AreEqual(product.Barcode, row.GetProperty("barcode").GetString());
        Assert.AreEqual(7, row.GetProperty("availableStock").GetInt32());
        Assert.IsFalse(row.TryGetProperty("price", out _));
        Assert.IsFalse(row.TryGetProperty("title", out _));
    }

    [TestMethod]
    public void ClaimRequiresApprovalAndRejectsChangedCatalog()
    {
        var store = new HepsiburadaDispatchStore(root);
        var plan = store.Preview(account.Id, [product.Id], HepsiburadaOperation.Price);
        Assert.ThrowsException<InvalidOperationException>(() => store.Claim(plan.Id, account.Id, false));
        product.Price++;
        new CatalogStore(root).SaveProduct(product);
        Assert.ThrowsException<InvalidOperationException>(() => store.Claim(plan.Id, account.Id, true));
        Assert.AreEqual(0, store.Receipts(account.Id).Count);
    }

    [TestMethod]
    public async Task TransportFailureBecomesUncertainAndCannotReplay()
    {
        var store = new HepsiburadaDispatchStore(root);
        var plan = store.Preview(account.Id, [product.Id], HepsiburadaOperation.Stock);
        var dispatch = new HepsiburadaDispatch(store, new FailingTransport());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => dispatch.SendAsync(plan.Id, account.Id, true));
        Assert.AreEqual("Belirsiz", store.Receipts(account.Id).Single().Status);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => dispatch.SendAsync(plan.Id, account.Id, true));
    }

    [TestMethod]
    public void BlankBarcodeCannotBePreviewed()
    {
        var catalog = new CatalogStore(root);
        catalog.Import(new XmlSource { Id = "blank-src", Location = "https://example.test/blank.xml" },
            [new CatalogProduct { SourceId = "blank-src", Sku = "SKU-BLANK", Barcode = "", Name = "Barkodsuz", Price = 10, Stock = 1, Currency = "TRY", Category = "Ev", Brand = "Marka" }]);
        var blank = catalog.Products().Single(x => x.Sku == "SKU-BLANK");
        var plan = new HepsiburadaDispatchStore(root).Preview(account.Id, [blank.Id], HepsiburadaOperation.CatalogCreate);
        Assert.AreEqual("Hatalı", plan.Rows.Single().Status);
        Assert.IsTrue(plan.Rows.Single().Error.Contains("Barkod", StringComparison.Ordinal));
    }

    sealed class FailingTransport : IHepsiburadaWriteTransport
    {
        public Task<string> SendAsync(HepsiburadaOperation operation, string payloadJson, CancellationToken cancellationToken) => throw new HttpRequestException("connection lost");
    }
}
