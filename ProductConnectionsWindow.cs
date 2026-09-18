using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Etsy;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;

public sealed record ProductAccountTarget(string ConnectionId, string Channel, string ShopId, string DisplayName);
public sealed record ProductAccountBadge(string ProductId, string ConnectionId, string Text, string ToolTip, string State);
public sealed class ProductConnectionReviewRow
{
    public string ProductId { get; init; } = "";
    public ProductChannelMatchOutcome Outcome { get; init; }
    public string RemoteSku { get; init; } = "";
    public string RemoteBarcode { get; init; } = "";
    public bool Reviewed { get; init; }
    public string Detail { get; init; } = "";
    public IReadOnlyList<string> RemoteOptions { get; init; } = Array.Empty<string>();
    public string SelectedRemoteId { get; set; } = "";
}
public sealed record ProductConnectionDetailRow(
    string ProductId, string ConnectionId, string Channel, string ShopId, string DisplayName,
    string RemoteId, string RemoteSku, string RemoteBarcode, string State,
    bool ManageContent, bool ManagePrice, bool ManageStock, long Version);
public sealed record ProductBindingFlagEdit(bool? ManageContent, bool? ManagePrice, bool? ManageStock);
public sealed record ProductLocalUnlinkPreview(
    string Id, string ProductId, string ConnectionId, string RemoteId,
    long BindingVersion, long ConnectionRevision, DateTime CreatedUtc);
public sealed record ProductLocalUnlinkReceipt(
    string PreviewId, string ProductId, string ConnectionId, string RemoteId,
    bool RemoteChanged, DateTime AppliedUtc);
public sealed record ProductRemoteDeactivationPreview(
    string Id, string ProductId, string ConnectionId, string RemoteId,
    long BindingVersion, long ConnectionRevision, DateTime CreatedUtc);
public sealed record ProductRemoteDeactivationReceipt(
    string PreviewId, string ProductId, string ConnectionId, string RemoteId,
    bool Succeeded, string ReceiptId, DateTime CompletedUtc);

public static class ProductConnectionPresentation
{
    public static IReadOnlyList<ProductConnectionDetailRow> Details(string? directory, string productId)
    {
        var connections = new MarketplaceConnectionStore(directory);
        var healthy = connections.List(false).ToDictionary(connection => connection.Id, StringComparer.Ordinal);
        var corrupt = connections.CorruptConnections().ToDictionary(connection => connection.Id, StringComparer.Ordinal);
        return new ProductChannelBindingStore(directory).List(productId).Select(binding =>
        {
            if (healthy.TryGetValue(binding.ConnectionId, out var connection))
                return new ProductConnectionDetailRow(binding.ProductId, binding.ConnectionId, connection.Channel, connection.ShopId, connection.DisplayName,
                    binding.RemoteId, binding.RemoteSku, binding.RemoteBarcode, binding.State,
                    binding.ManageContent, binding.ManagePrice, binding.ManageStock, binding.Version);
            if (corrupt.TryGetValue(binding.ConnectionId, out var broken))
                return new ProductConnectionDetailRow(binding.ProductId, binding.ConnectionId, broken.Channel, broken.ShopId,
                    broken.Channel + " / " + broken.ShopId + " (kayıt bozuk)", binding.RemoteId, binding.RemoteSku, binding.RemoteBarcode, binding.State,
                    binding.ManageContent, binding.ManagePrice, binding.ManageStock, binding.Version);
            return new ProductConnectionDetailRow(binding.ProductId, binding.ConnectionId, "?", "?", "Kaldırılmış hesap",
                binding.RemoteId, binding.RemoteSku, binding.RemoteBarcode, binding.State,
                binding.ManageContent, binding.ManagePrice, binding.ManageStock, binding.Version);
        }).ToArray();
    }

