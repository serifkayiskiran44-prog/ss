using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class ChannelPriceMergeTests
{
    [TestMethod]
    public void UntouchedPrefilledChannelsAreNotFrozenButChangedAndExistingOnesAre()
    {
        var existing = new Dictionary<string, XmlChannelPrice> { ["amazon"] = new(50m, null, "TRY") };
        var edited = new (string, decimal, decimal?, string)[]
        {
            ("trendyol", 100m, null, "TRY"),     // pre-filled default, untouched
            ("hepsiburada", 120m, null, "TRY"),  // user changed
            ("amazon", 50m, null, "TRY"),        // already had its own price
            ("etsy", 100m, 150m, "TRY"),         // list price set
            ("n11", 100m, null, "USD")           // currency changed
        };

        var kept = ChannelPriceMerge.Keep(existing, edited, 100m, "TRY");

        CollectionAssert.AreEquivalent(new[] { "hepsiburada", "amazon", "etsy", "n11" }, new List<string>(kept.Keys));
        Assert.AreEqual(150m, kept["etsy"].ListPrice);
    }
}
