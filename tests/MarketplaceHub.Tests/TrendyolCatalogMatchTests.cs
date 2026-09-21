using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using TrMarketplaceHubDesktop.Trendyol;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class TrendyolCatalogMatchTests
{
    [TestMethod]
    public void ApplyUsesGtinAndUpdatesOnlySelectedProfiles()
    {
        var state = new TrendyolWorkspaceState { SellerId = "42" };
        state.Categories.Add(new(123, "Mama", "Evcil Hayvan > Mama", true));
        state.Brands.Add(new(456, "Örnek Marka"));
        state.Products.Add(new("8680000000001", "REMOTE", "Katalog ürünü", 8, 1, 10, 11, true) { CategoryId = 123, BrandId = 456 });
        state.Profiles.Add(new() { ProductId = "selected", Title = "Korunacak başlık" });
        state.Profiles.Add(new() { ProductId = "other", CategoryId = 9, BrandId = 10 });

        var result = TrendyolCatalogMatch.Apply(state,
        [
            new("selected", "SKU-1", "", "8680000000001"),
        ]);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(123L, state.Profiles.Single(p => p.ProductId == "selected").CategoryId);
        Assert.AreEqual(456L, state.Profiles.Single(p => p.ProductId == "selected").BrandId);
        Assert.AreEqual("8680000000001", state.Profiles.Single(p => p.ProductId == "selected").IntegrationCode);
        Assert.AreEqual("Korunacak başlık", state.Profiles.Single(p => p.ProductId == "selected").Title);
        Assert.AreEqual(9L, state.Profiles.Single(p => p.ProductId == "other").CategoryId);
    }

    [TestMethod]
    public void PreviewRejectsAmbiguousBarcodeMatches()
    {
        var state = new TrendyolWorkspaceState { SellerId = "42" };
        state.Products.Add(new("X", "A", "Bir", 1, 1, 1, 1, true) { CategoryId = 2, BrandId = 3 });
        state.Products.Add(new("Y", "B", "İki", 2, 1, 1, 1, true) { CategoryId = 4, BrandId = 5 });
        var result = TrendyolCatalogMatch.Preview(state, [new("p", "SKU", "X", "Y")]).Single();
        Assert.IsFalse(result.CanApply);
        StringAssert.Contains(result.Detail, "birbiriyle çelişiyor");
    }
}