    public static IReadOnlyList<ProductAccountBadge> Badges(string? directory, string productId)
    {
        var connections = new MarketplaceConnectionStore(directory).List(false)
            .OrderBy(connection => connection.Channel, StringComparer.Ordinal)
            .ThenBy(connection => connection.DisplayName, StringComparer.Ordinal)
            .ThenBy(connection => connection.Id, StringComparer.Ordinal).ToArray();
        return Details(directory, productId).OrderBy(row => row.Channel, StringComparer.Ordinal).ThenBy(row => row.DisplayName, StringComparer.Ordinal).Select(row =>
        {
            var sameChannel = connections.Where(connection => connection.Channel.Equals(row.Channel, StringComparison.OrdinalIgnoreCase)).ToArray();
            var ordinal = Array.FindIndex(sameChannel, connection => connection.Id == row.ConnectionId) + 1;
            if (ordinal < 1) ordinal = 1;
            var text = row.Channel.Equals("etsy", StringComparison.OrdinalIgnoreCase) && sameChannel.Length == 1
                ? "Etsy"
                : row.Channel == "?" ? "?" : char.ToUpperInvariant(row.Channel[0]) + ordinal.ToString(CultureInfo.InvariantCulture);
            return new ProductAccountBadge(productId, row.ConnectionId, text,
                $"{row.DisplayName} · {row.ShopId} · {row.State}", row.State);
        }).ToArray();
    }
}

public interface IProductRemoteDeactivationPreviewRouter
{
    bool Supports(MarketplaceConnection connection, IMarketplaceAdapter adapter);
    ProductRemoteDeactivationPreview Preview(MarketplaceConnection connection, ProductChannelBinding binding);
    ProductRemoteDeactivationReceipt? Receipt(string previewId);
}

sealed class DisabledProductRemoteDeactivationPreviewRouter : IProductRemoteDeactivationPreviewRouter
{
    public bool Supports(MarketplaceConnection connection, IMarketplaceAdapter adapter) => false;
    public ProductRemoteDeactivationPreview Preview(MarketplaceConnection connection, ProductChannelBinding binding) =>
        throw new InvalidOperationException("Bu kanal için uzaktan pasife alma önizlemesi bağlı değil; yerel bağlantı korunuyor.");
    public ProductRemoteDeactivationReceipt? Receipt(string previewId) => null;
}

/// <summary>Reads previously refreshed account workspaces only; it never makes an HTTP request.</summary>
public sealed class CachedProductChannelSnapshotProvider(string? directory = null) : IProductChannelRemoteSnapshotProvider
{
    public ProductChannelRemoteSnapshot Read(MarketplaceConnection connection)
    {
        if (connection.Channel == "trendyol")
        {
            var rows = new TrendyolWorkspaceStore(directory).Load(connection.ShopId).Products.Select(product =>
                new ProductChannelRemoteRow(connection.Id, connection.ShopId,
                    product.ContentId.ToString(CultureInfo.InvariantCulture), product.StockCode, product.Barcode,
                    State: product.Approved ? "Approved" : "Pending")).ToArray();
            return new(connection.Id, connection.ShopId, rows);
        }
        if (connection.Channel == "etsy")
        {
            var rows = new EtsyWorkspaceStore(directory).Load(connection.ShopId).Listings.Select(listing =>
                new ProductChannelRemoteRow(connection.Id, connection.ShopId,
                    listing.ListingId.ToString(CultureInfo.InvariantCulture), listing.Sku, "", State: listing.State)).ToArray();
            return new(connection.Id, connection.ShopId, rows);
        }
        return new(connection.Id, connection.ShopId, Array.Empty<ProductChannelRemoteRow>());
    }
}

/// <summary>Durably hands candidates to the channel workspace without creating or sending a listing.</summary>
public sealed class LocalProductCreationPreviewHandoff : IProductChannelCreationPreviewHandoff
{
    readonly string connectionString;

