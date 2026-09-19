using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using TrMarketplaceHubDesktop.Hepsiburada;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class HepsiburadaCompetitionTests
{
    [TestMethod]
    public void SuggestionNeverDropsBelowProtectedMinimum()
    {
        var result = HepsiburadaCompetition.Suggest(new("HB1", "SKU1", 2, 90m, 100m, DateTime.UtcNow), 95m, 20m, 1m);
        Assert.AreEqual(95m, result.Price);
        Assert.IsFalse(result.AutomaticallyApplied);
    }

    [TestMethod]
    public void FirstPlaceKeepsOwnPriceAndNeverAutoApplies()
    {
        var result = HepsiburadaCompetition.Suggest(new("HB1", "SKU1", 1, 90m, 100m, DateTime.UtcNow), 80m, 30m, 1m);
        Assert.AreEqual(100m, result.Price);
        Assert.IsFalse(result.AutomaticallyApplied);
    }

    [DataTestMethod]
    [DataRow(0, 10, 1)]
    [DataRow(10, -1, 1)]
    [DataRow(10, 1, -1)]
    [DataRow(10.001, 1, 1)]
    public void InvalidBoundsDoNotProduceSuggestion(double minimum, double decrease, double undercut)
    {
        var result = HepsiburadaCompetition.Suggest(new("HB1", "SKU1", 2, 90m, 100m, DateTime.UtcNow), (decimal)minimum, (decimal)decrease, (decimal)undercut);
        Assert.IsNull(result.Price);
        Assert.IsFalse(result.AutomaticallyApplied);
    }

    [TestMethod]
    public void MissingRemotePricesDoNotProduceSuggestion()
    {
        var result = HepsiburadaCompetition.Suggest(new("HB1", "SKU1", null, null, null, DateTime.UtcNow), 80m, 20m, 1m);
        Assert.IsNull(result.Price);
    }
}
