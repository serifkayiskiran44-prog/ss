using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #926 (PRICING: discount stack calculation). The order is fixed -- supplier discount on the cost, the sale price
// from that cost, local then channel discount on the sale price, the shop's rounding last; a negative input or a
// percentage over a hundred is INVALID, an unknown channel discount is UNSUPPORTED, a price discounted to nothing
// is ZERO_PRICE, nothing typed is NO_DISCOUNT; the real pricing chain runs the stack, gives the money gate the
// cost actually paid, refuses the blocking states by name and names the steps in the preview.
[TestClass]
public sealed class DiscountStackTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static decimal Two(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    [TestMethod]
    public void TheOrderIsFixedAndEveryBadInputIsItsOwnState()
    {
        // No discount: the price as computed, said so.
        var none = DiscountStack.Apply(100m, c => c * 2, null, null, "", Two);
        Assert.AreEqual((DiscountStackResult.NoDiscount, 100m, 200m, 0), (none.State, none.EffectiveCost, none.SalePrice, none.Steps.Count)); Assert.AreEqual("indirim yok", none.Words); Assert.IsFalse(none.Blocks);
        Assert.AreEqual(DiscountStackResult.NoDiscount, DiscountStack.Apply(100m, c => c * 2, 0m, 0m, null, Two).State, "zero percentages are no discount");

        // Multiple discounts in the fixed order: supplier on the cost (100 -> 90), the price from that cost (180), local (162), channel percent (153.9), amount last.
        var stacked = DiscountStack.Apply(100m, c => c * 2, 10m, 10m, "percent:5", Two);
        Assert.AreEqual((DiscountStackResult.Ok, 90m, 153.9m), (stacked.State, stacked.EffectiveCost, stacked.SalePrice));
        CollectionAssert.AreEqual(new[] { DiscountStack.SupplierKind, DiscountStack.LocalKind, DiscountStack.ChannelKind }, stacked.Steps.Select(s => s.Kind).ToArray(), "the order never depends on which field was typed first");
        StringAssert.Contains(stacked.Words, "tedarikçi indirimi %10: maliyet 100 → 90"); StringAssert.Contains(stacked.Words, "yerel indirim %10: 180 → 162"); StringAssert.Contains(stacked.Words, "kanal indirimi percent 5: 162 → 153.9");
        Assert.AreEqual(158.1m, DiscountStack.Apply(100m, c => c * 2, 10m, 10m, "amount:3.9", Two).SalePrice, "an amount comes off the sale price in the sale currency: 162 - 3.9");
        Assert.AreEqual(162m, DiscountStack.Apply(100m, c => c * 2, 10m, 10m, "", Two).SalePrice);

        // 100%: a price discounted to nothing is a state that blocks, never a zero sent to a marketplace.
        var free = DiscountStack.Apply(100m, c => c * 2, null, 100m, null, Two);
        Assert.AreEqual(DiscountStackResult.ZeroPrice, free.State); Assert.IsTrue(free.Blocks); Assert.AreEqual(0m, free.SalePrice); StringAssert.Contains(free.Words, "sıfıra indirdi");
        Assert.AreEqual(DiscountStackResult.ZeroPrice, DiscountStack.Apply(100m, c => c * 2, null, null, "amount:250", Two).State, "an amount larger than the price is nothing too");
        var freeSupplier = DiscountStack.Apply(100m, c => 50m, 100m, null, null, c => c); Assert.AreEqual((DiscountStackResult.Ok, 0m, 50m), (freeSupplier.State, freeSupplier.EffectiveCost, freeSupplier.SalePrice), "a free supplier is a cost of zero under a price that does not follow the cost, not a zero price");

        // Negative and out-of-range inputs, and an unknown channel discount, each its own state.
        Assert.AreEqual(DiscountStackResult.Invalid, DiscountStack.Apply(100m, c => c * 2, -5m, null, null, Two).State);
        Assert.AreEqual(DiscountStackResult.Invalid, DiscountStack.Apply(100m, c => c * 2, null, 101m, null, Two).State);
        Assert.AreEqual(DiscountStackResult.Invalid, DiscountStack.Apply(100m, c => c * 2, null, null, "amount:-1", Two).State);
        Assert.AreEqual(DiscountStackResult.Invalid, DiscountStack.Apply(100m, c => c * 2, null, null, "percent:120", Two).State);
        var coupon = DiscountStack.Apply(100m, c => c * 2, null, null, "coupon:5", Two);
        Assert.AreEqual(DiscountStackResult.Unsupported, coupon.State); Assert.IsTrue(coupon.Blocks); StringAssert.Contains(coupon.Words, "desteklenmeyen kanal indirimi"); StringAssert.Contains(coupon.Words, "coupon:5");
        Assert.AreEqual(DiscountStackResult.Unsupported, DiscountStack.Apply(100m, c => c * 2, null, null, "percent:abc", Two).State);
        Assert.AreEqual(DiscountStackResult.Unsupported, DiscountStack.Apply(100m, c => c * 2, null, null, "10", Two).State, "a bare number names no form");
        Assert.ThrowsException<ArgumentException>(() => DiscountStack.Validate(-1m, null)); Assert.ThrowsException<ArgumentException>(() => DiscountStack.Validate(null, 100.5m)); DiscountStack.Validate(0m, 100m);

        // Rounding: four decimals between steps, the shop's rounding on the result -- and named when it moved the price.
        var third = DiscountStack.Apply(100m, c => c * 3, 33.3333m, null, null, Two);
        Assert.AreEqual(66.6667m, third.EffectiveCost); Assert.AreEqual(200m, third.SalePrice, "66.6667 * 3 = 200.0001 rounds to 200.00");
        var whole = DiscountStack.Apply(100m, c => c * 2, 10m, 10m, "percent:5", v => Math.Round(v, 0, MidpointRounding.ToNegativeInfinity));
        Assert.AreEqual(153m, whole.SalePrice); Assert.AreEqual("rounding", whole.Steps[^1].Kind); StringAssert.Contains(whole.Words, "yuvarlama: 153.9 → 153");
    }

    [TestMethod]
    public void ThePricingChainRunsTheStackPaysTheEffectiveCostAndRefusesTheBlockingStatesByName()
    {
        var root = Path.Combine(Path.GetTempPath(), "discount-chain-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CatalogStore(root);
            catalog.Import(new XmlSource { Id = "src", Name = "Src" }, new[] { new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Product", Cost = 100m, CostCurrency = "TRY", Stock = 10, Active = true } });
            var id = catalog.Products().Single().Id;
            PricePolicy Policy(int version, decimal? supplier, decimal? local, string channel, decimal minimumMargin = 0, string formula = "x*2") => new() { Channel = "local", Shop = "s1", Version = version, Formula = formula, Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 0, EstimatedShippingTry = 0, TransactionCostTry = 0, VatRatePercent = 0, MinimumMarginTry = minimumMargin, SupplierDiscountPercent = supplier, LocalDiscountPercent = local, ChannelDiscount = channel };
            int Version() => catalog.GetPricePolicy("local", "s1")!.Version;

            // No discount: the price as before, the preview saying so.
            catalog.SavePricePolicy(Policy(0, null, null, ""));
            var plain = catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now));
            Assert.AreEqual(200m, plain.Price); Assert.AreEqual("indirim yok", plain.DiscountOrigin);

            // The stack: 100 -> 90 cost, 180 price, 162 after the shop's discount, 153.9 after the channel's; the preview names the steps.
            catalog.SavePricePolicy(Policy(Version(), 10m, 10m, "percent:5"));
            var stacked = catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now));
            Assert.AreEqual(153.9m, stacked.Price); StringAssert.Contains(stacked.DiscountOrigin, "tedarikçi indirimi %10: maliyet 100 → 90"); StringAssert.Contains(stacked.DiscountOrigin, "kanal indirimi percent 5: 162 → 153.9");

            // The money gate pays the effective cost: with a price that does not depend on the cost (200), the net is 110 after the supplier's 10% (cost 90) and 100 without it; a 105 TRY minimum tells them apart.
            catalog.SavePricePolicy(Policy(Version(), 10m, null, "", minimumMargin: 105m, formula: "x*0+200"));
            var paidLess = catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now)); Assert.AreEqual(200m, paidLess.Price); Assert.AreEqual(100m, paidLess.CostTry, "the record's cost stays the record's; the words carry the effective one"); StringAssert.Contains(paidLess.DiscountOrigin, "maliyet 100 → 90");
            catalog.SavePricePolicy(Policy(Version(), null, null, "", minimumMargin: 105m, formula: "x*0+200"));
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now))).Message, "asgari kâr", "without the supplier discount the guard sees the full cost");

            // Unsupported and zero states are refused by name; a negative input never reaches the rule.
            catalog.SavePricePolicy(Policy(Version(), null, null, "coupon:5"));
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now))).Message, "desteklenmeyen kanal indirimi");
            catalog.SavePricePolicy(Policy(Version(), null, 100m, ""));
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now))).Message, "sıfıra indirdi");
            Assert.ThrowsException<ArgumentException>(() => catalog.SavePricePolicy(Policy(Version(), -5m, null, "")));
            Assert.ThrowsException<ArgumentException>(() => catalog.SavePricePolicy(Policy(Version(), null, 101m, "")));
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
