using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class OrderInvoiceWorkspaceTests
{
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request);
    }

    [TestMethod]
    public async Task TrendyolOrderV2MapsPackageInvoiceIdentityAndVatLines()
    {
        string? requested = null;
        var json = """
        {"totalElements":1,"totalPages":1,"page":0,"size":200,"content":[{
          "orderNumber":"10654411111","supplierId":411150,"shipmentPackageId":3330111111,
          "customerFirstName":"Çağrı","customerLastName":"Öztürk","customerId":"customer-888",
          "invoiceAddress":{"company":"Çağrı Öztürk","fullAddress":"Nişancı Mahallesi","countryCode":"TR","city":"İstanbul","district":"Eyüp","phone":"5550000000","email":"invoice@example.com","taxNumber":"11111111111","taxOffice":"Eyüp","eInvoiceAvailable":false},
          "shipmentAddress":{"fullName":"Çağrı Öztürk","fullAddress":"Teslimat adresi","countryCode":"TR","city":"İstanbul","district":"Eyüp","phone":"5551111111"},
          "lines":[{"quantity":2,"stockCode":"SKU-1","barcode":"8680000000001","productCode":321,"merchantSku":"M-SKU-1","productName":"Kedi maması","lineUnitPrice":120.0,"vatRate":20.0,"discount":4.0,"currencyCode":"TRY","orderLineItemStatusName":"Created"}],
          "orderDate":1789786800000,"lastModifiedDate":1789786860000,"currencyCode":"TRY","packageTotalPrice":240.0,
          "shipmentPackageStatus":"Created","commercial":false,"micro":false,"3pByTrendyol":false,"invoiceStatus":"NotInvoiced"
        }]}
        """;
        using var http = new HttpClient(new Handler(request =>
        {
            requested = request.RequestUri!.ToString();
            Assert.AreEqual("Basic", request.Headers.Authorization!.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }));

        var rows = await new TrendyolOrderClient(new TrendyolSettings("411150", "key", "secret", "411150 - Self Integration"), http).ReadRecentAsync();

        Assert.AreEqual("https://apigw.trendyol.com/integration/order/sellers/411150/v2/orders?orderByField=PackageLastModifiedDate&orderByDirection=DESC&size=200&page=0", requested);
        var order = rows.Single();
        Assert.AreEqual("10654411111", order.OrderId);
        Assert.AreEqual("3330111111", order.Shipments.Single().Id);
        Assert.AreEqual(false, order.EInvoiceAvailable);
        Assert.AreEqual("NotInvoiced", order.MarketplaceInvoiceStatus);
        Assert.AreEqual(120m, order.Items.Single().UnitPrice);
        Assert.AreEqual(20m, order.Items.Single().VatRate);
        Assert.AreEqual("Eyüp", order.CustomerTaxOffice);
        Assert.AreEqual("customer-888", order.CustomerId);
        Assert.AreEqual("invoice@example.com", order.CustomerEmail);
        Assert.AreEqual("TR", order.BillingCountry);
        Assert.AreEqual("5551111111", order.ShippingPhone);
        Assert.AreEqual("8680000000001", order.Items.Single().Barcode);
        Assert.AreEqual("M-SKU-1", order.Items.Single().MarketplaceProductCode);
        Assert.AreEqual(4m, order.Items.Single().DiscountAmount);
    }

    [TestMethod]
    public async Task TrendyolCorporateInvoiceAliasesMapToInvoiceCard()
    {
        var json = """
        {"totalElements":1,"totalPages":1,"page":0,"size":200,"content":[{
          "orderNumber":"10654422222","supplierId":411150,"shipmentPackageId":3330222222,
          "customerFirstName":"Abdurrahim","customerLastName":"Bulut","customerEmail":"pf+masked@trendyolmail.com",
          "invoiceAddress":{"companyName":"NUSAYBİN ŞİRİNLER KREŞ VE GÜNDÜZ BAKIMEVİ","fullAddress":"Mardin","country":"Türkiye","city":"Mardin","district":"Nusaybin","taxId":"1234567890","taxOfficeName":"NUSAYBİN","eInvoiceAvailable":true},
          "shipmentAddress":{"fullName":"ABDURRAHİM BULUT","fullAddress":"Mardin","city":"Mardin","district":"Nusaybin"},
          "lines":[{"quantity":1,"stockCode":"SKU-2","productName":"Ürün","lineUnitPrice":120.0,"vatRate":20.0}],
          "orderDate":1789786800000,"lastModifiedDate":1789786860000,"currencyCode":"TRY","packageTotalPrice":120.0,"shipmentPackageStatus":"Created"
        }]}
        """;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") })));

        var order = (await new TrendyolOrderClient(new TrendyolSettings("411150", "key", "secret", "411150 - Self Integration"), http).ReadRecentAsync()).Single();

        Assert.AreEqual("NUSAYBİN ŞİRİNLER KREŞ VE GÜNDÜZ BAKIMEVİ", order.BillingName);
        Assert.AreEqual("1234567890", order.TaxNumber);
        Assert.AreEqual("NUSAYBİN", order.CustomerTaxOffice);
        Assert.AreEqual("pf+masked@trendyolmail.com", order.BillingEmail);
        Assert.AreEqual(true, order.EInvoiceAvailable);
    }

    [TestMethod]
    public async Task TrendyolInvoiceUploadUsesOfficialPackageAndPdfFields()
    {
        string body = ""; string? uri = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            uri = request.RequestUri!.ToString(); body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") };
        }));
        var client = new TrendyolInvoiceClient(new TrendyolSettings("411150", "key", "secret", "411150 - Self Integration"), http);

        await client.UploadPdfAsync(3330111111, "MBF2026000000001", DateTimeOffset.Parse("2026-09-21T10:00:00+03:00"), Encoding.ASCII.GetBytes("%PDF-test"));

        Assert.AreEqual("https://apigw.trendyol.com/integration/sellers/411150/seller-invoice-file", uri);
        foreach (var expected in new[] { "name=shipmentPackageId", "3330111111", "name=invoiceNumber", "MBF2026000000001", "name=invoiceDateTime", "name=file", "invoice.pdf" }) StringAssert.Contains(body, expected);
    }

    [TestMethod]
    public void InvoiceRulesChooseDocumentTypeAndBlockUnsafeOrderStates()
    {
        var order = new OrderSnapshot { Marketplace = "Trendyol", ShopId = "411150", OrderId = "1", EInvoiceAvailable = true, RawStatus = "Created", Shipments = [new() { Id = "123" }] };
        Assert.AreEqual(NesDocumentKind.EInvoice, OrderInvoiceRules.DefaultKind(order));
        order.EInvoiceAvailable = false;
        Assert.AreEqual(NesDocumentKind.EArchive, OrderInvoiceRules.DefaultKind(order));
        order.CrossBorderByTrendyol = true;
        Assert.AreEqual(NesDocumentKind.EInvoice, OrderInvoiceRules.DefaultKind(order));
        order.MarketplaceInvoiceStatus = "Invoiced";
        Assert.ThrowsException<InvalidOperationException>(() => OrderInvoiceRules.ValidateCanInvoice(order));
        order.MarketplaceInvoiceStatus = "";
        order.RawStatus = "Cancelled";
        Assert.ThrowsException<InvalidOperationException>(() => OrderInvoiceRules.ValidateCanInvoice(order));
    }

    [TestMethod]
    public void InvoiceCardDisplaysArchiveFallbackWithoutChangingSourceTaxNumber()
    {
        var individual = new OrderSnapshot { BillingName = "Zeynep Nal Sezer", EInvoiceAvailable = null };
        Assert.AreEqual("11111111111", OrderInvoiceRules.DisplayIdentity(individual));
        Assert.AreEqual("", individual.TaxNumber);

        var corporate = new OrderSnapshot { BillingName = "Örnek Limited", TaxNumber = "1234567890", CustomerTaxOffice = "Kadıköy", EInvoiceAvailable = true };
        Assert.AreEqual("1234567890", OrderInvoiceRules.DisplayIdentity(corporate));
    }

    [TestMethod]
    public void OrderListLabelsExposeStableRecordInvoiceVatAndDates()
    {
        var order = new OrderSnapshot
        {
            Marketplace = "Trendyol", ShopId = "411150", OrderId = "11600461863", PlatformReference = "11600461863",
            MarketplaceInvoiceStatus = "Invoiced", Currency = "TRY", VatTotal = 12.49m,
            OrderDate = DateTimeOffset.Parse("2026-09-14T02:49:00+03:00"), AddedAt = DateTimeOffset.Parse("2026-09-21T16:12:00+03:00")
        };
        var copy = order.Copy();

        Assert.AreEqual(order.RecordIdLabel, copy.RecordIdLabel);
        Assert.AreEqual("NES ile eşleştir", order.InvoiceNumberDisplay);
        StringAssert.Contains(order.VatTotalLabel, "12,49");
        StringAssert.Contains(order.OrderDateLabel, "14.09.2026");
        StringAssert.Contains(order.AddedAtLabel, "21.09.2026");
    }

    [TestMethod]
    public void OrderListExposesProductTaxCargoAndTotalsWithoutDroppingMultipleLines()
    {
        var order = new OrderSnapshot
        {
            Marketplace = "Trendyol", ShopId = "411150", OrderId = "1", Currency = "TRY", TaxNumber = "1234567890",
            CustomerTaxOffice = "Nusaybin", NetTotal = 100m, VatTotal = 20m, CommissionTotal = 12.5m,
            Items =
            [
                new() { MarketplaceProductCode = "HB-1", Sku = "SKU-1", Barcode = "8680001", Title = "Mağaza ürünü", InvoiceTitle = "Fatura ürünü", Quantity = 2 },
                new() { MarketplaceProductCode = "HB-2", Sku = "SKU-2", Barcode = "8680002", Title = "İkinci ürün", Quantity = 1 }
            ],
            Shipments = [new() { Id = "123", PackageCode = "PK-1", PaymentParty = "Satıcı Öder", DeliveryType = "Dosya" }]
        };

        CollectionAssert.AreEquivalent(new[] { "SKU-1", "SKU-2" }, order.ProductSkus.Split(", "));
        StringAssert.Contains(order.InvoiceProducts, "Fatura ürünü");
        StringAssert.Contains(order.InvoiceProducts, "İkinci ürün");
        Assert.AreEqual("1234567890", order.TaxIdentityDisplay);
        Assert.AreEqual("PK-1", order.CargoCodes);
        Assert.AreEqual(2, order.TotalLineCount);
        Assert.AreEqual(3, order.TotalProductQuantity);
        StringAssert.Contains(order.CommissionTotalLabel, "12,50");
    }

    [TestMethod]
    public void OrderColumnPreferencesPersistKnownFieldsAndRecoverFromBadJson()
    {
        OrderColumnDefinition[] definitions =
        [
            new("order", "Sipariş No", "OrderId", 120, true, true),
            new("product", "Ürün", "MarketplaceProducts", 200, true),
            new("phone", "Telefon", "CustomerPhone", 130, false)
        ];

        var selected = OrderColumnPreferences.Resolve("[\"phone\",\"removed-field\"]", definitions);
        CollectionAssert.AreEquivalent(new[] { "order", "phone" }, selected.ToArray());
        var reopened = OrderColumnPreferences.Resolve(OrderColumnPreferences.Serialize(selected), definitions);
        CollectionAssert.AreEquivalent(selected.ToArray(), reopened.ToArray());
        var recovered = OrderColumnPreferences.Resolve("{broken", definitions);
        CollectionAssert.AreEquivalent(new[] { "order", "product" }, recovered.ToArray());
    }

    [TestMethod]
    public void SameTimestampApiRefreshEnrichesPreviouslyMissingInvoiceIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "order-enrichment-" + Guid.NewGuid().ToString("N"));
        try
        {
            var at = DateTimeOffset.Parse("2026-09-21T12:00:00+03:00");
            var store = new OrdersStore(directory);
            store.SaveBatch([new OrderSnapshot { Marketplace = "Trendyol", ShopId = "411150", OrderId = "order-1", UpdatedAt = at, SourceUpdatedAt = at, Source = "Trendyol API" }]);
            store.SaveBatch([new OrderSnapshot
            {
                Marketplace = "Trendyol", ShopId = "411150", OrderId = "order-1", UpdatedAt = at, SourceUpdatedAt = at, Source = "Trendyol API V2",
                BillingName = "Örnek Limited", BillingEmail = "pf+masked@trendyolmail.com", CustomerEmail = "pf+masked@trendyolmail.com",
                TaxNumber = "1234567890", CustomerTaxOffice = "Nusaybin", EInvoiceAvailable = true
            }]);

            var saved = store.ReadAll().Single();
            Assert.AreEqual("1234567890", saved.TaxNumber);
            Assert.AreEqual("pf+masked@trendyolmail.com", saved.BillingEmail);
            Assert.AreEqual("Nusaybin", saved.CustomerTaxOffice);
            Assert.AreEqual(true, saved.EInvoiceAvailable);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void OrdersScreenExposesInvoiceActionsWithoutDuplicatingOrdersInsideNesSettings()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "order-invoice-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                var orders = OrdersPanel.Create(directory);
                var buttons = Walk(orders).OfType<Button>().ToArray();
                Assert.IsTrue(buttons.Any(x => x.Name == "RefreshTrendyolOrders"));
                Assert.IsTrue(buttons.Any(x => x.Name == "InvoiceSelectedOrder"));
                Assert.IsTrue(buttons.Any(x => x.Name == "OrderColumns"));
                Assert.IsTrue(buttons.Any(x => x.Name == "RefreshNesInvoiceNumbers"));
                var grid = Walk(orders).OfType<DataGrid>().First();
                Assert.IsNotNull(grid.ContextMenu);
                var headers = grid.Columns.Select(x => x.Header?.ToString()).ToArray();
                foreach (var expected in new[] { "Kayıt ID", "Sipariş No", "Platform Referansı", "Fatura No", "KDV", "Sipariş Tarihi", "Sisteme Eklenme", "Pazaryeri Fatura Durumu", "Müşteri Sevk İsmi", "Müşteri Fatura İsmi", "Mağaza Ürün Kodu", "Ürün Kodu", "Barkod", "Fatura Ürünü", "VKN / TCKN", "Kargo Kodu", "Genel Toplam", "Komisyon Tutarı" })
                    CollectionAssert.Contains(headers, expected);
                var menu = grid.ContextMenu!.Items.OfType<MenuItem>().Select(x => x.Header?.ToString()).ToArray();
                CollectionAssert.Contains(menu, "Fatura işlemleri");

                var nes = NesPanel.Create(directory);
                Assert.IsFalse(Walk(nes).OfType<TabItem>().Any(x => x.Header?.ToString() == "Siparişten fatura"));
            }
            catch (Exception ex) { failure = ex; }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    static System.Collections.Generic.IEnumerable<DependencyObject> Walk(DependencyObject node)
    {
        yield return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) foreach (var descendant in Walk(child)) yield return descendant;
    }
}
