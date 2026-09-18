namespace TrMarketplaceHubDesktop;

public sealed record RemoteProductIdentity(string ConnectionId, string RemoteId, string Sku, string Barcode);

public interface IMarketplaceAdapter
{
    string Channel { get; }
    MarketplaceCapabilities Capabilities { get; }
    Task<IReadOnlyList<RemoteProductIdentity>> ReadProductsAsync(string connectionId, CancellationToken token);
    Task<IReadOnlyList<OrderSnapshot>> ReadOrdersAsync(string connectionId, DateTime fromUtc, CancellationToken token);
}

public sealed class MarketplaceAdapterRegistry
{
    readonly IReadOnlyDictionary<string, IMarketplaceAdapter> adapters;

    public static MarketplaceAdapterRegistry Default { get; } = new(
        MarketplaceConnectionCatalog.All.Select(definition => new CatalogMarketplaceAdapter(definition)));

    public MarketplaceAdapterRegistry(IEnumerable<IMarketplaceAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var map = new Dictionary<string, IMarketplaceAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            if (adapter is null || string.IsNullOrWhiteSpace(adapter.Channel))
                throw new ArgumentException("Bağdaştırıcı kanalı zorunlu.", nameof(adapters));
            if (!map.TryAdd(adapter.Channel.Trim(), adapter))
                throw new ArgumentException("Aynı kanal için birden fazla bağdaştırıcı kaydedilemez.", nameof(adapters));
        }
        this.adapters = map;
    }

    public IMarketplaceAdapter Get(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || !adapters.TryGetValue(channel.Trim(), out var adapter))
            throw new ArgumentException("Desteklenmeyen kanal.", nameof(channel));
        return adapter;
    }

    sealed class CatalogMarketplaceAdapter(MarketplaceConnectionDefinition definition) : IMarketplaceAdapter
    {
        public string Channel { get; } = definition.Id;
        public MarketplaceCapabilities Capabilities { get; } = definition.Capabilities;

        public Task<IReadOnlyList<RemoteProductIdentity>> ReadProductsAsync(string connectionId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            EnsureSupported(MarketplaceOperation.ProductsRead);
            throw new InvalidOperationException("Ürün okuma, seçili hesabın uzman çalışma alanından başlatılmalıdır.");
        }

        public Task<IReadOnlyList<OrderSnapshot>> ReadOrdersAsync(string connectionId, DateTime fromUtc, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            EnsureSupported(MarketplaceOperation.OrdersRead);
            throw new InvalidOperationException("Sipariş okuma, hesap kapsamlı okuyucuya bağlanmadı.");
        }

        void EnsureSupported(MarketplaceOperation operation)
        {
            if (!Capabilities.Supports(operation))
                throw new InvalidOperationException($"{Channel}/{operation}: capability desteklenmiyor; HTTP isteği oluşturulmadı.");
        }
    }
}
