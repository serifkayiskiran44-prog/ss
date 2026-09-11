using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

public sealed record ProductRow(int Id, string ProductCode, string Barcode, string Name, int Stock, decimal Price, string Currency, string Brand, string Category, string Status);
public sealed record LogRow(string CreatedAt, string Area, string Operation, string Result);
public sealed record SqlResult(bool Allowed, string Message, DataTable Table);

public sealed class HubData
{
    private readonly string _connectionString;

    public HubData(string? directory = null)
    {
        var dir = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "hub.db") }.ToString();
        Initialize();
    }

    public ObservableCollection<ProductRow> Products { get; } = [];
    public ObservableCollection<LogRow> Logs { get; } = [];

    public void Refresh()
    {
        Products.Clear();
        Logs.Clear();
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, ProductCode, Barcode, Name, Stock, Price, Currency, Brand, Category, Status FROM Products ORDER BY Id DESC LIMIT 1000";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                Products.Add(new ProductRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetDecimal(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9)));
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT CreatedAt, Area, Operation, Result FROM Logs ORDER BY Id DESC LIMIT 200";
            using var reader = command.ExecuteReader();
            while (reader.Read()) Logs.Add(new LogRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
    }

    public string ImportStandardXml(string path, string itemElement)
    {
        if (!File.Exists(path)) return "XML dosyasi bulunamadi.";
        var doc = XDocument.Load(path);
        var items = doc.Descendants().Where(x => x.Name.LocalName.Equals(itemElement, StringComparison.OrdinalIgnoreCase)).Take(10000).ToList();
        if (items.Count == 0) return $"XML icinde {itemElement} elemani bulunamadi.";
        using var connection = Open();
        var imported = 0;
        foreach (var item in items)
        {
            var code = Read(item, "Product_code", "stockCode", "KOD");
            var barcode = Read(item, "Barcode", "BARCODE", "GTIN");
            if (string.IsNullOrWhiteSpace(code)) code = barcode;
            if (string.IsNullOrWhiteSpace(code)) continue;
            Upsert(connection, code, barcode, Read(item, "Name", "URUNADI"), ParseInt(Read(item, "Stock", "stockAmount", "MIKTAR")), ParseMoney(Read(item, "Price", "satisfiyati", "FIYAT")), Read(item, "CurrencyType", "currency", "DOVIZ"), Read(item, "Brand", "Marka"), Read(item, "category", "mainCategory", "subCategory"));
            imported++;
        }
        AddLog(connection, "XML", "Ice Aktarim", $"{imported} urun islendi.");
        Refresh();
        return $"{imported} urun XML'den islendi.";
    }

    public string MockMarketplaceSync(string marketplace)
    {
        using var connection = Open();
        var ready = ScalarInt(connection, "SELECT COUNT(*) FROM Products WHERE Barcode <> '' AND Brand <> '' AND Category <> '' AND Price > 0");
        var waiting = ScalarInt(connection, "SELECT COUNT(*) FROM Products WHERE Barcode = '' OR Brand = '' OR Category = '' OR Price <= 0");
        var result = $"{marketplace}: {ready} urun mock stok/fiyat batch hazir, {waiting} urun hazir degil.";
        AddLog(connection, marketplace, "Mock Stok/Fiyat", result);
        Refresh();
        return result;
    }

    public SqlResult RunSql(string sql, bool allowWrite)
    {
        var normalized = Regex.Replace(sql.Trim(), @"\s+", " ");
        var table = new DataTable();
        if (string.IsNullOrWhiteSpace(normalized)) return new(false, "Sorgu bos.", table);
        var isSelect = normalized.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("WITH ", StringComparison.OrdinalIgnoreCase);
        var dangerous = Regex.IsMatch(normalized, @"\b(DROP|ALTER|TRUNCATE|ATTACH|DETACH|VACUUM|PRAGMA)\b", RegexOptions.IgnoreCase);
        var write = Regex.IsMatch(normalized, @"\b(UPDATE|DELETE|INSERT|REPLACE)\b", RegexOptions.IgnoreCase);
        var unsafeWrite = Regex.IsMatch(normalized, @"\b(UPDATE|DELETE)\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(normalized, @"\bWHERE\b", RegexOptions.IgnoreCase);
        if (dangerous) return LogSql(normalized, false, "Tehlikeli SQL engellendi.", table);
        if (!isSelect && (!allowWrite || write)) return LogSql(normalized, false, "Varsayilan olarak sadece SELECT calisir.", table);
        if (unsafeWrite) return LogSql(normalized, false, "WHERE olmayan UPDATE/DELETE engellendi.", table);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = normalized;
        try { using var reader = command.ExecuteReader(); table.Load(reader); return LogSql(normalized, true, $"{table.Rows.Count} satır döndü.", table); }
        catch (SqliteException) { return LogSql(normalized, false, "Sorgu çalıştırılamadı. Veritabanı salt okunur; yalnızca geçerli SELECT sorgusu kullanın.", new DataTable()); }
    }

    private void Initialize()
    {
        using var connection = Open();
        Execute(connection, """
        CREATE TABLE IF NOT EXISTS Products (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProductCode TEXT NOT NULL UNIQUE,
            Barcode TEXT NOT NULL DEFAULT '',
            Name TEXT NOT NULL,
            Stock INTEGER NOT NULL DEFAULT 0,
            Price REAL NOT NULL DEFAULT 0,
            Currency TEXT NOT NULL DEFAULT 'TRY',
            Brand TEXT NOT NULL DEFAULT '',
            Category TEXT NOT NULL DEFAULT '',
            Status TEXT NOT NULL DEFAULT 'Hazir Degil'
        );
        CREATE TABLE IF NOT EXISTS Logs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CreatedAt TEXT NOT NULL,
            Area TEXT NOT NULL,
            Operation TEXT NOT NULL,
            Result TEXT NOT NULL
        );
        """);
        Refresh();
    }

    private void Upsert(SqliteConnection connection, string code, string barcode, string name, int stock, decimal price, string currency, string brand, string category)
    {
        var status = string.IsNullOrWhiteSpace(barcode) || string.IsNullOrWhiteSpace(brand) || string.IsNullOrWhiteSpace(category) || price <= 0 ? "Hazir Degil" : "Hazir";
        Execute(connection, """
        INSERT INTO Products (ProductCode, Barcode, Name, Stock, Price, Currency, Brand, Category, Status)
        VALUES ($code, $barcode, $name, $stock, $price, $currency, $brand, $category, $status)
        ON CONFLICT(ProductCode) DO UPDATE SET Barcode = excluded.Barcode, Name = excluded.Name, Stock = excluded.Stock, Price = excluded.Price, Currency = excluded.Currency, Brand = excluded.Brand, Category = excluded.Category, Status = excluded.Status
        """, ("$code", code), ("$barcode", barcode), ("$name", string.IsNullOrWhiteSpace(name) ? code : name), ("$stock", stock.ToString(CultureInfo.InvariantCulture)), ("$price", price.ToString(CultureInfo.InvariantCulture)), ("$currency", string.IsNullOrWhiteSpace(currency) ? "TRY" : currency), ("$brand", brand), ("$category", category), ("$status", status));
    }

    private SqlResult LogSql(string query, bool allowed, string message, DataTable table)
    {
        using var connection = Open();
        AddLog(connection, "SQL", allowed ? "Calisti" : "Engellendi", message);
        return new SqlResult(allowed, message, table);
    }

    private static string Read(XElement item, params string[] names) => names.Select(name => item.Descendants().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "").FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
    private static int ParseInt(string value) => int.TryParse(Regex.Replace(value, @"[^\d-]", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static decimal ParseMoney(string value) => decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }
    private static int ScalarInt(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture); }
    private static void AddLog(SqliteConnection connection, string area, string operation, string result) => Execute(connection, "INSERT INTO Logs (CreatedAt, Area, Operation, Result) VALUES ($at, $area, $operation, $result)", ("$at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")), ("$area", area), ("$operation", operation), ("$result", result));
    private static void Execute(SqliteConnection connection, string sql, params (string, string)[] parameters) { using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var p in parameters) command.Parameters.AddWithValue(p.Item1, p.Item2); command.ExecuteNonQuery(); }
}
