using System.Net.Http;

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

    public static MarketplaceAdapterRegistry Default { get; } = CreateDefault();

    public static MarketplaceAdapterRegistry CreateDefault(string? directory = null, Func<HttpClient>? httpFactory = null) => new(
        MarketplaceConnectionCatalog.All.Select(definition => definition.Id is "etsy" or "trendyol"
            ? (IMarketplaceAdapter)new AccountScopedMarketplaceAdapter(definition, directory, httpFactory)
            : new CatalogMarketplaceAdapter(definition)));

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

    sealed class AccountScopedMarketplaceAdapter(
        MarketplaceConnectionDefinition definition,
        string? directory,
        Func<HttpClient>? httpFactory) : IMarketplaceAdapter
    {
        public string Channel { get; } = definition.Id;
        public MarketplaceCapabilities Capabilities { get; } = definition.Capabilities;

        public Task<IReadOnlyList<RemoteProductIdentity>> ReadProductsAsync(string connectionId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Ürün okuma, seçili hesabın uzman çalışma alanından başlatılmalıdır.");
        }

        public async Task<IReadOnlyList<OrderSnapshot>> ReadOrdersAsync(string connectionId, DateTime fromUtc, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Capabilities.Supports(MarketplaceOperation.OrdersRead))
                throw new InvalidOperationException($"{Channel}/OrdersRead: capability desteklenmiyor; HTTP isteği oluşturulmadı.");
            var store = new MarketplaceConnectionStore(directory);
            var connection = store.Get(connectionId) ?? throw new InvalidOperationException("Sipariş hesabı bulunamadı.");
            if (!connection.Channel.Equals(Channel, StringComparison.OrdinalIgnoreCase) || !MarketplaceOperationalAccounts.IsEligible(connection, store))
                throw new InvalidOperationException("Sipariş hesabı etkin veya operasyonel değil.");
            var vault = new MarketplaceCredentialVault(directory);
            using var http = httpFactory?.Invoke() ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };
            if (Channel == "trendyol")
            {
                var settings = vault.Load<TrendyolSettings>(connection.Id, connection.Channel, connection.ShopId)
                    ?? throw new InvalidOperationException("Trendyol bağlantı bilgileri bu hesap için bulunamadı.");
                using var client = new Trendyol.TrendyolApiClient(settings, http);
                return await client.GetOrdersAsync(fromUtc, token).ConfigureAwait(false);
            }
            if (Channel == "etsy")
            {
                var credentials = vault.Load<EtsyCredentials>(connection.Id, connection.Channel, connection.ShopId)
                    ?? throw new InvalidOperationException("Etsy bağlantı bilgileri bu hesap için bulunamadı.");
                var since = fromUtc == DateTime.MinValue ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc));
                return await new OrdersEtsyClient(http).ReadAsync(credentials, since, token).ConfigureAwait(false);
            }
            throw new InvalidOperationException("Sipariş okuyucu uygulanmadı.");
        }
    }
}