    public LocalProductCreationPreviewHandoff(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        _ = new CatalogStore(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ProductChannelCreationPreviewRequests(
                Id TEXT PRIMARY KEY,
                ConnectionId TEXT NOT NULL,
                Channel TEXT NOT NULL,
                ShopId TEXT NOT NULL,
                ProductIdsJson TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL
            );
            CREATE TRIGGER IF NOT EXISTS ProductChannelCreationPreviewRequests_NoUpdate
            BEFORE UPDATE ON ProductChannelCreationPreviewRequests BEGIN SELECT RAISE(ABORT,'immutable product creation preview request'); END;
            CREATE TRIGGER IF NOT EXISTS ProductChannelCreationPreviewRequests_NoDelete
            BEFORE DELETE ON ProductChannelCreationPreviewRequests BEGIN SELECT RAISE(ABORT,'immutable product creation preview request'); END;
            """;
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    public string Preview(MarketplaceConnection connection, IReadOnlyList<string> productIds)
    {
        if (productIds.Count == 0 || productIds.Any(string.IsNullOrWhiteSpace) || productIds.Distinct(StringComparer.Ordinal).Count() != productIds.Count)
            throw new InvalidOperationException("Yeni ilan önizlemesi için benzersiz ürünler gerekli.");
        var id = Guid.NewGuid().ToString("N");
        using var database = Open();
        using var command = database.CreateCommand();
        command.CommandText = "INSERT INTO ProductChannelCreationPreviewRequests VALUES($id,$connection,$channel,$shop,$products,$created)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$connection", connection.Id);
        command.Parameters.AddWithValue("$channel", connection.Channel);
        command.Parameters.AddWithValue("$shop", connection.ShopId);
        command.Parameters.AddWithValue("$products", JsonSerializer.Serialize(productIds.ToArray()));
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return id;
    }
}

public sealed class ProductConnectionsModel
{
    readonly string? directory;
    readonly MarketplaceConnectionStore connections;
    readonly ProductChannelBindingStore bindings;
    readonly IProductChannelRemoteSnapshotProvider remoteSnapshots;
    readonly ProductChannelMatchService matches;
    readonly MarketplaceAdapterRegistry adapters;
    readonly IProductRemoteDeactivationPreviewRouter remoteDeactivation;
    readonly Dictionary<string, ProductLocalUnlinkPreview> localUnlinkPreviews = new(StringComparer.Ordinal);
    ProductChannelBindingPreview? preview;

    public IReadOnlyList<string> SelectedProductIds { get; }
    public IReadOnlyList<ProductAccountTarget> Targets { get; private set; } = Array.Empty<ProductAccountTarget>();
    public IReadOnlyList<ProductConnectionDetailRow> Details { get; private set; } = Array.Empty<ProductConnectionDetailRow>();
    public ProductChannelBindingPreview? CurrentPreview => preview;
    public bool CanApply => preview is not null
        && preview.Rows.All(row => row.Outcome is not (ProductChannelMatchOutcome.Conflict or ProductChannelMatchOutcome.Error))
        && preview.Rows.Where(row => row.Outcome == ProductChannelMatchOutcome.Matched).All(row => row.Reviewed);

    public ProductConnectionsModel(
        string? directory,
        IReadOnlyList<string> selectedProductIds,
        IProductChannelRemoteSnapshotProvider? remoteSnapshots = null,
        IProductChannelCreationPreviewHandoff? creationHandoff = null,
        MarketplaceAdapterRegistry? adapters = null,
        IProductRemoteDeactivationPreviewRouter? remoteDeactivation = null)
    {
        if (selectedProductIds.Count == 0 || selectedProductIds.Any(string.IsNullOrWhiteSpace) || selectedProductIds.Distinct(StringComparer.Ordinal).Count() != selectedProductIds.Count)
            throw new InvalidOperationException("Mağazaya bağlamak için ürün tablosundan en az bir ürünü açıkça seçin.");
        var exact = selectedProductIds.ToArray();
        var known = new CatalogStore(directory).Products().Select(product => product.Id).ToHashSet(StringComparer.Ordinal);
        if (exact.Any(id => !known.Contains(id))) throw new InvalidOperationException("Seçilen ürünlerden biri katalogda bulunamadı; listeyi yenileyin.");
        this.directory = directory;
        SelectedProductIds = Array.AsReadOnly(exact);
        connections = new(directory);
        bindings = new(directory);
        this.remoteSnapshots = remoteSnapshots ?? new CachedProductChannelSnapshotProvider(directory);
        this.adapters = adapters ?? MarketplaceAdapterRegistry.Default;
        this.remoteDeactivation = remoteDeactivation ?? new DisabledProductRemoteDeactivationPreviewRouter();
        matches = new(directory, this.remoteSnapshots, creationHandoff ?? new LocalProductCreationPreviewHandoff(directory));
        Refresh();
    }

    public void Refresh()
    {
        Targets = MarketplaceOperationalAccounts.List(connections)
            .Where(connection => !connection.Status.Equals("NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase))
            .Where(connection => adapters.Get(connection.Channel).Capabilities.Supports(MarketplaceOperation.ProductsRead))
            .Select(connection => new ProductAccountTarget(connection.Id, connection.Channel, connection.ShopId, connection.DisplayName))
            .ToArray();
        Details = SelectedProductIds.SelectMany(productId => ProductConnectionPresentation.Details(directory, productId)).ToArray();
    }

    public IReadOnlyList<ProductAccountBadge> Badges(string productId)
    {
        if (!SelectedProductIds.Contains(productId, StringComparer.Ordinal))
            throw new InvalidOperationException("Ürün bu bağlantı oturumunun seçimine ait değil.");
        return ProductConnectionPresentation.Badges(directory, productId);
    }

    public ProductChannelBindingPreview PreviewConnection(string connectionId)
    {
        var target = Targets.SingleOrDefault(item => item.ConnectionId == connectionId)
            ?? throw new InvalidOperationException("Seçilen mağaza hesabı bağlantı hedefi olarak uygun değil.");
        var connection = connections.Get(target.ConnectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        var snapshot = remoteSnapshots.Read(connection);
        preview = matches.PreviewMatch(connection.Id, SelectedProductIds, snapshot.Rows);
        return preview;
    }

    public ProductChannelBindingPreview Review(IReadOnlyList<ProductChannelMatchReview> selections)
    {
        if (preview is null) throw new InvalidOperationException("Önce güncel bir eşleştirme önizlemesi alın.");
        preview = matches.ReviewMatches(preview.Id, selections);
        return preview;
    }

    public ProductChannelBindingReceipt Apply()
    {
        if (!CanApply || preview is null) throw new InvalidOperationException("Uygulamadan önce güncel önizlemeyi ve eşleşmeleri açıkça inceleyin.");
        var receipt = matches.ApplyMatches(preview.Id);
        preview = null;
        Refresh();
        return receipt;
    }

    public ProductChannelBinding UpdateFlags(string productId, string connectionId, ProductBindingFlagEdit edit, long expectedVersion)
    {
        var current = RequiredBinding(productId, connectionId);
        if (current.Version != expectedVersion) throw new InvalidOperationException("Ürün-mağaza bağlantısı değişti; ayrıntıları yenileyin.");
        if (edit.ManageContent is null && edit.ManagePrice is null && edit.ManageStock is null)
            throw new InvalidOperationException("Değiştirilecek en az bir yönetim alanı seçin.");
        var saved = bindings.Save(current with
        {
            ManageContent = edit.ManageContent ?? current.ManageContent,
            ManagePrice = edit.ManagePrice ?? current.ManagePrice,
            ManageStock = edit.ManageStock ?? current.ManageStock
        }, expectedVersion);
        Refresh();
        return saved;
    }

    public ProductLocalUnlinkPreview PreviewLocalUnlink(string productId, string connectionId)
    {
        var binding = RequiredBinding(productId, connectionId);
        var connection = connections.Get(connectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        var result = new ProductLocalUnlinkPreview(Guid.NewGuid().ToString("N"), productId, connectionId, binding.RemoteId, binding.Version, connection.Revision, DateTime.UtcNow);
        localUnlinkPreviews[result.Id] = result;
        return result;
    }

    public ProductLocalUnlinkReceipt ApplyLocalUnlink(ProductLocalUnlinkPreview preview, bool approved)
    {
        if (preview is null) throw new InvalidOperationException("Önce yerel bağlantı kaldırma önizlemesi alın.");
        if (!approved) throw new InvalidOperationException("Yerel bağlantıyı kaldırmak için önizleme onayı gerekli.");
        if (!localUnlinkPreviews.TryGetValue(preview.Id, out var issued) || issued != preview)
            throw new InvalidOperationException("Yerel kaldırma önizlemesi geçersiz veya değiştirilmiş; yeni önizleme alın.");
        var currentConnection = connections.Get(preview.ConnectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        if (currentConnection.Revision != preview.ConnectionRevision)
            throw new InvalidOperationException("Mağaza bağlantısı değişti; yeni önizleme alın.");
        var current = RequiredBinding(preview.ProductId, preview.ConnectionId);
        if (current.Version != preview.BindingVersion || current.RemoteId != preview.RemoteId)
            throw new InvalidOperationException("Ürün-mağaza bağlantısı değişti; yeni önizleme alın.");
        bindings.Delete(preview.ProductId, preview.ConnectionId, preview.BindingVersion, preview.ConnectionRevision);
        localUnlinkPreviews.Remove(preview.Id);
        Refresh();
        return new(preview.Id, preview.ProductId, preview.ConnectionId, preview.RemoteId, false, DateTime.UtcNow);
    }

    public bool CanPreviewRemoteDeactivate(string productId, string connectionId)
    {
        var binding = bindings.Get(productId, connectionId);
        if (binding is null) return false;
        MarketplaceConnection? connection;
        try { connection = connections.Get(connectionId); }
        catch (MarketplaceConnectionCorruptException) { return false; }
        return connection is not null && remoteDeactivation.Supports(connection, adapters.Get(connection.Channel));
    }

    public ProductRemoteDeactivationPreview PreviewRemoteDeactivate(string productId, string connectionId)
    {
        var binding = RequiredBinding(productId, connectionId);
        var connection = connections.Get(connectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        var adapter = adapters.Get(connection.Channel);
        if (!remoteDeactivation.Supports(connection, adapter))
            throw new InvalidOperationException("Bu kanal uzaktan pasife alma önizlemesini desteklemiyor; bağlantı korunuyor.");
        var result = remoteDeactivation.Preview(connection, binding);
        if (result.ProductId != productId || result.ConnectionId != connectionId || result.RemoteId != binding.RemoteId || result.BindingVersion != binding.Version || result.ConnectionRevision != connection.Revision)
            throw new InvalidOperationException("Uzaktan pasife alma önizlemesi yanlış ürün veya mağaza hesabına ait.");
        return result;
    }

    public ProductLocalUnlinkReceipt CompleteRemoteDeactivateAndUnlink(ProductRemoteDeactivationPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var receipt = remoteDeactivation.Receipt(preview.Id)
            ?? throw new InvalidOperationException("Kanal gönderim makbuzu bulunamadı; yerel bağlantı korunuyor.");
        if (!receipt.Succeeded || receipt.PreviewId != preview.Id || receipt.ProductId != preview.ProductId ||
            receipt.ConnectionId != preview.ConnectionId || receipt.RemoteId != preview.RemoteId || string.IsNullOrWhiteSpace(receipt.ReceiptId))
            throw new InvalidOperationException("Uzaktan pasife alma makbuzu başarısız veya yanlış hesap/ürüne ait; yerel bağlantı korunuyor.");
        var connection = connections.Get(preview.ConnectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        var binding = RequiredBinding(preview.ProductId, preview.ConnectionId);
        if (connection.Revision != preview.ConnectionRevision || binding.Version != preview.BindingVersion || binding.RemoteId != preview.RemoteId)
            throw new InvalidOperationException("Mağaza veya ürün bağlantısı gönderim önizlemesinden sonra değişti; yerel bağlantı korunuyor.");
        bindings.Delete(preview.ProductId, preview.ConnectionId, preview.BindingVersion, preview.ConnectionRevision);
        Refresh();
        return new(preview.Id, preview.ProductId, preview.ConnectionId, preview.RemoteId, true, DateTime.UtcNow);
    }

    ProductChannelBinding RequiredBinding(string productId, string connectionId)
    {
        if (!SelectedProductIds.Contains(productId, StringComparer.Ordinal))
            throw new InvalidOperationException("Ürün bu bağlantı oturumunun seçimine ait değil.");
        return bindings.Get(productId, connectionId) ?? throw new InvalidOperationException("Ürün-mağaza bağlantısı bulunamadı.");
    }
}

public sealed class ProductConnectionsWindow : Window
{
    readonly ProductConnectionsModel model;
    readonly ComboBox targets = new() { Name = "ProductConnectionTarget", MinWidth = 280, DisplayMemberPath = nameof(ProductAccountTarget.DisplayName), SelectedValuePath = nameof(ProductAccountTarget.ConnectionId) };
    readonly DataGrid previewRows = new() { Name = "ProductConnectionPreviewRows", AutoGenerateColumns = false, IsReadOnly = false, CanUserAddRows = false, Height = 260 };
    readonly DataGrid details = new() { Name = "ProductConnectionDetails", AutoGenerateColumns = false, IsReadOnly = true, Height = 220 };
    readonly Button previewButton = new() { Name = "ProductConnectionPreviewButton", Content = "Eşleştirmeyi önizle", IsEnabled = false };
    readonly Button reviewButton = new() { Name = "ProductConnectionReviewButton", Content = "Eşleşmeleri inceledim", IsEnabled = false };
    readonly Button applyButton = new() { Name = "ProductConnectionApplyButton", Content = "Bağlantıları uygula", IsEnabled = false };
    readonly Button localUnlinkButton = new() { Name = "ProductLocalUnlinkPreviewButton", Content = "Yalnız yerel bağlantıyı kaldır", IsEnabled = false };
    readonly Button remoteDeactivateButton = new() { Name = "ProductRemoteDeactivatePreviewButton", Content = "Uzakta pasife alma önizlemesi", IsEnabled = false };
    readonly CheckBox manageContentEdit = new() { Name = "ProductManageContentEdit", Content = "İçerik", IsThreeState = true, IsChecked = null, IsEnabled = false };
    readonly CheckBox managePriceEdit = new() { Name = "ProductManagePriceEdit", Content = "Fiyat", IsThreeState = true, IsChecked = null, IsEnabled = false };
    readonly CheckBox manageStockEdit = new() { Name = "ProductManageStockEdit", Content = "Stok", IsThreeState = true, IsChecked = null, IsEnabled = false };
    readonly Button flagsApplyButton = new() { Name = "ProductBindingFlagsApplyButton", Content = "Seçilen yönetim alanlarını uygula", IsEnabled = false };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(4) };

    public IReadOnlyList<string> SelectedProductIds => model.SelectedProductIds;
    public bool IsApplyEnabled => applyButton.IsEnabled;

    public ProductConnectionsWindow(
        string? directory,
        IReadOnlyList<string> selectedProductIds,
        IProductChannelRemoteSnapshotProvider? remoteSnapshots = null,
        IProductChannelCreationPreviewHandoff? creationHandoff = null,
        MarketplaceAdapterRegistry? adapters = null,
        IProductRemoteDeactivationPreviewRouter? remoteDeactivation = null)
    {
        model = new(directory, selectedProductIds, remoteSnapshots, creationHandoff, adapters, remoteDeactivation);
        Title = "Ürün mağaza bağlantıları";
        Width = 1180;
        Height = 780;
        Content = Build();
        BindTargets();
        details.ItemsSource = model.Details;
        targets.SelectionChanged += (_, _) => previewButton.IsEnabled = targets.SelectedValue is string;
        details.SelectionChanged += (_, _) => UpdateDetailCommands();
        foreach (var flag in new[] { manageContentEdit, managePriceEdit, manageStockEdit })
        {
            flag.Checked += (_, _) => UpdateFlagApplyState();
            flag.Unchecked += (_, _) => UpdateFlagApplyState();
            flag.Indeterminate += (_, _) => UpdateFlagApplyState();
        }
        flagsApplyButton.Click += (_, _) => Run(() =>
        {
            var row = details.SelectedItem as ProductConnectionDetailRow ?? throw new InvalidOperationException("Bağlantı satırını seçin.");
            model.UpdateFlags(row.ProductId, row.ConnectionId,
                new(manageContentEdit.IsChecked, managePriceEdit.IsChecked, manageStockEdit.IsChecked), row.Version);
            details.ItemsSource = model.Details;
            manageContentEdit.IsChecked = managePriceEdit.IsChecked = manageStockEdit.IsChecked = null;
            status.Text = "Seçilen yerel yönetim alanları kaydedildi; boş bırakılan alanlar korundu.";
        });
        previewButton.Click += (_, _) => Run(() =>
        {
            var value = targets.SelectedValue as string ?? throw new InvalidOperationException("Hedef mağaza hesabını seçin.");
            var result = model.PreviewConnection(value);
            var choices = ReviewRows(result);
            previewRows.ItemsSource = choices;
            reviewButton.IsEnabled = choices.Any(row => row.Outcome == ProductChannelMatchOutcome.Matched || row.RemoteOptions.Count > 0);
            applyButton.IsEnabled = model.CanApply;
            status.Text = Summary(result);
        });
        reviewButton.Click += (_, _) => Run(() =>
        {
            _ = model.CurrentPreview ?? throw new InvalidOperationException("Önizleme bulunamadı.");
            previewRows.CommitEdit(DataGridEditingUnit.Cell, true);
            previewRows.CommitEdit(DataGridEditingUnit.Row, true);
            var selections = previewRows.ItemsSource.Cast<ProductConnectionReviewRow>()
                .Where(row => !string.IsNullOrWhiteSpace(row.SelectedRemoteId))
                .Select(row => new ProductChannelMatchReview(row.ProductId, row.SelectedRemoteId)).ToArray();
            if (selections.Length == 0) throw new InvalidOperationException("İncelenen uzak ilanı açıkça seçin.");
            var reviewed = model.Review(selections);
            previewRows.ItemsSource = ReviewRows(reviewed);
            applyButton.IsEnabled = model.CanApply;
            status.Text = Summary(reviewed);
        });
        applyButton.Click += (_, _) => Run(() =>
        {
            var receipt = model.Apply();
            details.ItemsSource = model.Details;
            applyButton.IsEnabled = false;
            reviewButton.IsEnabled = false;
            status.Text = $"{receipt.AppliedBindings.Count} bağlantı kaydedildi; {receipt.NewListingCandidates.Count} ürün yalnız yeni ilan önizlemesine aktarıldı.";
        });
        localUnlinkButton.Click += (_, _) => Run(() =>
        {
            var row = (ProductConnectionDetailRow)details.SelectedItem;
            var unlink = model.PreviewLocalUnlink(row.ProductId, row.ConnectionId);
            if (MessageBox.Show(this, $"{row.DisplayName} yerel yönetim bağlantısı kaldırılacak. Uzak ilan değişmeyecek. Devam edilsin mi?", "Yerel bağlantıyı kaldır", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            model.ApplyLocalUnlink(unlink, true);
            details.ItemsSource = model.Details;
            status.Text = "Yerel bağlantı kaldırıldı; uzak ilan değiştirilmedi.";
        });
        remoteDeactivateButton.Click += (_, _) => Run(() =>
        {
            var row = (ProductConnectionDetailRow)details.SelectedItem;
            var dispatch = model.PreviewRemoteDeactivate(row.ProductId, row.ConnectionId);
            status.Text = $"Uzaktan pasife alma gönderim önizlemesi oluşturuldu: {dispatch.Id}. Kanal makbuzu oluşmadan yerel bağlantı kaldırılmadı.";
        });
    }

    void BindTargets()
    {
        var view = CollectionViewSource.GetDefaultView(model.Targets);
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProductAccountTarget.Channel)));
        targets.ItemsSource = view;
    }

    UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = $"Mağazaya bağla · {model.SelectedProductIds.Count} açık seçili ürün", FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Hesap kimliği, ürün seçimi ve uzak snapshot bu önizlemeye sabitlenir. Bu ekran canlı marketplace yazımı yapmaz.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 4, 0, 8) });
        var actions = new WrapPanel();
        actions.Children.Add(targets); actions.Children.Add(previewButton); actions.Children.Add(reviewButton); actions.Children.Add(applyButton);
        header.Children.Add(actions); header.Children.Add(status);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var tabs = new TabControl();
        AddColumn(previewRows, "Ürün", nameof(ProductChannelMatchRow.ProductId), 180);
        AddColumn(previewRows, "Sonuç", nameof(ProductChannelMatchRow.Outcome), 150);
        var remoteChoice = new FrameworkElementFactory(typeof(ComboBox));
        remoteChoice.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ProductConnectionReviewRow.RemoteOptions)));
        remoteChoice.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(ProductConnectionReviewRow.SelectedRemoteId)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        previewRows.Columns.Add(new DataGridTemplateColumn { Header = "İncelenen uzak ilan", Width = 165, CellTemplate = new DataTemplate { VisualTree = remoteChoice } });
        AddColumn(previewRows, "Uzak SKU", nameof(ProductChannelMatchRow.RemoteSku), 140);
        AddColumn(previewRows, "Uzak barkod", nameof(ProductChannelMatchRow.RemoteBarcode), 150);
        AddColumn(previewRows, "İncelendi", nameof(ProductChannelMatchRow.Reviewed), 90);
        AddColumn(previewRows, "Açıklama", nameof(ProductChannelMatchRow.Detail), 430);
        tabs.Items.Add(new TabItem { Header = "Bağlama önizlemesi", Content = previewRows });
        AddColumn(details, "Ürün", nameof(ProductConnectionDetailRow.ProductId), 160);
        AddColumn(details, "Hesap", nameof(ProductConnectionDetailRow.DisplayName), 170);
        AddColumn(details, "Uzak kimlik", nameof(ProductConnectionDetailRow.RemoteId), 120);
        AddColumn(details, "İçerik", nameof(ProductConnectionDetailRow.ManageContent), 75);
        AddColumn(details, "Fiyat", nameof(ProductConnectionDetailRow.ManagePrice), 75);
        AddColumn(details, "Stok", nameof(ProductConnectionDetailRow.ManageStock), 75);
        AddColumn(details, "Durum", nameof(ProductConnectionDetailRow.State), 120);
        var detailHost = new DockPanel();
        var detailActions = new WrapPanel();
        detailActions.Children.Add(new TextBlock { Text = "Değiştir:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) });
        detailActions.Children.Add(manageContentEdit); detailActions.Children.Add(managePriceEdit); detailActions.Children.Add(manageStockEdit); detailActions.Children.Add(flagsApplyButton);
        detailActions.Children.Add(localUnlinkButton); detailActions.Children.Add(remoteDeactivateButton);
        DockPanel.SetDock(detailActions, Dock.Bottom); detailHost.Children.Add(detailActions); detailHost.Children.Add(details);
        tabs.Items.Add(new TabItem { Header = "Bağlantılar", Content = detailHost });
        root.Children.Add(tabs);
        return root;
    }

    void UpdateDetailCommands()
    {
        var row = details.SelectedItem as ProductConnectionDetailRow;
        localUnlinkButton.IsEnabled = row is not null;
        remoteDeactivateButton.IsEnabled = row is not null && model.CanPreviewRemoteDeactivate(row.ProductId, row.ConnectionId);
        manageContentEdit.IsEnabled = managePriceEdit.IsEnabled = manageStockEdit.IsEnabled = row is not null;
        manageContentEdit.IsChecked = managePriceEdit.IsChecked = manageStockEdit.IsChecked = null;
        if (row is not null)
        {
            manageContentEdit.ToolTip = $"Şu an: {(row.ManageContent ? "Açık" : "Kapalı")}; boş = koru";
            managePriceEdit.ToolTip = $"Şu an: {(row.ManagePrice ? "Açık" : "Kapalı")}; boş = koru";
            manageStockEdit.ToolTip = $"Şu an: {(row.ManageStock ? "Açık" : "Kapalı")}; boş = koru";
        }
        UpdateFlagApplyState();
    }

    void UpdateFlagApplyState() => flagsApplyButton.IsEnabled = details.SelectedItem is ProductConnectionDetailRow
        && (manageContentEdit.IsChecked.HasValue || managePriceEdit.IsChecked.HasValue || manageStockEdit.IsChecked.HasValue);

    void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { status.Text = MarketplaceConnectionStore.Redact(error.Message); applyButton.IsEnabled = false; }
    }

    static string Summary(ProductChannelBindingPreview value) => string.Join(" · ", Enum.GetValues<ProductChannelMatchOutcome>()
        .Select(outcome => (outcome, count: value.Rows.Count(row => row.Outcome == outcome))).Where(pair => pair.count > 0).Select(pair => $"{pair.outcome}: {pair.count}"));

    static IReadOnlyList<ProductConnectionReviewRow> ReviewRows(ProductChannelBindingPreview value) => value.Rows.Select(row =>
    {
        var options = value.RemoteRows.Where(remote =>
                row.Outcome == ProductChannelMatchOutcome.Matched && remote.RemoteId == row.RemoteId ||
                row.Outcome == ProductChannelMatchOutcome.NewListingCandidate && !string.IsNullOrWhiteSpace(row.LocalSku) && remote.RemoteSku == row.LocalSku)
            .Select(remote => remote.RemoteId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new ProductConnectionReviewRow
        {
            ProductId = row.ProductId,
            Outcome = row.Outcome,
            RemoteSku = row.RemoteSku,
            RemoteBarcode = row.RemoteBarcode,
            Reviewed = row.Reviewed,
            Detail = row.Detail,
            RemoteOptions = options,
            SelectedRemoteId = row.Outcome == ProductChannelMatchOutcome.Matched ? row.RemoteId : ""
        };
    }).ToArray();

    static void AddColumn(DataGrid grid, string header, string path, double width) =>
        grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });
}

public sealed class ProductBadgeTextConverter(string? directory = null) : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not CatalogProduct product) return "";
        try
        {
            return string.Join(" ", ProductConnectionPresentation.Badges(directory, product.Id).Select(badge => badge.Text));
        }
        catch (InvalidOperationException) { return ""; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
