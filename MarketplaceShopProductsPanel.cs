using Microsoft.Data.Sqlite;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Etsy;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop;

public enum MarketplaceShopBindingFilter { All, Linked, Unlinked, Error }
public enum MarketplaceShopManagementFilter { All, Managed, Unmanaged, Content, Price, Stock }
public enum MarketplaceShopBulkOperation
{
    Management, Category, Brand, Delivery, Taxonomy, Properties, Shipping, Readiness,
    CreatePreview, PricePreview, StockPreview, ContentPreview, PriceAndStockPreview
}

public sealed record MarketplaceShopProductFilter(
    string Search = "",
    MarketplaceShopBindingFilter Binding = MarketplaceShopBindingFilter.All,
    MarketplaceShopManagementFilter Management = MarketplaceShopManagementFilter.All);

public sealed record MarketplaceShopProductRow(
    string ProductId, string Sku, string Gtin, string Barcode, string Name,
    int LocalStock, decimal LocalPrice, string LocalCurrency,
    int? RemoteStock, decimal? RemotePrice, string RemoteCurrency,
    string Category, string Brand, string RemoteId, string RemoteState, string Error,
    bool ManageContent, bool ManagePrice, bool ManageStock)
{
    public string ManagementState => $"İçerik {(ManageContent ? "✓" : "–")} · Fiyat {(ManagePrice ? "✓" : "–")} · Stok {(ManageStock ? "✓" : "–")}";
}

public sealed record MarketplaceShopSelectionSnapshot(
    string Id, string ConnectionId, long ConnectionRevision, DateTime CreatedUtc,
    IReadOnlyList<string> ProductIds, IReadOnlyDictionary<string,long> BindingVersions);

public sealed record MarketplaceShopSpecialistPreviewRow(
    string ProductId, long BindingVersion, string RemoteId, string RemoteSku, string RemoteBarcode,
    bool ManageContent, bool ManagePrice, bool ManageStock, string CategoryId, string TemplateId, string State);
public sealed record MarketplaceShopSpecialistPreview(
    string Id, string ConnectionId, string Channel, string ShopId, long ConnectionRevision,
    MarketplaceShopBulkOperation Operation, DateTime CreatedUtc,
    IReadOnlyList<MarketplaceShopSpecialistPreviewRow> Rows);

public sealed record MarketplaceShopBulkEdit(
    bool? ManageContent = null, bool? ManagePrice = null, bool? ManageStock = null,
    string? CategoryId = null, string? TemplateId = null, string? Value = null);

public sealed record MarketplaceShopBulkPreviewRow(
    string ProductId, DateTime CatalogUpdatedUtc, long BindingVersion,
    bool? ManageContent, bool? ManagePrice, bool? ManageStock,
    string? CategoryId, string? TemplateId, string? Value);

public sealed record MarketplaceShopBulkPreview(
    string Id, string ConnectionId, string Channel, string ShopId,
    long ConnectionRevision, MarketplaceShopBulkOperation Operation,
    DateTime CreatedUtc, IReadOnlyList<MarketplaceShopBulkPreviewRow> Rows);

public sealed record MarketplaceShopBulkReceipt(
    string PreviewId, string ConnectionId, DateTime AppliedUtc,
    IReadOnlyList<string> ProductIds, int RemoteWrites);

public sealed class MarketplaceShopProductsModel
{
    readonly string directory;
    readonly MarketplaceConnectionStore connections;
    readonly ProductChannelBindingStore bindings;
    readonly CatalogStore catalog;
    readonly MarketplaceAdapterRegistry registry;
    readonly string connectionString;
    MarketplaceConnection connection;

    public string ConnectionId => connection.Id;
    public string AccountLabel => $"{connection.DisplayName} / {connection.ShopId}";
    public IReadOnlyList<MarketplaceShopBulkOperation> BulkOperations { get; }

    public MarketplaceShopProductsModel(string connectionId, string? directory = null, MarketplaceAdapterRegistry? registry = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        connections = new(this.directory);
        connection = RequireOperational(connectionId);
        this.registry = registry ?? MarketplaceAdapterRegistry.Default;
        bindings = new(this.directory);
        catalog = new(this.directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(this.directory, "catalog.db"), DefaultTimeout = 15 }.ToString();
        BulkOperations = OperationsFor(this.registry.Get(connection.Channel).Capabilities);
        Initialize();
    }

