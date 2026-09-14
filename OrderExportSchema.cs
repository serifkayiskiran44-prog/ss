namespace TrMarketplaceHubDesktop;

public enum OrderExportFieldScope { Header, Item, Shipment }

public sealed record OrderExportFieldDefinition(string Id, string Label, OrderExportFieldScope Scope, bool DefaultSelected, bool IsPii);

/// Versioned registry of order fields that may appear in an Excel export
/// (#1979). This issue covers only the field catalog itself - the row model
/// that turns selected fields into worksheet rows is a separate, later
/// sequence (#1982) and is intentionally not built here. Any field marked
/// IsPii must default to unselected so a template starts redacted by
/// default; OrderSnapshot currently carries no buyer/contact PII, so no
/// field is flagged today, but the invariant is enforced for whichever
/// fields future sequences add.
public static class OrderExportFieldRegistry
{
    public const int SchemaVersion = 1;

    public static readonly IReadOnlyList<OrderExportFieldDefinition> Fields = new List<OrderExportFieldDefinition>
    {
        new("Marketplace", "Pazaryeri", OrderExportFieldScope.Header, true, false),
        new("ShopId", "Mağaza", OrderExportFieldScope.Header, true, false),
        new("OrderId", "Sipariş numarası", OrderExportFieldScope.Header, true, false),
        new("RawStatus", "Sipariş durumu (kaynak)", OrderExportFieldScope.Header, true, false),
        new("PaymentStatus", "Ödeme durumu", OrderExportFieldScope.Header, false, false),
        new("Source", "Kaynak", OrderExportFieldScope.Header, false, false),
        new("Total", "Toplam", OrderExportFieldScope.Header, true, false),
        new("Currency", "Para birimi", OrderExportFieldScope.Header, true, false),
        new("UpdatedAt", "Son güncelleme", OrderExportFieldScope.Header, false, false),
        new("ItemTitle", "Ürün adı", OrderExportFieldScope.Item, true, false),
        new("ItemSku", "SKU", OrderExportFieldScope.Item, true, false),
        new("ItemQuantity", "Adet", OrderExportFieldScope.Item, true, false),
        new("ShipmentCarrier", "Taşıyıcı", OrderExportFieldScope.Shipment, false, false),
        new("ShipmentTrackingNumber", "Takip numarası", OrderExportFieldScope.Shipment, false, false),
        new("ShipmentState", "Kargo durumu", OrderExportFieldScope.Shipment, false, false),
    }.AsReadOnly();

    static readonly Dictionary<string, OrderExportFieldDefinition> ById =
        Fields.ToDictionary(f => f.Id, f => f, StringComparer.Ordinal);

    public static bool IsValidFieldId(string id) => ById.ContainsKey(id);

    public static OrderExportFieldDefinition? TryGet(string id) => ById.GetValueOrDefault(id);

    public static IReadOnlyList<string> DefaultSelectedFieldIds =>
        Fields.Where(f => f.DefaultSelected).Select(f => f.Id).ToList();
}
