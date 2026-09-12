using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record SavedCatalogFilter(string Name, CatalogFilter Filter);

public sealed class CatalogFilterStore
{
    readonly string connectionString;
    public CatalogFilterStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS CatalogFilterViews(Name TEXT PRIMARY KEY, Json TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = SqliteConnectionPolicy.Open(connectionString); return c; }
    public IReadOnlyList<SavedCatalogFilter> List()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Name,Json FROM CatalogFilterViews ORDER BY Name";
        using var reader = command.ExecuteReader(); var rows = new List<SavedCatalogFilter>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), JsonSerializer.Deserialize<CatalogFilter>(reader.GetString(1)) ?? new()));
        return rows;
    }
    public void Save(string name, CatalogFilter filter)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl)) throw new ArgumentException("Filtre adı 1–100 karakter olmalı.", nameof(name));
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO CatalogFilterViews(Name,Json) VALUES($name,$json) ON CONFLICT(Name) DO UPDATE SET Json=excluded.Json";
        command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(filter)); command.ExecuteNonQuery();
    }
    public void Delete(string name)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "DELETE FROM CatalogFilterViews WHERE Name=$name"; command.Parameters.AddWithValue("$name", name); command.ExecuteNonQuery();
    }
}
