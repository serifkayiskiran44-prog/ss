using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

public enum MessageStatus { Unread, Read, Draft, Failed }

public sealed class MessageRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Marketplace { get; set; } = "Yerel";
    public string ShopId { get; set; } = "Mağazam";
    public string ExternalId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Direction { get; set; } = "Inbound";
    public MessageStatus Status { get; set; } = MessageStatus.Unread;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string LastError { get; set; } = "";
}

public sealed record MessageTemplate(string Id, string Name, string Body, DateTime UpdatedUtc);
/// Bounded diagnostics only (id/marketplace/shop identity, a short reason,
/// detection time) - never Customer, Subject, Body, OrderId, or ProductId - for a
/// Messages row with an unparsable CreatedUtc/UpdatedUtc. See CatalogStore's
/// CorruptProductRow for the same pattern.
public sealed record CorruptMessageRow(string Id, string Marketplace, string ShopId, string Reason, DateTime DetectedUtc);
/// Same idea for MessageTemplates - never the raw Name/Body.
public sealed record CorruptMessageTemplateRow(string Id, string Reason, DateTime DetectedUtc);
/// Raised by Get(id) when the row exists but is corrupt - kept distinct from
/// returning null (which still means "no such message"), so a caller can never
/// mistake "needs repair" for "not found".
public sealed class MessageCorruptException : Exception
{
    public string MessageId { get; }
    public MessageCorruptException(string messageId, string reason) : base($"Mesaj kaydı bozuk (REVIEW_REQUIRED): {reason}") => MessageId = messageId;
}

