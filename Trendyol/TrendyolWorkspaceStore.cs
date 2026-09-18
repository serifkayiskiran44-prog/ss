using Microsoft.Data.Sqlite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Trendyol;

/// <summary>Outbound mappings are seller-scoped and independent of XML import mappings.</summary>
public sealed partial class TrendyolWorkspaceStore
{
    readonly string connectionString;
    public TrendyolWorkspaceStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        _ = new CatalogStore(directory);
        _ = new TaxonomyStore(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15 }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS TrendyolWorkspace(SellerId TEXT PRIMARY KEY,Revision INTEGER NOT NULL,Json TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS TrendyolPlans(Id TEXT PRIMARY KEY,SellerId TEXT NOT NULL,Json TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS TrendyolReceipts(PlanId TEXT PRIMARY KEY,SellerId TEXT NOT NULL,PayloadHash TEXT NOT NULL,CreatedUtc TEXT NOT NULL,Json TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    internal static void Seller(string seller) { if (seller.Length is < 1 or > 32 || !seller.All(char.IsAsciiDigit)) throw new ArgumentException("Geçerli satıcı ID gerekli."); }
    public TrendyolWorkspaceState Load(string seller) { Seller(seller); using var c = Open(); return Load(c, null, seller); }
    static TrendyolWorkspaceState Load(SqliteConnection c, SqliteTransaction? tx, string seller)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT Revision,Json FROM TrendyolWorkspace WHERE SellerId=$seller"; cmd.Parameters.AddWithValue("$seller", seller);
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) return new() { SellerId = seller };
        var state = JsonSerializer.Deserialize<TrendyolWorkspaceState>(reader.GetString(1)) ?? throw new InvalidDataException("Trendyol kaydı okunamadı.");
        if (state.SellerId != seller || state.Revision != reader.GetInt64(0)) throw new InvalidDataException("Trendyol kayıt kimliği tutarsız.");
        Validate(state); return state;
    }
    public void Save(TrendyolWorkspaceState state)
    {
        // Round-trip before validation so caller mutations cannot change the written snapshot.
        var next = JsonSerializer.Deserialize<TrendyolWorkspaceState>(JsonSerializer.Serialize(state))!; Validate(next);
        using var c = Open(); using var tx = c.BeginTransaction();
        if (Load(c, tx, next.SellerId).Revision != next.Revision) throw new InvalidOperationException("Trendyol ayarları değişti; ekranı yenileyin.");
        next.Revision++;
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO TrendyolWorkspace VALUES($seller,$revision,$json) ON CONFLICT(SellerId) DO UPDATE SET Revision=excluded.Revision,Json=excluded.Json";
        cmd.Parameters.AddWithValue("$seller", next.SellerId); cmd.Parameters.AddWithValue("$revision", next.Revision); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(next)); cmd.ExecuteNonQuery(); tx.Commit();
    }
    static void Unique<T,K>(IEnumerable<T> values, Func<T,K> key) { if (values.GroupBy(key).Any(g => g.Count() > 1)) throw new InvalidOperationException("Trendyol verisinde yinelenen kimlik var; kayıt korunuyor."); }
    static void Validate(TrendyolWorkspaceState s)
    {
        Seller(s.SellerId);
        Unique(s.Categories,c=>c.Id); Unique(s.Brands,b=>b.Id); Unique(s.Products,p=>p.Barcode); Unique(s.Addresses,a=>a.Id); Unique(s.Carriers,c=>c.Code);
        Unique(s.Mappings,m=>(m.Kind,m.LocalId)); Unique(s.Profiles,p=>p.ProductId); Unique(s.Templates,t=>t.Id);
        Unique(s.Profiles.Where(p=>p.IntegrationCode.Length>0),p=>p.IntegrationCode);
        Unique(s.BrandSafetyTemplates,t=>TrendyolMatching.Normalize(t.BrandName));
        foreach(var template in s.BrandSafetyTemplates)TrendyolProductSafety.Validate(template);
        if(s.Categories.Any(c=>c.Id<=0)||s.Brands.Any(b=>b.Id<=0)||s.Revision<0) throw new InvalidDataException("Geçersiz Trendyol kimliği.");
        foreach(var m in s.Mappings) if (m.RemoteId<=0 || m.LocalId.Length==0 || m.Kind is not (TaxonomyKind.Category or TaxonomyKind.Brand)) throw new InvalidOperationException("Geçersiz kategori/marka eşleştirmesi.");
        foreach(var t in s.Templates) if(t.Id.Length==0 || string.IsNullOrWhiteSpace(t.Name) || t.DurationDays is <0 or >30 || t.ShipmentAddressId is <=0 || t.ReturningAddressId is <=0) throw new InvalidOperationException("Teslimat şablonunu kontrol edin.");
        foreach(var p in s.Profiles) { if(p.ProductId.Length==0 || p.IntegrationCode.Length>40 || p.IntegrationCode.Any(char.IsWhiteSpace) || p.ListingBarcode.Length>40 || p.ListingBarcode.Any(ch=>!char.IsLetterOrDigit(ch)&&ch!='.'&&ch!='-'&&ch!='_') || p.SalePriceTry is <=0 || p.ListPriceTry is <=0) throw new InvalidOperationException("Ürün Trendyol ayarlarını kontrol edin."); Unique(p.Attributes,a=>a.AttributeId); }
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string AccountFingerprint(TrendyolSettings settings) => Hash(JsonSerializer.Serialize(new[]{settings.SupplierId,settings.ApiKey,settings.ApiSecret}));
    static string CatalogFingerprint(SqliteConnection c, SqliteTransaction? tx, IEnumerable<string> ids)
    {
        var snapshot = new List<string>();
        foreach(var id in ids.Distinct().Order(StringComparer.Ordinal)) { using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);snapshot.Add(id);snapshot.Add(cmd.ExecuteScalar() as string ?? throw new InvalidOperationException("Ürün silinmiş; önizlemeyi yenileyin.")); }
        using(var cmd=c.CreateCommand()) { cmd.Transaction=tx;cmd.CommandText="SELECT Id,Kind,Name,Active,UpdatedUtc FROM TaxonomyEntries ORDER BY Id";using var r=cmd.ExecuteReader();while(r.Read()) for(int i=0;i<r.FieldCount;i++) snapshot.Add(Convert.ToString(r.GetValue(i),System.Globalization.CultureInfo.InvariantCulture)??""); }
        return Hash(JsonSerializer.Serialize(snapshot));
    }
}
