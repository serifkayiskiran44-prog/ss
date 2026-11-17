namespace TrMarketplaceHubDesktop.Hepsiburada;

public sealed class HepsiburadaOrderReader(HepsiburadaApiClient client)
{
    public async Task<IReadOnlyList<global::TrMarketplaceHubDesktop.OrderSnapshot>> ReadAsync(
        HepsiburadaCredentials credentials, string connectionId, DateTime fromUtc, CancellationToken cancellationToken = default)
    {
        global::TrMarketplaceHubDesktop.HepsiburadaConnection.Validate(credentials);
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Bağlantı kimliği gerekli.", nameof(connectionId));
        var remote = await client.GetOrdersAsync(fromUtc, cancellationToken).ConfigureAwait(false);
        return remote.GroupBy(line => line.OrderNumber, StringComparer.Ordinal).Select(group =>
        {
            var first = group.OrderBy(line => line.UpdatedUtc).First();
            var updated = group.Max(line => line.UpdatedUtc);
            var currencies = group.Select(line => line.Currency).Distinct(StringComparer.Ordinal).ToArray();
            var review = currencies.Length != 1;
            return new global::TrMarketplaceHubDesktop.OrderSnapshot
            {
                Marketplace = "hepsiburada", ShopId = credentials.MerchantId, ConnectionId = connectionId,
                OrderId = group.Key, RawStatus = first.Status.Length == 0 ? "paid" : first.Status,
                PaymentStatus = "Ödendi", Source = "Hepsiburada API", UpdatedAt = updated, SourceUpdatedAt = updated,
                CustomerName = first.CustomerName, ShippingName = first.CustomerName, ShippingAddress = first.ShippingAddress,
                ShippingCity = first.ShippingCity, ShippingDistrict = first.ShippingDistrict,
                Currency = currencies.Length == 1 ? currencies[0] : "", Total = group.All(x => x.UnitPrice.HasValue) ? group.Sum(x => x.UnitPrice!.Value * x.Quantity) : null,
                ReviewRequired = review, ReviewReason = review ? "Sipariş kalemlerinin para birimleri farklı." : "",
                Items = group.Select(line => new global::TrMarketplaceHubDesktop.OrderItem
                {
                    Sku = line.MerchantSku, Barcode = line.Barcode, ProductId = line.HepsiburadaSku, Title = line.Title,
                    Quantity = line.Quantity, UnitPrice = line.UnitPrice
                }).ToList(),
                Shipments = group.Where(line => line.PackageNumber.Length > 0).GroupBy(line => line.PackageNumber, StringComparer.Ordinal)
                    .Select(package => new global::TrMarketplaceHubDesktop.OrderShipment { Id = package.Key, Carrier = package.First().CargoCompany, State = "Preparing", Source = "Hepsiburada API" }).ToList()
            };
        }).ToArray();
    }
}