public sealed class MessageStore
{
    readonly string connectionString;
    public MessageStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "messages.db") }.ToString();
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS Messages(Id TEXT PRIMARY KEY,Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,ExternalId TEXT NOT NULL,OrderId TEXT NOT NULL,ProductId TEXT NOT NULL,Customer TEXT NOT NULL,Subject TEXT NOT NULL,Body TEXT NOT NULL,Direction TEXT NOT NULL,Status TEXT NOT NULL,CreatedUtc TEXT NOT NULL,UpdatedUtc TEXT NOT NULL,LastError TEXT NOT NULL);CREATE UNIQUE INDEX IF NOT EXISTS IX_Messages_External ON Messages(Marketplace,ShopId,ExternalId) WHERE ExternalId <> '';CREATE TABLE IF NOT EXISTS MessageTemplates(Id TEXT PRIMARY KEY,Name TEXT UNIQUE NOT NULL,Body TEXT NOT NULL,UpdatedUtc TEXT NOT NULL);"; command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public MessageRecord Upsert(MessageRecord message)
    {
        Validate(message); message.Marketplace = message.Marketplace.Trim().ToLowerInvariant(); message.ShopId = message.ShopId.Trim(); message.ExternalId = message.ExternalId.Trim(); message.OrderId = message.OrderId.Trim(); message.ProductId = message.ProductId.Trim(); message.Customer = Limit(message.Customer, 240); message.Subject = Limit(message.Subject, 300); message.Body = Limit(message.Body, 10000); message.Direction = Limit(message.Direction, 30); message.LastError = AuditStore.Sanitize(Limit(message.LastError, 1000)); message.UpdatedUtc = DateTime.UtcNow;
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO Messages VALUES($id,$marketplace,$shop,$external,$order,$product,$customer,$subject,$body,$direction,$status,$created,$updated,$error) ON CONFLICT(Id) DO UPDATE SET Marketplace=excluded.Marketplace,ShopId=excluded.ShopId,ExternalId=excluded.ExternalId,OrderId=excluded.OrderId,ProductId=excluded.ProductId,Customer=excluded.Customer,Subject=excluded.Subject,Body=excluded.Body,Direction=excluded.Direction,Status=excluded.Status,UpdatedUtc=excluded.UpdatedUtc,LastError=excluded.LastError"; command.Parameters.AddWithValue("$id", message.Id); command.Parameters.AddWithValue("$marketplace", message.Marketplace); command.Parameters.AddWithValue("$shop", message.ShopId); command.Parameters.AddWithValue("$external", message.ExternalId); command.Parameters.AddWithValue("$order", message.OrderId); command.Parameters.AddWithValue("$product", message.ProductId); command.Parameters.AddWithValue("$customer", message.Customer); command.Parameters.AddWithValue("$subject", message.Subject); command.Parameters.AddWithValue("$body", message.Body); command.Parameters.AddWithValue("$direction", message.Direction); command.Parameters.AddWithValue("$status", message.Status.ToString()); command.Parameters.AddWithValue("$created", message.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$updated", message.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$error", message.LastError); command.ExecuteNonQuery(); return Get(message.Id)!;
    }
    /// A corrupt target row throws MessageCorruptException rather than returning
    /// null, so "needs repair" is never confused with "not found".
    public MessageRecord? Get(string id)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT * FROM Messages WHERE Id=$id"; command.Parameters.AddWithValue("$id", id); using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        if (!TryRead(r, out var message, out var corrupt)) throw new MessageCorruptException(id, corrupt!.Reason);
        return message;
    }
    /// A malformed CreatedUtc/UpdatedUtc must never crash the whole read - the row
    /// is excluded from the healthy result and reported only via
    /// CorruptMessages(); detection re-runs from the row's own stored text every
    /// call, so it stays stable across a restart without a separate tracking table.
    public IReadOnlyList<MessageRecord> List(string? marketplace = null, string? shop = null, MessageStatus? status = null, string? query = null)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT * FROM Messages WHERE ($marketplace='' OR Marketplace=$marketplace) AND ($shop='' OR ShopId=$shop) AND ($status='' OR Status=$status) AND ($query='' OR Marketplace LIKE $like OR ShopId LIKE $like OR ExternalId LIKE $like OR OrderId LIKE $like OR ProductId LIKE $like OR Customer LIKE $like OR Subject LIKE $like OR Body LIKE $like) ORDER BY UpdatedUtc DESC"; command.Parameters.AddWithValue("$marketplace", marketplace?.Trim().ToLowerInvariant() ?? ""); command.Parameters.AddWithValue("$shop", shop?.Trim() ?? ""); command.Parameters.AddWithValue("$status", status?.ToString() ?? ""); var q = query?.Trim() ?? ""; command.Parameters.AddWithValue("$query", q); command.Parameters.AddWithValue("$like", $"%{q}%"); using var r = command.ExecuteReader(); var rows = new List<MessageRecord>(); while (r.Read()) if (TryRead(r, out var message, out _)) rows.Add(message!); return rows;
    }
    /// Bounded diagnostics for every row whose CreatedUtc/UpdatedUtc failed to
    /// parse - never the raw Customer/Subject/Body/OrderId/ProductId.
    public IReadOnlyList<CorruptMessageRow> CorruptMessages()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT * FROM Messages"; using var r = command.ExecuteReader(); var rows = new List<CorruptMessageRow>(); while (r.Read()) if (!TryRead(r, out _, out var corrupt)) rows.Add(corrupt!); return rows;
    }
    /// Explicit, transactional recovery action: re-validates the row is still
    /// corrupt at delete time, so a row fixed/replaced since it was last listed is
    /// never silently discarded, and a healthy row can never be removed this way.
    public void DeleteCorruptMessage(string id)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var find = c.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT * FROM Messages WHERE Id=$id"; find.Parameters.AddWithValue("$id", id);
        using (var r = find.ExecuteReader()) { if (!r.Read()) throw new InvalidOperationException("Mesaj bulunamadı."); if (TryRead(r, out _, out _)) throw new InvalidOperationException("Bu mesaj bozuk değil; normal silme akışını kullanın."); }
        using var del = c.CreateCommand(); del.Transaction = tx; del.CommandText = "DELETE FROM Messages WHERE Id=$id"; del.Parameters.AddWithValue("$id", id); del.ExecuteNonQuery(); tx.Commit();
    }
    public void SetStatus(string id, MessageStatus status)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "UPDATE Messages SET Status=$status,UpdatedUtc=$updated WHERE Id=$id"; command.Parameters.AddWithValue("$status", status.ToString()); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }
    /// Same row-level isolation as Messages: a malformed template UpdatedUtc is
    /// excluded here and reported via CorruptMessageTemplates() instead of
    /// crashing the whole template picker.
    public IReadOnlyList<MessageTemplate> Templates()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Id,Name,Body,UpdatedUtc FROM MessageTemplates ORDER BY Name"; using var r = command.ExecuteReader(); var rows = new List<MessageTemplate>();
        while (r.Read()) { var id = r.GetString(0); if (TryParseUtc(r.GetString(3), out var updated)) rows.Add(new(id, r.GetString(1), r.GetString(2), updated)); }
        return rows;
    }
    public IReadOnlyList<CorruptMessageTemplateRow> CorruptMessageTemplates()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Id,Name,Body,UpdatedUtc FROM MessageTemplates"; using var r = command.ExecuteReader(); var rows = new List<CorruptMessageTemplateRow>();
        while (r.Read()) { var id = r.GetString(0); if (!TryParseUtc(r.GetString(3), out _)) rows.Add(new(id, "Malformed UpdatedUtc timestamp", DateTime.UtcNow)); }
        return rows;
    }
    public void SaveTemplate(string name, string body, string? id = null)
    {
        name = Limit(name.Trim(), 120); body = Limit(body, 10000); if (name.Length == 0 || body.Length == 0) throw new ArgumentException("Şablon adı ve metni zorunlu."); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO MessageTemplates VALUES($id,$name,$body,$updated) ON CONFLICT(Name) DO UPDATE SET Body=excluded.Body,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$id", id ?? Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$body", body); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery();
    }
    public void DeleteTemplate(string name)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "DELETE FROM MessageTemplates WHERE Name=$name"; command.Parameters.AddWithValue("$name", name); command.ExecuteNonQuery();
    }
    static bool TryRead(SqliteDataReader r, out MessageRecord? message, out CorruptMessageRow? corrupt)
    {
        message = null; corrupt = null; var id = r.GetString(0); var marketplace = r.GetString(1); var shop = r.GetString(2);
        if (!TryParseUtc(r.GetString(11), out var created)) { corrupt = new(id, marketplace, shop, "Malformed CreatedUtc timestamp", DateTime.UtcNow); return false; }
        if (!TryParseUtc(r.GetString(12), out var updated)) { corrupt = new(id, marketplace, shop, "Malformed UpdatedUtc timestamp", DateTime.UtcNow); return false; }
        message = new() { Id = id, Marketplace = marketplace, ShopId = shop, ExternalId = r.GetString(3), OrderId = r.GetString(4), ProductId = r.GetString(5), Customer = r.GetString(6), Subject = r.GetString(7), Body = r.GetString(8), Direction = r.GetString(9), Status = Enum.TryParse<MessageStatus>(r.GetString(10), out var status) ? status : MessageStatus.Unread, CreatedUtc = created, UpdatedUtc = updated, LastError = r.GetString(13) };
        return true;
    }
    /// Only ever a format/parse failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised by the surrounding command, not this parse.
    static bool TryParseUtc(string value, out DateTime result) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
    static void Validate(MessageRecord message) { if (new[] { message.Marketplace, message.ShopId, message.Subject, message.Body }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Mesaj kanalı, mağaza, konu ve metin içermeli."); }
    static string Limit(string value, int max) => value.Length > max ? value[..max] : value;
}

public static class MessageCapabilityService
{
    public static string Describe(string marketplace) => marketplace.Equals("Yerel", StringComparison.OrdinalIgnoreCase) ? "Yerel yardımcı kayıt — API yok" : "NOT_SUPPORTED/LIVE_API_BLOCKED: doğrulanmış mesaj API capability'si yok; HTTP isteği oluşturulmaz.";
    public static bool CanRead(string marketplace) => false;
    public static bool CanWrite(string marketplace) => false;
}
