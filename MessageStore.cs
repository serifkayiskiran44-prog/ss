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
    public MessageRecord? Get(string id)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT * FROM Messages WHERE Id=$id"; command.Parameters.AddWithValue("$id", id); using var r = command.ExecuteReader(); return r.Read() ? Read(r) : null;
    }
    public IReadOnlyList<MessageRecord> List(string? marketplace = null, string? shop = null, MessageStatus? status = null, string? query = null)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT * FROM Messages WHERE ($marketplace='' OR Marketplace=$marketplace) AND ($shop='' OR ShopId=$shop) AND ($status='' OR Status=$status) AND ($query='' OR Marketplace LIKE $like OR ShopId LIKE $like OR ExternalId LIKE $like OR OrderId LIKE $like OR ProductId LIKE $like OR Customer LIKE $like OR Subject LIKE $like OR Body LIKE $like) ORDER BY UpdatedUtc DESC"; command.Parameters.AddWithValue("$marketplace", marketplace?.Trim().ToLowerInvariant() ?? ""); command.Parameters.AddWithValue("$shop", shop?.Trim() ?? ""); command.Parameters.AddWithValue("$status", status?.ToString() ?? ""); var q = query?.Trim() ?? ""; command.Parameters.AddWithValue("$query", q); command.Parameters.AddWithValue("$like", $"%{q}%"); using var r = command.ExecuteReader(); var rows = new List<MessageRecord>(); while (r.Read()) rows.Add(Read(r)); return rows;
    }
    public void SetStatus(string id, MessageStatus status)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "UPDATE Messages SET Status=$status,UpdatedUtc=$updated WHERE Id=$id"; command.Parameters.AddWithValue("$status", status.ToString()); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }
    public IReadOnlyList<MessageTemplate> Templates()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Id,Name,Body,UpdatedUtc FROM MessageTemplates ORDER BY Name"; using var r = command.ExecuteReader(); var rows = new List<MessageTemplate>(); while (r.Read()) rows.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))); return rows;
    }
    public void SaveTemplate(string name, string body, string? id = null)
    {
        name = Limit(name.Trim(), 120); body = Limit(body, 10000); if (name.Length == 0 || body.Length == 0) throw new ArgumentException("Şablon adı ve metni zorunlu."); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO MessageTemplates VALUES($id,$name,$body,$updated) ON CONFLICT(Name) DO UPDATE SET Body=excluded.Body,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$id", id ?? Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$body", body); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery();
    }
    public void DeleteTemplate(string name)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "DELETE FROM MessageTemplates WHERE Name=$name"; command.Parameters.AddWithValue("$name", name); command.ExecuteNonQuery();
    }
    static MessageRecord Read(SqliteDataReader r) => new() { Id = r.GetString(0), Marketplace = r.GetString(1), ShopId = r.GetString(2), ExternalId = r.GetString(3), OrderId = r.GetString(4), ProductId = r.GetString(5), Customer = r.GetString(6), Subject = r.GetString(7), Body = r.GetString(8), Direction = r.GetString(9), Status = Enum.TryParse<MessageStatus>(r.GetString(10), out var status) ? status : MessageStatus.Unread, CreatedUtc = DateTime.Parse(r.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), UpdatedUtc = DateTime.Parse(r.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), LastError = r.GetString(13) };
    static void Validate(MessageRecord message) { if (new[] { message.Marketplace, message.ShopId, message.Subject, message.Body }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Mesaj kanalı, mağaza, konu ve metin içermeli."); }
    static string Limit(string value, int max) => value.Length > max ? value[..max] : value;
}

public static class MessageCapabilityService
{
    public static string Describe(string marketplace) => marketplace.Equals("Yerel", StringComparison.OrdinalIgnoreCase) ? "Yerel yardımcı kayıt — API yok" : "NOT_SUPPORTED/LIVE_API_BLOCKED: doğrulanmış mesaj API capability'si yok; HTTP isteği oluşturulmaz.";
    public static bool CanRead(string marketplace) => false;
    public static bool CanWrite(string marketplace) => false;
}
