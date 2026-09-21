using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed record NesQueuePlanRow(string OrderId, string Decision, string Reason, NesQueueRequest? Request);

public sealed record NesQueuePlan(IReadOnlyList<NesQueuePlanRow> Rows)
{
    public int ReadyCount => Rows.Count(x => x.Decision == "Ready");
    public int BlockedCount => Rows.Count(x => x.Decision == "Blocked");
}

public static class NesQueuePlanner
{
    public static NesQueuePlan Build(
        IEnumerable<OrderSnapshot> orders,
        NesSettings settings,
        Func<string, string?> receiverAliasByTaxNumber,
        bool uploadMarketplace)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(receiverAliasByTaxNumber);
        NesSettingsValidator.Validate(settings);
        var rows = new List<NesQueuePlanRow>();
        foreach (var order in orders)
        {
            if (order.InvoiceUuid.Length > 0 || order.InvoiceNumber.Length > 0 || order.IsMarketplaceInvoiced)
            {
                rows.Add(new(order.OrderId, "AlreadyInvoiced", "Sipariş daha önce faturalanmış; ikinci fatura oluşturulmadı.", null));
                continue;
            }
            try
            {
                OrderInvoiceRules.ValidateCanInvoice(order);
                var kind = OrderInvoiceRules.DefaultKind(order);
                var identity = NesReceiverIdentityResolver.Resolve(order, kind);
                var alias = kind == NesDocumentKind.EInvoice ? (receiverAliasByTaxNumber(identity.Value) ?? "").Trim() : "";
                if (kind == NesDocumentKind.EInvoice && (alias.Length == 0 || !alias.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("e-Fatura alıcısı için kayıtlı NES alıcı etiketi gerekli.");
                var packageId = order.Shipments.Select(x => x.Id).FirstOrDefault(x => long.TryParse(x, out var value) && value > 0) ?? "";
                if (uploadMarketplace && order.Marketplace.Equals("Trendyol", StringComparison.OrdinalIgnoreCase) && packageId.Length == 0)
                    throw new InvalidOperationException("Trendyol PDF yüklemesi için pozitif paket kimliği gerekli.");
                var fingerprint = Fingerprint(order, settings, kind, identity.Value, alias);
                rows.Add(new(order.OrderId, "Ready", "Fatura kuyruğuna eklenmeye hazır.",
                    new(order.Marketplace, order.ShopId, order.OrderId, packageId, kind,
                        uploadMarketplace, alias, fingerprint)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                rows.Add(new(order.OrderId, "Blocked", MarketplaceConnectionStore.Redact(ex.Message), null));
            }
        }
        return new(rows);
    }

    static string Fingerprint(OrderSnapshot order, NesSettings settings, NesDocumentKind kind, string identity, string alias)
    {
        var projection = new
        {
            order.Marketplace, order.ShopId, order.OrderId,
            DocumentKind = kind.ToString(), ReceiverIdentity = identity, ReceiverAlias = alias,
            Currency = order.Currency.Trim().ToUpperInvariant(),
            Billing = new { order.BillingName, order.BillingAddress, order.BillingCountry, order.BillingCity, order.BillingDistrict, order.CustomerTaxOffice },
            Items = order.Items.Select(x => new
            {
                x.Sku, Title = x.InvoiceTitle.Length > 0 ? x.InvoiceTitle : x.Title, x.Quantity,
                NetUnitPrice = (x.NetUnitPrice ?? Net(x.UnitPrice ?? 0m, x.VatRate)).ToString("0.############################", CultureInfo.InvariantCulture),
                VatRate = x.VatRate.ToString("0.############################", CultureInfo.InvariantCulture),
                Discount = (x.DiscountAmount ?? 0m).ToString("0.############################", CultureInfo.InvariantCulture)
            }).ToArray(),
            Seller = new { settings.CompanyTitle, settings.TaxNumber, settings.TaxOffice, settings.Address, settings.City, settings.District }
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(projection)));
    }

    static decimal Net(decimal gross, decimal vatRate) => vatRate <= 0 ? gross : decimal.Round(gross / (1m + vatRate / 100m), 8, MidpointRounding.AwayFromZero);
}
