using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2605: PreviewPrice must fail closed for a disabled policy,
/// checked before any formula/rate work - never silently reusing stale
/// policy data to keep producing prices.
[TestClass]
public sealed class PricePolicyFailClosedTests
{
    static CatalogStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "pricepolicy-failclosed-" + Guid.NewGuid().ToString("N"));
        return new CatalogStore(root);
    }

    static CatalogProduct Product() => new() { Sku = "SKU-1", Name = "Ürün", Cost = 10, Price = 20, Stock = 1, Active = true };

    [TestMethod]
    public void EnabledPolicyStillProducesAPreview()
    {
        var store = NewStore(out var root);
        try
        {
            var product = store.CreateManual(Product());
            store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true });

            var preview = store.PreviewPrice("etsy", "shop1", product.Id);
            Assert.AreEqual(20m, preview.Price);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DisabledPolicyNeverProducesAPreviewEvenWithValidProductAndFormula()
    {
        var store = NewStore(out var root);
        try
        {
            var product = store.CreateManual(Product());
            store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = false });

            var ex = Assert.ThrowsException<InvalidOperationException>(() => store.PreviewPrice("etsy", "shop1", product.Id));
            StringAssert.Contains(ex.Message, "pasif");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DisabledPolicyWithOverflowFormulaStillFailsOnEnabledCheckNotFormulaEvaluation()
    {
        var store = NewStore(out var root);
        try
        {
            var product = store.CreateManual(Product());
            // Formula that would throw/overflow if actually evaluated - proves
            // Enabled is checked before any formula work, not just before the result.
            store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop1", Formula = "x*999999999999999999", Currency = "TRY", TryPerUnit = 1, Enabled = false });

            var ex = Assert.ThrowsException<InvalidOperationException>(() => store.PreviewPrice("etsy", "shop1", product.Id));
            StringAssert.Contains(ex.Message, "pasif");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TogglingDisabledThenEnabledRestoresPreviewDeterministically()
    {
        var store = NewStore(out var root);
        try
        {
            var product = store.CreateManual(Product());
            var saved = store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = false });
            Assert.ThrowsException<InvalidOperationException>(() => store.PreviewPrice("etsy", "shop1", product.Id));

            saved.Enabled = true;
            store.SavePricePolicy(saved);
            var preview = store.PreviewPrice("etsy", "shop1", product.Id);
            Assert.AreEqual(20m, preview.Price);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void EnabledThenDisabledBlocksSubsequentPreviews()
    {
        var store = NewStore(out var root);
        try
        {
            var product = store.CreateManual(Product());
            var saved = store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true });
            store.PreviewPrice("etsy", "shop1", product.Id);

            saved.Enabled = false;
            store.SavePricePolicy(saved);
            Assert.ThrowsException<InvalidOperationException>(() => store.PreviewPrice("etsy", "shop1", product.Id));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WrongShopPolicyIsNeverUsedForPreview()
    {
        var store = NewStore(out var root);
        try
        {
            var product = store.CreateManual(Product());
            store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true });
            Assert.ThrowsException<InvalidOperationException>(() => store.PreviewPrice("etsy", "shop2", product.Id));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MissingPolicyStillReportsItsOwnDistinctError()
    {
        var store = NewStore(out var root);
        try
        {
            var product = store.CreateManual(Product());
            var ex = Assert.ThrowsException<InvalidOperationException>(() => store.PreviewPrice("etsy", "shop1", product.Id));
            StringAssert.Contains(ex.Message, "kaydedin");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MinimumMarginAndPriceGuardsStillEnforcedWhenEnabled()
    {
        var store = NewStore(out var root);
        try
        {
            var product = store.CreateManual(Product());
            store.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "shop1", Formula = "x*1.01", Currency = "TRY", TryPerUnit = 1, MinimumMarginTry = 50, Enabled = true });
            var ex = Assert.ThrowsException<InvalidOperationException>(() => store.PreviewPrice("etsy", "shop1", product.Id));
            StringAssert.Contains(ex.Message, "koruma");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
