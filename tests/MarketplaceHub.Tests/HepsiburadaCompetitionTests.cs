using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class HepsiburadaCompetitionTests
{
    [TestMethod]
    public void SuggestionRespectsMinimumAndMaximum()
    {
        Assert.AreEqual(90m,HepsiburadaCompetition.Suggest(80m,90m,120m,1m).Price);
        Assert.AreEqual(120m,HepsiburadaCompetition.Suggest(150m,90m,120m,1m).Price);
    }
}
