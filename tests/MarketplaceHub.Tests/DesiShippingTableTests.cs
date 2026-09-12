using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

[TestClass]
public sealed class DesiShippingTableTests
{
    static ShippingCostTable SampleTable()
    {
        var table = new ShippingCostTable();
        table.Add(new ShippingCostBracket("Yurtiçi", 0, 1, 45m, "TRY"));
        table.Add(new ShippingCostBracket("Yurtiçi", 1, 3, 65m, "TRY"));
        table.Add(new ShippingCostBracket("Yurtiçi", 3, 5, 90m, "TRY"));
        return table;
    }

    static ProfitabilityInput BaseInput(decimal shippingCost = 0) => new(
        Sku: "SKU-1", Channel: "etsy", SalePrice: 500m, Cost: 200m, VatRate: 10m, CommissionRate: 5m,
        ShippingCost: shippingCost, TransactionCost: 5m, Currency: "TRY",
        CostAtUtc: DateTimeOffset.UtcNow, CommissionAtUtc: DateTimeOffset.UtcNow);

    [TestMethod]
    public void ShippingCostTable_ResolvesCorrectBracketForGivenWeight()
    {
        var table = SampleTable();

        Assert.AreEqual(45m, table.Resolve("Yurtiçi", 0.5m)!.Cost);
        Assert.AreEqual(65m, table.Resolve("Yurtiçi", 1m)!.Cost);
        Assert.AreEqual(65m, table.Resolve("Yurtiçi", 2.9m)!.Cost);
        Assert.AreEqual(90m, table.Resolve("Yurtiçi", 3m)!.Cost);
    }

    [TestMethod]
    public void ShippingCostTable_RejectsOverlappingBracketsForSameCarrier()
    {
        var table = SampleTable();
        Assert.ThrowsException<ArgumentException>(() => table.Add(new ShippingCostBracket("Yurtiçi", 0.5m, 2m, 50m, "TRY")));
    }

    [TestMethod]
    public void ProfitabilitySimulator_UsesAutoShippingCostWhenWeightPresent()
    {
        var table = SampleTable();
        var result = ProfitabilitySimulator.SimulateAuto(BaseInput(), table, "Yurtiçi", desi: 2m);

        Assert.AreEqual("OK", result.Status);
        var expectedNet = 500m - 500m * 0.10m - 500m * 0.05m - 200m - 65m - 5m;
        Assert.AreEqual(decimal.Round(expectedNet, 4), result.NetContribution);
    }

    [TestMethod]
    public void ProfitabilitySimulator_ReturnsNeedsWeightDataWhenWeightMissing()
    {
        var table = SampleTable();
        var result = ProfitabilitySimulator.SimulateAuto(BaseInput(), table, "Yurtiçi", desi: null);

        Assert.AreEqual("NEEDS_WEIGHT_DATA", result.Status);
        Assert.AreEqual(0, result.NetContribution); // explicit non-answer, not a real (mis)computed profit
    }

    [TestMethod]
    public void ProfitabilitySimulator_ReturnsShippingCostUnknownInsteadOfExtrapolatingOutOfRangeDesi()
    {
        var table = SampleTable();
        var result = ProfitabilitySimulator.SimulateAuto(BaseInput(), table, "Yurtiçi", desi: 50m);

        Assert.AreEqual("SHIPPING_COST_UNKNOWN", result.Status);
        Assert.AreEqual(0, result.NetContribution);
    }

    [TestMethod]
    public void ResolveShippingCost_NeverSilentlyReturnsZeroForNullOrUnresolvedDesi()
    {
        var table = SampleTable();

        var missing = ProfitabilitySimulator.ResolveShippingCost(table, "Yurtiçi", null);
        Assert.AreEqual("NEEDS_WEIGHT_DATA", missing.Status);
        Assert.IsNull(missing.Cost);

        var unknownCarrier = ProfitabilitySimulator.ResolveShippingCost(table, "Bilinmeyen Kargo", 1m);
        Assert.AreEqual("SHIPPING_COST_UNKNOWN", unknownCarrier.Status);
        Assert.IsNull(unknownCarrier.Cost);
    }

    [TestMethod]
    public void Migration_ExistingProductWithoutDesiOpensWithNullDesiNotZero()
    {
        var root = Path.Combine(Path.GetTempPath(), "desi-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CatalogStore(root);
            var source = new XmlSource { Id = "fixture", Name = "Fixture" };
            store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "LEGACY", Name = "Legacy product", Cost = 40m, Price = 80m } });

            var reloaded = store.Products().Single(x => x.Sku == "LEGACY");

            Assert.IsNull(reloaded.Desi);
            Assert.AreEqual(80m, reloaded.Price);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
