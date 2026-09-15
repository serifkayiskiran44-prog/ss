using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record SavedUiView(string Module, string Name, string Payload, DateTime UpdatedUtc);
/// Bounded diagnostics only (module/name identity, a short reason, detection
/// time) - never the raw Payload - for a UiViews row with an unparsable
/// persisted UpdatedUtc. See CatalogStore's CorruptProductRow for the same
/// pattern.
public sealed record CorruptUiView(string Module, string Name, string Reason, DateTime DetectedUtc);

/// <summary>Stores non-sensitive, per-Windows-user UI preferences. Credentials never enter this store.</summary>
public sealed class UiPreferenceStore
{
    readonly string connectionString;
    public UiPreferenceStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "ui-preferences.db") }.ToString();
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS UiPreferences([Key] TEXT PRIMARY KEY,Value TEXT NOT NULL);CREATE TABLE IF NOT EXISTS UiViews(Module TEXT NOT NULL,Name TEXT NOT NULL,Payload TEXT NOT NULL,UpdatedUtc TEXT NOT NULL,PRIMARY KEY(Module,Name));";
        command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public string? Get(string key)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Value FROM UiPreferences WHERE [Key]=$key"; command.Parameters.AddWithValue("$key", key); return command.ExecuteScalar() as string;
    }
    public void Set(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 120) throw new ArgumentException("Tercih anahtarı geçersiz.", nameof(key));
        if (value.Length > 20000) throw new ArgumentException("Tercih değeri çok uzun.", nameof(value));
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO UiPreferences([Key],Value) VALUES($key,$value) ON CONFLICT([Key]) DO UPDATE SET Value=excluded.Value"; command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery();
    }
    public void SaveView(string module, string name, string payload)
    {
        module = Normalize(module, 80); name = Normalize(name, 100);
        if (payload.Length > 20000) throw new ArgumentException("Görünüm verisi çok uzun.", nameof(payload));
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO UiViews(Module,Name,Payload,UpdatedUtc) VALUES($module,$name,$payload,$utc) ON CONFLICT(Module,Name) DO UPDATE SET Payload=excluded.Payload,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$module", module); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery();
    }
    /// A malformed persisted UpdatedUtc must never crash the whole saved-view
    /// list - the row is excluded from the healthy result and reported only via
    /// CorruptViews(); detection re-derives from the row's own stored text every
    /// call, so it stays stable across a restart without a separate tracking
    /// table (never a silent-crash-loop, and never a fabricated date).
    public IReadOnlyList<SavedUiView> ListViews(string module)
    {
        module = Normalize(module, 80); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Module,Name,Payload,UpdatedUtc FROM UiViews WHERE Module=$module ORDER BY Name"; command.Parameters.AddWithValue("$module", module); using var reader = command.ExecuteReader(); var rows = new List<SavedUiView>(); while (reader.Read()) if (TryReadView(reader, out var row, out _)) rows.Add(row!); return rows;
    }
    /// Bounded diagnostics for every row whose UpdatedUtc failed to parse -
    /// never the raw Payload.
    public IReadOnlyList<CorruptUiView> CorruptViews(string module)
    {
        module = Normalize(module, 80); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Module,Name,Payload,UpdatedUtc FROM UiViews WHERE Module=$module ORDER BY Name"; command.Parameters.AddWithValue("$module", module); using var reader = command.ExecuteReader(); var rows = new List<CorruptUiView>(); while (reader.Read()) if (!TryReadView(reader, out _, out var corrupt)) rows.Add(corrupt!); return rows;
    }
    public void DeleteView(string module, string name)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "DELETE FROM UiViews WHERE Module=$module AND Name=$name"; command.Parameters.AddWithValue("$module", Normalize(module, 80)); command.Parameters.AddWithValue("$name", Normalize(name, 100)); command.ExecuteNonQuery();
    }
    /// Explicit, transactional recovery action: re-validates the row is still
    /// corrupt at delete time, so a view fixed/re-saved since it was last listed
    /// is never silently discarded, and a healthy view can never be removed
    /// this way.
    public void DeleteCorruptView(string module, string name)
    {
        module = Normalize(module, 80); name = Normalize(name, 100);
        using var c = Open(); using var tx = c.BeginTransaction();
        using var find = c.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT Module,Name,Payload,UpdatedUtc FROM UiViews WHERE Module=$module AND Name=$name"; find.Parameters.AddWithValue("$module", module); find.Parameters.AddWithValue("$name", name);
        using (var reader = find.ExecuteReader()) { if (!reader.Read()) throw new InvalidOperationException("Görünüm bulunamadı."); if (TryReadView(reader, out _, out _)) throw new InvalidOperationException("Bu görünüm bozuk değil; normal silme akışını kullanın."); }
        using var del = c.CreateCommand(); del.Transaction = tx; del.CommandText = "DELETE FROM UiViews WHERE Module=$module AND Name=$name"; del.Parameters.AddWithValue("$module", module); del.Parameters.AddWithValue("$name", name); del.ExecuteNonQuery();
        tx.Commit();
    }
    static bool TryReadView(SqliteDataReader reader, out SavedUiView? row, out CorruptUiView? corrupt)
    {
        row = null; corrupt = null; var module = reader.GetString(0); var name = reader.GetString(1);
        if (!DateTime.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updated)) { corrupt = new(module, name, "Malformed UpdatedUtc timestamp", DateTime.UtcNow); return false; }
        row = new(module, name, reader.GetString(2), updated); return true;
    }
    static string Normalize(string value, int max)
    {
        var clean = value.Trim(); if (clean.Length < 1 || clean.Length > max || clean.Any(char.IsControl)) throw new ArgumentException("Tercih adı geçersiz."); return clean;
    }
}
