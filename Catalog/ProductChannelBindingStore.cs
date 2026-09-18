using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrMarketplaceHubDesktop.Etsy;
using TrMarketplaceHubDesktop.Trendyol;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed class ProductChannelBindingStore
{
    readonly string directory;
    readonly string connectionString;

    public ProductChannelBindingStore(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(this.directory);
        _ = new CatalogStore(this.directory);
        _ = new MarketplaceConnectionStore(this.directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(this.directory, "catalog.db"), DefaultTimeout = 15 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ProductChannelBindings(
                ProductId TEXT NOT NULL,
                ConnectionId TEXT NOT NULL,
                RemoteId TEXT NOT NULL,
                RemoteSku TEXT NOT NULL,
                RemoteBarcode TEXT NOT NULL,
                ManageContent INTEGER NOT NULL,
                ManagePrice INTEGER NOT NULL,
                ManageStock INTEGER NOT NULL,
                CategoryId TEXT NOT NULL,
                TemplateId TEXT NOT NULL,
                State TEXT NOT NULL,
                Version INTEGER NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY(ProductId,ConnectionId),
                UNIQUE(ConnectionId,RemoteId)
            );
            CREATE TABLE IF NOT EXISTS ProductChannelBindingPreviews(
                Id TEXT PRIMARY KEY,
                ConnectionId TEXT NOT NULL,
                Json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ProductChannelBindingReceipts(
                PreviewId TEXT PRIMARY KEY,
                ConnectionId TEXT NOT NULL,
                Json TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    public ProductChannelBinding? Get(string productId, string connectionId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectBindings + " WHERE ProductId=$product AND ConnectionId=$connection";
        command.Parameters.AddWithValue("$product", Required(productId, nameof(productId)));
        command.Parameters.AddWithValue("$connection", Required(connectionId, nameof(connectionId)));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBinding(reader) : null;
    }

    public IReadOnlyList<ProductChannelBinding> List(string? productId = null, string? connectionId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectBindings + " WHERE ($product='' OR ProductId=$product) AND ($connection='' OR ConnectionId=$connection) ORDER BY ProductId,ConnectionId";
        command.Parameters.AddWithValue("$product", productId ?? "");
        command.Parameters.AddWithValue("$connection", connectionId ?? "");
        using var reader = command.ExecuteReader();
        var result = new List<ProductChannelBinding>();
        while (reader.Read()) result.Add(ReadBinding(reader));
        return result;
    }

    public ProductChannelBinding Save(ProductChannelBinding binding, long expectedVersion)
    {
        Validate(binding);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var saved = Save(connection, transaction, binding, expectedVersion);
        transaction.Commit();
        return saved;
    }

    internal ProductChannelBinding Save(SqliteConnection connection, SqliteTransaction transaction, ProductChannelBinding binding, long expectedVersion)
    {
        long currentVersion;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT Version FROM ProductChannelBindings WHERE ProductId=$product AND ConnectionId=$connection";
            find.Parameters.AddWithValue("$product", binding.ProductId);
            find.Parameters.AddWithValue("$connection", binding.ConnectionId);
            var value = find.ExecuteScalar();
            currentVersion = value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        if (currentVersion != expectedVersion) throw new InvalidOperationException("Ürün-mağaza bağlantısı değişti; yeni önizleme alın.");
        EnsureProductAndConnection(connection, transaction, binding.ProductId, binding.ConnectionId);
        var saved = binding with { Version = currentVersion + 1, UpdatedUtc = DateTime.UtcNow };
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ProductChannelBindings(ProductId,ConnectionId,RemoteId,RemoteSku,RemoteBarcode,ManageContent,ManagePrice,ManageStock,CategoryId,TemplateId,State,Version,UpdatedUtc)
            VALUES($product,$connection,$remote,$sku,$barcode,$content,$price,$stock,$category,$template,$state,$version,$updated)
            ON CONFLICT(ProductId,ConnectionId) DO UPDATE SET
                RemoteId=excluded.RemoteId,RemoteSku=excluded.RemoteSku,RemoteBarcode=excluded.RemoteBarcode,
                ManageContent=excluded.ManageContent,ManagePrice=excluded.ManagePrice,ManageStock=excluded.ManageStock,
                CategoryId=excluded.CategoryId,TemplateId=excluded.TemplateId,State=excluded.State,
                Version=excluded.Version,UpdatedUtc=excluded.UpdatedUtc
            """;
        Bind(command, saved);
        try { command.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("Uzak ilan bu mağazada başka bir ürüne bağlı."); }
        return saved;
    }

    public IReadOnlyList<ProductChannelBindingPreview> Previews()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM ProductChannelBindingPreviews ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var result = new List<ProductChannelBindingPreview>();
        while (reader.Read()) result.Add(Deserialize<ProductChannelBindingPreview>(reader.GetString(0)));
        return result;
    }

    public ProductChannelBindingPreview Preview(string previewId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM ProductChannelBindingPreviews WHERE Id=$id";
        command.Parameters.AddWithValue("$id", Required(previewId, nameof(previewId)));
        return Deserialize<ProductChannelBindingPreview>(command.ExecuteScalar() as string ?? throw new InvalidOperationException("Eşleştirme önizlemesi bulunamadı."));
    }

    public ProductChannelBindingReceipt? Receipt(string previewId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM ProductChannelBindingReceipts WHERE PreviewId=$id";
        command.Parameters.AddWithValue("$id", Required(previewId, nameof(previewId)));
        return command.ExecuteScalar() is string json ? Deserialize<ProductChannelBindingReceipt>(json) : null;
    }

    internal void PersistPreview(ProductChannelBindingPreview preview)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ProductChannelBindingPreviews(Id,ConnectionId,Json) VALUES($id,$connection,$json)";
        command.Parameters.AddWithValue("$id", preview.Id);
        command.Parameters.AddWithValue("$connection", preview.ConnectionId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(preview));
        command.ExecuteNonQuery();
    }

    internal ProductChannelBindingReceipt Apply(ProductChannelBindingPreview preview, string currentRemoteHash, string creationPreviewId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (ReceiptExists(connection, transaction, preview.Id)) throw new InvalidOperationException("Bu eşleştirme önizlemesi daha önce uygulandı.");
        ValidateFences(connection, transaction, preview, currentRemoteHash);
        var applied = new List<ProductChannelBinding>();
        foreach (var row in preview.Rows.Where(row => row.Reviewed && row.Outcome == ProductChannelMatchOutcome.Matched))
        {
            var current = ReadBinding(connection, transaction, row.ProductId, preview.ConnectionId);
            var binding = new ProductChannelBinding(row.ProductId, preview.ConnectionId, row.RemoteId, row.RemoteSku, row.RemoteBarcode,
                current?.ManageContent ?? true, current?.ManagePrice ?? true, current?.ManageStock ?? true,
                preview.RemoteRows.Single(x => x.RemoteId == row.RemoteId).CategoryId,
                preview.RemoteRows.Single(x => x.RemoteId == row.RemoteId).TemplateId,
                preview.RemoteRows.Single(x => x.RemoteId == row.RemoteId).State,
                current?.Version ?? 0, current?.UpdatedUtc ?? default);
            applied.Add(Save(connection, transaction, binding, current?.Version ?? 0));
        }
        var candidates = preview.Rows.Where(row => row.Outcome == ProductChannelMatchOutcome.NewListingCandidate).Select(row => row.ProductId).Distinct(StringComparer.Ordinal).ToArray();
        var receipt = new ProductChannelBindingReceipt(preview.Id, preview.ConnectionId, DateTime.UtcNow, applied.ToArray(), candidates, creationPreviewId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ProductChannelBindingReceipts(PreviewId,ConnectionId,Json) VALUES($id,$connection,$json)";
        command.Parameters.AddWithValue("$id", preview.Id);
        command.Parameters.AddWithValue("$connection", preview.ConnectionId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt));
        command.ExecuteNonQuery();
        transaction.Commit();
        return Clone(receipt);
    }

    internal void ValidatePreview(ProductChannelBindingPreview preview, string currentRemoteHash)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (ReceiptExists(connection, transaction, preview.Id)) throw new InvalidOperationException("Bu eşleştirme önizlemesi daha önce uygulandı.");
        ValidateFences(connection, transaction, preview, currentRemoteHash);
        transaction.Commit();
    }

    public int MigrateVerifiedProfiles()
    {
        var migrated = 0;
        var catalogIds = new CatalogStore(directory).Products().Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var connection in new MarketplaceConnectionStore(directory).List(false).Where(x => x.Enabled))
        {
            if (connection.Channel == "trendyol")
            {
                TrendyolWorkspaceState state;
                try { state = new TrendyolWorkspaceStore(directory).Load(connection.ShopId); }
                catch (InvalidOperationException) { continue; }
                foreach (var profile in state.Profiles.Where(x => catalogIds.Contains(x.ProductId) && Get(x.ProductId, connection.Id) is null))
                {
                    var barcode = string.IsNullOrWhiteSpace(profile.IntegrationCode) ? profile.ListingBarcode : profile.IntegrationCode;
                    var matches = state.Products.Where(x => !string.IsNullOrWhiteSpace(barcode) && x.Barcode == barcode).ToArray();
                    if (matches.Length != 1) continue;
                    var remote = matches[0];
                    Save(new(profile.ProductId, connection.Id, remote.ContentId.ToString(CultureInfo.InvariantCulture), remote.StockCode, remote.Barcode,
                        true, true, true, profile.CategoryId?.ToString(CultureInfo.InvariantCulture) ?? "", profile.DeliveryTemplateId,
                        remote.Approved ? "Approved" : "Pending", 0, default), 0);
                    migrated++;
                }
            }
            else if (connection.Channel == "etsy")
            {
                EtsyWorkspaceState state;
                try { state = new EtsyWorkspaceStore(directory).Load(connection.ShopId); }
                catch (InvalidOperationException) { continue; }
                foreach (var profile in state.Profiles.Where(x => x.ListingId.HasValue && catalogIds.Contains(x.ProductId) && Get(x.ProductId, connection.Id) is null))
                {
                    var matches = state.Listings.Where(x => x.ListingId == profile.ListingId).ToArray();
                    if (matches.Length != 1) continue;
                    var remote = matches[0];
                    Save(new(profile.ProductId, connection.Id, remote.ListingId.ToString(CultureInfo.InvariantCulture), remote.Sku, "",
                        true, true, true, profile.TaxonomyId?.ToString(CultureInfo.InvariantCulture) ?? "", profile.TemplateId,
                        remote.State, 0, default), 0);
                    migrated++;
                }
            }
        }
        return migrated;
    }

    internal string CatalogHash(IEnumerable<string> productIds)
    {
        using var connection = Open();
        return CatalogHash(connection, null, productIds);
    }

    internal string BindingVersionHash(string connectionId, IEnumerable<string> productIds)
    {
        using var connection = Open();
        return BindingVersionHash(connection, null, connectionId, productIds);
    }

    internal static string RemoteHash(IEnumerable<ProductChannelRemoteRow> rows) => Hash(JsonSerializer.Serialize(rows
        .OrderBy(x => x.ConnectionId, StringComparer.Ordinal).ThenBy(x => x.ShopId, StringComparer.Ordinal).ThenBy(x => x.RemoteId, StringComparer.Ordinal)
        .Select(x => new[] { x.ConnectionId, x.ShopId, x.RemoteId, x.RemoteSku, x.RemoteBarcode, x.CategoryId, x.TemplateId, x.State })));

    void ValidateFences(SqliteConnection connection, SqliteTransaction transaction, ProductChannelBindingPreview preview, string currentRemoteHash)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT Channel,ShopId,Enabled,Revision FROM MarketplaceConnections WHERE Id=$id";
            command.Parameters.AddWithValue("$id", preview.ConnectionId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(2) != 1 || reader.GetInt64(3) != preview.ConnectionRevision || reader.GetString(0) != preview.Channel || reader.GetString(1) != preview.ShopId)
                throw new InvalidOperationException("Mağaza bağlantısı değişti; yeni önizleme alın.");
        }
        var productIds = preview.Rows.Select(x => x.ProductId);
        if (CatalogHash(connection, transaction, productIds) != preview.CatalogHash) throw new InvalidOperationException("Katalog değişti; yeni önizleme alın.");
        if (BindingVersionHash(connection, transaction, preview.ConnectionId, productIds) != preview.BindingVersionHash) throw new InvalidOperationException("Ürün-mağaza bağlantıları değişti; yeni önizleme alın.");
        if (currentRemoteHash != preview.RemoteSnapshotHash) throw new InvalidOperationException("Uzak mağaza ürünleri değişti; yeniden okuyup önizleyin.");
    }

    static string CatalogHash(SqliteConnection connection, SqliteTransaction? transaction, IEnumerable<string> productIds)
    {
        var values = new List<string>();
        foreach (var id in productIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Json FROM CatalogProducts WHERE Id=$id";
            command.Parameters.AddWithValue("$id", id);
            values.Add(id);
            values.Add(command.ExecuteScalar() as string ?? "<deleted>");
        }
        return Hash(JsonSerializer.Serialize(values));
    }

    static string BindingVersionHash(SqliteConnection connection, SqliteTransaction? transaction, string connectionId, IEnumerable<string> productIds)
    {
        var values = new List<string>();
        foreach (var id in productIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Version,RemoteId FROM ProductChannelBindings WHERE ProductId=$product AND ConnectionId=$connection";
            command.Parameters.AddWithValue("$product", id);
            command.Parameters.AddWithValue("$connection", connectionId);
            using var reader = command.ExecuteReader();
            values.Add(id);
            values.Add(reader.Read() ? reader.GetInt64(0).ToString(CultureInfo.InvariantCulture) + ":" + reader.GetString(1) : "<none>");
        }
        return Hash(JsonSerializer.Serialize(values));
    }

    static void EnsureProductAndConnection(SqliteConnection connection, SqliteTransaction transaction, string productId, string connectionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT (SELECT COUNT(*) FROM CatalogProducts WHERE Id=$product),(SELECT COUNT(*) FROM MarketplaceConnections WHERE Id=$connection AND Enabled=1)";
        command.Parameters.AddWithValue("$product", productId);
        command.Parameters.AddWithValue("$connection", connectionId);
        using var reader = command.ExecuteReader();
        reader.Read();
        if (reader.GetInt64(0) != 1) throw new InvalidOperationException("Katalog ürünü bulunamadı.");
        if (reader.GetInt64(1) != 1) throw new InvalidOperationException("Etkin mağaza bağlantısı bulunamadı.");
    }

    static bool ReceiptExists(SqliteConnection connection, SqliteTransaction transaction, string previewId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM ProductChannelBindingReceipts WHERE PreviewId=$id";
        command.Parameters.AddWithValue("$id", previewId);
        return command.ExecuteScalar() is not null;
    }

    static ProductChannelBinding? ReadBinding(SqliteConnection connection, SqliteTransaction transaction, string productId, string connectionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectBindings + " WHERE ProductId=$product AND ConnectionId=$connection";
        command.Parameters.AddWithValue("$product", productId);
        command.Parameters.AddWithValue("$connection", connectionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBinding(reader) : null;
    }

    const string SelectBindings = "SELECT ProductId,ConnectionId,RemoteId,RemoteSku,RemoteBarcode,ManageContent,ManagePrice,ManageStock,CategoryId,TemplateId,State,Version,UpdatedUtc FROM ProductChannelBindings";

    static ProductChannelBinding ReadBinding(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetInt32(5) == 1, reader.GetInt32(6) == 1, reader.GetInt32(7) == 1,
        reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetInt64(11),
        DateTime.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    static void Bind(SqliteCommand command, ProductChannelBinding binding)
    {
        command.Parameters.AddWithValue("$product", binding.ProductId);
        command.Parameters.AddWithValue("$connection", binding.ConnectionId);
        command.Parameters.AddWithValue("$remote", binding.RemoteId);
        command.Parameters.AddWithValue("$sku", binding.RemoteSku);
        command.Parameters.AddWithValue("$barcode", binding.RemoteBarcode);
        command.Parameters.AddWithValue("$content", binding.ManageContent ? 1 : 0);
        command.Parameters.AddWithValue("$price", binding.ManagePrice ? 1 : 0);
        command.Parameters.AddWithValue("$stock", binding.ManageStock ? 1 : 0);
        command.Parameters.AddWithValue("$category", binding.CategoryId);
        command.Parameters.AddWithValue("$template", binding.TemplateId);
        command.Parameters.AddWithValue("$state", binding.State);
        command.Parameters.AddWithValue("$version", binding.Version);
        command.Parameters.AddWithValue("$updated", binding.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    static void Validate(ProductChannelBinding binding)
    {
        Required(binding.ProductId, nameof(binding.ProductId));
        Required(binding.ConnectionId, nameof(binding.ConnectionId));
        Required(binding.RemoteId, nameof(binding.RemoteId));
        if (binding.Version < 0) throw new ArgumentException("Bağlantı sürümü geçersiz.");
        foreach (var value in new[] { binding.RemoteSku, binding.RemoteBarcode, binding.CategoryId, binding.TemplateId, binding.State })
            if (value is null || value.Length > 512 || value.Any(char.IsControl)) throw new ArgumentException("Ürün-mağaza bağlantı alanı geçersiz.");
    }

    static string Required(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)) throw new ArgumentException("Kimlik zorunlu ve geçerli olmalı.", parameter);
        return value;
    }

    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("Ürün-mağaza kaydı okunamadı.");
    static T Clone<T>(T value) => Deserialize<T>(JsonSerializer.Serialize(value));
}
