using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed class ExcelImportProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Yeni Excel profili";
    public string CultureName { get; set; } = CultureInfo.CurrentCulture.Name;
    public Dictionary<string, string> ColumnMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> HeaderAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> VisibleFields { get; set; } = [];
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ExcelProfileStore
{
    readonly string connectionString;
    public ExcelProfileStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "excel-profiles.db") }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS ExcelProfiles(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Json TEXT NOT NULL,UpdatedUtc TEXT NOT NULL)"; command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var connection = SqliteConnectionPolicy.Open(connectionString); return connection; }
    public void Save(ExcelImportProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Trim().Length > 120) throw new InvalidOperationException("Excel profil adı 1-120 karakter olmalı.");
        _ = Culture(profile.CultureName);
        profile.Name = profile.Name.Trim(); profile.UpdatedUtc = DateTime.UtcNow;
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO ExcelProfiles(Id,Name,Json,UpdatedUtc) VALUES($id,$name,$json,$updated) ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,Json=excluded.Json,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$id", profile.Id); command.Parameters.AddWithValue("$name", profile.Name); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile)); command.Parameters.AddWithValue("$updated", profile.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery();
    }
    public IReadOnlyList<ExcelImportProfile> List()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Json FROM ExcelProfiles ORDER BY Name"; using var reader = command.ExecuteReader(); var result = new List<ExcelImportProfile>(); while (reader.Read()) { try { var profile = JsonSerializer.Deserialize<ExcelImportProfile>(reader.GetString(0)); if (profile is not null) result.Add(profile); } catch (JsonException) { } } return result;
    }
    public ExcelImportProfile? Find(string id) => List().FirstOrDefault(x => x.Id == id);
    public void Delete(string id) { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM ExcelProfiles WHERE Id=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery(); }
    public static CultureInfo Culture(string? name) { try { return string.IsNullOrWhiteSpace(name) ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(name); } catch (CultureNotFoundException) { throw new InvalidOperationException("Excel profilinin sayı/tarih kültürü geçersiz."); } }
}
