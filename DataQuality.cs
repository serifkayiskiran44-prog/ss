using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed class DataQualityIssue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Fingerprint { get; set; } = "";
    public string Severity { get; set; } = "Warning";
    public string Type { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Marketplace { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string Message { get; set; } = "";
    public string Suggestion { get; set; } = "";
    public string Status { get; set; } = "Open";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed record DataQualitySummary(int Total, int Critical, int Error, int Warning, int Open, int Resolved);

public sealed class DataQualityStore
{
    readonly string connectionString;
    public DataQualityStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "quality.db") }.ToString(); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS QualityIssues(Id TEXT PRIMARY KEY,Fingerprint TEXT UNIQUE NOT NULL,Severity TEXT NOT NULL,Type TEXT NOT NULL,ProductId TEXT NOT NULL,Sku TEXT NOT NULL,SourceId TEXT NOT NULL,Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,Message TEXT NOT NULL,Suggestion TEXT NOT NULL,Status TEXT NOT NULL,CreatedUtc TEXT NOT NULL,UpdatedUtc TEXT NOT NULL);CREATE INDEX IF NOT EXISTS IX_Quality_Status ON QualityIssues(Status,Severity,UpdatedUtc DESC);"; command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public DataQualityIssue Upsert(DataQualityIssue issue)
    {
        Validate(issue); issue.Fingerprint = issue.Fingerprint.Trim(); issue.Message = MarketplaceConnectionStore.Redact(issue.Message); issue.Suggestion = MarketplaceConnectionStore.Redact(issue.Suggestion); issue.UpdatedUtc = DateTime.UtcNow; using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO QualityIssues VALUES($id,$fingerprint,$severity,$type,$product,$sku,$source,$marketplace,$shop,$message,$suggestion,$status,$created,$updated) ON CONFLICT(Fingerprint) DO UPDATE SET Severity=excluded.Severity,Type=excluded.Type,ProductId=excluded.ProductId,Sku=excluded.Sku,SourceId=excluded.SourceId,Marketplace=excluded.Marketplace,ShopId=excluded.ShopId,Message=excluded.Message,Suggestion=excluded.Suggestion,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$id", issue.Id); command.Parameters.AddWithValue("$fingerprint", issue.Fingerprint); command.Parameters.AddWithValue("$severity", issue.Severity); command.Parameters.AddWithValue("$type", issue.Type); command.Parameters.AddWithValue("$product", issue.ProductId); command.Parameters.AddWithValue("$sku", issue.Sku); command.Parameters.AddWithValue("$source", issue.SourceId); command.Parameters.AddWithValue("$marketplace", issue.Marketplace); command.Parameters.AddWithValue("$shop", issue.ShopId); command.Parameters.AddWithValue("$message", issue.Message); command.Parameters.AddWithValue("$suggestion", issue.Suggestion); command.Parameters.AddWithValue("$status", issue.Status); command.Parameters.AddWithValue("$created", issue.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$updated", issue.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery(); return List().Single(x => x.Fingerprint == issue.Fingerprint);
    }
    public IReadOnlyList<DataQualityIssue> List(string? query = null, string? severity = null, string? status = null, string? type = null)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT * FROM QualityIssues WHERE ($query='' OR Message LIKE $like OR Suggestion LIKE $like OR Sku LIKE $like OR ProductId LIKE $like OR SourceId LIKE $like OR Marketplace LIKE $like OR ShopId LIKE $like) AND ($severity='' OR Severity=$severity) AND ($status='' OR Status=$status) AND ($type='' OR Type=$type) ORDER BY CASE Severity WHEN 'Critical' THEN 0 WHEN 'Error' THEN 1 WHEN 'Warning' THEN 2 ELSE 3 END,UpdatedUtc DESC"; var q = query?.Trim() ?? ""; command.Parameters.AddWithValue("$query", q); command.Parameters.AddWithValue("$like", $"%{q}%"); command.Parameters.AddWithValue("$severity", severity?.Trim() ?? ""); command.Parameters.AddWithValue("$status", status?.Trim() ?? ""); command.Parameters.AddWithValue("$type", type?.Trim() ?? ""); using var r = command.ExecuteReader(); var rows = new List<DataQualityIssue>(); while (r.Read()) rows.Add(Read(r)); return rows;
    }
    public void SetStatus(string id, string status) { if (status is not ("Open" or "Resolved")) throw new ArgumentException("Kalite durumu geçersiz."); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "UPDATE QualityIssues SET Status=$status,UpdatedUtc=$updated WHERE Id=$id"; command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery(); }
    public DataQualitySummary Summary() { var all = List(); return new(all.Count, all.Count(x => x.Severity == "Critical"), all.Count(x => x.Severity == "Error"), all.Count(x => x.Severity == "Warning"), all.Count(x => x.Status == "Open"), all.Count(x => x.Status == "Resolved")); }
    static DataQualityIssue Read(SqliteDataReader r) => new() { Id = r.GetString(0), Fingerprint = r.GetString(1), Severity = r.GetString(2), Type = r.GetString(3), ProductId = r.GetString(4), Sku = r.GetString(5), SourceId = r.GetString(6), Marketplace = r.GetString(7), ShopId = r.GetString(8), Message = r.GetString(9), Suggestion = r.GetString(10), Status = r.GetString(11), CreatedUtc = DateTime.Parse(r.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), UpdatedUtc = DateTime.Parse(r.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) };
    static void Validate(DataQualityIssue issue) { if (string.IsNullOrWhiteSpace(issue.Fingerprint) || string.IsNullOrWhiteSpace(issue.Type) || string.IsNullOrWhiteSpace(issue.Message)) throw new ArgumentException("Kalite kaydı fingerprint, tür ve açıklama içermeli."); }
}

