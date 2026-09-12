using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #795 (DESIGN: Product list selection summary bar). The bar has to answer three questions honestly before the
// operator presses a bulk action: how many rows are selected, what the selection actually covers now that the
// list may have been filtered underneath it, and which of the risky actions can really run on that set.
[TestClass]
public sealed class ProductSelectionSummaryTests
{
    static CatalogProduct Product(string sku, Action<CatalogProduct>? tweak = null)
    {
        var p = new CatalogProduct { Id = "id-" + sku, Sku = sku, Name = "Ürün " + sku, Active = true, Price = 10, Stock = 1 };
        tweak?.Invoke(p); return p;
    }

    [TestMethod]
    public void ZeroOneAndAThousandSelectedRowsEachGetTheirOwnHonestHeadline()
    {
        var visible = Enumerable.Range(0, 1000).Select(i => Product("S" + i)).ToList();

        var none = ProductSelectionSummary.Describe([], visible, matchTotal: 5000);
        Assert.AreEqual(0, none.Count);
        Assert.IsFalse(none.IsVisible, "With nothing selected the bar stays out of the way.");

        var one = ProductSelectionSummary.Describe([visible[0]], visible, 5000);
        Assert.AreEqual(1, one.Count);
        Assert.IsTrue(one.IsVisible);
        StringAssert.Contains(one.Headline, "1 ürün seçildi");

        var thousand = ProductSelectionSummary.Describe(visible, visible, 5000);
        Assert.AreEqual(1000, thousand.Count);
        StringAssert.Contains(thousand.Headline, "1.000 ürün seçildi");
        StringAssert.Contains(thousand.ScopeText, "1.000", "The scope says what the selection covers...");
        StringAssert.Contains(thousand.ScopeText, "5.000", "...against the full match count, so 'select all' on a page is not mistaken for the whole filter.");
    }

    [TestMethod]
    public void ASelectionLeftBehindByAFilterChangeIsReportedAndDroppedInsteadOfActedOn()
    {
        var kept = Product("A"); var gone = Product("B");
        var afterFilter = new[] { kept };

        var summary = ProductSelectionSummary.Describe([kept, gone], afterFilter, matchTotal: 1);

        Assert.AreEqual(1, summary.Count, "Only rows still in the list can be acted on.");
        Assert.AreEqual(1, summary.StaleCount);
        CollectionAssert.AreEqual(new[] { "id-A" }, summary.LiveIds.ToArray());
        StringAssert.Contains(summary.ScopeText, "1 seçim listeden düştü", "The operator is told their selection shrank rather than silently losing a row.");

        var allStale = ProductSelectionSummary.Describe([gone], afterFilter, 1);
        Assert.AreEqual(0, allStale.Count);
        Assert.IsFalse(allStale.IsVisible, "A selection that no longer matches anything is not a selection.");
        Assert.IsFalse(allStale.CanDeactivate);
    }

    [TestMethod]
    public void RiskyActionAvailabilityIsComputedFromTheSelectionRatherThanAlwaysOffered()
    {
        var active = Product("A"); var passive = Product("B", p => p.Active = false);
        var listed = Product("C", p => p.EtsyListingId = "12345");
        var attempted = Product("D", p => p.EtsyCreationAttempted = true);
        var visible = new[] { active, passive, listed, attempted };

        var mixed = ProductSelectionSummary.Describe(visible, visible, 4);
        Assert.IsTrue(mixed.CanActivate, "Something in the set is passive, so 'activate' does something.");
        Assert.IsTrue(mixed.CanDeactivate);
        Assert.AreEqual(2, mixed.DeletableCount, "Only the two rows with no Etsy listing and no dispatch attempt can be deleted.");
        Assert.AreEqual(2, mixed.BlockedDeleteCount);
        Assert.IsTrue(mixed.CanDelete);
        StringAssert.Contains(mixed.RiskText, "2 ürün silinemez", "The bar names what will be refused before the operator presses delete.");

        var allActive = ProductSelectionSummary.Describe([active], [active], 1);
        Assert.IsFalse(allActive.CanActivate, "Every row is already active: offering 'activate' would be a no-op dressed as an action.");
        Assert.IsTrue(allActive.CanDeactivate);

        var allBlocked = ProductSelectionSummary.Describe([listed], [listed], 1);
        Assert.IsFalse(allBlocked.CanDelete);
        Assert.AreEqual(0, allBlocked.DeletableCount);
        StringAssert.Contains(allBlocked.RiskText, "silinemez");
    }

    [TestMethod]
    public void TheSummaryNeverEchoesProductTextThatCouldCarryAnythingSensitive()
    {
        var sneaky = Product("A", p => { p.Name = "Authorization: Bearer abc123secret"; p.Cost = 1234.56m; });

        var summary = ProductSelectionSummary.Describe([sneaky], [sneaky], 1);

        foreach (var text in new[] { summary.Headline, summary.ScopeText, summary.RiskText })
        {
            Assert.IsFalse(text.Contains("abc123secret", StringComparison.Ordinal), "The bar counts rows; it must not echo product text: " + text);
            Assert.IsFalse(text.Contains("1234", StringComparison.Ordinal), "Buying cost has no business in a selection summary: " + text);
        }
    }
}