    public IReadOnlyList<MarketplaceShopProductRow> Filter(MarketplaceShopProductFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        connection = RequireOperational(connection.Id);
        var bindingByProduct = bindings.List(connectionId: connection.Id).ToDictionary(x => x.ProductId, StringComparer.Ordinal);
        var remote = RemoteCache();
        var rows = catalog.Products().Select(product => Row(product, bindingByProduct.GetValueOrDefault(product.Id), remote)).Where(row =>
        {
            var hasBinding = bindingByProduct.ContainsKey(row.ProductId);
            var hasError = hasBinding && IsError(row.RemoteState, row.Error);
            var bindingMatch = filter.Binding switch
            {
                MarketplaceShopBindingFilter.Linked => hasBinding && !hasError,
                MarketplaceShopBindingFilter.Unlinked => !hasBinding,
                MarketplaceShopBindingFilter.Error => hasError,
                _ => true
            };
            var managementMatch = filter.Management switch
            {
                MarketplaceShopManagementFilter.Managed => row.ManageContent || row.ManagePrice || row.ManageStock,
                MarketplaceShopManagementFilter.Unmanaged => !row.ManageContent && !row.ManagePrice && !row.ManageStock,
                MarketplaceShopManagementFilter.Content => row.ManageContent,
                MarketplaceShopManagementFilter.Price => row.ManagePrice,
                MarketplaceShopManagementFilter.Stock => row.ManageStock,
                _ => true
            };
            var query = filter.Search?.Trim() ?? "";
            var searchMatch = query.Length == 0 || new[] { row.Sku, row.Gtin, row.Barcode, row.Name, row.Category, row.Brand, row.RemoteId, row.RemoteState, row.Error }
                .Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            return bindingMatch && managementMatch && searchMatch;
        }).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.ProductId, StringComparer.Ordinal).ToArray();
        return rows;
    }

    public MarketplaceShopSelectionSnapshot SelectAllFiltered(MarketplaceShopProductFilter filter) => SelectPage(Filter(filter).Select(x => x.ProductId));

    public MarketplaceShopSelectionSnapshot SelectPage(IEnumerable<string> productIds)
    {
        var ids = productIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (ids.Length == 0) throw new InvalidOperationException("Önce en az bir ürün seçin.");
        connection = RequireOperational(connection.Id);
        var currentBindings = bindings.List(connectionId: connection.Id).ToDictionary(x => x.ProductId, x => x.Version, StringComparer.Ordinal);
        var versions = new ReadOnlyDictionary<string,long>(ids.ToDictionary(id => id, id => currentBindings.GetValueOrDefault(id), StringComparer.Ordinal));
        return new(Guid.NewGuid().ToString("N"), connection.Id, connection.Revision, DateTime.UtcNow, Array.AsReadOnly(ids), versions);
    }

    public IReadOnlyList<string> Resolve(MarketplaceShopSelectionSnapshot snapshot)
    {
        ValidateSelection(snapshot);
        return snapshot.ProductIds.ToArray();
    }

    public MarketplaceShopBulkPreview PreviewBulk(MarketplaceShopSelectionSnapshot selection, MarketplaceShopBulkOperation operation, MarketplaceShopBulkEdit edit)
    {
        ValidateSelection(selection);
        if (!BulkOperations.Contains(operation)) throw new InvalidOperationException("Bu işlem seçili kanal tarafından desteklenmiyor.");
        ArgumentNullException.ThrowIfNull(edit);
        var productById = catalog.Products().ToDictionary(x => x.Id, StringComparer.Ordinal);
        var currentBindings = bindings.List(connectionId: connection.Id).ToDictionary(x => x.ProductId, StringComparer.Ordinal);
        var rows = selection.ProductIds.Select(id =>
        {
            if (!productById.TryGetValue(id, out var product)) throw new InvalidOperationException("Ürün silinmiş; seçimi yenileyin.");
            currentBindings.TryGetValue(id, out var binding);
            if (binding is null && operation is MarketplaceShopBulkOperation.Management
                or MarketplaceShopBulkOperation.Category
                or MarketplaceShopBulkOperation.Taxonomy
                or MarketplaceShopBulkOperation.Delivery
                or MarketplaceShopBulkOperation.Shipping)
                throw new InvalidOperationException("Yerel yönetim ve eşleme alanları yalnız bağlı ürünlerde değiştirilebilir.");
            return new MarketplaceShopBulkPreviewRow(id, product.UpdatedUtc, binding?.Version ?? 0,
                edit.ManageContent, edit.ManagePrice, edit.ManageStock,
                NullIfBlank(edit.CategoryId), NullIfBlank(edit.TemplateId), NullIfBlank(edit.Value));
        }).ToArray();
        var preview = new MarketplaceShopBulkPreview(Guid.NewGuid().ToString("N"), connection.Id, connection.Channel,
            connection.ShopId, connection.Revision, operation, DateTime.UtcNow, rows);
        using var database = Open();
        using var command = database.CreateCommand();
        command.CommandText = "INSERT INTO MarketplaceShopBulkPreviews(Id,ConnectionId,Json) VALUES($id,$connection,$json)";
        command.Parameters.AddWithValue("$id", preview.Id);
        command.Parameters.AddWithValue("$connection", preview.ConnectionId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(preview));
        command.ExecuteNonQuery();
        return preview;
    }

    public MarketplaceShopSpecialistPreview PreviewSpecialist(MarketplaceShopSelectionSnapshot selection, MarketplaceShopBulkOperation operation)
        => PreviewSpecialist(selection, operation, directScopedWorkflow: false);

    internal MarketplaceShopSpecialistPreview PreviewDirectSpecialist(MarketplaceShopSelectionSnapshot selection, MarketplaceShopBulkOperation operation)
        => PreviewSpecialist(selection, operation, directScopedWorkflow: true);

    MarketplaceShopSpecialistPreview PreviewSpecialist(MarketplaceShopSelectionSnapshot selection, MarketplaceShopBulkOperation operation, bool directScopedWorkflow)
    {
        ValidateSelection(selection);
        if (operation == MarketplaceShopBulkOperation.Management ||
            (!BulkOperations.Contains(operation) && !(directScopedWorkflow && operation == MarketplaceShopBulkOperation.CreatePreview) &&
             !(directScopedWorkflow && operation == MarketplaceShopBulkOperation.PriceAndStockPreview &&
               BulkOperations.Contains(MarketplaceShopBulkOperation.PricePreview) && BulkOperations.Contains(MarketplaceShopBulkOperation.StockPreview))))
            throw new InvalidOperationException("Bu uzman önizleme işlemi seçili kanal tarafından desteklenmiyor.");
        var currentBindings = bindings.List(connectionId: connection.Id).ToDictionary(x => x.ProductId, x => x.Version, StringComparer.Ordinal);
        var rows = selection.ProductIds.Select(id =>
        {
            if (!selection.BindingVersions.TryGetValue(id, out var capturedVersion) || currentBindings.GetValueOrDefault(id) != capturedVersion)
                throw new InvalidOperationException("Ürün-mağaza bağlantısı değişti; seçimi yenileyin.");
            var binding = bindings.Get(id, connection.Id);
            if ((binding?.Version ?? 0) != capturedVersion) throw new InvalidOperationException("Ürün-mağaza bağlantısı değişti; seçimi yenileyin.");
            if (binding is null)
            {
                if (operation != MarketplaceShopBulkOperation.CreatePreview)
                    throw new InvalidOperationException("Uzak mağaza güncellemesi için etkin ürün bağlantısı gerekli.");
                return new MarketplaceShopSpecialistPreviewRow(id, 0, "", "", "", false, false, false, "", "", "");
            }
            if (operation == MarketplaceShopBulkOperation.CreatePreview)
                throw new InvalidOperationException("Yeni ilan önizlemesi yalnız mağazaya bağlı olmayan ürünler için oluşturulabilir.");
            if (operation != MarketplaceShopBulkOperation.CreatePreview &&
                (!IsActiveBinding(binding) || !AllowsSpecialistOperation(binding.ManageContent, binding.ManagePrice, binding.ManageStock, operation)))
                throw new InvalidOperationException("Ürün bağlantısı bu uzak mağaza işlemi için yönetime açık değil.");
            return new MarketplaceShopSpecialistPreviewRow(id, binding.Version, binding.RemoteId, binding.RemoteSku, binding.RemoteBarcode,
                binding.ManageContent, binding.ManagePrice, binding.ManageStock, binding.CategoryId, binding.TemplateId, binding.State);
        }).ToArray();
        var preview = new MarketplaceShopSpecialistPreview(Guid.NewGuid().ToString("N"), connection.Id, connection.Channel,
            connection.ShopId, connection.Revision, operation, DateTime.UtcNow, rows);
        using var database = Open(); using var command = database.CreateCommand();
        command.CommandText = "INSERT INTO MarketplaceShopSpecialistPreviews(Id,ConnectionId,Json) VALUES($id,$connection,$json)";
        command.Parameters.AddWithValue("$id", preview.Id); command.Parameters.AddWithValue("$connection", preview.ConnectionId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(preview)); command.ExecuteNonQuery();
        return preview;
    }

    public void ValidateSpecialistPreview(MarketplaceShopSpecialistPreview supplied)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        using var database = Open(); using var transaction = database.BeginTransaction(deferred: false);
        ValidateSpecialistPreview(database, transaction, supplied);
        transaction.Commit();
    }

    public void AssociateSpecialistPlan(MarketplaceShopSpecialistPreview supplied, string planId)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        if (string.IsNullOrWhiteSpace(planId) || planId.Length > 512 || planId.Any(char.IsControl)) throw new ArgumentException("Kanal plan kimliği geçersiz.", nameof(planId));
        using var database = Open(); using var transaction = database.BeginTransaction(deferred: false);
        ValidateSpecialistPreview(database, transaction, supplied);
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO MarketplaceShopSpecialistPlanLinks(PlanId,PreviewId,ConnectionId,Cleared) VALUES($plan,$preview,$connection,0)";
        command.Parameters.AddWithValue("$plan", planId); command.Parameters.AddWithValue("$preview", supplied.Id); command.Parameters.AddWithValue("$connection", connection.Id);
        command.ExecuteNonQuery(); transaction.Commit();
    }

    public MarketplaceShopSpecialistPreview ValidateSpecialistPlan(string planId)
    {
        if (string.IsNullOrWhiteSpace(planId)) throw new ArgumentException("Kanal plan kimliği gerekli.", nameof(planId));
        using var database = Open(); using var transaction = database.BeginTransaction(deferred: false);
        using var read = database.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT p.Json FROM MarketplaceShopSpecialistPlanLinks l JOIN MarketplaceShopSpecialistPreviews p ON p.Id=l.PreviewId AND p.ConnectionId=l.ConnectionId WHERE l.PlanId=$plan AND l.ConnectionId=$connection AND l.Cleared=0";
        read.Parameters.AddWithValue("$plan", planId); read.Parameters.AddWithValue("$connection", connection.Id);
        var supplied = JsonSerializer.Deserialize<MarketplaceShopSpecialistPreview>(read.ExecuteScalar() as string
            ?? throw new InvalidOperationException("Kanal planı hesap kapsamlı uzman önizlemesine bağlı değil."))
            ?? throw new InvalidDataException("Uzman önizleme bağlantısı okunamadı.");
        ValidateSpecialistPreview(database, transaction, supplied);
        transaction.Commit(); return supplied;
    }

    public static bool ValidateAssociatedSpecialistPlan(string planId, string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15 }.ToString();
        using var database = new SqliteConnection(connectionString); database.Open();
        var scopedConnectionId = ScopedPlanConnection(database, planId);
        using (var table = database.CreateCommand())
        {
            table.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='MarketplaceShopSpecialistPlanLinks'";
            if (table.ExecuteScalar() is null)
            {
                if (scopedConnectionId is not null) throw new InvalidOperationException("Hesap kapsamlı kanal planı uzman önizlemesine bağlanmadı; gönderim engellendi.");
                return false;
            }
        }
        string connectionId;bool cleared;
        using (var read = database.CreateCommand())
        {
            read.CommandText = "SELECT ConnectionId,Cleared FROM MarketplaceShopSpecialistPlanLinks WHERE PlanId=$plan";
            read.Parameters.AddWithValue("$plan", planId);
            using var reader = read.ExecuteReader(); if (!reader.Read())
            {
                if (scopedConnectionId is not null) throw new InvalidOperationException("Hesap kapsamlı kanal planı uzman önizlemesine bağlanmadı; gönderim engellendi.");
                return false;
            }
            connectionId=reader.GetString(0);cleared=reader.GetInt32(1)!=0;
        }
        if (scopedConnectionId is not null && !string.Equals(scopedConnectionId, connectionId, StringComparison.Ordinal))
            throw new InvalidOperationException("Hesap kapsamlı kanal planının mağaza bağlantısı tutarsız; gönderim engellendi.");
        if(cleared)throw new InvalidOperationException("Hesap kapsamlı kanal planı artık etkin değil; yeni önizleme alın.");
        new MarketplaceShopProductsModel(connectionId, directory).ValidateSpecialistPlan(planId);
        return true;
    }

    internal static void MarkScopedPlan(SqliteConnection database, SqliteTransaction transaction, string planId, string connectionId)
    {
        using (var table = database.CreateCommand())
        {
            table.Transaction = transaction;
            table.CommandText = "CREATE TABLE IF NOT EXISTS MarketplaceShopScopedPlans(PlanId TEXT PRIMARY KEY,ConnectionId TEXT NOT NULL)";
            table.ExecuteNonQuery();
        }
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO MarketplaceShopScopedPlans(PlanId,ConnectionId) VALUES($plan,$connection)";
        command.Parameters.AddWithValue("$plan", planId); command.Parameters.AddWithValue("$connection", connectionId); command.ExecuteNonQuery();
    }

    static string? ScopedPlanConnection(SqliteConnection database, string planId)
    {
        using (var table = database.CreateCommand())
        {
            table.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='MarketplaceShopScopedPlans'";
            if (table.ExecuteScalar() is null) return null;
        }
        using var command = database.CreateCommand(); command.CommandText = "SELECT ConnectionId FROM MarketplaceShopScopedPlans WHERE PlanId=$plan";
        command.Parameters.AddWithValue("$plan", planId); return command.ExecuteScalar() as string;
    }

    public static void ClearAssociatedSpecialistPlan(string planId, string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15 }.ToString();
        using var database = new SqliteConnection(connectionString); database.Open();
        using (var table = database.CreateCommand())
        {
            table.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='MarketplaceShopSpecialistPlanLinks'";
            if (table.ExecuteScalar() is null) return;
        }
        using var command = database.CreateCommand(); command.CommandText = "UPDATE MarketplaceShopSpecialistPlanLinks SET Cleared=1 WHERE PlanId=$plan AND Cleared=0";
        command.Parameters.AddWithValue("$plan", planId); command.ExecuteNonQuery();
    }

    public void ClearSpecialistPlanAssociation(string planId)
    {
        if (string.IsNullOrWhiteSpace(planId)) return;
        using var database = Open(); using var command = database.CreateCommand();
        command.CommandText = "UPDATE MarketplaceShopSpecialistPlanLinks SET Cleared=1 WHERE PlanId=$plan AND ConnectionId=$connection AND Cleared=0";
        command.Parameters.AddWithValue("$plan", planId); command.Parameters.AddWithValue("$connection", connection.Id); command.ExecuteNonQuery();
    }

    void ValidateSpecialistPreview(SqliteConnection database, SqliteTransaction transaction, MarketplaceShopSpecialistPreview supplied)
    {
        string persistedJson;
        using (var read = database.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Json FROM MarketplaceShopSpecialistPreviews WHERE Id=$id AND ConnectionId=$connection";
            read.Parameters.AddWithValue("$id", supplied.Id); read.Parameters.AddWithValue("$connection", connection.Id);
            persistedJson = read.ExecuteScalar() as string ?? throw new InvalidOperationException("Uzman önizleme isteği bulunamadı.");
        }
        if (!string.Equals(persistedJson, JsonSerializer.Serialize(supplied), StringComparison.Ordinal))
            throw new InvalidOperationException("Uzman önizleme isteği değiştirilmiş.");
        EnsureConnectionCurrent(database, transaction, new(supplied.Id, supplied.ConnectionId, supplied.Channel, supplied.ShopId,
            supplied.ConnectionRevision, supplied.Operation, supplied.CreatedUtc, []));
        foreach (var row in supplied.Rows)
        {
            using var binding = database.CreateCommand(); binding.Transaction = transaction;
            binding.CommandText = "SELECT RemoteId,RemoteSku,RemoteBarcode,ManageContent,ManagePrice,ManageStock,CategoryId,TemplateId,State,Version FROM ProductChannelBindings WHERE ProductId=$product AND ConnectionId=$connection";
            binding.Parameters.AddWithValue("$product", row.ProductId); binding.Parameters.AddWithValue("$connection", supplied.ConnectionId);
            using var reader = binding.ExecuteReader();
            if (!reader.Read())
            {
                if (supplied.Operation != MarketplaceShopBulkOperation.CreatePreview || row.BindingVersion != 0 || row.RemoteId.Length != 0)
                    throw new InvalidOperationException("Ürün-mağaza bağlantısı değişti; yeni önizleme alın.");
                continue;
            }
            if (supplied.Operation == MarketplaceShopBulkOperation.CreatePreview)
                throw new InvalidOperationException("Ürün artık mağazaya bağlı; yeni ilan gönderimi engellendi.");
            if (reader.GetInt64(9) != row.BindingVersion || reader.GetString(0) != row.RemoteId || reader.GetString(1) != row.RemoteSku ||
                reader.GetString(2) != row.RemoteBarcode || (reader.GetInt32(3) != 0) != row.ManageContent ||
                (reader.GetInt32(4) != 0) != row.ManagePrice || (reader.GetInt32(5) != 0) != row.ManageStock ||
                reader.GetString(6) != row.CategoryId || reader.GetString(7) != row.TemplateId || reader.GetString(8) != row.State)
                throw new InvalidOperationException("Ürün-mağaza bağlantısı veya yönetim durumu değişti; yeni önizleme alın.");
            if (supplied.Operation != MarketplaceShopBulkOperation.CreatePreview &&
                (!IsActiveBinding(row.RemoteId, row.State) || !AllowsSpecialistOperation(row.ManageContent, row.ManagePrice, row.ManageStock, supplied.Operation)))
                throw new InvalidOperationException("Ürün bağlantısı bu uzak mağaza işlemi için yönetime açık değil; yeni önizleme alın.");
        }
    }

    public MarketplaceShopBulkReceipt Apply(MarketplaceShopBulkPreview supplied, bool explicitlyApproved)
    {
        if (!explicitlyApproved) throw new InvalidOperationException("Toplu işlem için açık önizleme onayı gerekli.");
        ArgumentNullException.ThrowIfNull(supplied);
        using var database = Open();
        using var transaction = database.BeginTransaction(deferred: false);
        MarketplaceShopBulkPreview persisted;
        string persistedJson;
        using (var read = database.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Json FROM MarketplaceShopBulkPreviews WHERE Id=$id AND ConnectionId=$connection";
            read.Parameters.AddWithValue("$id", supplied.Id); read.Parameters.AddWithValue("$connection", connection.Id);
            persistedJson = read.ExecuteScalar() as string ?? throw new InvalidOperationException("Toplu işlem önizlemesi bulunamadı.");
            persisted = JsonSerializer.Deserialize<MarketplaceShopBulkPreview>(persistedJson) ?? throw new InvalidDataException("Toplu işlem önizlemesi bozuk.");
        }
        if (!string.Equals(persistedJson, JsonSerializer.Serialize(supplied), StringComparison.Ordinal))
            throw new InvalidOperationException("Toplu işlem önizlemesi değiştirilmiş.");
        EnsureConnectionCurrent(database, transaction, persisted);
        if (!IsLocallyApplicable(persisted))
            throw new InvalidOperationException("Bu işlem yalnız kanalın uzman önizlemesinde uygulanabilir; yerel başarı makbuzu üretilmedi.");
        foreach (var row in persisted.Rows) ApplyRow(database, transaction, persisted, row);
        var receipt = new MarketplaceShopBulkReceipt(persisted.Id, persisted.ConnectionId, DateTime.UtcNow,
            persisted.Rows.Select(x => x.ProductId).ToArray(), 0);
        using (var write = database.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = "INSERT INTO MarketplaceShopBulkReceipts(PreviewId,ConnectionId,Json) VALUES($id,$connection,$json)";
            write.Parameters.AddWithValue("$id", receipt.PreviewId); write.Parameters.AddWithValue("$connection", receipt.ConnectionId);
            write.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt));
            try { write.ExecuteNonQuery(); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("Bu önizleme daha önce uygulandı."); }
        }
        transaction.Commit();
        return receipt;
    }

    void ApplyRow(SqliteConnection database, SqliteTransaction transaction, MarketplaceShopBulkPreview preview, MarketplaceShopBulkPreviewRow row)
    {
        using (var product = database.CreateCommand())
        {
            product.Transaction = transaction;
            product.CommandText = "SELECT Json FROM CatalogProducts WHERE Id=$id"; product.Parameters.AddWithValue("$id", row.ProductId);
            var json = product.ExecuteScalar() as string ?? throw new InvalidOperationException("Ürün silinmiş; yeni önizleme alın.");
            var current = JsonSerializer.Deserialize<CatalogProduct>(json) ?? throw new InvalidDataException("Ürün kaydı bozuk.");
            if (current.UpdatedUtc != row.CatalogUpdatedUtc) throw new InvalidOperationException("Ürün değişti; yeni önizleme alın.");
        }
        if (row.BindingVersion == 0)
        {
            // Non-management commands without a binding remain specialized preview handoffs.
            return;
        }
        using var update = database.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE ProductChannelBindings SET
              ManageContent=COALESCE($content,ManageContent),
              ManagePrice=COALESCE($price,ManagePrice),
              ManageStock=COALESCE($stock,ManageStock),
              CategoryId=COALESCE($category,CategoryId),
              TemplateId=COALESCE($template,TemplateId),
              Version=Version+1,UpdatedUtc=$updated
            WHERE ProductId=$product AND ConnectionId=$connection AND Version=$version
              AND EXISTS(SELECT 1 FROM MarketplaceConnections WHERE Id=$connection AND Enabled=1 AND Revision=$connectionRevision)
            """;
        update.Parameters.AddWithValue("$content", row.ManageContent.HasValue ? (row.ManageContent.Value ? 1 : 0) : DBNull.Value);
        update.Parameters.AddWithValue("$price", row.ManagePrice.HasValue ? (row.ManagePrice.Value ? 1 : 0) : DBNull.Value);
        update.Parameters.AddWithValue("$stock", row.ManageStock.HasValue ? (row.ManageStock.Value ? 1 : 0) : DBNull.Value);
        update.Parameters.AddWithValue("$category", (object?)row.CategoryId ?? DBNull.Value);
        update.Parameters.AddWithValue("$template", (object?)row.TemplateId ?? DBNull.Value);
        update.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$product", row.ProductId); update.Parameters.AddWithValue("$connection", preview.ConnectionId);
        update.Parameters.AddWithValue("$version", row.BindingVersion); update.Parameters.AddWithValue("$connectionRevision", preview.ConnectionRevision);
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Mağaza hesabı veya ürün bağlantısı değişti; yeni önizleme alın.");
    }

    void ValidateSelection(MarketplaceShopSelectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var current = RequireOperational(connection.Id);
        if (snapshot.ConnectionId != current.Id || snapshot.ConnectionRevision != current.Revision)
            throw new InvalidOperationException("Mağaza hesabı değişti; seçimi yenileyin.");
        if (snapshot.ProductIds.Count == 0 || snapshot.ProductIds.Distinct(StringComparer.Ordinal).Count() != snapshot.ProductIds.Count)
            throw new InvalidOperationException("Ürün seçimi geçersiz.");
        connection = current;
    }

    MarketplaceConnection RequireOperational(string id)
    {
        MarketplaceConnection? current;
        try { current = connections.Get(id); }
        catch (MarketplaceConnectionCorruptException) { throw new InvalidOperationException("Mağaza bağlantısı bozuk; ürün yönetimi açılamaz."); }
        if (current is null || !MarketplaceOperationalAccounts.IsEligible(current, connections))
            throw new InvalidOperationException("Etkin ve operasyonel mağaza bağlantısı gerekli.");
        return current;
    }

    void EnsureConnectionCurrent(SqliteConnection database, SqliteTransaction transaction, MarketplaceShopBulkPreview preview)
    {
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT Channel,ShopId,Enabled,Status,LastTestUtc,Revision FROM MarketplaceConnections WHERE Id=$id";
        command.Parameters.AddWithValue("$id", preview.ConnectionId);
        string channel, shopId, status;
        DateTime? lastTestUtc = null;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt32(2) != 1 || reader.GetInt64(5) != preview.ConnectionRevision ||
                reader.GetString(0) != preview.Channel || reader.GetString(1) != preview.ShopId)
                throw new InvalidOperationException("Mağaza hesabı değişti veya devre dışı; yeni önizleme alın.");
            channel = reader.GetString(0); shopId = reader.GetString(1); status = reader.GetString(3);
            if (!reader.IsDBNull(4))
            {
                if (!DateTime.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    throw new InvalidOperationException("Mağaza bağlantısı bozuk; yeni önizleme alın.");
                lastTestUtc = parsed;
            }
        }
        var seededDefault = preview.ConnectionId.Equals(channel + ":default", StringComparison.OrdinalIgnoreCase)
            && shopId.Equals("default", StringComparison.OrdinalIgnoreCase);
        if (status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) || status.Equals("LIVE_API_BLOCKED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mağaza bağlantı testi başarısız; yeni önizleme alın.");
        if (!seededDefault) return;
        var verifiedStatus = lastTestUtc.HasValue && (status.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)
            || status.Equals("CONNECTED_READ_ONLY", StringComparison.OrdinalIgnoreCase));
        using var migration = database.CreateCommand(); migration.Transaction = transaction;
        migration.CommandText = "SELECT 1 FROM MarketplaceCredentialMigrations WHERE Channel=$channel AND ConnectionId=$connection AND ShopId=$shop";
        migration.Parameters.AddWithValue("$channel", channel); migration.Parameters.AddWithValue("$connection", preview.ConnectionId); migration.Parameters.AddWithValue("$shop", shopId);
        if (!verifiedStatus && migration.ExecuteScalar() is null)
            throw new InvalidOperationException("Mağaza hesabı değişti veya devre dışı; yeni önizleme alın.");
    }

    MarketplaceShopProductRow Row(CatalogProduct product, ProductChannelBinding? binding, IReadOnlyDictionary<string, RemoteValues> remote)
    {
        var key = binding?.RemoteId ?? "";
        var remoteValue = key.Length > 0 ? remote.GetValueOrDefault(key) : null;
        var error = binding is not null && IsError(binding.State, "") ? binding.State : "";
        return new(product.Id, product.Sku, product.Gtin, product.Barcode, product.Name, product.Stock, product.Price, product.Currency,
            remoteValue?.Stock, remoteValue?.Price, remoteValue?.Currency ?? "", product.Category, product.Brand,
            binding?.RemoteId ?? "", binding?.State ?? "NewListingCandidate", error,
            binding?.ManageContent ?? false, binding?.ManagePrice ?? false, binding?.ManageStock ?? false);
    }

    IReadOnlyDictionary<string, RemoteValues> RemoteCache()
    {
        if (connection.Channel == "trendyol")
        {
            var state = new TrendyolWorkspaceStore(directory).Load(connection.ShopId);
            return state.Products.ToDictionary(x => x.ContentId.ToString(CultureInfo.InvariantCulture),
                x => new RemoteValues(x.Quantity, x.SalePrice, "TRY"), StringComparer.Ordinal);
        }
        if (connection.Channel == "etsy")
        {
            var state = new EtsyWorkspaceStore(directory).Load(connection.ShopId);
            return state.Listings.ToDictionary(x => x.ListingId.ToString(CultureInfo.InvariantCulture),
                x => new RemoteValues(x.Quantity, x.Price, x.Currency), StringComparer.Ordinal);
        }
        return new Dictionary<string, RemoteValues>();
    }

    static IReadOnlyList<MarketplaceShopBulkOperation> OperationsFor(MarketplaceCapabilities capabilities)
    {
        var result = new List<MarketplaceShopBulkOperation>();
        Add(MarketplaceOperation.ProductManagement, MarketplaceShopBulkOperation.Management);
        Add(MarketplaceOperation.CategoryWrite, MarketplaceShopBulkOperation.Category);
        Add(MarketplaceOperation.BrandWrite, MarketplaceShopBulkOperation.Brand);
        Add(MarketplaceOperation.DeliveryWrite, MarketplaceShopBulkOperation.Delivery);
        Add(MarketplaceOperation.TaxonomyWrite, MarketplaceShopBulkOperation.Taxonomy);
        Add(MarketplaceOperation.PropertiesWrite, MarketplaceShopBulkOperation.Properties);
        Add(MarketplaceOperation.ShippingWrite, MarketplaceShopBulkOperation.Shipping);
        Add(MarketplaceOperation.ReadinessWrite, MarketplaceShopBulkOperation.Readiness);
        Add(MarketplaceOperation.ListingCreate, MarketplaceShopBulkOperation.CreatePreview);
        if (capabilities.Supports(MarketplaceOperation.PriceWrite)) result.Add(MarketplaceShopBulkOperation.PricePreview);
        if (capabilities.Supports(MarketplaceOperation.StockWrite)) result.Add(MarketplaceShopBulkOperation.StockPreview);
        Add(MarketplaceOperation.ContentWrite, MarketplaceShopBulkOperation.ContentPreview);
        return result.AsReadOnly();
        void Add(MarketplaceOperation capability, MarketplaceShopBulkOperation operation)
        { if (capabilities.Supports(capability)) result.Add(operation); }
    }

    static bool IsLocallyApplicable(MarketplaceShopBulkPreview preview) => preview.Operation switch
    {
        MarketplaceShopBulkOperation.Management => preview.Rows.Any(row => row.ManageContent.HasValue || row.ManagePrice.HasValue || row.ManageStock.HasValue || row.CategoryId is not null || row.TemplateId is not null),
        MarketplaceShopBulkOperation.Category or MarketplaceShopBulkOperation.Taxonomy => preview.Rows.All(row => row.CategoryId is not null),
        MarketplaceShopBulkOperation.Delivery or MarketplaceShopBulkOperation.Shipping => preview.Rows.All(row => row.TemplateId is not null),
        _ => false
    };

    internal static bool IsActiveBinding(ProductChannelBinding binding) => IsActiveBinding(binding.RemoteId, binding.State);
    internal static bool IsActiveBinding(string remoteId, string state) =>
        !string.IsNullOrWhiteSpace(remoteId) && !IsError(state, "");
    internal static bool AllowsSpecialistOperation(bool manageContent, bool managePrice, bool manageStock, MarketplaceShopBulkOperation operation) => operation switch
    {
        MarketplaceShopBulkOperation.CreatePreview => true,
        MarketplaceShopBulkOperation.StockPreview => manageStock,
        MarketplaceShopBulkOperation.PricePreview => managePrice,
        MarketplaceShopBulkOperation.PriceAndStockPreview => managePrice && manageStock,
        MarketplaceShopBulkOperation.ContentPreview or MarketplaceShopBulkOperation.Category or MarketplaceShopBulkOperation.Brand or
        MarketplaceShopBulkOperation.Delivery or MarketplaceShopBulkOperation.Taxonomy or MarketplaceShopBulkOperation.Properties or
        MarketplaceShopBulkOperation.Shipping or MarketplaceShopBulkOperation.Readiness => manageContent,
        _ => false
    };

    void Initialize()
    {
        using var database = Open(); using var command = database.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS MarketplaceShopBulkPreviews(Id TEXT PRIMARY KEY,ConnectionId TEXT NOT NULL,Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS MarketplaceShopBulkReceipts(PreviewId TEXT PRIMARY KEY,ConnectionId TEXT NOT NULL,Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS MarketplaceShopSpecialistPreviews(Id TEXT PRIMARY KEY,ConnectionId TEXT NOT NULL,Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS MarketplaceShopSpecialistPlanLinks(PlanId TEXT PRIMARY KEY,PreviewId TEXT NOT NULL,ConnectionId TEXT NOT NULL,Cleared INTEGER NOT NULL DEFAULT 0 CHECK(Cleared IN (0,1)));
            CREATE TABLE IF NOT EXISTS MarketplaceShopScopedPlans(PlanId TEXT PRIMARY KEY,ConnectionId TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS MarketplaceShopBulkPreviews_NoUpdate BEFORE UPDATE ON MarketplaceShopBulkPreviews BEGIN SELECT RAISE(ABORT,'immutable shop bulk preview'); END;
            CREATE TRIGGER IF NOT EXISTS MarketplaceShopBulkPreviews_NoDelete BEFORE DELETE ON MarketplaceShopBulkPreviews BEGIN SELECT RAISE(ABORT,'immutable shop bulk preview'); END;
            CREATE TRIGGER IF NOT EXISTS MarketplaceShopSpecialistPreviews_NoUpdate BEFORE UPDATE ON MarketplaceShopSpecialistPreviews BEGIN SELECT RAISE(ABORT,'immutable shop specialist preview'); END;
            CREATE TRIGGER IF NOT EXISTS MarketplaceShopSpecialistPreviews_NoDelete BEFORE DELETE ON MarketplaceShopSpecialistPreviews BEGIN SELECT RAISE(ABORT,'immutable shop specialist preview'); END;
            """;
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var database = new SqliteConnection(connectionString); database.Open(); return database; }
    static bool IsError(string state, string error) => new[] { state, error }.Any(value => value.Contains("error", StringComparison.OrdinalIgnoreCase) || value.Contains("conflict", StringComparison.OrdinalIgnoreCase) || value.Contains("failed", StringComparison.OrdinalIgnoreCase) || value.Contains("rejected", StringComparison.OrdinalIgnoreCase));
    static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    sealed record RemoteValues(int? Stock, decimal? Price, string Currency);
}