public sealed class DataQualityService
{
    readonly string? directory; readonly CatalogStore catalog; readonly DataQualityStore issues; readonly ChannelProductsStore plans;
    public DataQualityService(string? directory = null) { this.directory = directory; catalog = new CatalogStore(directory); issues = new DataQualityStore(directory); plans = new ChannelProductsStore(directory); }
    public IReadOnlyList<DataQualityIssue> Scan()
    {
        var products = catalog.Products(); var sources = catalog.Sources().ToDictionary(x => x.Id); var discovered = new List<DataQualityIssue>();
        void Add(DataQualityIssue issue) { issue.Fingerprint = Fingerprint(issue.Type, issue.ProductId, issue.Sku, issue.SourceId, issue.Marketplace, issue.ShopId, issue.Message); discovered.Add(issues.Upsert(issue)); }
        foreach (var group in products.Where(x => !string.IsNullOrWhiteSpace(x.Sku)).GroupBy(x => x.Sku.Trim(), StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) foreach (var product in group.OrderBy(x => x.Id, StringComparer.Ordinal)) Add(new() { Severity = "Critical", Type = "DuplicateSku", ProductId = product.Id, Sku = product.Sku, SourceId = product.SourceId, Message = $"SKU {group.Key} birden fazla üründe kullanılıyor.", Suggestion = "SKU'yu tek üründe bırakın veya güvenli bir düzeltme preview'i hazırlayın." });
        foreach (var group in products.Where(x => !string.IsNullOrWhiteSpace(x.Barcode)).GroupBy(x => x.Barcode.Trim(), StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) foreach (var product in group.OrderBy(x => x.Id, StringComparer.Ordinal)) Add(new() { Severity = "Critical", Type = "DuplicateBarcode", ProductId = product.Id, Sku = product.Sku, SourceId = product.SourceId, Message = $"Barkod {group.Key} kaynak/mağaza bağlamında birden fazla üründe kullanılıyor (kaynak: {product.SourceId}).", Suggestion = "Kaynak ve mağaza eşlemesini doğrulamadan sync başlatmayın." });
        foreach (var product in products)
        {
            if (string.IsNullOrWhiteSpace(product.Name)) Add(new() { Severity = "Error", Type = "MissingName", ProductId = product.Id, Sku = product.Sku, SourceId = product.SourceId, Message = "Ürün adı eksik.", Suggestion = "Ürün kartında ad girin." });
            if (string.IsNullOrWhiteSpace(product.Sku) && string.IsNullOrWhiteSpace(product.Barcode)) Add(new() { Severity = "Critical", Type = "MissingIdentity", ProductId = product.Id, Sku = product.Sku, SourceId = product.SourceId, Message = "SKU veya barkod eksik.", Suggestion = "Tekil SKU veya barkod girin." });
            if (product.Cost < 0 || product.Price < 0 || product.Stock < 0) Add(new() { Severity = "Critical", Type = "InvalidNumber", ProductId = product.Id, Sku = product.Sku, SourceId = product.SourceId, Message = "Maliyet, fiyat veya stok negatif.", Suggestion = "Değeri düzeltmeden sync başlatmayın." });
            if (!LocaleSettings.SupportedCurrencies.Contains(product.Currency)) Add(new() { Severity = "Error", Type = "InvalidCurrency", ProductId = product.Id, Sku = product.Sku, SourceId = product.SourceId, Message = $"Desteklenmeyen döviz: {product.Currency}.", Suggestion = "TRY, USD, EUR veya GBP seçin." });
            if (product.VatRate < 0 || product.VatRate > 100) Add(new() { Severity = "Error", Type = "InvalidVatRate", ProductId = product.Id, Sku = product.Sku, SourceId = product.SourceId, Message = $"Geçersiz KDV oranı: {product.VatRate}.", Suggestion = "KDV oranını 0 ile 100 arasında düzeltin." });
            if (!string.IsNullOrWhiteSpace(product.SourceId) && !sources.ContainsKey(product.SourceId)) Add(new() { Severity = "Warning", Type = "MissingXmlSource", ProductId = product.Id, Sku = product.Sku, SourceId = product.SourceId, Message = "Ürünün XML kaynağı artık bulunamıyor.", Suggestion = "XML kaynağını geri ekleyin veya ürünü yerel olarak yönetin." });
            foreach (var url in product.ImageUrls.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) Add(new() { Severity = "Error", Type = "InvalidImageUrl", ProductId = product.Id, Sku = product.Sku, SourceId = product.SourceId, Message = "Görsel URL'si yayın için geçersiz; yalnızca host içeren HTTP(S) adresleri kabul edilir.", Suggestion = "Kimlik bilgisi içermeyen HTTPS görsel adresi kullanın." });
        }
        foreach (var group in plans.List().Where(x => !string.IsNullOrWhiteSpace(x.ListingId)).GroupBy(x => $"{x.ChannelId}|{x.ShopId}|{x.ListingId}", StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1)) foreach (var plan in group) Add(new() { Severity = "Critical", Type = "DuplicateListing", ProductId = plan.ProductId, Marketplace = plan.ChannelId, ShopId = plan.ShopId, Message = $"Listing ID {plan.ListingId} aynı kanal/mağazada birden fazla ürüne bağlı.", Suggestion = "İlan eşlemesini düzeltmeden canlı write başlatmayın." });
        foreach (var plan in plans.List()) { var definition = MarketplaceConnectionCatalog.All.FirstOrDefault(x => x.Id.Equals(plan.ChannelId, StringComparison.OrdinalIgnoreCase)); if (definition is not null && definition.LiveApiBlocked) Add(new() { Severity = "Warning", Type = "LiveApiBlocked", ProductId = plan.ProductId, Marketplace = plan.ChannelId, ShopId = plan.ShopId, Message = "Bu kanal için doğrulanmış canlı API capability'si yok.", Suggestion = "Yerel plan preview olarak kalır; resmi endpoint/credential doğrulanmadan write yapılmaz." }); }
        try { foreach (var run in new XmlRunStore(directory).List().Where(x => x.Status == "Failed")) Add(new() { Severity = "Error", Type = "XmlRunFailed", SourceId = run.SourceId, Message = "XML çalışması başarısız: " + run.Error, Suggestion = "Kaynak sağlık kontrolünü çalıştırıp mapping/erişim bilgilerini düzeltin." }); } catch { }
        return discovered;
    }
    public IReadOnlyList<DataQualityIssue> List(string? query = null, string? severity = null, string? status = null, string? type = null) => issues.List(query, severity, status, type);
    public DataQualitySummary Summary() => issues.Summary();
    public void Resolve(string id) => issues.SetStatus(id, "Resolved");
    static string Fingerprint(params string[] values) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", values)))); }
}
