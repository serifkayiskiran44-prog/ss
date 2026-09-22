using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class BizimHesapCreateSafetyTests
{
    static BizimHesapCreateProduct Product() => new(" SKU-1 ", " BAR-1 ", "Ürün", 100m, 20m, 5, "TRY");

    [TestMethod]
    public void Match_requires_one_consistent_barcode_or_sku_match()
    {
        var remote = new BizimHesapProduct("42", "sku-1", "bar-1", "Ürün", 100m, 5);

        var result = BizimHesapCreateSafety.Match(Product(), new[] { remote });

        Assert.AreEqual(BizimHesapReadbackStatus.Matched, result.Status);
        Assert.AreSame(remote, result.Product);
    }

    [TestMethod]
    public void Match_reports_conflict_when_barcode_and_sku_point_to_different_products()
    {
        var rows = new[]
        {
            new BizimHesapProduct("1", "SKU-X", "BAR-1", "A", 1m, 1),
            new BizimHesapProduct("2", "SKU-1", "BAR-X", "B", 1m, 1)
        };

        var result = BizimHesapCreateSafety.Match(Product(), rows);

        Assert.AreEqual(BizimHesapReadbackStatus.Conflict, result.Status);
        Assert.IsNull(result.Product);
    }

    [TestMethod]
    public void Match_reports_ambiguous_duplicate_identity()
    {
        var rows = new[]
        {
            new BizimHesapProduct("1", "SKU-1", "BAR-1", "A", 1m, 1),
            new BizimHesapProduct("2", "SKU-1", "BAR-1", "B", 1m, 1)
        };

        Assert.AreEqual(BizimHesapReadbackStatus.Ambiguous, BizimHesapCreateSafety.Match(Product(), rows).Status);
    }

    [TestMethod]
    public void Hashes_are_stable_and_do_not_contain_credentials()
    {
        var account = BizimHesapCreateSafety.AccountKey(new BizimHesapSettings(" firm-1 ", "secret-token"));
        var payload = BizimHesapCreateSafety.PayloadHash(Product());

        Assert.AreEqual(account, BizimHesapCreateSafety.AccountKey(new BizimHesapSettings("FIRM-1", "another-token")));
        Assert.AreEqual(payload, BizimHesapCreateSafety.PayloadHash(Product()));
        Assert.IsFalse(account.Contains("firm-1", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(account.Contains("secret-token", StringComparison.Ordinal));
    }
}
