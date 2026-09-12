using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

[TestClass]
public sealed class TrendyolConnectionTests
{
    [TestMethod]
    public void ValidateAcceptsSelfIntegrationUserAgentMatchingSupplierId()
    {
        TrendyolConnection.Validate(new TrendyolSettings("12345", "key", "secret", "12345 - SelfIntegration"));
    }

    [TestMethod]
    public void ValidateAcceptsIntegrationCompanyUserAgentMatchingSupplierId()
    {
        TrendyolConnection.Validate(new TrendyolSettings("12345", "key", "secret", "12345 - Acme Integrator"));
    }

    [TestMethod]
    public void ValidateRejectsUserAgentWithWrongSupplierIdPrefix()
    {
        Assert.ThrowsException<ArgumentException>(() => TrendyolConnection.Validate(new TrendyolSettings("12345", "key", "secret", "99999 - SelfIntegration")));
    }

    [TestMethod]
    public void ValidateRejectsUserAgentMissingSeparator()
    {
        Assert.ThrowsException<ArgumentException>(() => TrendyolConnection.Validate(new TrendyolSettings("12345", "key", "secret", "12345 SelfIntegration")));
    }

    [TestMethod]
    public void ValidateRejectsUserAgentWithEmptySuffix()
    {
        Assert.ThrowsException<ArgumentException>(() => TrendyolConnection.Validate(new TrendyolSettings("12345", "key", "secret", "12345 - ")));
    }

    [TestMethod]
    public void ValidateProductBatchAcceptsOfficialThousandItemLimit()
    {
        var items = Enumerable.Range(1, 1000).Select(i => new TrendyolProductV2Item($"BC{i}", $"SC{i}", $"Item {i}", "Brand", 1, 100m, 90m)).ToList();
        TrendyolPilot.ValidateProductBatch(items);
    }

    [TestMethod]
    public void ValidateProductBatchRejectsMoreThanOfficialThousandItemLimit()
    {
        var items = Enumerable.Range(1, 1001).Select(i => new TrendyolProductV2Item($"BC{i}", $"SC{i}", $"Item {i}", "Brand", 1, 100m, 90m)).ToList();
        Assert.ThrowsException<InvalidOperationException>(() => TrendyolPilot.ValidateProductBatch(items));
    }

    [TestMethod]
    public void ValidateProductBatchRejectsListPriceBelowSalePricePerOfficialRule()
    {
        var items = new[] { new TrendyolProductV2Item("BC1", "SC1", "Item", "Brand", 1, ListPrice: 90m, SalePrice: 100m) };
        Assert.ThrowsException<InvalidOperationException>(() => TrendyolPilot.ValidateProductBatch(items));
    }
}