public sealed class MarketplaceShopProductsPanel : UserControl
{
    readonly MarketplaceShopProductsModel model;
    readonly DataGrid products = new() { Name = "MarketplaceShopProducts", AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Extended, IsReadOnly = true };
    readonly TextBox search = new() { MinWidth = 240, Margin = new Thickness(3) };
    readonly ComboBox binding = new() { MinWidth = 130, Margin = new Thickness(3), ItemsSource = Enum.GetValues<MarketplaceShopBindingFilter>() };
    readonly ComboBox management = new() { MinWidth = 130, Margin = new Thickness(3), ItemsSource = Enum.GetValues<MarketplaceShopManagementFilter>() };
    readonly TextBlock summary = new() { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap };
    MarketplaceShopProductFilter filter = new();

    public MarketplaceShopSelectionSnapshot? SelectionSnapshot { get; private set; }
    public event Action<MarketplaceShopSpecialistPreview>? BulkPreviewRequested;

    public MarketplaceShopProductsPanel(string connectionId, string? directory = null, MarketplaceAdapterRegistry? registry = null)
    {
        model = new(connectionId, directory, registry);
        binding.SelectedItem = MarketplaceShopBindingFilter.All; management.SelectedItem = MarketplaceShopManagementFilter.All;
        var root = new DockPanel { Margin = new Thickness(6) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = model.AccountLabel, FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(3) });
        var filters = new WrapPanel(); filters.Children.Add(search); filters.Children.Add(binding); filters.Children.Add(management);
        filters.Children.Add(Button("Filtrele", "MarketplaceApplyShopFilters", Refresh));
        top.Children.Add(filters);
        var actions = new WrapPanel();
        foreach (var operation in model.BulkOperations)
        {
            var captured = operation;
            actions.Children.Add(Button(Label(operation), "MarketplaceBulk_" + operation, () =>
            {
                SelectionSnapshot ??= model.SelectPage(products.SelectedItems.Cast<MarketplaceShopProductRow>().Select(x => x.ProductId));
                if(captured==MarketplaceShopBulkOperation.Management)OpenManagementPreview(SelectionSnapshot);
                else if(BulkPreviewRequested is not null)BulkPreviewRequested.Invoke(model.PreviewSpecialist(SelectionSnapshot, captured));
                else throw new InvalidOperationException("Bu işlem için kanal önizleme yönlendiricisi bulunamadı.");
            }));
        }
        top.Children.Add(actions); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        AddColumns(); root.Children.Add(products);
        var footer = new WrapPanel();
        footer.Children.Add(Button("Sayfayı seç", "MarketplaceSelectPage", () => { products.SelectAll(); SelectionSnapshot = model.SelectPage(products.Items.Cast<MarketplaceShopProductRow>().Select(x => x.ProductId)); }));
        footer.Children.Add(Button("Filtrelenenlerin tümünü seç", "MarketplaceSelectAllFiltered", () => { SelectionSnapshot = model.SelectAllFiltered(filter); summary.Text = $"{SelectionSnapshot.ProductIds.Count} ürün snapshot olarak seçildi."; }));
        footer.Children.Add(Button("Seçimi kaldır", "MarketplaceClearSelection", () => { products.UnselectAll(); SelectionSnapshot = null; }));
        footer.Children.Add(summary); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Content = root; Refresh();
    }

    void Refresh()
    {
        filter = new(search.Text, (MarketplaceShopBindingFilter)(binding.SelectedItem ?? MarketplaceShopBindingFilter.All), (MarketplaceShopManagementFilter)(management.SelectedItem ?? MarketplaceShopManagementFilter.All));
        var rows = model.Filter(filter); products.ItemsSource = rows; summary.Text = $"{rows.Count} ürün · Hesap: {model.ConnectionId}";
    }

    void AddColumns()
    {
        Column("SKU", "Sku", 105); Column("GTIN", "Gtin", 120); Column("Barkod", "Barcode", 120); Column("Ürün", "Name", 230);
        Column("Yerel stok", "LocalStock", 80); Column("Yerel fiyat", "LocalPrice", 90); Column("Uzak stok", "RemoteStock", 80); Column("Uzak fiyat", "RemotePrice", 90);
        Column("Kategori", "Category", 170); Column("Marka", "Brand", 120); Column("Uzak durum", "RemoteState", 110); Column("Hata", "Error", 180); Column("Yönetim", "ManagementState", 210);
        products.FrozenColumnCount = 4;
    }

    void OpenManagementPreview(MarketplaceShopSelectionSnapshot selection)
    {
        var content=new CheckBox{Content="İçerik yönetimi",IsThreeState=true,IsChecked=null,Margin=new Thickness(4)};
        var price=new CheckBox{Content="Fiyat yönetimi",IsThreeState=true,IsChecked=null,Margin=new Thickness(4)};
        var stock=new CheckBox{Content="Stok yönetimi",IsThreeState=true,IsChecked=null,Margin=new Thickness(4)};
        var category=new TextBox{MinWidth=240,Margin=new Thickness(4)};
        var template=new TextBox{MinWidth=240,Margin=new Thickness(4)};
        var panel=new StackPanel{Margin=new Thickness(12)};
        panel.Children.Add(new TextBlock{Text=$"{selection.ProductIds.Count} ürün için yalnız işaretlenen alanlar değişir. Boş alanlar mevcut değeri korur.",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(content);panel.Children.Add(price);panel.Children.Add(stock);
        panel.Children.Add(new TextBlock{Text="Kategori / taksonomi kimliği (isteğe bağlı)"});panel.Children.Add(category);
        panel.Children.Add(new TextBlock{Text="Şablon / kargo kimliği (isteğe bağlı)"});panel.Children.Add(template);
        var preview=new Button{Content="Değişmez önizlemeyi oluştur",Margin=new Thickness(4),Padding=new Thickness(8,5,8,5)};panel.Children.Add(preview);
        var dialog=new Window{Title="Hesap kapsamlı yönetim önizlemesi",Width=520,Height=390,MinWidth=420,MinHeight=320,Owner=Window.GetWindow(this),WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=panel};
        preview.Click+=(_,_)=>
        {
            try
            {
                if(content.IsChecked is null&&price.IsChecked is null&&stock.IsChecked is null&&string.IsNullOrWhiteSpace(category.Text)&&string.IsNullOrWhiteSpace(template.Text))
                    throw new InvalidOperationException("Önizlenecek en az bir değişiklik seçin.");
                var immutable=model.PreviewBulk(selection,MarketplaceShopBulkOperation.Management,new(content.IsChecked,price.IsChecked,stock.IsChecked,category.Text,template.Text));
                var approved=MessageBox.Show(dialog,$"Hesap: {model.AccountLabel}\n{immutable.Rows.Count} ürün\nBu yerel yönetim önizlemesi uygulansın mı?","Toplu yönetim onayı",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
                if(!approved)return;
                var receipt=model.Apply(immutable,true);dialog.Close();SelectionSnapshot=null;Refresh();summary.Text=$"{receipt.ProductIds.Count} ürünün hesap kapsamlı yönetim ayarı güncellendi. Uzak API yazımı yapılmadı.";
            }
            catch(Exception ex){MessageBox.Show(dialog,MarketplaceConnectionStore.Redact(ex.Message),"Toplu yönetim",MessageBoxButton.OK,MessageBoxImage.Warning);}
        };
        dialog.ShowDialog();
    }

    void Column(string header, string path, double width) => products.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });
    static Button Button(string label, string name, Action action) { var button = new Button { Content = label, Name = name, Margin = new Thickness(3), Padding = new Thickness(7, 3, 7, 3) }; button.Click += (_, _) => action(); return button; }
    static string Label(MarketplaceShopBulkOperation operation) => operation switch
    {
        MarketplaceShopBulkOperation.Management => "Yönetim anahtarları · önizle",
        MarketplaceShopBulkOperation.Category => "Kategori · önizle",
        MarketplaceShopBulkOperation.Brand => "Marka · önizle",
        MarketplaceShopBulkOperation.Delivery => "Teslimat · önizle",
        MarketplaceShopBulkOperation.Taxonomy => "Taksonomi · önizle",
        MarketplaceShopBulkOperation.Properties => "Özellikler · önizle",
        MarketplaceShopBulkOperation.Shipping => "Kargo · önizle",
        MarketplaceShopBulkOperation.Readiness => "Hazırlık · önizle",
        MarketplaceShopBulkOperation.CreatePreview => "Yeni ilan · önizle",
        MarketplaceShopBulkOperation.PricePreview => "Fiyat · önizle",
        MarketplaceShopBulkOperation.StockPreview => "Stok · önizle",
        _ => "İçerik · önizle"
    };
}
