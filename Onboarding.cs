using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed class OnboardingState
{
    public bool Skipped { get; set; }
    public string CurrentStep { get; set; } = "store";
    public HashSet<string> CompletedSteps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Channel { get; set; } = "etsy";
    public string ShopId { get; set; } = "default";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed record OnboardingStepStatus(string Key, string Title, string State, string Detail, string Route);
public sealed record OnboardingSummary(IReadOnlyList<OnboardingStepStatus> Steps, int Completed, int Total, string ApiStatus);

public sealed class OnboardingStore
{
    readonly string connectionString;
    public OnboardingStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "onboarding.db") }.ToString(); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS OnboardingState(Id INTEGER PRIMARY KEY CHECK(Id=1),Json TEXT NOT NULL,UpdatedUtc TEXT NOT NULL)"; command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = SqliteConnectionPolicy.Open(connectionString); return c; }
    public OnboardingState Get() { using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Json FROM OnboardingState WHERE Id=1"; var json = command.ExecuteScalar() as string; if (string.IsNullOrWhiteSpace(json)) return new(); try { return JsonSerializer.Deserialize<OnboardingState>(json) ?? new(); } catch (JsonException) { return new(); } }
    public OnboardingState Save(OnboardingState state) { state.CurrentStep = string.IsNullOrWhiteSpace(state.CurrentStep) ? "store" : state.CurrentStep.Trim().ToLowerInvariant(); state.UpdatedUtc = DateTime.UtcNow; using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO OnboardingState(Id,Json,UpdatedUtc) VALUES(1,$json,$updated) ON CONFLICT(Id) DO UPDATE SET Json=excluded.Json,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state)); command.Parameters.AddWithValue("$updated", state.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery(); return state; }
    public bool ShouldPrompt() { var state = Get(); return !state.Skipped && !state.CompletedSteps.Contains("complete"); }
    public void Complete(string step) { var state = Get(); state.CompletedSteps.Add(step.Trim().ToLowerInvariant()); state.CurrentStep = step; Save(state); }
    public void Skip() { var state = Get(); state.Skipped = true; Save(state); }
    public void Reset() { Save(new()); }
}

public sealed class OnboardingSetupService
{
    readonly string? directory;
    public OnboardingSetupService(string? directory = null) => this.directory = directory;
    public MarketplaceConnection SaveStore(string channel, string shopId, string displayName) => new MarketplaceConnectionStore(directory).Save(channel, shopId, displayName, true);
    public XmlSource SaveXml(string name, string location)
    {
        name = name.Trim(); location = location.Trim(); if (name.Length == 0 || name.Length > 160) throw new ArgumentException("XML kaynak adı zorunlu ve 160 karakterden kısa olmalı."); if (location.Length == 0 || location.Length > 2000) throw new ArgumentException("XML URL veya yerel dosya yolu zorunlu."); if (ContainsSecret(location)) throw new InvalidOperationException("XML adresine kullanıcı adı/parola/token yazılamaz; CredentialStore ile ayrı yetkilendirin.");
        var source = new XmlSource { Id = Guid.NewGuid().ToString("N"), Name = name, Location = location, Enabled = true, AutoImport = false, Currency = "TRY", CostCurrency = "TRY", ExchangeRate = 1, MarkupPercent = 0, SafetyStock = 0, MaximumStock = 999999, Fields = new() }; XmlCatalog.ValidateSource(source); new CatalogStore(directory).SaveSource(source); return source;
    }
    public StockPolicy SaveStock(string channel, string shop, int safetyStock, int? maximumStock) { var store = new CatalogStore(directory); return store.SaveStockPolicy(new StockPolicy { Channel = channel, Shop = shop, SafetyStock = safetyStock, MaximumStock = maximumStock, Enabled = true, Version = store.GetStockPolicy(channel, shop)?.Version ?? 0 }); }
    public PricePolicy SavePrice(string channel, string shop, string formula, string currency, decimal rate) { var store = new CatalogStore(directory); return store.SavePricePolicy(new PricePolicy { Channel = channel, Shop = shop, Formula = formula, Currency = currency, TryPerUnit = rate, Enabled = true, Version = store.GetPricePolicy(channel, shop)?.Version ?? 0 }); }
    public ExcelImportProfile SaveExcelProfile(string name, string cultureName = "tr-TR") { var profile = new ExcelImportProfile { Name = name, CultureName = cultureName, ColumnMappings = new(StringComparer.OrdinalIgnoreCase) { ["Sku"] = "SKU", ["Name"] = "Ürün", ["Cost"] = "Alış", ["Price"] = "Satış", ["Stock"] = "Stok", ["Currency"] = "Döviz", ["VatRate"] = "KDV %" }, VisibleFields = ["Sku", "Name", "Cost", "Price", "Currency", "VatRate", "Stock"] }; new ExcelProfileStore(directory).Save(profile); return profile; }
    static bool ContainsSecret(string value) { if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.UserInfo)) return true; var key = value.ToLowerInvariant(); return key.Contains("password=") || key.Contains("passwd=") || key.Contains("token=") || key.Contains("secret=") || key.Contains("api_key=") || key.Contains("apikey="); }
}

public sealed class OnboardingStatusService
{
    readonly string? directory;
    public OnboardingStatusService(string? directory = null) => this.directory = directory;
    public OnboardingSummary Build(string channel = "etsy", string shop = "default")
    {
        var connection = new MarketplaceConnectionStore(directory).List().FirstOrDefault(x => x.Channel == channel && x.ShopId == shop); var sources = new CatalogStore(directory).Sources(); var catalog = new CatalogStore(directory); var profile = new ExcelProfileStore(directory).List().Count > 0; var steps = new List<OnboardingStepStatus> { new("store", "Mağaza bağlantısı", connection is null ? "NOT_CONFIGURED" : connection.Status, connection is null ? "Kanal ve mağaza metadata'sı girilmedi." : $"{connection.DisplayName} · {connection.Status}", "connections"), new("xml", "XML tedarikçi", sources.Count == 0 ? "NOT_CONFIGURED" : "READY", sources.Count == 0 ? "Kaynak eklenmedi." : $"{sources.Count} kaynak tanımlı.", "xml"), new("stock", "Stok politikası", catalog.GetStockPolicy(channel, shop) is null ? "NOT_CONFIGURED" : "READY", catalog.GetStockPolicy(channel, shop) is null ? "Varsayılan stok politikası yok." : "Mağaza güvenlik stoğu hazır.", "stock-policies"), new("price", "Fiyat politikası", catalog.GetPricePolicy(channel, shop) is null ? "NOT_CONFIGURED" : "READY", catalog.GetPricePolicy(channel, shop) is null ? "Varsayılan fiyat politikası yok." : "Mağaza fiyat formülü hazır.", "price-policies"), new("excel", "Excel profili", profile ? "READY" : "NOT_CONFIGURED", profile ? "En az bir Excel profili hazır." : "Başlangıç profili oluşturulmadı.", "excel") };
        var apiStatus = connection is null ? "NOT_CONFIGURED" : MarketplaceConnectionCatalog.Get(connection.Channel).LiveApiBlocked ? "LIVE_API_BLOCKED" : connection.Status; return new(steps, steps.Count(x => x.State == "READY" || x.State == "CONNECTED_READ_ONLY"), steps.Count, apiStatus);
    }
}
