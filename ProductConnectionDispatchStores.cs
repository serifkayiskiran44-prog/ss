using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Etsy;

namespace TrMarketplaceHubDesktop;

public sealed record ProductChannelCreationPreviewRequest(
    string Id, string ConnectionId, string Channel, string ShopId,
    IReadOnlyList<string> ProductIds, DateTime CreatedUtc);

/// <summary>Account-scoped inbox consumed by the existing channel workspaces.</summary>
public sealed class ProductChannelCreationPreviewInbox
{
    readonly string connectionString;
    readonly MarketplaceConnectionStore connections;

    public ProductChannelCreationPreviewInbox(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        _ = new CatalogStore(directory);
        connections = new(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15 }.ToString();
        using var database = Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ProductChannelCreationPreviewRequests(
                Id TEXT PRIMARY KEY,
                ConnectionId TEXT NOT NULL,
                Channel TEXT NOT NULL,
                ShopId TEXT NOT NULL,
                ProductIdsJson TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ProductChannelCreationPreviewAcknowledgements(
                RequestId TEXT PRIMARY KEY,
                ConnectionId TEXT NOT NULL,
                RecognizedUtc TEXT NOT NULL
            );
            CREATE TRIGGER IF NOT EXISTS ProductChannelCreationPreviewRequests_NoUpdate
            BEFORE UPDATE ON ProductChannelCreationPreviewRequests BEGIN SELECT RAISE(ABORT,'immutable product creation preview request'); END;
            CREATE TRIGGER IF NOT EXISTS ProductChannelCreationPreviewRequests_NoDelete
            BEFORE DELETE ON ProductChannelCreationPreviewRequests BEGIN SELECT RAISE(ABORT,'immutable product creation preview request'); END;
            """;
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var database = new SqliteConnection(connectionString); database.Open(); return database; }

    public string Enqueue(MarketplaceConnection connection, IReadOnlyList<string> productIds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var exact = ExactProductIds(productIds);
        var current = connections.Get(connection.Id) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        if (!MarketplaceOperationalAccounts.IsEligible(current, connections) || current.Channel != connection.Channel ||
            current.ShopId != connection.ShopId || current.Revision != connection.Revision)
            throw new InvalidOperationException("Mağaza bağlantısı değişti veya operasyonel değil; yeni önizleme alın.");
        var id = Guid.NewGuid().ToString("N");
        using var database = Open();
        using var command = database.CreateCommand();
        command.CommandText = "INSERT INTO ProductChannelCreationPreviewRequests(Id,ConnectionId,Channel,ShopId,ProductIdsJson,CreatedUtc) VALUES($id,$connection,$channel,$shop,$products,$created)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$connection", current.Id);
        command.Parameters.AddWithValue("$channel", current.Channel);
        command.Parameters.AddWithValue("$shop", current.ShopId);
        command.Parameters.AddWithValue("$products", JsonSerializer.Serialize(exact));
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return id;
    }

    public IReadOnlyList<ProductChannelCreationPreviewRequest> Pending(string connectionId)
    {
        var current = connections.Get(connectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        if (!MarketplaceOperationalAccounts.IsEligible(current, connections))
            throw new InvalidOperationException("Mağaza bağlantısı operasyonel değil.");
        using var database = Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            SELECT r.Id,r.ConnectionId,r.Channel,r.ShopId,r.ProductIdsJson,r.CreatedUtc
            FROM ProductChannelCreationPreviewRequests r
            LEFT JOIN ProductChannelCreationPreviewAcknowledgements a ON a.RequestId=r.Id
            WHERE r.ConnectionId=$connection AND a.RequestId IS NULL
            ORDER BY r.CreatedUtc,r.Id
            """;
        command.Parameters.AddWithValue("$connection", current.Id);
        using var reader = command.ExecuteReader();
        var result = new List<ProductChannelCreationPreviewRequest>();
        while (reader.Read())
        {
            var products = JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? throw new InvalidDataException("Yeni ilan önizleme ürünleri okunamadı.");
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Array.AsReadOnly(ExactProductIds(products)), ParseUtc(reader.GetString(5))));
        }
        return result;
    }

    public void Recognize(string requestId, string connectionId)
    {
        var current = connections.Get(connectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        if (!MarketplaceOperationalAccounts.IsEligible(current, connections)) throw new InvalidOperationException("Mağaza bağlantısı operasyonel değil.");
        using var database = Open();
        using var transaction = database.BeginTransaction(deferred: false);
        using (var read = database.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Channel,ShopId FROM ProductChannelCreationPreviewRequests WHERE Id=$id AND ConnectionId=$connection";
            read.Parameters.AddWithValue("$id", Required(requestId, nameof(requestId)));
            read.Parameters.AddWithValue("$connection", current.Id);
            using var reader = read.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != current.Channel || reader.GetString(1) != current.ShopId)
                throw new InvalidOperationException("Yeni ilan önizleme isteği başka mağaza hesabına ait.");
        }
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ProductChannelCreationPreviewAcknowledgements(RequestId,ConnectionId,RecognizedUtc) VALUES($id,$connection,$time)";
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$connection", current.Id);
        command.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        try { command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { throw new InvalidOperationException("Yeni ilan önizleme isteği daha önce çalışma alanına aktarıldı."); }
        transaction.Commit();
    }

    static string[] ExactProductIds(IReadOnlyList<string> values)
    {
        var exact = values.Select(value => value?.Trim() ?? "").ToArray();
        if (exact.Length == 0 || exact.Any(string.IsNullOrWhiteSpace) || exact.Distinct(StringComparer.Ordinal).Count() != exact.Length)
            throw new InvalidOperationException("Yeni ilan önizlemesi için benzersiz ürünler gerekli.");
        return exact;
    }
    static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Kimlik gerekli.", name) : value.Trim();
    static DateTime ParseUtc(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : throw new InvalidDataException("Yeni ilan önizleme zamanı okunamadı.");
}

public sealed record ProductRemoteDeactivationDispatch(
    ProductRemoteDeactivationPreview Preview, string Channel, string ShopId,
    bool Approved, string ChannelPlanId)
{
    public string Id => Preview.Id;
}

/// <summary>Durable bridge between Product Management and the channel's existing dispatch preview/receipt flow.</summary>
public sealed class ProductRemoteDeactivationDispatchStore
{
    readonly string connectionString;
    readonly MarketplaceConnectionStore connections;

    public ProductRemoteDeactivationDispatchStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        _ = new ProductChannelBindingStore(directory);
        connections = new(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15 }.ToString();
        using var database = Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ProductRemoteDeactivationPreviews(
                Id TEXT PRIMARY KEY,ProductId TEXT NOT NULL,ConnectionId TEXT NOT NULL,Channel TEXT NOT NULL,ShopId TEXT NOT NULL,
                RemoteId TEXT NOT NULL,BindingVersion INTEGER NOT NULL,ConnectionRevision INTEGER NOT NULL,CreatedUtc TEXT NOT NULL,Json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ProductRemoteDeactivationApprovals(
                PreviewId TEXT PRIMARY KEY,ConnectionId TEXT NOT NULL,ApprovedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ProductRemoteDeactivationPlans(
                PreviewId TEXT PRIMARY KEY,ConnectionId TEXT NOT NULL,ChannelPlanId TEXT NOT NULL UNIQUE,AttachedUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ProductRemoteDeactivationReceipts(
                PreviewId TEXT PRIMARY KEY,ChannelPlanId TEXT NOT NULL,Json TEXT NOT NULL
            );
            CREATE TRIGGER IF NOT EXISTS ProductRemoteDeactivationPreviews_NoUpdate
            BEFORE UPDATE ON ProductRemoteDeactivationPreviews BEGIN SELECT RAISE(ABORT,'immutable remote deactivation preview'); END;
            CREATE TRIGGER IF NOT EXISTS ProductRemoteDeactivationPreviews_NoDelete
            BEFORE DELETE ON ProductRemoteDeactivationPreviews BEGIN SELECT RAISE(ABORT,'immutable remote deactivation preview'); END;
            """;
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var database = new SqliteConnection(connectionString); database.Open(); return database; }

    public ProductRemoteDeactivationPreview Create(MarketplaceConnection connection, ProductChannelBinding binding)
    {
        if (connection.Channel != "etsy" || !long.TryParse(binding.RemoteId, NumberStyles.None, CultureInfo.InvariantCulture, out var listingId) || listingId <= 0)
            throw new InvalidOperationException("Bu bağlantı için güvenli uzaktan pasife alma önizlemesi desteklenmiyor.");
        var preview = new ProductRemoteDeactivationPreview(Guid.NewGuid().ToString("N"), binding.ProductId, connection.Id, binding.RemoteId, binding.Version, connection.Revision, DateTime.UtcNow);
        using var database = Open();
        using var transaction = database.BeginTransaction(deferred: false);
        EnsureCurrent(database, transaction, preview);
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ProductRemoteDeactivationPreviews VALUES($id,$product,$connection,$channel,$shop,$remote,$bindingVersion,$connectionRevision,$created,$json)";
        command.Parameters.AddWithValue("$id", preview.Id); command.Parameters.AddWithValue("$product", preview.ProductId); command.Parameters.AddWithValue("$connection", preview.ConnectionId);
        command.Parameters.AddWithValue("$channel", connection.Channel); command.Parameters.AddWithValue("$shop", connection.ShopId); command.Parameters.AddWithValue("$remote", preview.RemoteId);
        command.Parameters.AddWithValue("$bindingVersion", preview.BindingVersion); command.Parameters.AddWithValue("$connectionRevision", preview.ConnectionRevision);
        command.Parameters.AddWithValue("$created", preview.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(preview));
        command.ExecuteNonQuery(); transaction.Commit(); return preview;
    }

    public void Approve(ProductRemoteDeactivationPreview preview, bool explicitlyApproved)
    {
        if (!explicitlyApproved) throw new InvalidOperationException("Uzaktan pasife alma gönderim önizlemesi açıkça onaylanmalı.");
        using var database = Open(); using var transaction = database.BeginTransaction(deferred: false);
        var persisted = Read(database, transaction, preview.Id) ?? throw new InvalidOperationException("Uzaktan pasife alma önizlemesi bulunamadı.");
        if (persisted.Preview != preview) throw new InvalidOperationException("Uzaktan pasife alma önizlemesi değiştirilmiş.");
        EnsureCurrent(database, transaction, preview);
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO ProductRemoteDeactivationApprovals VALUES($preview,$connection,$time)";
        command.Parameters.AddWithValue("$preview", preview.Id); command.Parameters.AddWithValue("$connection", preview.ConnectionId); command.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        try { command.ExecuteNonQuery(); } catch (SqliteException error) when (error.SqliteErrorCode == 19) { throw new InvalidOperationException("Bu uzaktan pasife alma önizlemesi daha önce onaylandı."); }
        transaction.Commit();
    }

    public IReadOnlyList<ProductRemoteDeactivationDispatch> Pending(string connectionId)
    {
        using var database = Open(); using var command = database.CreateCommand();
        command.CommandText = """
            SELECT p.Id FROM ProductRemoteDeactivationPreviews p
            JOIN ProductRemoteDeactivationApprovals a ON a.PreviewId=p.Id AND a.ConnectionId=p.ConnectionId
            LEFT JOIN ProductRemoteDeactivationPlans plan ON plan.PreviewId=p.Id
            LEFT JOIN ProductRemoteDeactivationReceipts receipt ON receipt.PreviewId=p.Id
            WHERE p.ConnectionId=$connection AND plan.PreviewId IS NULL AND receipt.PreviewId IS NULL
            ORDER BY p.CreatedUtc,p.Id
            """;
        command.Parameters.AddWithValue("$connection", connectionId);
        using var reader = command.ExecuteReader(); var ids = new List<string>(); while (reader.Read()) ids.Add(reader.GetString(0));
        return ids.Select(id => Get(id)!).ToArray();
    }

    public void AttachChannelPlan(string previewId, string connectionId, string channelPlanId)
    {
        using var database = Open(); using var transaction = database.BeginTransaction(deferred: false);
        var dispatch = Read(database, transaction, previewId) ?? throw new InvalidOperationException("Uzaktan pasife alma önizlemesi bulunamadı.");
        if (!dispatch.Approved || dispatch.Preview.ConnectionId != connectionId) throw new InvalidOperationException("Onay başka mağaza hesabına ait veya eksik.");
        EnsureCurrent(database, transaction, dispatch.Preview);
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO ProductRemoteDeactivationPlans VALUES($preview,$connection,$plan,$time)";
        command.Parameters.AddWithValue("$preview", previewId); command.Parameters.AddWithValue("$connection", connectionId); command.Parameters.AddWithValue("$plan", Required(channelPlanId, nameof(channelPlanId))); command.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        try { command.ExecuteNonQuery(); } catch (SqliteException error) when (error.SqliteErrorCode == 19) { throw new InvalidOperationException("Kanal gönderim önizlemesi daha önce bağlandı."); }
        transaction.Commit();
    }

    public void RecordChannelReceipt(string previewId, string channelPlanId, string productId, string connectionId, string remoteId, bool succeeded, string receiptId)
    {
        using var database = Open(); using var transaction = database.BeginTransaction(deferred: false);
        var dispatch = Read(database, transaction, previewId) ?? throw new InvalidOperationException("Uzaktan pasife alma önizlemesi bulunamadı.");
        if (dispatch.ChannelPlanId != channelPlanId || dispatch.Preview.ProductId != productId || dispatch.Preview.ConnectionId != connectionId || dispatch.Preview.RemoteId != remoteId)
            throw new InvalidOperationException("Kanal makbuzu başka önizleme, ürün veya mağaza hesabına ait.");
        var receipt = new ProductRemoteDeactivationReceipt(previewId, productId, connectionId, remoteId, succeeded, Required(receiptId, nameof(receiptId)), DateTime.UtcNow);
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO ProductRemoteDeactivationReceipts VALUES($preview,$plan,$json)";
        command.Parameters.AddWithValue("$preview", previewId); command.Parameters.AddWithValue("$plan", channelPlanId); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt));
        try { command.ExecuteNonQuery(); } catch (SqliteException error) when (error.SqliteErrorCode == 19) { throw new InvalidOperationException("Kanal makbuzu daha önce kaydedildi."); }
        transaction.Commit();
    }

    public ProductRemoteDeactivationReceipt? Receipt(string previewId)
    {
        using var database = Open(); using var command = database.CreateCommand(); command.CommandText = "SELECT Json FROM ProductRemoteDeactivationReceipts WHERE PreviewId=$id"; command.Parameters.AddWithValue("$id", previewId);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ProductRemoteDeactivationReceipt>(json) ?? throw new InvalidDataException("Uzaktan pasife alma makbuzu okunamadı.") : null;
    }

    public ProductRemoteDeactivationDispatch? Get(string previewId) { using var database = Open(); return Read(database, null, previewId); }

    public ProductRemoteDeactivationDispatch? Latest(string productId, string connectionId)
    {
        using var database = Open(); using var command = database.CreateCommand();
        command.CommandText = "SELECT p.Id FROM ProductRemoteDeactivationPreviews p JOIN ProductRemoteDeactivationApprovals a ON a.PreviewId=p.Id LEFT JOIN ProductRemoteDeactivationReceipts r ON r.PreviewId=p.Id WHERE p.ProductId=$product AND p.ConnectionId=$connection AND r.PreviewId IS NULL ORDER BY p.CreatedUtc DESC,p.Id DESC LIMIT 1";
        command.Parameters.AddWithValue("$product", productId); command.Parameters.AddWithValue("$connection", connectionId);
        return command.ExecuteScalar() is string id ? Read(database, null, id) : null;
    }

    static ProductRemoteDeactivationDispatch? Read(SqliteConnection database, SqliteTransaction? transaction, string id)
    {
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT p.Json,p.Channel,p.ShopId,
                   CASE WHEN a.PreviewId IS NULL THEN 0 ELSE 1 END,
                   COALESCE(plan.ChannelPlanId,'')
            FROM ProductRemoteDeactivationPreviews p
            LEFT JOIN ProductRemoteDeactivationApprovals a ON a.PreviewId=p.Id
            LEFT JOIN ProductRemoteDeactivationPlans plan ON plan.PreviewId=p.Id
            WHERE p.Id=$id
            """;
        command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var preview = JsonSerializer.Deserialize<ProductRemoteDeactivationPreview>(reader.GetString(0)) ?? throw new InvalidDataException("Uzaktan pasife alma önizlemesi okunamadı.");
        return new(preview, reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0, reader.GetString(4));
    }

    void EnsureCurrent(SqliteConnection database, SqliteTransaction transaction, ProductRemoteDeactivationPreview preview)
    {
        var current = connections.Get(preview.ConnectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        if (!MarketplaceOperationalAccounts.IsEligible(current, connections) || current.Revision != preview.ConnectionRevision)
            throw new InvalidOperationException("Mağaza bağlantısı değişti veya operasyonel değil; yeni önizleme alın.");
        using var command = database.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM MarketplaceConnections c
            JOIN ProductChannelBindings b ON b.ConnectionId=c.Id
            WHERE c.Id=$connection AND c.Enabled=1 AND c.Revision=$connectionRevision
              AND b.ProductId=$product AND b.RemoteId=$remote AND b.Version=$bindingVersion
            """;
        command.Parameters.AddWithValue("$connection", preview.ConnectionId); command.Parameters.AddWithValue("$connectionRevision", preview.ConnectionRevision);
        command.Parameters.AddWithValue("$product", preview.ProductId); command.Parameters.AddWithValue("$remote", preview.RemoteId); command.Parameters.AddWithValue("$bindingVersion", preview.BindingVersion);
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("Mağaza veya ürün bağlantısı değişti; yeni önizleme alın.");
    }

    static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Kimlik gerekli.", name) : value.Trim();
}

public sealed class ProductRemoteDeactivationDispatchRouter : IProductRemoteDeactivationPreviewRouter
{
    readonly string? directory;
    readonly ProductRemoteDeactivationDispatchStore store;
    readonly MarketplaceConnectionStore connections;

    public ProductRemoteDeactivationDispatchRouter(string? directory = null) { this.directory = directory; store = new(directory); connections = new(directory); }
    public bool Supports(MarketplaceConnection connection, IMarketplaceAdapter adapter) =>
        connection.Channel == "etsy" && adapter.Channel == "etsy" && adapter.Capabilities.Supports(MarketplaceOperation.ProductsRead)
        && MarketplaceOperationalAccounts.IsEligible(connection, connections);
    public ProductRemoteDeactivationPreview Preview(MarketplaceConnection connection, ProductChannelBinding binding) => store.Create(connection, binding);
    public void Approve(ProductRemoteDeactivationPreview preview, bool explicitlyApproved) => store.Approve(preview, explicitlyApproved);
    public ProductRemoteDeactivationPreview? Latest(string productId, string connectionId) => store.Latest(productId, connectionId)?.Preview;
    public ProductRemoteDeactivationReceipt? Receipt(string previewId)
    {
        var direct = store.Receipt(previewId); if (direct is not null) return direct;
        var dispatch = store.Get(previewId); if (dispatch is null || dispatch.Channel != "etsy" || dispatch.ChannelPlanId.Length == 0) return null;
        var receipt = new EtsyWorkspaceStore(directory).Receipts(dispatch.ShopId).FirstOrDefault(item =>
            item.PlanId == dispatch.ChannelPlanId && item.ProductId == dispatch.Preview.ProductId && item.Status == "Succeeded");
        if (receipt is null) return null;
        if (receipt.ListingId?.ToString(CultureInfo.InvariantCulture) != dispatch.Preview.RemoteId) return null;
        store.RecordChannelReceipt(previewId, dispatch.ChannelPlanId, dispatch.Preview.ProductId, dispatch.Preview.ConnectionId, dispatch.Preview.RemoteId,
            true, $"{receipt.PlanId}:{receipt.ProductId}:{receipt.CreatedUtc:O}");
        return store.Receipt(previewId);
    }
}
