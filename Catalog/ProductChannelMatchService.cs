using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed class ProductChannelMatchService
{
    readonly CatalogStore catalog;
    readonly MarketplaceConnectionStore connections;
    readonly ProductChannelBindingStore bindings;
    readonly IProductChannelRemoteSnapshotProvider? remoteSnapshots;
    readonly IProductChannelCreationPreviewHandoff? creationHandoff;

    public ProductChannelMatchService(string? directory = null, IProductChannelRemoteSnapshotProvider? remoteSnapshots = null, IProductChannelCreationPreviewHandoff? creationHandoff = null)
    {
        catalog = new(directory);
        connections = new(directory);
        bindings = new(directory);
        this.remoteSnapshots = remoteSnapshots;
        this.creationHandoff = creationHandoff;
    }

    public ProductChannelBindingPreview PreviewMatch(string connectionId, IReadOnlyList<string> productIds, IReadOnlyList<ProductChannelRemoteRow> remoteRows)
    {
        var connection = EnabledConnection(connectionId);
        if (productIds.Count == 0 || productIds.Any(string.IsNullOrWhiteSpace) || productIds.Distinct(StringComparer.Ordinal).Count() != productIds.Count)
            throw new ArgumentException("Benzersiz katalog ürünleri seçin.", nameof(productIds));
        ValidateRemoteIdentity(connection, remoteRows);
        var providerSnapshot = remoteSnapshots?.Read(connection);
        if (providerSnapshot is not null)
        {
            ValidateSnapshotIdentity(connection, providerSnapshot);
            if (ProductChannelBindingStore.RemoteHash(providerSnapshot.Rows) != ProductChannelBindingStore.RemoteHash(remoteRows))
                throw new InvalidOperationException("Uzak mağaza snapshot'ı değişti; ürünleri yeniden okuyun.");
        }
        var products = catalog.Products().Where(x => productIds.Contains(x.Id, StringComparer.Ordinal)).ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (products.Count != productIds.Count) throw new InvalidOperationException("Seçilen katalog ürünü bulunamadı veya bozuk.");
        var allProducts = catalog.Products();
        var duplicateLocalBarcodes = allProducts.Where(x => !string.IsNullOrWhiteSpace(x.Barcode)).GroupBy(x => x.Barcode, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var duplicateRemoteBarcodes = remoteRows.Where(x => !string.IsNullOrWhiteSpace(x.RemoteBarcode)).GroupBy(x => x.RemoteBarcode, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var existing = bindings.List(connectionId: connection.Id);
        var rows = new List<ProductChannelMatchRow>();
        foreach (var id in productIds)
        {
            var product = products[id];
            if (string.IsNullOrWhiteSpace(product.Barcode))
            {
                rows.Add(NewCandidate(product, connection.Id, "Yerel barkod boş; otomatik eşleştirme yapılmadı."));
                continue;
            }
            if (duplicateLocalBarcodes.Contains(product.Barcode) || duplicateRemoteBarcodes.Contains(product.Barcode))
            {
                rows.Add(Conflict(product, connection.Id, "Yinelenen yerel veya uzak barkod; otomatik eşleştirme engellendi."));
                continue;
            }
            var matches = remoteRows.Where(x => x.RemoteBarcode == product.Barcode).ToArray();
            if (matches.Length == 0)
            {
                rows.Add(NewCandidate(product, connection.Id, "Tam barkod eşleşmesi yok; yeni ilan önizlemesine aday."));
                continue;
            }
            var remote = matches[0];
            if (existing.Any(x => x.ProductId != product.Id && x.RemoteId == remote.RemoteId))
            {
                rows.Add(Conflict(product, connection.Id, "Uzak ilan bu hesapta başka bir ürüne bağlı."));
                continue;
            }
            var current = existing.SingleOrDefault(x => x.ProductId == product.Id);
            if (current is not null && current.RemoteId == remote.RemoteId && current.RemoteSku == remote.RemoteSku && current.RemoteBarcode == remote.RemoteBarcode)
            {
                rows.Add(new(product.Id, connection.Id, product.Sku, product.Barcode, remote.RemoteId, remote.RemoteSku, remote.RemoteBarcode, ProductChannelMatchOutcome.Skipped, false, "Bağlantı zaten güncel."));
                continue;
            }
            rows.Add(new(product.Id, connection.Id, product.Sku, product.Barcode, remote.RemoteId, remote.RemoteSku, remote.RemoteBarcode, ProductChannelMatchOutcome.Matched, false, "Tam barkod eşleşmesi; uygulamadan önce inceleme gerekli."));
        }
        var preview = NewPreview(connection, "", remoteRows, rows);
        bindings.PersistPreview(preview);
        return Clone(preview);
    }

    public ProductChannelBindingPreview ReviewMatches(string previewId, IReadOnlyList<ProductChannelMatchReview> selections)
    {
        if (selections.Count == 0 || selections.GroupBy(x => x.ProductId, StringComparer.Ordinal).Any(x => x.Count() > 1) || selections.GroupBy(x => x.RemoteId, StringComparer.Ordinal).Any(x => x.Count() > 1))
            throw new InvalidOperationException("Benzersiz ürün ve uzak ilan seçimleri gerekli.");
        var original = bindings.Preview(previewId);
        if (bindings.Receipt(previewId) is not null) throw new InvalidOperationException("Uygulanmış önizleme yeniden incelenemez.");
        var rows = original.Rows.ToDictionary(x => x.ProductId, StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            if (!rows.TryGetValue(selection.ProductId, out var row)) throw new InvalidOperationException("Seçilen ürün önizlemede yok.");
            if (row.Outcome is ProductChannelMatchOutcome.Conflict or ProductChannelMatchOutcome.Error or ProductChannelMatchOutcome.Skipped)
                throw new InvalidOperationException("Çakışan, hatalı veya atlanmış satır eşleştirilemez.");
            var remote = original.RemoteRows.SingleOrDefault(x => x.RemoteId == selection.RemoteId)
                ?? throw new InvalidOperationException("Seçilen uzak ilan bu hesap snapshot'ında yok.");
            var exactBarcode = !string.IsNullOrWhiteSpace(row.LocalBarcode) && row.LocalBarcode == remote.RemoteBarcode;
            var explicitSku = !string.IsNullOrWhiteSpace(row.LocalSku) && row.LocalSku == remote.RemoteSku;
            if (!exactBarcode && !explicitSku) throw new InvalidOperationException("Manuel seçim için tam SKU eşleşmesi gerekli.");
            rows[selection.ProductId] = row with
            {
                RemoteId = remote.RemoteId,
                RemoteSku = remote.RemoteSku,
                RemoteBarcode = remote.RemoteBarcode,
                Outcome = ProductChannelMatchOutcome.Matched,
                Reviewed = true,
                Detail = exactBarcode ? "Tam barkod eşleşmesi incelendi." : "Tam SKU seçimi kullanıcı tarafından incelendi."
            };
        }
        var connection = EnabledConnection(original.ConnectionId);
        var reviewed = original with { Id = Guid.NewGuid().ToString("N"), ParentPreviewId = original.Id, CreatedUtc = DateTime.UtcNow, Rows = rows.Values.OrderBy(x => original.Rows.ToList().FindIndex(y => y.ProductId == x.ProductId)).ToArray() };
        if (connection.Revision != original.ConnectionRevision || connection.Channel != original.Channel || connection.ShopId != original.ShopId)
            throw new InvalidOperationException("Mağaza bağlantısı değişti; yeni önizleme alın.");
        bindings.PersistPreview(reviewed);
        return Clone(reviewed);
    }

    public ProductChannelBindingReceipt ApplyMatches(string previewId)
    {
        var preview = bindings.Preview(previewId);
        var connection = EnabledConnection(preview.ConnectionId);
        ProductChannelRemoteSnapshot snapshot = remoteSnapshots?.Read(connection) ?? new(connection.Id, connection.ShopId, preview.RemoteRows);
        ValidateSnapshotIdentity(connection, snapshot);
        var currentRemoteHash = ProductChannelBindingStore.RemoteHash(snapshot.Rows);
        bindings.ValidatePreview(preview, currentRemoteHash);
        var candidates = preview.Rows.Where(x => x.Outcome == ProductChannelMatchOutcome.NewListingCandidate).Select(x => x.ProductId).Distinct(StringComparer.Ordinal).ToArray();
        var creationPreviewId = candidates.Length > 0 && creationHandoff is not null ? creationHandoff.Preview(connection, candidates) : "";
        return bindings.Apply(preview, currentRemoteHash, creationPreviewId);
    }

    ProductChannelBindingPreview NewPreview(MarketplaceConnection connection, string parentId, IReadOnlyList<ProductChannelRemoteRow> remoteRows, IReadOnlyList<ProductChannelMatchRow> rows) => new(
        Guid.NewGuid().ToString("N"), parentId, connection.Id, connection.Channel, connection.ShopId, DateTime.UtcNow, connection.Revision,
        bindings.CatalogHash(rows.Select(x => x.ProductId)), bindings.BindingVersionHash(connection.Id, rows.Select(x => x.ProductId)),
        ProductChannelBindingStore.RemoteHash(remoteRows), remoteRows.Select(x => x with { }).ToArray(), rows.Select(x => x with { }).ToArray());

    MarketplaceConnection EnabledConnection(string connectionId)
    {
        var connection = connections.Get(connectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        if (!connection.Enabled) throw new InvalidOperationException("Mağaza bağlantısı etkin değil.");
        return connection;
    }

    static void ValidateRemoteIdentity(MarketplaceConnection connection, IEnumerable<ProductChannelRemoteRow> rows)
    {
        if (rows.Any(x => x.ConnectionId != connection.Id || x.ShopId != connection.ShopId)) throw new InvalidOperationException("Uzak ürün satırı başka bir mağaza hesabına ait.");
        if (rows.Any(x => string.IsNullOrWhiteSpace(x.RemoteId)) || rows.GroupBy(x => x.RemoteId, StringComparer.Ordinal).Any(x => x.Count() > 1)) throw new InvalidOperationException("Uzak ilan kimlikleri zorunlu ve benzersiz olmalı.");
    }

    static void ValidateSnapshotIdentity(MarketplaceConnection connection, ProductChannelRemoteSnapshot snapshot)
    {
        if (snapshot.ConnectionId != connection.Id || snapshot.ShopId != connection.ShopId) throw new InvalidOperationException("Uzak snapshot başka bir mağaza hesabına ait.");
        ValidateRemoteIdentity(connection, snapshot.Rows);
    }

    static ProductChannelMatchRow NewCandidate(CatalogProduct product, string connectionId, string detail) => new(product.Id, connectionId, product.Sku, product.Barcode, "", "", "", ProductChannelMatchOutcome.NewListingCandidate, false, detail);
    static ProductChannelMatchRow Conflict(CatalogProduct product, string connectionId, string detail) => new(product.Id, connectionId, product.Sku, product.Barcode, "", "", "", ProductChannelMatchOutcome.Conflict, false, detail);
    static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
