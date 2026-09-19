using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Hepsiburada;

public enum HepsiburadaOperation
{
    CatalogCreate,
    CatalogUpdate,
    ListingCreate,
    Price,
    Stock,
    PriceAndStock,
    DispatchTime,
    SaleState
}

public sealed record HepsiburadaPlanRow(string ProductId, string Barcode, string RemoteId, string Status, string Error, string? ItemJson);

public sealed record HepsiburadaPlan(
    string Id, string ConnectionId, string MerchantId, HepsiburadaEnvironment Environment,
    HepsiburadaOperation Operation, DateTime CreatedUtc, long ConnectionRevision,
    string CatalogHash, string BindingHash, string WorkspaceHash,
    IReadOnlyList<HepsiburadaPlanRow> Rows, string PayloadJson);

public sealed record HepsiburadaReceipt(
    string PlanId, string ConnectionId, DateTime CreatedUtc, string Operation,
    string Status, string TrackingId, string Detail);

public interface IHepsiburadaWriteTransport
{
    Task<string> SendAsync(HepsiburadaOperation operation, string payloadJson, CancellationToken cancellationToken);
}

/// <summary>
/// Creates account-scoped immutable previews and claims them before any network write.
/// The production application intentionally has no live write transport until the
/// account passes read verification and the exact Hepsiburada write permission is enabled.
/// </summary>
public sealed class HepsiburadaDispatchStore
{
    readonly string directory;
    readonly string connectionString;

