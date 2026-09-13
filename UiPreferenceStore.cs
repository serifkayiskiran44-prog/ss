using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record SavedUiView(string Module, string Name, string Payload, DateTime UpdatedUtc);

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
    SqliteConnection Open() { var c = SqliteConnectionPolicy.Open(connectionString); return c; }
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
    /// <summary>Every preference key, ordered.</summary>
    public IReadOnlyList<string> Keys()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT [Key] FROM UiPreferences ORDER BY [Key]"; using var reader = command.ExecuteReader(); var keys = new List<string>(); while (reader.Read()) keys.Add(reader.GetString(0)); return keys;
    }
    /// <summary>Removes every preference record and returns how many went; the saved views stay.</summary>
    public int Clear()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "DELETE FROM UiPreferences"; return command.ExecuteNonQuery();
    }
    public void SaveView(string module, string name, string payload)
    {
        module = Normalize(module, 80); name = Normalize(name, 100);
        if (payload.Length > 20000) throw new ArgumentException("Görünüm verisi çok uzun.", nameof(payload));
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO UiViews(Module,Name,Payload,UpdatedUtc) VALUES($module,$name,$payload,$utc) ON CONFLICT(Module,Name) DO UPDATE SET Payload=excluded.Payload,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$module", module); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery();
    }
    public IReadOnlyList<SavedUiView> ListViews(string module)
    {
        module = Normalize(module, 80); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Module,Name,Payload,UpdatedUtc FROM UiViews WHERE Module=$module ORDER BY Name"; command.Parameters.AddWithValue("$module", module); using var reader = command.ExecuteReader(); var rows = new List<SavedUiView>(); while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))); return rows;
    }
    public void DeleteView(string module, string name)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "DELETE FROM UiViews WHERE Module=$module AND Name=$name"; command.Parameters.AddWithValue("$module", Normalize(module, 80)); command.Parameters.AddWithValue("$name", Normalize(name, 100)); command.ExecuteNonQuery();
    }
    static string Normalize(string value, int max)
    {
        var clean = value.Trim(); if (clean.Length < 1 || clean.Length > max || clean.Any(char.IsControl)) throw new ArgumentException("Tercih adı geçersiz."); return clean;
    }
}
