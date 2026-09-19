using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Hepsiburada;

public sealed class HepsiburadaWorkspaceService(HepsiburadaWorkspaceStore store) : IProductChannelRemoteSnapshotProvider
{
    public async Task<HepsiburadaWorkspaceState> RefreshProductsAsync(
        string connectionId,
        HepsiburadaCredentials credentials,
        HepsiburadaApiClient client,
        CancellationToken cancellationToken = default)
    {
        global::TrMarketplaceHubDesktop.HepsiburadaConnection.Validate(credentials);
        var products = await client.GetMerchantProductsAsync(cancellationToken);
        store.ReplaceProducts(connectionId, credentials.MerchantId, products);
        return store.Read(connectionId);
    }

    public IReadOnlyList<HepsiburadaProductMatch> SuggestMatches(string connectionId, IReadOnlyList<CatalogProduct> products)
    {
        var state = store.Read(connectionId);
        return products.Select(product => HepsiburadaMatching.Suggest(product, state.Products)).ToArray();
    }

    public ProductChannelRemoteSnapshot Read(global::TrMarketplaceHubDesktop.MarketplaceConnection connection)
    {
        if (!string.Equals(connection.Channel, "hepsiburada", StringComparison.Ordinal)) throw new InvalidOperationException("Bağlantı Hepsiburada hesabı değil.");
        var state = store.Read(connection.Id);
        if (state.ShopId.Length > 0 && !string.Equals(state.ShopId, connection.ShopId, StringComparison.Ordinal)) throw new InvalidOperationException("Hepsiburada önbelleği başka mağazaya ait.");
        return new(connection.Id, connection.ShopId, state.Products.Select(product => new ProductChannelRemoteRow(
            connection.Id, connection.ShopId, product.HepsiburadaSku, product.MerchantSku, product.Barcode)).ToArray());
    }
}
