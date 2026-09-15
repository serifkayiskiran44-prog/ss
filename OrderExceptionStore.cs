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

/// Bounded diagnostics only (id/marketplace/shop/order identity, a short reason,
/// detection time) - never Message or event payload content - for an
/// OrderExceptions row with an unparsable CreatedUtc/UpdatedUtc, an unrecognized
/// Severity, or an unrecognized Status. See CatalogStore's CorruptProductRow for
/// the same pattern.
public sealed record CorruptOrderExceptionRow(string Id, string Marketplace, string ShopId, string OrderId, string Reason, DateTime DetectedUtc);

public sealed class OrderExceptionStore
{
    /// The only severities the app itself ever assigns (Save/Reconcile) or orders
    /// by (List's CASE expression) - a closed set, not free text, so a typo or a
    /// fabricated value can never be "silently a valid priority". See #2661.
    public static readonly string[] Severities = ["Critical", "Error", "Warning"];
    /// The only statuses the app itself ever assigns (Reconcile's Pending/
    /// PreviewReady, or the panel's Resolved/Rejected decisions). See #2661.
    public static readonly string[] Statuses = ["Pending", "PreviewReady", "Resolved", "Rejected"];
    readonly string connectionString;
    public OrderExceptionStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "order-exceptions.db") }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS OrderExceptions(Id TEXT PRIMARY KEY,Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,OrderId TEXT NOT NULL,Type TEXT NOT NULL,EventKey TEXT NOT NULL,Severity TEXT NOT NULL,Message TEXT NOT NULL,Status TEXT NOT NULL,CreatedUtc TEXT NOT NULL,UpdatedUtc TEXT NOT NULL,UNIQUE(Marketplace,ShopId,OrderId,Type,EventKey))"; command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    public OrderExceptionRecord Save(OrderExceptionRecord record)
    {
        if (new[] { record.Marketplace, record.ShopId, record.OrderId, record.Type, record.EventKey, record.Message }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("İstisna kanal, mağaza, sipariş, tür, olay ve açıklama içermeli.");
        if (!Severities.Contains(record.Severity, StringComparer.Ordinal)) throw new ArgumentException($"Geçersiz öncelik: '{record.Severity}'.");
        if (!Statuses.Contains(record.Status, StringComparer.Ordinal)) throw new ArgumentException($"Geçersiz durum: '{record.Status}'.");
        record.Marketplace = Limit(record.Marketplace, 80).ToLowerInvariant(); record.ShopId = Limit(record.ShopId, 200); record.OrderId = Limit(record.OrderId, 200); record.Type = Limit(record.Type, 80); record.EventKey = Limit(record.EventKey, 240); record.Severity = Limit(record.Severity, 30); record.Status = Limit(record.Status, 40); record.Message = MarketplaceConnectionStore.Redact(Limit(record.Message, 2000)); record.UpdatedUtc = DateTime.UtcNow;
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO OrderExceptions(Id,Marketplace,ShopId,OrderId,Type,EventKey,Severity,Message,Status,CreatedUtc,UpdatedUtc) VALUES($id,$marketplace,$shop,$order,$type,$event,$severity,$message,$status,$created,$updated) ON CONFLICT(Marketplace,ShopId,OrderId,Type,EventKey) DO UPDATE SET Severity=excluded.Severity,Message=excluded.Message,Status=excluded.Status,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$id", record.Id); command.Parameters.AddWithValue("$marketplace", record.Marketplace); command.Parameters.AddWithValue("$shop", record.ShopId); command.Parameters.AddWithValue("$order", record.OrderId); command.Parameters.AddWithValue("$type", record.Type); command.Parameters.AddWithValue("$event", record.EventKey); command.Parameters.AddWithValue("$severity", record.Severity); command.Parameters.AddWithValue("$message", record.Message); command.Parameters.AddWithValue("$status", record.Status); command.Parameters.AddWithValue("$created", record.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$updated", record.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery(); return List(record.Marketplace, record.ShopId).Single(x => x.OrderId == record.OrderId && x.Type == record.Type && x.EventKey == record.EventKey);
    }
    /// A malformed CreatedUtc/UpdatedUtc must never crash the whole read - the row
    /// is excluded from the healthy result and reported only via
    /// CorruptExceptions(); detection re-runs from the row's own stored text every
    /// call, so it stays stable across a restart without a separate tracking table.
    public IReadOnlyList<OrderExceptionRecord> List(string? marketplace = null, string? shop = null, string? status = null, string? query = null)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Id,Marketplace,ShopId,OrderId,Type,EventKey,Severity,Message,Status,CreatedUtc,UpdatedUtc FROM OrderExceptions WHERE ($marketplace='' OR Marketplace=$marketplace) AND ($shop='' OR ShopId=$shop) AND ($status='' OR Status=$status) AND ($query='' OR OrderId LIKE $like OR Message LIKE $like OR Type LIKE $like) ORDER BY CASE Severity WHEN 'Critical' THEN 0 WHEN 'Error' THEN 1 ELSE 2 END,UpdatedUtc DESC"; command.Parameters.AddWithValue("$marketplace", marketplace?.Trim().ToLowerInvariant() ?? ""); command.Parameters.AddWithValue("$shop", shop?.Trim() ?? ""); var state = status?.Trim() ?? ""; command.Parameters.AddWithValue("$status", state); var q = query?.Trim() ?? ""; command.Parameters.AddWithValue("$query", q); command.Parameters.AddWithValue("$like", $"%{q}%"); using var reader = command.ExecuteReader(); var result = new List<OrderExceptionRecord>(); while (reader.Read()) if (TryRead(reader, out var record, out _)) result.Add(record!); return result;
    }
    /// Bounded diagnostics for every row whose CreatedUtc/UpdatedUtc failed to
    /// parse - never the raw Message or other business fields.
    public IReadOnlyList<CorruptOrderExceptionRow> CorruptExceptions()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Id,Marketplace,ShopId,OrderId,Type,EventKey,Severity,Message,Status,CreatedUtc,UpdatedUtc FROM OrderExceptions";
        using var reader = command.ExecuteReader(); var result = new List<CorruptOrderExceptionRow>(); while (reader.Read()) if (!TryRead(reader, out _, out var corrupt)) result.Add(corrupt!); return result;
    }
    public OrderExceptionRecord? Find(string id) => List().FirstOrDefault(x => x.Id == id);
    public void SetStatus(string id, string status) => SetStatus(id, null, status);
    /// expectedStatus, when given, is a compare-and-swap guard: the write only
    /// applies if the row's current persisted Status still equals it - so a
    /// decision made against a queue snapshot can never silently clobber a
    /// different decision (e.g. Reject) another concurrent action already
    /// committed. A target/expected value outside the closed Statuses set is
    /// rejected before any row is touched.
    public void SetStatus(string id, string? expectedStatus, string status)
    {
        if (!Statuses.Contains(status, StringComparer.Ordinal)) throw new ArgumentException($"Geçersiz durum: '{status}'.");
        if (expectedStatus is not null && !Statuses.Contains(expectedStatus, StringComparer.Ordinal)) throw new ArgumentException($"Geçersiz beklenen durum: '{expectedStatus}'.");
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE OrderExceptions SET Status=$status,UpdatedUtc=$updated WHERE Id=$id" + (expectedStatus is null ? "" : " AND Status=$expected");
        command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$id", id);
        if (expectedStatus is not null) command.Parameters.AddWithValue("$expected", expectedStatus);
        var affected = command.ExecuteNonQuery();
        if (affected == 1) return;
        using var find = connection.CreateCommand(); find.CommandText = "SELECT 1 FROM OrderExceptions WHERE Id=$id"; find.Parameters.AddWithValue("$id", id);
        if (find.ExecuteScalar() is null) throw new InvalidOperationException("İstisna kaydı bulunamadı.");
        throw new InvalidOperationException("İstisna durumu başka bir işlemde değişti; kuyruğu yenileyip tekrar deneyin.");
    }
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
    static bool TryRead(SqliteDataReader reader, out OrderExceptionRecord? record, out CorruptOrderExceptionRow? corrupt)
    {
        record = null; corrupt = null; var id = reader.GetString(0); var marketplace = reader.GetString(1); var shop = reader.GetString(2); var order = reader.GetString(3);
        if (!TryParseUtc(reader.GetString(9), out var created)) { corrupt = new(id, marketplace, shop, order, "Malformed CreatedUtc timestamp", DateTime.UtcNow); return false; }
        if (!TryParseUtc(reader.GetString(10), out var updated)) { corrupt = new(id, marketplace, shop, order, "Malformed UpdatedUtc timestamp", DateTime.UtcNow); return false; }
        var severity = reader.GetString(6);
        if (!Severities.Contains(severity, StringComparer.Ordinal)) { corrupt = new(id, marketplace, shop, order, "Unrecognized Severity value", DateTime.UtcNow); return false; }
        var status = reader.GetString(8);
        if (!Statuses.Contains(status, StringComparer.Ordinal)) { corrupt = new(id, marketplace, shop, order, "Unrecognized Status value", DateTime.UtcNow); return false; }
        record = new() { Id = id, Marketplace = marketplace, ShopId = shop, OrderId = order, Type = reader.GetString(4), EventKey = reader.GetString(5), Severity = severity, Message = reader.GetString(7), Status = status, CreatedUtc = created, UpdatedUtc = updated };
        return true;
    }
    /// Only ever a format/parse failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised by the surrounding command, not this parse.
    static bool TryParseUtc(string value, out DateTime result) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
    static string Limit(string value, int max) { value = (value ?? "").Trim(); return value.Length > max ? value[..max] : value; }
}
