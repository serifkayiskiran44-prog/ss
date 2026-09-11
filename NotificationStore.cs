using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

public sealed record LocalNotification(string Id, string Fingerprint, string Severity, string Channel, string ShopId, string Title, string Detail, bool Acknowledged, DateTime AtUtc);

public sealed class NotificationStore
{
    readonly string connectionString;
    public NotificationStore(string directory)
    {
        Directory.CreateDirectory(directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "notifications.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS Notifications(Id TEXT PRIMARY KEY,Fingerprint TEXT NOT NULL UNIQUE,Severity TEXT NOT NULL,Channel TEXT NOT NULL,ShopId TEXT NOT NULL,Title TEXT NOT NULL,Detail TEXT NOT NULL,Acknowledged INTEGER NOT NULL,AtUtc TEXT NOT NULL)"; cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public LocalNotification Add(string fingerprint, string severity, string channel, string shopId, string title, string detail)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(severity)) throw new ArgumentException("Bildirim fingerprint ve severity gerekli.");
        var safe = AuditStore.Sanitize(detail); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT OR IGNORE INTO Notifications VALUES($id,$fp,$sev,$ch,$shop,$title,$detail,0,$at)"; var id = Guid.NewGuid().ToString("N"); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$fp", fingerprint.Trim()); cmd.Parameters.AddWithValue("$sev", severity.Trim().ToUpperInvariant()); cmd.Parameters.AddWithValue("$ch", channel?.Trim() ?? ""); cmd.Parameters.AddWithValue("$shop", shopId?.Trim() ?? ""); cmd.Parameters.AddWithValue("$title", AuditStore.Sanitize(title)); cmd.Parameters.AddWithValue("$detail", safe); cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery(); return List().First(x => x.Fingerprint == fingerprint.Trim());
    }
    public void Acknowledge(string id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE Notifications SET Acknowledged=1 WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    public IReadOnlyList<LocalNotification> List() { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Id,Fingerprint,Severity,Channel,ShopId,Title,Detail,Acknowledged,AtUtc FROM Notifications ORDER BY AtUtc DESC"; using var r = cmd.ExecuteReader(); var result = new List<LocalNotification>(); while (r.Read()) result.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt32(7)==1,DateTime.Parse(r.GetString(8)))); return result; }
}
