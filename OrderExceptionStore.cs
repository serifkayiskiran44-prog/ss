using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed class OrderExceptionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Marketplace { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string Type { get; set; } = "";
    public string EventKey { get; set; } = "";
    public string Severity { get; set; } = "Warning";
    public string Message { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class OrderExceptionStore
{
    readonly string connectionString;
    public OrderExceptionStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "order-exceptions.db") }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS OrderExceptions(Id TEXT PRIMARY KEY,Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,OrderId TEXT NOT NULL,Type TEXT NOT NULL,EventKey TEXT NOT NULL,Severity TEXT NOT NULL,Message TEXT NOT NULL,Status TEXT NOT NULL,CreatedUtc TEXT NOT NULL,UpdatedUtc TEXT NOT NULL,UNIQUE(Marketplace,ShopId,OrderId,Type,EventKey))"; command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var connection = SqliteConnectionPolicy.Open(connectionString); return connection; }
    public OrderExceptionRecord Save(OrderExceptionRecord record)
    {
        if (new[] { record.Marketplace, record.ShopId, record.OrderId, record.Type, record.EventKey, record.Message }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("İstisna kanal, mağaza, sipariş, tür, olay ve açıklama içermeli.");
        record.Marketplace = Limit(record.Marketplace, 80).ToLowerInvariant(); record.ShopId = Limit(record.ShopId, 200); record.OrderId = Limit(record.OrderId, 200); record.Type = Limit(record.Type, 80); record.EventKey = Limit(record.EventKey, 240); record.Severity = Limit(record.Severity, 30); record.Status = Limit(record.Status, 40); record.Message = MarketplaceConnectionStore.Redact(Limit(record.Message, 2000)); record.UpdatedUtc = DateTime.UtcNow;
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO OrderExceptions(Id,Marketplace,ShopId,OrderId,Type,EventKey,Severity,Message,Status,CreatedUtc,UpdatedUtc) VALUES($id,$marketplace,$shop,$order,$type,$event,$severity,$message,$status,$created,$updated) ON CONFLICT(Marketplace,ShopId,OrderId,Type,EventKey) DO UPDATE SET Severity=excluded.Severity,Message=excluded.Message,Status=excluded.Status,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$id", record.Id); command.Parameters.AddWithValue("$marketplace", record.Marketplace); command.Parameters.AddWithValue("$shop", record.ShopId); command.Parameters.AddWithValue("$order", record.OrderId); command.Parameters.AddWithValue("$type", record.Type); command.Parameters.AddWithValue("$event", record.EventKey); command.Parameters.AddWithValue("$severity", record.Severity); command.Parameters.AddWithValue("$message", record.Message); command.Parameters.AddWithValue("$status", record.Status); command.Parameters.AddWithValue("$created", record.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$updated", record.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery(); return List(record.Marketplace, record.ShopId).Single(x => x.OrderId == record.OrderId && x.Type == record.Type && x.EventKey == record.EventKey);
    }
    public IReadOnlyList<OrderExceptionRecord> List(string? marketplace = null, string? shop = null, string? status = null, string? query = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Id,Marketplace,ShopId,OrderId,Type,EventKey,Severity,Message,Status,CreatedUtc,UpdatedUtc FROM OrderExceptions WHERE ($marketplace='' OR Marketplace=$marketplace) AND ($shop='' OR ShopId=$shop) AND ($status='' OR Status=$status) AND ($query='' OR OrderId LIKE $like OR Message LIKE $like OR Type LIKE $like) ORDER BY CASE Severity WHEN 'Critical' THEN 0 WHEN 'Error' THEN 1 ELSE 2 END,UpdatedUtc DESC"; command.Parameters.AddWithValue("$marketplace", marketplace?.Trim().ToLowerInvariant() ?? ""); command.Parameters.AddWithValue("$shop", shop?.Trim() ?? ""); var state = status?.Trim() ?? ""; command.Parameters.AddWithValue("$status", state); var q = query?.Trim() ?? ""; command.Parameters.AddWithValue("$query", q); command.Parameters.AddWithValue("$like", $"%{q}%"); using var reader = command.ExecuteReader(); var result = new List<OrderExceptionRecord>(); while (reader.Read()) result.Add(Read(reader)); return result;
    }
    public OrderExceptionRecord? Find(string id) => List().FirstOrDefault(x => x.Id == id);
    public void SetStatus(string id, string status) { if (string.IsNullOrWhiteSpace(status)) throw new ArgumentException("İstisna durumu zorunlu."); using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE OrderExceptions SET Status=$status,UpdatedUtc=$updated WHERE Id=$id"; command.Parameters.AddWithValue("$status", status.Trim()); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$id", id); if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("İstisna kaydı bulunamadı."); }
    public int Reconcile(IReadOnlyList<OrderSnapshot> orders, Catalog.CatalogStore catalog)
    {
        var count = 0; var products = catalog.Products();
        foreach (var order in orders)
        {
            foreach (var item in order.Items)
            {
                List<Catalog.CatalogProduct> matches = string.IsNullOrWhiteSpace(item.Sku) ? [] : products.Where(x => x.Sku.Equals(item.Sku, StringComparison.OrdinalIgnoreCase)).ToList();
                if (string.IsNullOrWhiteSpace(item.Sku) || matches.Count != 1) { Save(new() { Marketplace = order.Marketplace, ShopId = order.ShopId, OrderId = order.OrderId, Type = matches.Count == 0 ? "MissingSku" : "AmbiguousSku", EventKey = "item:" + item.Title + ":" + item.Sku, Severity = "Error", Message = string.IsNullOrWhiteSpace(item.Sku) ? $"{item.Title} satırında SKU eksik." : $"{item.Sku} birden fazla veya hiç ürünle eşleşiyor." }); count++; }
            }
            var raw = (order.RawStatus ?? "").Trim().ToLowerInvariant(); var returned = order.Shipments.Any(x => x.State == OrdersRules.States[6]) || raw.Contains("return", StringComparison.Ordinal) || raw.Contains("iade", StringComparison.Ordinal); var cancelled = raw.Contains("cancel", StringComparison.Ordinal) || raw.Contains("iptal", StringComparison.Ordinal);
            if (returned || cancelled)
            {
                var type = returned ? "Return" : "Cancel"; var hasReceipt = catalog.GetOrderStockStatus(order.Marketplace, order.ShopId, order.OrderId) is not null; Save(new() { Marketplace = order.Marketplace, ShopId = order.ShopId, OrderId = order.OrderId, Type = type, EventKey = type.ToLowerInvariant() + ":current", Severity = "Warning", Status = hasReceipt ? "PreviewReady" : "Pending", Message = hasReceipt ? "İptal/iade için stok geri koyma önizlemesi hazır; kullanıcı onayı bekleniyor." : "İptal/iade algılandı ancak daha önce uygulanmış stok hareketi bulunamadı." }); count++;
            }
        }
        return count;
    }
    static OrderExceptionRecord Read(SqliteDataReader reader) => new() { Id = reader.GetString(0), Marketplace = reader.GetString(1), ShopId = reader.GetString(2), OrderId = reader.GetString(3), Type = reader.GetString(4), EventKey = reader.GetString(5), Severity = reader.GetString(6), Message = reader.GetString(7), Status = reader.GetString(8), CreatedUtc = DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), UpdatedUtc = DateTime.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) };
    static string Limit(string value, int max) { value = (value ?? "").Trim(); return value.Length > max ? value[..max] : value; }
}
