using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class NesIntegrationTests
{
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(reply(request));
        }
    }

    sealed class MultipartCaptureHandler : HttpMessageHandler
    {
        public string RequestUri { get; private set; } = "";
        public string Authorization { get; private set; } = "";
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"uuid\":\"55555555-5555-5555-5555-555555555555\",\"documentNumber\":\"MBF2026000000001\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    static NesSettings Settings() => new()
    {
        ApiKey = "secret-token",
        EInvoiceBaseUrl = "https://apitest.nes.com.tr/einvoice/",
        EArchiveBaseUrl = "https://apitest.nes.com.tr/earchive/",
        SenderAlias = "urn:mail:gb@test.example",
        CompanyTitle = "MonoBridge Test",
        TaxNumber = "1234567890",
        TaxOffice = "Test",
        Address = "Test adresi",
        City = "İstanbul",
        District = "Eyüp",
        Email = "test@example.com",
        Phone = "5550000000",
    };

    [TestMethod]
    public async Task ReadOutgoingUsesBearerOnlyOnValidatedNesHost()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"page\":1,\"pageSize\":100,\"totalCount\":1,\"data\":[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"documentNumber\":\"MBF2026000000001\",\"issueDate\":\"2026-09-19T00:00:00+03:00\",\"payableAmount\":125.5,\"documentCurrencyCode\":\"TRY\",\"outgoingStatus\":\"Sent\",\"sourceAppRecordId\":\"Trendyol:411150:11600461863\",\"marketPlaceOrderSummary\":{\"orderNumber\":\"11600461863\",\"marketPlaceSupplierId\":\"411150\",\"marketPlaceProvider\":\"Trendyol\"}}]}" , Encoding.UTF8, "application/json") } );
        using var http = new HttpClient(handler);

        var rows = await new NesConnection(http).ReadOutgoingAsync(Settings(), NesDocumentKind.EInvoice);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("MBF2026000000001", rows[0].DocumentNumber);
        Assert.AreEqual("11600461863", rows[0].MarketplaceOrderNumber);
        Assert.AreEqual("411150", rows[0].MarketplaceSupplierId);
        Assert.AreEqual("Trendyol", rows[0].MarketplaceProvider);
        Assert.AreEqual("Trendyol:411150:11600461863", rows[0].SourceAppRecordId);
        Assert.AreEqual("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.AreEqual("secret-token", handler.Request.Headers.Authorization.Parameter);
        Assert.AreEqual("https://apitest.nes.com.tr/einvoice/v1/outgoing/invoices?sort=CreatedAt desc&pageSize=100&page=1", handler.Request.RequestUri!.ToString());
    }

    [TestMethod]
    public void SettingsRejectTokenExfiltrationHost()
    {
        var settings = Settings();
        settings.EInvoiceBaseUrl = "https://attacker.example/einvoice/";
        Assert.ThrowsException<ArgumentException>(() => NesSettingsValidator.Validate(settings));
    }

    [TestMethod]
    public async Task EInvoiceUploadUsesOfficialMultipartFieldsAndMarketplaceIdentity()
    {
        var handler = new MultipartCaptureHandler();
        using var http = new HttpClient(handler);
        var order = new OrderSnapshot
        {
            Marketplace = "trendyol", ShopId = "seller-42", OrderId = "order-99",
            UpdatedAt = DateTimeOffset.Parse("2026-09-19T12:30:00+03:00")
        };

        var result = await new NesConnection(http).UploadAsync(Settings(), NesDocumentKind.EInvoice, Encoding.UTF8.GetBytes("<Invoice />"), order, "urn:mail:pk@test.example", true);

        Assert.AreEqual("55555555-5555-5555-5555-555555555555", result.Uuid);
        Assert.AreEqual("https://apitest.nes.com.tr/einvoice/v1/uploads/document", handler.RequestUri);
        Assert.AreEqual("Bearer secret-token", handler.Authorization);
        foreach (var expected in new[] { "name=File", "name=SenderAlias", "name=ReceiverAlias", "name=IsDirectSend", "name=PreviewType", "name=SourceApp", "name=SourceAppRecordId", "name=AutoSaveCompany", "name=MarketPlaceOrderNumber", "name=MarketPlaceProvider", "name=MarketPlaceSupplierId", "trendyol:seller-42:order-99", "urn:mail:pk@test.example", "Trendyol" })
            StringAssert.Contains(handler.Body, expected);
    }

    [TestMethod]
    public void UblPreviewUsesOrderIdentityTurkishTextAndBalancedTotals()
    {
        var order = new OrderSnapshot
        {
            Marketplace = "trendyol", ShopId = "shop-1", OrderId = "SİP-42", Currency = "TRY",
            BillingName = "Çağrı Öztürk", BillingAddress = "Nişancı Mahallesi", BillingCity = "İstanbul", BillingDistrict = "Eyüp", TaxNumber = "11111111111",
            Items = [new() { Title = "Kedi maması", Sku = "SKU-1", Quantity = 2, UnitPrice = 120m, VatRate = 20m }]
        };

        var draft = NesUblBuilder.Build(Settings(), order, NesDocumentKind.EArchive, "MBA2026000000001", Guid.Parse("22222222-2222-2222-2222-222222222222"), DateTimeOffset.Parse("2026-09-19T12:00:00+03:00"));

        var xml = Encoding.UTF8.GetString(draft.Xml);
        StringAssert.Contains(xml, "SİP-42");
        StringAssert.Contains(xml, "Çağrı Öztürk");
        Assert.AreEqual(240m, draft.PayableAmount);
        Assert.AreEqual(200m, draft.TaxExclusiveAmount);
        Assert.AreEqual(40m, draft.TaxAmount);
    }

    [TestMethod]
    public void ReceiverIdentityKeepsCorporateVknAndDefaultsOnlyIndividualEArchive()
    {
        var corporate = new OrderSnapshot { TaxNumber = "1234567890", EInvoiceAvailable = true, CustomerTaxOffice = "Nusaybin" };
        var corporateIdentity = NesReceiverIdentityResolver.Resolve(corporate, NesDocumentKind.EInvoice);
        Assert.AreEqual("1234567890", corporateIdentity.Value);
        Assert.AreEqual("VKN", corporateIdentity.Scheme);
        Assert.IsFalse(corporateIdentity.UsedEArchiveFallback);

        var individual = new OrderSnapshot { BillingName = "Fatma Kahraman", EInvoiceAvailable = false };
        var archiveIdentity = NesReceiverIdentityResolver.Resolve(individual, NesDocumentKind.EArchive);
        Assert.AreEqual("11111111111", archiveIdentity.Value);
        Assert.AreEqual("TCKN", archiveIdentity.Scheme);
        Assert.IsTrue(archiveIdentity.UsedEArchiveFallback);
    }

    [TestMethod]
    public void ReceiverIdentityDoesNotHideMissingCorporateOrEInvoiceIdentity()
    {
        Assert.ThrowsException<InvalidOperationException>(() => NesReceiverIdentityResolver.Resolve(
            new OrderSnapshot { BillingName = "Örnek Limited", CustomerTaxOffice = "Nusaybin", EInvoiceAvailable = true },
            NesDocumentKind.EInvoice));
        Assert.ThrowsException<InvalidOperationException>(() => NesReceiverIdentityResolver.Resolve(
            new OrderSnapshot { BillingName = "Örnek Limited", CustomerTaxOffice = "Nusaybin" },
            NesDocumentKind.EArchive));
    }

    [TestMethod]
    public void UblUsesResolvedEArchiveIdentityAndCustomerTaxOffice()
    {
        var order = new OrderSnapshot
        {
            Marketplace = "trendyol", ShopId = "shop-1", OrderId = "order-archive", Currency = "TRY",
            BillingName = "Fatma Kahraman", BillingAddress = "Başakşehir", BillingCity = "İstanbul", BillingDistrict = "Başakşehir",
            EInvoiceAvailable = false,
            Items = [new() { Title = "Kedi maması", Sku = "SKU-1", Quantity = 1, UnitPrice = 120m, VatRate = 20m }]
        };

        var draft = NesUblBuilder.Build(Settings(), order, NesDocumentKind.EArchive, "MBA2026000000002", Guid.Parse("22222222-2222-2222-2222-222222222223"), DateTimeOffset.Parse("2026-09-21T12:00:00+03:00"));
        var xml = Encoding.UTF8.GetString(draft.Xml);
        StringAssert.Contains(xml, "schemeID=\"TCKN\">11111111111");
    }

    [TestMethod]
    public void DraftStoreKeepsOneNumberPerOrderAndBlocksSecondSuccessfulSend()
    {
        var root = Path.Combine(Path.GetTempPath(), "nes-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new NesDocumentStore(root);
            var first = store.Prepare("trendyol", "shop-1", "order-1", NesDocumentKind.EArchive, "MBA", "hash-1");
            var replay = store.Prepare("trendyol", "shop-1", "order-1", NesDocumentKind.EArchive, "MBA", "hash-1");
            Assert.AreEqual(first.DocumentNumber, replay.DocumentNumber);
            store.MarkSent(first, "33333333-3333-3333-3333-333333333333", "MBA2026000000001");
            Assert.ThrowsException<InvalidOperationException>(() => store.Prepare("trendyol", "shop-1", "order-1", NesDocumentKind.EArchive, "MBA", "hash-2"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void NesInvoiceBindingSurvivesLaterMarketplaceRefresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "nes-order-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OrdersStore(root); var original = new OrderSnapshot { Marketplace = "trendyol", ShopId = "shop-1", OrderId = "order-1", RawStatus = "paid", Currency = "TRY", UpdatedAt = DateTimeOffset.Parse("2026-09-19T10:00:00Z") };
            store.SaveManual(original); store.AttachNesInvoice("trendyol", "shop-1", "order-1", "e‑Arşiv", "44444444-4444-4444-4444-444444444444", "MBA2026000000001", "Gönderildi");
            var refreshed = original.Copy(); refreshed.Source = "Trendyol API"; refreshed.RawStatus = "shipped"; refreshed.UpdatedAt = DateTimeOffset.Parse("2026-09-19T11:00:00Z"); store.SaveBatch([refreshed]);
            var saved = store.ReadAll().Single(); Assert.AreEqual("MBA2026000000001", saved.InvoiceNumber); Assert.AreEqual("44444444-4444-4444-4444-444444444444", saved.InvoiceUuid);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void OutgoingNesDocumentsAutomaticallyAttachRealNumberByMarketplaceOrderIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "nes-reconcile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OrdersStore(root);
            store.SaveBatch([new OrderSnapshot { Marketplace = "Trendyol", ShopId = "411150", OrderId = "11600461863", RawStatus = "Delivered", MarketplaceInvoiceStatus = "Invoiced", Currency = "TRY", UpdatedAt = DateTimeOffset.UtcNow }]);
            var issued = DateTimeOffset.Parse("2026-09-19T10:00:00+03:00");
            var result = NesOrderReconciler.Reconcile(store,
            [new NesRemoteDocument("66666666-6666-6666-6666-666666666666", "EAR2026000000433", issued, 74.92m, "TRY", "Sent", NesDocumentKind.EArchive, "", "11600461863", "411150", "Trendyol")]);

            Assert.AreEqual(1, result.Matched);
            var saved = store.ReadAll().Single();
            Assert.AreEqual("EAR2026000000433", saved.InvoiceNumber);
            Assert.AreEqual("66666666-6666-6666-6666-666666666666", saved.InvoiceUuid);
            Assert.AreEqual(issued, saved.InvoiceIssuedAt);
            Assert.AreEqual("EAR2026000000433", saved.InvoiceNumberDisplay);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void ReconciliationDoesNotGuessWhenOrderNumberIsAmbiguousAcrossShops()
    {
        var root = Path.Combine(Path.GetTempPath(), "nes-reconcile-ambiguous-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OrdersStore(root);var at = DateTimeOffset.UtcNow;
            store.SaveBatch([
                new OrderSnapshot { Marketplace = "Trendyol", ShopId = "shop-a", OrderId = "same-order", RawStatus = "Delivered", Currency = "TRY", UpdatedAt = at },
                new OrderSnapshot { Marketplace = "Trendyol", ShopId = "shop-b", OrderId = "same-order", RawStatus = "Delivered", Currency = "TRY", UpdatedAt = at }
            ]);
            var result = NesOrderReconciler.Reconcile(store,
            [new NesRemoteDocument("77777777-7777-7777-7777-777777777777", "EAR2026000000434", at, 10m, "TRY", "Sent", NesDocumentKind.EArchive, "", "same-order", "", "Trendyol")]);

            Assert.AreEqual(1, result.Ambiguous);
            Assert.IsTrue(store.ReadAll().All(x => x.InvoiceNumber.Length == 0));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