    public HepsiburadaDispatchStore(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(this.directory);
        _ = new CatalogStore(this.directory);
        _ = new MarketplaceConnectionStore(this.directory);
        _ = new HepsiburadaWorkspaceStore(this.directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(this.directory, "catalog.db"), DefaultTimeout = 15 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS HepsiburadaPlans(
                Id TEXT PRIMARY KEY,
                ConnectionId TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                Json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS HepsiburadaReceipts(
                PlanId TEXT PRIMARY KEY,
                ConnectionId TEXT NOT NULL,
                PayloadHash TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                Json TEXT NOT NULL
            );
            CREATE TRIGGER IF NOT EXISTS HepsiburadaPlans_NoUpdate BEFORE UPDATE ON HepsiburadaPlans BEGIN SELECT RAISE(ABORT,'immutable Hepsiburada plan'); END;
            CREATE TRIGGER IF NOT EXISTS HepsiburadaPlans_NoDelete BEFORE DELETE ON HepsiburadaPlans BEGIN SELECT RAISE(ABORT,'immutable Hepsiburada plan'); END;
            """;
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    public HepsiburadaPlan Preview(string connectionId, IReadOnlyCollection<string> productIds, HepsiburadaOperation operation)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (productIds is null || productIds.Count == 0 || productIds.Count > 1000 || productIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Önizleme için 1-1000 geçerli ürün seçin.");
        var account = RequireAccount(connectionId);
        var catalog = new CatalogStore(directory).Products().Where(x => productIds.Contains(x.Id, StringComparer.Ordinal)).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (catalog.Length != productIds.Distinct(StringComparer.Ordinal).Count()) throw new InvalidOperationException("Seçilen ürünlerden biri katalogda bulunamadı.");
        var bindingStore = new ProductChannelBindingStore(directory);
        var bindings = catalog.Select(product => bindingStore.Get(product.Id, connectionId)).ToArray();
        var rows = new List<HepsiburadaPlanRow>(catalog.Length);
        var items = new List<JsonElement>(catalog.Length);
        for (var index = 0; index < catalog.Length; index++)
        {
            var product = catalog[index]; var binding = bindings[index];
            var barcode = (binding?.RemoteBarcode.Length > 0 ? binding.RemoteBarcode : product.Barcode).Trim();
            string? error = barcode.Length == 0 ? "Barkod zorunludur." : null;
            if (operation is HepsiburadaOperation.Price or HepsiburadaOperation.Stock or HepsiburadaOperation.PriceAndStock or HepsiburadaOperation.DispatchTime or HepsiburadaOperation.SaleState && binding is null)
                error = "Ürün bu Hepsiburada mağazasına bağlı değil.";
            if ((operation is HepsiburadaOperation.Price or HepsiburadaOperation.PriceAndStock) && product.Price <= 0) error = "Satış fiyatı sıfırdan büyük olmalıdır.";
            if ((operation is HepsiburadaOperation.CatalogCreate or HepsiburadaOperation.CatalogUpdate or HepsiburadaOperation.ListingCreate) && (product.Name.Trim().Length == 0 || product.Brand.Trim().Length == 0 || product.Category.Trim().Length == 0))
                error = "Ürün adı, marka ve kategori zorunludur.";
            if (error is not null) { rows.Add(new(product.Id, barcode, binding?.RemoteId ?? "", "Hatalı", error, null)); continue; }
            var item = BuildItem(operation, product, binding, barcode);
            var itemJson = JsonSerializer.Serialize(item);
            items.Add(JsonSerializer.Deserialize<JsonElement>(itemJson));
            rows.Add(new(product.Id, barcode, binding?.RemoteId ?? "", "Gönderime hazır", "", itemJson));
        }
        var rootName = operation is HepsiburadaOperation.CatalogCreate or HepsiburadaOperation.CatalogUpdate ? "products" : "listings";
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?> { [rootName] = items });
        var workspace = new HepsiburadaWorkspaceStore(directory).Read(connectionId);
        var plan = new HepsiburadaPlan(Guid.NewGuid().ToString("N"), connectionId, account.ShopId, HepsiburadaEnvironment.Production,
            operation, DateTime.UtcNow, account.Revision, CatalogHash(catalog), BindingHash(bindings), WorkspaceHash(workspace), rows.AsReadOnly(), payload);
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO HepsiburadaPlans(Id,ConnectionId,CreatedUtc,Json) VALUES($id,$connection,$utc,$json)";
        command.Parameters.AddWithValue("$id", plan.Id); command.Parameters.AddWithValue("$connection", plan.ConnectionId);
        command.Parameters.AddWithValue("$utc", plan.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(plan));
        command.ExecuteNonQuery(); return plan;
    }

    public HepsiburadaPlan Claim(string planId, string connectionId, bool explicitlyApproved)
    {
        if (!explicitlyApproved) throw new InvalidOperationException("Önizlemenin açıkça onaylanması gerekli.");
        using var connection = Open();
        HepsiburadaPlan plan;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT Json FROM HepsiburadaPlans WHERE Id=$id AND ConnectionId=$connection";
            read.Parameters.AddWithValue("$id", planId); read.Parameters.AddWithValue("$connection", connectionId);
            plan = JsonSerializer.Deserialize<HepsiburadaPlan>(read.ExecuteScalar() as string ?? throw new InvalidOperationException("Önizleme bu mağazada bulunamadı."))!;
        }
        var account = RequireAccount(connectionId);
        var catalog = new CatalogStore(directory).Products().Where(x => plan.Rows.Select(r => r.ProductId).Contains(x.Id, StringComparer.Ordinal)).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var bindingStore = new ProductChannelBindingStore(directory);
        var bindings = catalog.Select(product => bindingStore.Get(product.Id, connectionId)).ToArray();
        var workspace = new HepsiburadaWorkspaceStore(directory).Read(connectionId);
        if (plan.ConnectionId != connectionId || plan.MerchantId != account.ShopId || plan.ConnectionRevision != account.Revision ||
            plan.Environment != HepsiburadaEnvironment.Production || DateTime.UtcNow - plan.CreatedUtc > TimeSpan.FromMinutes(15) || plan.CreatedUtc > DateTime.UtcNow ||
            plan.CatalogHash != CatalogHash(catalog) || plan.BindingHash != BindingHash(bindings) || plan.WorkspaceHash != WorkspaceHash(workspace))
            throw new InvalidOperationException("Önizleme eskidi; mağaza, katalog veya bağlantılar değişti. Yeniden önizleyin.");
        if (plan.Rows.Any(row => row.Status == "Hatalı" || row.ItemJson is null) || plan.Rows.Count == 0)
            throw new InvalidOperationException("Hatalı satırları düzeltin veya seçimden çıkarıp yeniden önizleyin.");
        var rebuiltItems = plan.Rows.Select(row => JsonSerializer.Deserialize<JsonElement>(row.ItemJson!)).ToArray();
        var rootName = plan.Operation is HepsiburadaOperation.CatalogCreate or HepsiburadaOperation.CatalogUpdate ? "products" : "listings";
        var rebuilt = JsonSerializer.Serialize(new Dictionary<string, object?> { [rootName] = rebuiltItems });
        if (rebuilt != plan.PayloadJson) throw new InvalidDataException("Önizleme içeriği tutarsız.");
        var payloadHash = Hash(plan.Operation + "|" + plan.PayloadJson);
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var duplicates = connection.CreateCommand())
        {
            duplicates.Transaction = transaction;
            duplicates.CommandText = "SELECT PlanId,PayloadHash,Json FROM HepsiburadaReceipts WHERE ConnectionId=$connection";
            duplicates.Parameters.AddWithValue("$connection", connectionId);
            using var reader = duplicates.ExecuteReader();
            while (reader.Read())
            {
                var receipt = JsonSerializer.Deserialize<HepsiburadaReceipt>(reader.GetString(2))!;
                if (reader.GetString(0) == plan.Id) throw new InvalidOperationException("Bu önizleme daha önce gönderildi; işlem geçmişini kontrol edin.");
                if (receipt.Status is "Sonuç bekleniyor" or "Belirsiz") throw new InvalidOperationException("Önceki gönderimin sonucu belirsiz; mağazayı kontrol etmeden tekrar gönderilmez.");
                if (reader.GetString(1) == payloadHash && DateTime.UtcNow - receipt.CreatedUtc < TimeSpan.FromMinutes(15))
                    throw new InvalidOperationException("Aynı içerik kısa süre önce gönderildi; mağaza sonucunu kontrol edin.");
            }
        }
        var pending = new HepsiburadaReceipt(plan.Id, connectionId, DateTime.UtcNow, plan.Operation.ToString(), "Sonuç bekleniyor", "", "İstek hazırlanıyor; tekrar göndermeyin.");
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO HepsiburadaReceipts(PlanId,ConnectionId,PayloadHash,CreatedUtc,Json) VALUES($id,$connection,$hash,$utc,$json)";
        insert.Parameters.AddWithValue("$id", plan.Id); insert.Parameters.AddWithValue("$connection", connectionId); insert.Parameters.AddWithValue("$hash", payloadHash);
        insert.Parameters.AddWithValue("$utc", pending.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)); insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(pending));
        insert.ExecuteNonQuery(); transaction.Commit(); return plan;
    }

    public IReadOnlyList<HepsiburadaReceipt> Receipts(string connectionId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM HepsiburadaReceipts WHERE ConnectionId=$connection ORDER BY CreatedUtc DESC"; command.Parameters.AddWithValue("$connection", connectionId);
        using var reader = command.ExecuteReader(); var result = new List<HepsiburadaReceipt>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<HepsiburadaReceipt>(reader.GetString(0)) ?? throw new InvalidDataException("Hepsiburada işlem geçmişi okunamadı."));
        return result.AsReadOnly();
    }

    public HepsiburadaReceipt UpdateReceipt(string planId, string connectionId, string status, string trackingId, string detail)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        HepsiburadaReceipt previous;
        using (var read = connection.CreateCommand()) { read.Transaction = transaction; read.CommandText = "SELECT Json FROM HepsiburadaReceipts WHERE PlanId=$id AND ConnectionId=$connection"; read.Parameters.AddWithValue("$id", planId); read.Parameters.AddWithValue("$connection", connectionId); previous = JsonSerializer.Deserialize<HepsiburadaReceipt>(read.ExecuteScalar() as string ?? throw new InvalidOperationException("Gönderim kaydı bulunamadı."))!; }
        var next = previous with { Status = status, TrackingId = trackingId, Detail = MarketplaceConnectionStore.Redact(detail) };
        using var update = connection.CreateCommand(); update.Transaction = transaction; update.CommandText = "UPDATE HepsiburadaReceipts SET Json=$json WHERE PlanId=$id AND ConnectionId=$connection";
        update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(next)); update.Parameters.AddWithValue("$id", planId); update.Parameters.AddWithValue("$connection", connectionId); update.ExecuteNonQuery(); transaction.Commit(); return next;
    }

    MarketplaceConnection RequireAccount(string connectionId)
    {
        var account = new MarketplaceConnectionStore(directory).Get(connectionId) ?? throw new InvalidOperationException("Hepsiburada mağazası bulunamadı.");
        if (!account.Enabled || account.Channel != "hepsiburada") throw new InvalidOperationException("Etkin bir Hepsiburada mağazası gerekli.");
        return account;
    }

    static object BuildItem(HepsiburadaOperation operation, CatalogProduct product, ProductChannelBinding? binding, string barcode) => operation switch
    {
        HepsiburadaOperation.Stock => new { barcode, availableStock = product.Stock },
        HepsiburadaOperation.Price => new { barcode, price = product.Price },
        HepsiburadaOperation.PriceAndStock => new { barcode, price = product.Price, availableStock = product.Stock },
        HepsiburadaOperation.SaleState => new { barcode, active = product.Active },
        HepsiburadaOperation.DispatchTime => new { barcode, dispatchTime = 1 },
        _ => new { merchantSku = product.Sku, barcode, title = product.Name, brand = product.Brand, category = product.Category, price = product.Price, availableStock = product.Stock, description = product.Description, images = product.ImageUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) }
    };

    static string CatalogHash(IEnumerable<CatalogProduct> products) => Hash(JsonSerializer.Serialize(products.OrderBy(x => x.Id, StringComparer.Ordinal)));
    static string BindingHash(IEnumerable<ProductChannelBinding?> bindings) => Hash(JsonSerializer.Serialize(bindings.OrderBy(x => x?.ProductId, StringComparer.Ordinal)));
    static string WorkspaceHash(HepsiburadaWorkspaceState state) => Hash($"{state.ConnectionId}|{state.ShopId}|{state.Revision}|{state.RefreshedUtc:O}");
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class HepsiburadaDispatch
{
    readonly HepsiburadaDispatchStore store;
    readonly IHepsiburadaWriteTransport transport;
    public HepsiburadaDispatch(HepsiburadaDispatchStore store, IHepsiburadaWriteTransport transport) { this.store = store; this.transport = transport; }

    public async Task<HepsiburadaReceipt> SendAsync(string planId, string connectionId, bool approved, CancellationToken cancellationToken = default)
    {
        var plan = store.Claim(planId, connectionId, approved);
        try
        {
            var tracking = await transport.SendAsync(plan.Operation, plan.PayloadJson, cancellationToken);
            if (string.IsNullOrWhiteSpace(tracking)) throw new InvalidDataException("Hepsiburada takip kimliği boş döndü.");
            return store.UpdateReceipt(plan.Id, connectionId, "Kuyrukta", tracking.Trim(), "İstek kabul edildi; sonuç ayrıca doğrulanmalıdır.");
        }
        catch
        {
            store.UpdateReceipt(plan.Id, connectionId, "Belirsiz", "", "Gönderim sonucu doğrulanamadı. Mağazada kontrol etmeden tekrar göndermeyin.");
            throw new InvalidOperationException("Gönderim sonucu belirsiz. Hepsiburada mağazasını kontrol etmeden tekrar göndermeyin.");
        }
    }
}
