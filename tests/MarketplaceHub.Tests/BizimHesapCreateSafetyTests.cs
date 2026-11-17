using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class BizimHesapCreateSafetyTests
{
    [TestMethod]
    public void Stored_remote_guid_matches_even_when_code_and_barcode_are_empty()
    {
        var remote = new[] { new BizimHesapProduct("REMOTE-GUID-42", "", "", "Ürün", 100m, 5) };

        var result = BizimHesapCreateSafety.MatchRemoteId("remote-guid-42", remote);

        Assert.AreEqual(BizimHesapReadbackStatus.Matched, result.Status);
        Assert.AreEqual("REMOTE-GUID-42", result.Product!.Id);
    }

    [TestMethod]
    public void Price_comparison_converts_local_net_price_to_remote_gross_price()
    {
        var product = new BizimHesapCreateProduct("SKU-1", "BAR-1", "Ürün", 20m, 20m, 7, "TRY");

        Assert.IsTrue(BizimHesapCreateSafety.IsPriceCurrent(product, new("42", "SKU-1", "BAR-1", "Ürün", 24m, 5)));
        Assert.IsFalse(BizimHesapCreateSafety.IsPriceCurrent(product, new("42", "SKU-1", "BAR-1", "Ürün", 12m, 5)));
    }

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
