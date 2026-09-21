using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class NesQueuePlannerTests
{
    static OrderSnapshot ValidOrder() => new()
    {
        Marketplace = "Trendyol", ShopId = "411150", OrderId = "11600461863",
        PlatformReference = "11600461863", RawStatus = "Created", Currency = "TRY",
        CustomerName = "Test Alıcı", BillingName = "Test Alıcı", BillingAddress = "Test adresi",
        BillingCity = "İstanbul", BillingDistrict = "Eyüp", Total = 120m,
        Items = [new() { Sku = "SKU-1", Title = "Test ürün", Quantity = 1, UnitPrice = 120m, VatRate = 20m }],
        Shipments = [new() { Id = "4154085881", State = "Preparing" }]
    };

    static NesSettings Settings() => new()
    {
        ApiKey = "test-key", CompanyTitle = "MonoBridge Test", TaxNumber = "12218715546",
        Address = "Test adresi", City = "İstanbul", District = "Eyüp"
    };

    [TestMethod]
    public void PlannerBlocksEInvoiceWithoutReceiverAlias()
    {
        var order = ValidOrder(); order.EInvoiceAvailable = true; order.TaxNumber = "1234567890";

        var plan = NesQueuePlanner.Build([order], Settings(), _ => null, uploadMarketplace: true);

        Assert.AreEqual("Blocked", plan.Rows.Single().Decision);
        StringAssert.Contains(plan.Rows.Single().Reason, "alıcı etiketi");
    }

    [TestMethod]
    public void PlannerUsesSavedAliasAndProducesStableFingerprint()
    {
        var order = ValidOrder(); order.EInvoiceAvailable = true; order.TaxNumber = "1234567890";

        var first = NesQueuePlanner.Build([order], Settings(), _ => "urn:mail:pk@buyer.example", true);
        var second = NesQueuePlanner.Build([order.Copy()], Settings(), _ => "urn:mail:pk@buyer.example", true);

        Assert.AreEqual("Ready", first.Rows.Single().Decision);
        Assert.AreEqual(first.Rows.Single().Request!.OrderFingerprint, second.Rows.Single().Request!.OrderFingerprint);
    }

    [TestMethod]
    public void PlannerNeverQueuesAnAlreadyInvoicedOrderForAnotherInvoice()
    {
        var order = ValidOrder();
        order.InvoiceUuid = "11111111-1111-1111-1111-111111111111";
        order.InvoiceNumber = "EAR2026000000001";

        var plan = NesQueuePlanner.Build([order], Settings(), _ => null, true);

        Assert.IsNull(plan.Rows.Single().Request);
        Assert.AreEqual("AlreadyInvoiced", plan.Rows.Single().Decision);
    }

    [TestMethod]
    public void AliasStoreValidatesAndPersistsReceiverAlias()
    {
        using var temp = new TemporaryDirectory();
        var store = new NesRecipientAliasStore(temp.Path);
        store.Save("1234567890", "urn:mail:pk@buyer.example", DateTimeOffset.Parse("2026-09-21T10:00:00Z"));

        Assert.AreEqual("urn:mail:pk@buyer.example", new NesRecipientAliasStore(temp.Path).Get("1234567890"));
        Assert.ThrowsException<ArgumentException>(() => store.Save("12", "urn:test", DateTimeOffset.UtcNow));
        Assert.ThrowsException<ArgumentException>(() => store.Save("1234567890", "buyer.example", DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void OrdersStoreFindReturnsExactOrder()
    {
        using var temp = new TemporaryDirectory();
        var store = new OrdersStore(temp.Path);
        store.SaveBatch([ValidOrder()]);

        Assert.AreEqual("11600461863", store.Find("Trendyol", "411150", "11600461863")!.OrderId);
        Assert.IsNull(store.Find("Trendyol", "411150", "missing"));
    }
}
