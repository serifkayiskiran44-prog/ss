using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

public sealed record TrendyolProductV2Item(string Barcode, string StockCode, string Title, string Brand, int Quantity, decimal ListPrice, decimal SalePrice);
public sealed record TrendyolInvoicePreview(long SellerId, long ShipmentPackageId, string InvoiceLink, string? InvoiceNumber, long? InvoiceDateTime);

public static class TrendyolPilot
{
    // Batch limit and listPrice>=salePrice rule confirmed against https://developers.trendyol.com/v3.0/docs/9-stock-and-price-update-1
    // and https://developers.trendyol.com/v2.0/docs/product-create-createproducts (fetched 2026-09-12); not taken from an unverified report.
    public static void ValidateProductBatch(IReadOnlyList<TrendyolProductV2Item> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is < 1 or > 1000) throw new InvalidOperationException("Trendyol Product V2 batch 1–1000 ürün içermeli.");
        if (items.Any(x => string.IsNullOrWhiteSpace(x.Barcode) || string.IsNullOrWhiteSpace(x.StockCode) || string.IsNullOrWhiteSpace(x.Title) || x.Quantity < 0 || x.ListPrice < x.SalePrice)) throw new InvalidOperationException("Trendyol Product V2 ürün alanları geçersiz; listPrice salePrice’tan küçük olamaz.");
    }

    public static void ValidateInvoice(TrendyolInvoicePreview preview)
    {
        if (preview.SellerId <= 0 || preview.ShipmentPackageId <= 0 || !Uri.TryCreate(preview.InvoiceLink, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Trendyol fatura önizlemesi seller/package/HTTPS link içermeli.");
        if (preview.InvoiceNumber is not null && !Regex.IsMatch(preview.InvoiceNumber, "^[A-Za-z0-9]{3}\\d{13}$")) throw new InvalidOperationException("Trendyol invoiceNumber 16 karakterlik resmi formata uymuyor.");
        if (preview.InvoiceDateTime is not null && preview.InvoiceDateTime <= 0) throw new InvalidOperationException("Trendyol invoiceDateTime pozitif Unix zamanı olmalı.");
    }
}
