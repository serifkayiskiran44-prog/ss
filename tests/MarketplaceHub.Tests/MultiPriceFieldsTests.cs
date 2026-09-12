using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;

[TestClass]
public sealed class MultiPriceFieldsTests
{
    static (CatalogStore Store, string Root) NewStore([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var root = Path.Combine(Path.GetTempPath(), "multi-price-" + caller + "-" + Guid.NewGuid().ToString("N"));
        return (new CatalogStore(root), root);
    }

    static CatalogProduct SeedProduct(CatalogStore store)
    {
        var source = new XmlSource { Id = "fixture", Name = "Fixture" };
        store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "SKU-1", Name = "Product", Cost = 100m, Price = 150m } });
        return store.Products().Single();
    }

    static void Cleanup(string root) { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

    [TestMethod]
    public void PricePolicy_UsesSelectedPriceFieldWhenConfigured()
    {
        var (store, root) = NewStore();
        try
        {
            var product = SeedProduct(store);
            store.AddPriceField(product.Id, new CatalogPriceField("Etsy sabit", 219.90m, "TRY"));
            var policy = store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop-1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, PriceFieldName = "Etsy sabit" });

            var preview = store.PreviewPrice(policy.Channel, policy.Shop, product.Id);

            Assert.AreEqual(219.90m, preview.Price);
            Assert.AreEqual("TRY", preview.Currency);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void PricePolicy_FallsBackToLegacyPriceWhenNoFieldSelected()
    {
        var (store, root) = NewStore();
        try
        {
            var product = SeedProduct(store);
            var policy = store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop-1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1 });

            var preview = store.PreviewPrice(policy.Channel, policy.Shop, product.Id);

            Assert.AreEqual(200m, preview.Price); // formula x*2 against Cost=100
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void Migration_ExistingProductsWithoutPriceFieldsKeepLegacyPriceUnchanged()
    {
        var (store, root) = NewStore();
        try
        {
            var source = new XmlSource { Id = "fixture", Name = "Fixture" };
            // Simulates a pre-existing record written before PriceFields existed: JSON without that property.
            store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "LEGACY", Name = "Legacy product", Cost = 40m, Price = 80m } });

            var reloaded = store.Products().Single(x => x.Sku == "LEGACY");

            Assert.AreEqual(80m, reloaded.Price);
            Assert.AreEqual(40m, reloaded.Cost);
            Assert.AreEqual(0, reloaded.PriceFields.Count);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void PreviewPrice_ThrowsInsteadOfSilentZeroWhenConfiguredFieldNameDoesNotExistOnProduct()
    {
        var (store, root) = NewStore();
        try
        {
            var product = SeedProduct(store);
            var policy = store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop-1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, PriceFieldName = "Olmayan alan" });

            var ex = Assert.ThrowsException<InvalidOperationException>(() => store.PreviewPrice(policy.Channel, policy.Shop, product.Id));
            StringAssert.Contains(ex.Message, "Olmayan alan");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AddPriceField_RejectsDuplicateNameOnSameProduct()
    {
        var (store, root) = NewStore();
        try
        {
            var product = SeedProduct(store);
            store.AddPriceField(product.Id, new CatalogPriceField("Wholesale", 90m, "TRY"));

            Assert.ThrowsException<InvalidOperationException>(() => store.AddPriceField(product.Id, new CatalogPriceField("wholesale", 95m, "TRY")));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void UpdateAndRemovePriceField_RequireAnExistingNamedField()
    {
        var (store, root) = NewStore();
        try
        {
            var product = SeedProduct(store);
            Assert.ThrowsException<InvalidOperationException>(() => store.UpdatePriceField(product.Id, new CatalogPriceField("Ghost", 1m, "TRY")));
            Assert.ThrowsException<InvalidOperationException>(() => store.RemovePriceField(product.Id, "Ghost"));

            store.AddPriceField(product.Id, new CatalogPriceField("Wholesale", 90m, "TRY"));
            var updated = store.UpdatePriceField(product.Id, new CatalogPriceField("Wholesale", 95m, "TRY"));
            Assert.AreEqual(95m, updated.PriceFields.Single().Value);

            var removed = store.RemovePriceField(product.Id, "wholesale");
            Assert.AreEqual(0, removed.PriceFields.Count);
        }
        finally { Cleanup(root); }
    }
}
