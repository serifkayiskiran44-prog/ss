using Microsoft.Data.Sqlite;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop;

/// <summary>One alert in the local ledger: unique by fingerprint; open until a sync stops seeing it; acknowledged by a person; reopened when it comes back.</summary>
public sealed record LocalNotification(string Id, string Fingerprint, string Severity, string Channel, string ShopId, string Title, string Detail, bool Acknowledged, DateTime AtUtc, string Source = "", string State = "Open", int Occurrences = 1, DateTime? ResolvedUtc = null, int Reopened = 0)
{
    public bool IsOpen => string.Equals(State, "Open", StringComparison.OrdinalIgnoreCase);
    public string StoreKey => Channel.Length == 0 && ShopId.Length == 0 ? "" : DashboardStoreFilter.KeyFor(Channel, ShopId);
}

/// <summary>What a live finding hands the ledger: the identity (fingerprint), the words (sanitized on write) and the scope.</summary>
public sealed record AlertInput(string Fingerprint, string Severity, string Channel, string ShopId, string Title, string Detail, string Source = "");

public sealed record NotificationSyncResult(int Added, int Repeated, int Reopened, int Resolved);

/// <summary>
/// The persisted alert ledger (M145, #851). A fingerprint is one alert however often it is seen: a repeat counts,
/// a person's acknowledgement survives repeats, an alert a sync no longer sees is resolved, and a resolved one
/// that comes back is reopened with its acknowledgement cleared. Titles and details are sanitized on the way in,
/// so a token, an e-mail or a profile path never reaches the ledger or any screen that reads it.
/// </summary>
public sealed class NotificationStore
{
    readonly string connectionString;
    public NotificationStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "notifications.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS Notifications(Id TEXT PRIMARY KEY,Fingerprint TEXT NOT NULL UNIQUE,Severity TEXT NOT NULL,Channel TEXT NOT NULL,ShopId TEXT NOT NULL,Title TEXT NOT NULL,Detail TEXT NOT NULL,Acknowledged INTEGER NOT NULL,AtUtc TEXT NOT NULL)"; cmd.ExecuteNonQuery();
        // #851: additive columns; an older ledger gains them with their defaults.
        foreach (var column in new[] { "Source TEXT NOT NULL DEFAULT ''", "State TEXT NOT NULL DEFAULT 'Open'", "Occurrences INTEGER NOT NULL DEFAULT 1", "ResolvedUtc TEXT NULL", "Reopened INTEGER NOT NULL DEFAULT 0" })
        {
            try { using var alter = c.CreateCommand(); alter.CommandText = "ALTER TABLE Notifications ADD COLUMN " + column; alter.ExecuteNonQuery(); }
            catch (SqliteException) { /* the column exists */ }
        }
    }
    SqliteConnection Open() { var c = SqliteConnectionPolicy.Open(connectionString); return c; }

    /// <summary>The identity of an alert: severity, source, store and title, case-folded -- never the detail, which changes with every count.</summary>
    public static string Fingerprint(string severity, string source, string storeKey, string title)
    {
        var text = string.Join("|", (severity ?? "").Trim().ToUpperInvariant(), (source ?? "").Trim().ToLowerInvariant(), (storeKey ?? "").Trim().ToLowerInvariant(), (title ?? "").Trim().ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    public LocalNotification Add(string fingerprint, string severity, string channel, string shopId, string title, string detail, string source = "")
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || string.IsNullOrWhiteSpace(severity)) throw new ArgumentException("Bildirim fingerprint ve severity gerekli.");
        Sync(new[] { new AlertInput(fingerprint, severity, channel ?? "", shopId ?? "", title, detail, source) }, DateTime.UtcNow, resolveMissing: false);
        return List().First(x => x.Fingerprint == fingerprint.Trim());
    }

    public void Acknowledge(string id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE Notifications SET Acknowledged=1 WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    public void Unacknowledge(string id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE Notifications SET Acknowledged=0 WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }

    /// <summary>Reconciles the ledger with what is live now: new fingerprints are added, seen ones repeat, resolved ones that are back reopen (acknowledgement cleared), and open ones no longer live resolve (when <paramref name="resolveMissing"/>).</summary>
    public NotificationSyncResult Sync(IEnumerable<AlertInput> live, DateTime nowUtc, bool resolveMissing = true)
    {
        ArgumentNullException.ThrowIfNull(live);
        var inputs = new List<AlertInput>(); var repeatedInBatch = 0; var seen = new HashSet<string>(StringComparer.Ordinal); var extra = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var input in live) { if (input is null || string.IsNullOrWhiteSpace(input.Fingerprint) || string.IsNullOrWhiteSpace(input.Severity)) continue; var key = input.Fingerprint.Trim(); if (seen.Add(key)) inputs.Add(input); else { repeatedInBatch++; extra[key] = extra.GetValueOrDefault(key) + 1; } }
        var added = 0; var repeated = repeatedInBatch; var reopened = 0; var resolved = 0; var at = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        using var c = Open(); using var tx = c.BeginTransaction();
        var existing = new Dictionary<string, (string Id, string State)>(StringComparer.Ordinal);
        using (var read = c.CreateCommand()) { read.Transaction = tx; read.CommandText = "SELECT Fingerprint,Id,State FROM Notifications"; using var r = read.ExecuteReader(); while (r.Read()) existing[r.GetString(0)] = (r.GetString(1), r.GetString(2)); }
        foreach (var input in inputs)
        {
            var fp = input.Fingerprint.Trim(); var severity = input.Severity.Trim().ToUpperInvariant(); var title = AuditStore.Sanitize(input.Title); var detail = AuditStore.Sanitize(input.Detail); var source = (input.Source ?? "").Trim();
            var sightings = 1 + extra.GetValueOrDefault(fp); // a duplicate inside the batch is one more sighting of the same alert
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            if (!existing.TryGetValue(fp, out var row))
            {
                cmd.CommandText = "INSERT INTO Notifications(Id,Fingerprint,Severity,Channel,ShopId,Title,Detail,Acknowledged,AtUtc,Source,State,Occurrences,ResolvedUtc,Reopened) VALUES($id,$fp,$sev,$ch,$shop,$title,$detail,0,$at,$src,'Open',$n,NULL,0)";
                cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); cmd.Parameters.AddWithValue("$ch", (input.Channel ?? "").Trim()); cmd.Parameters.AddWithValue("$shop", (input.ShopId ?? "").Trim()); added++;
            }
            else if (!string.Equals(row.State, "Open", StringComparison.OrdinalIgnoreCase))
            {
                cmd.CommandText = "UPDATE Notifications SET State='Open',Acknowledged=0,Reopened=Reopened+1,Occurrences=Occurrences+$n,AtUtc=$at,ResolvedUtc=NULL,Severity=$sev,Title=$title,Detail=$detail,Source=$src WHERE Fingerprint=$fp"; reopened++;
            }
            else
            {
                cmd.CommandText = "UPDATE Notifications SET Occurrences=Occurrences+$n,AtUtc=$at,Severity=$sev,Title=$title,Detail=$detail,Source=$src WHERE Fingerprint=$fp"; repeated++;
            }
            cmd.Parameters.AddWithValue("$n", sightings); cmd.Parameters.AddWithValue("$fp", fp); cmd.Parameters.AddWithValue("$sev", severity); cmd.Parameters.AddWithValue("$title", title); cmd.Parameters.AddWithValue("$detail", detail); cmd.Parameters.AddWithValue("$at", at); cmd.Parameters.AddWithValue("$src", source);
            cmd.ExecuteNonQuery();
        }
        if (resolveMissing)
        {
            foreach (var (fp, row) in existing)
            {
                if (seen.Contains(fp) || !string.Equals(row.State, "Open", StringComparison.OrdinalIgnoreCase)) continue;
                using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "UPDATE Notifications SET State='Resolved',ResolvedUtc=$at WHERE Fingerprint=$fp"; cmd.Parameters.AddWithValue("$at", at); cmd.Parameters.AddWithValue("$fp", fp); cmd.ExecuteNonQuery(); resolved++;
            }
        }
        tx.Commit();
        return new(added, repeated, reopened, resolved);
    }

    public IReadOnlyList<LocalNotification> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Id,Fingerprint,Severity,Channel,ShopId,Title,Detail,Acknowledged,AtUtc,Source,State,Occurrences,ResolvedUtc,Reopened FROM Notifications ORDER BY AtUtc DESC";
        using var r = cmd.ExecuteReader(); var result = new List<LocalNotification>();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt32(7) == 1, Parse(r.GetString(8)), r.GetString(9), r.GetString(10), r.GetInt32(11), r.IsDBNull(12) ? null : Parse(r.GetString(12)), r.GetInt32(13)));
        return result;
    }

    static DateTime Parse(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
