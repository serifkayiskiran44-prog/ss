using System;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #796 (DESIGN: Product quick-inspect drawer). The drawer is read-only by construction: it is built as a list
// of label/value rows, so there is nothing in the model that could mutate a product. What it must get right is
// covering the five things the issue names (identity, price, stock, source/readiness, last error), saying "—"
// instead of nothing when data is missing, and never carrying a secret or a customer's details.
[TestClass]
public sealed class ProductQuickInspectTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    static CatalogProduct Product(Action<CatalogProduct>? tweak = null)
    {
        var p = new CatalogProduct
        {
            Id = "p1", Sku = "SKU-1", Barcode = "869000000001", Name = "Ürün A", Brand = "Marka", Category = "Kategori",
            Price = 149.90m, Currency = "TRY", Cost = 90m, CostCurrency = "TRY", Stock = 7, Active = true,
            SourceKind = "xml", SourceId = "src-1", SourceUpdatedUtc = Now.AddHours(-3), UpdatedUtc = Now.AddHours(-3),
            ImageUrls = "https://cdn.example/a.jpg", Description = "Açıklama",
        };
        tweak?.Invoke(p); return p;
    }

    static SyncJob Job(SyncStatus status, string error, DateTime at) => new()
    { Channel = "etsy", ShopId = "shop-1", Operation = "price", EntityId = "p1", Version = "v", Status = status, LastError = error, UpdatedUtc = at };

    static string Value(ProductQuickInspectView view, string label) => view.Rows.Single(r => r.Label == label).Value;

    [TestMethod]
    public void TheDrawerCoversIdentityPriceStockSourceReadinessAndTheLastError()
    {
        var view = ProductQuickInspect.Build(Product(), [Job(SyncStatus.Succeeded, "", Now.AddHours(-5)), Job(SyncStatus.Failed, "Etsy 429: istek sınırı", Now.AddHours(-2))], Now);

        CollectionAssert.AreEquivalent(new[] { "Kimlik", "Fiyat", "Stok", "Kaynak", "Hazırlık", "Son hata" }, view.Sections.ToArray(), "The drawer answers exactly the questions the row cannot.");
        StringAssert.Contains(Value(view, "SKU"), "SKU-1");
        StringAssert.Contains(Value(view, "Satış fiyatı"), 149.90m.ToString("N2", CultureInfo.CurrentCulture), "Prices are formatted for the operator's locale, so the expectation is computed the same way.");
        StringAssert.Contains(Value(view, "Satış fiyatı"), "TRY");
        Assert.AreEqual("7", Value(view, "Stok"));
        StringAssert.Contains(Value(view, "Kaynak türü"), "xml");
        StringAssert.Contains(Value(view, "Son kaynak güncellemesi"), "3 saat");
        StringAssert.Contains(Value(view, "Son hata"), "429", "The most recent failure is the one shown...");
        Assert.IsFalse(Value(view, "Son hata").Contains("Succeeded", StringComparison.Ordinal), "...and a later success does not erase it, nor a success get reported as an error.");
        Assert.IsTrue(view.Rows.All(r => r.Value.Length > 0), "Every row shows something.");
    }

    [TestMethod]
    public void ReadinessIsDerivedFromWhatTheProductActuallyLacks()
    {
        var ready = ProductQuickInspect.Build(Product(p => p.EtsyListingId = "12345"), [], Now);
        StringAssert.Contains(Value(ready, "Durum"), "Hazır");

        var incomplete = ProductQuickInspect.Build(Product(p => { p.Description = ""; p.ImageUrls = ""; p.Price = 0m; }), [], Now);
        var blockers = Value(incomplete, "Eksikler");
        StringAssert.Contains(blockers, "görsel");
        StringAssert.Contains(blockers, "açıklama");
        StringAssert.Contains(blockers, "fiyat");
        StringAssert.Contains(Value(incomplete, "Durum"), "Eksik");
    }

    [TestMethod]
    public void MissingDataIsShownAsAnExplicitDashRatherThanAnEmptyRowOrACrash()
    {
        var bare = ProductQuickInspect.Build(new CatalogProduct { Id = "p2", Sku = "", Name = "" }, [], Now);

        Assert.AreEqual("—", Value(bare, "SKU"));
        Assert.AreEqual("—", Value(bare, "Barkod"));
        Assert.AreEqual("—", Value(bare, "Son hata"));
        Assert.AreEqual("—", Value(bare, "Son kaynak güncellemesi"), "A product no feed ever touched says so instead of showing an epoch date.");
        Assert.IsTrue(bare.Rows.All(r => !string.IsNullOrWhiteSpace(r.Value)));
        StringAssert.Contains(Value(bare, "Eksikler"), "SKU");
    }

    [TestMethod]
    public void NothingSensitiveTravelsIntoTheDrawerHoweverItGotIntoTheData()
    {
        var product = Product(p => { p.Name = "Ürün Authorization: Bearer abc123secret"; p.Description = "musteri@example.com 0532 111 22 33"; });
        var view = ProductQuickInspect.Build(product, [Job(SyncStatus.Failed, "401 access_token=zzz999token", Now)], Now);

        var all = string.Join("\n", view.Rows.Select(r => r.Label + ": " + r.Value));
        Assert.IsFalse(all.Contains("abc123secret", StringComparison.Ordinal), all);
        Assert.IsFalse(all.Contains("zzz999token", StringComparison.Ordinal), all);
        Assert.IsFalse(all.Contains("musteri@example.com", StringComparison.Ordinal), "A customer address pasted into a description must not resurface in a hover-level view: " + all);
        Assert.IsTrue(view.Rows.All(r => r.Value.Length <= 300), "No single row can become a wall of text.");
    }
}
