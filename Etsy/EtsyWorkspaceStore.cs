using Microsoft.Data.Sqlite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Etsy;

public sealed class EtsyWorkspaceStore
{
    readonly string connectionString;
    public EtsyWorkspaceStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");
        _ = new CatalogStore(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource=Path.Combine(directory,"catalog.db"), DefaultTimeout=15 }.ToString();
        using var c=Open(); using var cmd=c.CreateCommand();
        cmd.CommandText="CREATE TABLE IF NOT EXISTS EtsyWorkspace(ShopId TEXT PRIMARY KEY,Revision INTEGER NOT NULL,Json TEXT NOT NULL);"+
            "CREATE TABLE IF NOT EXISTS EtsyWorkspacePlans(Id TEXT PRIMARY KEY,ShopId TEXT NOT NULL,Json TEXT NOT NULL);"+
            "CREATE TABLE IF NOT EXISTS EtsyWorkspaceReceipts(PlanId TEXT NOT NULL,ProductId TEXT NOT NULL,ShopId TEXT NOT NULL,Operation TEXT NOT NULL,OperationKey TEXT NOT NULL UNIQUE,Status TEXT NOT NULL,Json TEXT NOT NULL,PRIMARY KEY(PlanId,ProductId));";
        cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c=new SqliteConnection(connectionString); c.Open(); return c; }
    internal static string Shop(string id) { if(!long.TryParse(id,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var value)||value<=0||value.ToString(System.Globalization.CultureInfo.InvariantCulture)!=id)throw new InvalidOperationException("Geçerli Etsy mağaza kimliği gerekli."); return id; }
    internal static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public EtsyWorkspaceState Load(string shopId) { using var c=Open(); return Load(c,null,Shop(shopId)); }
    static EtsyWorkspaceState Load(SqliteConnection c,SqliteTransaction? tx,string id)
    {
        using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="SELECT Json FROM EtsyWorkspace WHERE ShopId=$id"; cmd.Parameters.AddWithValue("$id",id);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<EtsyWorkspaceState>(json)! : new() { ShopId=id };
    }
    public void Save(EtsyWorkspaceState state)
    {
        var next=JsonSerializer.Deserialize<EtsyWorkspaceState>(JsonSerializer.Serialize(state))!; Validate(next);
        using var c=Open(); using var tx=c.BeginTransaction();
        if(Load(c,tx,next.ShopId).Revision!=next.Revision)throw new InvalidOperationException("Etsy ayarları değişti; ekranı yenileyin.");
        next.Revision++; PutState(c,tx,next); tx.Commit();
    }
    static void PutState(SqliteConnection c,SqliteTransaction tx,EtsyWorkspaceState next)
    {
        using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="INSERT INTO EtsyWorkspace VALUES($id,$revision,$json) ON CONFLICT(ShopId) DO UPDATE SET Revision=excluded.Revision,Json=excluded.Json";
        cmd.Parameters.AddWithValue("$id",next.ShopId); cmd.Parameters.AddWithValue("$revision",next.Revision); cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(next)); cmd.ExecuteNonQuery();
    }
    static void Validate(EtsyWorkspaceState s)
    {
        Shop(s.ShopId);
        if(s.Revision<0||s.Profiles.Any(p=>string.IsNullOrWhiteSpace(p.ProductId)||p.ListingId is <=0||p.Price is <=0)||s.Templates.Any(t=>string.IsNullOrWhiteSpace(t.Name)||string.IsNullOrWhiteSpace(t.Id)))throw new InvalidOperationException("Etsy kayıt değerlerini kontrol edin.");
        if(s.Profiles.GroupBy(p=>p.ProductId).Any(g=>g.Count()>1)||s.Profiles.Where(p=>p.ListingId.HasValue).GroupBy(p=>p.ListingId).Any(g=>g.Count()>1)||s.Templates.GroupBy(t=>t.Id).Any(g=>g.Count()>1)||s.Listings.GroupBy(l=>l.ListingId).Any(g=>g.Count()>1)||s.CategoryMappings.GroupBy(m=>m.LocalCategory.Trim(),StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidOperationException("Yinelenen Etsy ürün/ilan/şablon eşleştirmesi kaydedilemez.");
    }
    internal static string CatalogHash(SqliteConnection c,SqliteTransaction? tx,IEnumerable<string> ids)
    {
        var values=new List<string>();
        foreach(var id in ids.Distinct().Order(StringComparer.Ordinal)) { using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id"; cmd.Parameters.AddWithValue("$id",id); values.Add(id); values.Add(cmd.ExecuteScalar() as string ?? "<deleted>"); }
        return Hash(JsonSerializer.Serialize(values));
    }
    internal string CatalogHash(IEnumerable<string> ids) { using var c=Open(); return CatalogHash(c,null,ids); }
    internal void Persist(EtsyOperationPlan plan)
    {
        using var c=Open(); using var tx=c.BeginTransaction(); ValidateCurrent(c,tx,plan);
        using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="INSERT INTO EtsyWorkspacePlans VALUES($id,$shop,$json)";
        cmd.Parameters.AddWithValue("$id",plan.Id); cmd.Parameters.AddWithValue("$shop",plan.ShopId); cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(plan)); cmd.ExecuteNonQuery(); tx.Commit();
    }
    internal EtsyOperationPlan Plan(string id)
    {
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT Json FROM EtsyWorkspacePlans WHERE Id=$id"; cmd.Parameters.AddWithValue("$id",id);
        return JsonSerializer.Deserialize<EtsyOperationPlan>(cmd.ExecuteScalar() as string ?? throw new InvalidOperationException("Kaydedilmiş Etsy önizlemesi bulunamadı."))!;
    }
    static void ValidateCurrent(SqliteConnection c,SqliteTransaction tx,EtsyOperationPlan plan)
    {
        if(Load(c,tx,plan.ShopId).Revision!=plan.WorkspaceRevision||CatalogHash(c,tx,plan.Rows.Select(r=>r.ProductId))!=plan.CatalogFingerprint)throw new InvalidOperationException("Katalog veya Etsy ayarları değişti; yeni önizleme alın.");
    }
    internal void ValidateCurrent(EtsyOperationPlan plan) { using var c=Open(); using var tx=c.BeginTransaction(); ValidateCurrent(c,tx,plan); tx.Commit(); }
    public IReadOnlyList<EtsyOperationReceipt> Receipts(string shopId)
    {
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="SELECT Json FROM EtsyWorkspaceReceipts WHERE ShopId=$shop ORDER BY rowid DESC"; cmd.Parameters.AddWithValue("$shop",Shop(shopId));
        using var r=cmd.ExecuteReader(); var rows=new List<EtsyOperationReceipt>(); while(r.Read())rows.Add(JsonSerializer.Deserialize<EtsyOperationReceipt>(r.GetString(0))!); return rows;
    }
    internal bool HasUnresolved(string shop,string product)=>Receipts(shop).Any(r=>r.ProductId==product&&r.Status is "Claimed" or "Unknown" or "Partial");
    internal void Claim(EtsyOperationPlan plan)
    {
        using var c=Open(); using var tx=c.BeginTransaction(); ValidateCurrent(c,tx,plan);
        foreach(var row in plan.Rows.Where(r=>r.CanSend))
        {
            using(var check=c.CreateCommand()) { check.Transaction=tx; check.CommandText="SELECT COUNT(*) FROM EtsyWorkspaceReceipts WHERE ShopId=$shop AND ProductId=$product AND (Status IN ('Claimed','Unknown','Partial') OR (Operation='CreateDraft' AND $create=1))"; check.Parameters.AddWithValue("$shop",plan.ShopId); check.Parameters.AddWithValue("$product",row.ProductId); check.Parameters.AddWithValue("$create",plan.Operation==EtsyOperation.CreateDraft?1:0); if(Convert.ToInt64(check.ExecuteScalar())>0)throw new InvalidOperationException("Önceki Etsy işlemi tekrar gönderilemez; mağaza ve işlem geçmişini kontrol edin."); }
            var receipt=new EtsyOperationReceipt { PlanId=plan.Id,ProductId=row.ProductId,Sku=row.Sku,ListingId=row.ListingId,Status="Claimed",Detail="Gönderim ayrıldı; sonuç kesinleşmeden tekrar gönderilmez." };
            using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="INSERT INTO EtsyWorkspaceReceipts VALUES($plan,$product,$shop,$operation,$key,'Claimed',$json)";
            cmd.Parameters.AddWithValue("$plan",plan.Id); cmd.Parameters.AddWithValue("$product",row.ProductId); cmd.Parameters.AddWithValue("$shop",plan.ShopId); cmd.Parameters.AddWithValue("$operation",plan.Operation.ToString()); cmd.Parameters.AddWithValue("$key",Hash(JsonSerializer.Serialize(new object?[]{plan.ShopId,row.ProductId,plan.Operation,row.ListingId,row.PayloadJson,row.RemoteFingerprint,row.InventoryFingerprint}))); cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(receipt));
            try { cmd.ExecuteNonQuery(); } catch(SqliteException ex) when(ex.SqliteErrorCode==19) { throw new InvalidOperationException("Bu Etsy önizlemesi daha önce gönderildi; tekrar gönderim engellendi."); }
        }
        tx.Commit();
    }
    internal void Complete(EtsyOperationReceipt receipt)
    {
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="UPDATE EtsyWorkspaceReceipts SET Status=$status,Json=$json WHERE PlanId=$plan AND ProductId=$product";
        cmd.Parameters.AddWithValue("$status",receipt.Status); cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(receipt)); cmd.Parameters.AddWithValue("$plan",receipt.PlanId); cmd.Parameters.AddWithValue("$product",receipt.ProductId); if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Etsy gönderim kaydı bulunamadı.");
    }
    internal void BindCreated(string shop,string product,long listing)
    {
        using var c=Open(); using var tx=c.BeginTransaction(); var state=Load(c,tx,shop);
        if(state.Profiles.Any(p=>p.ProductId!=product&&p.ListingId==listing))throw new InvalidOperationException("Oluşturulan Etsy ilanı başka ürüne bağlı; eşleştirme kontrol edilmeli.");
        var profile=state.Profiles.SingleOrDefault(p=>p.ProductId==product);
        if(profile is null) { profile=new() { ProductId=product }; state.Profiles.Add(profile); }
        if(profile.ListingId.HasValue&&profile.ListingId!=listing)throw new InvalidOperationException("Etsy ürün eşleştirmesi gönderim sırasında değişti.");
        profile.ListingId=listing; state.Revision++; PutState(c,tx,state); tx.Commit();
    }
    internal void ApplyMatches(EtsyWorkspaceState requested,IReadOnlyList<EtsyMatchRow> matches)
    {
        using var c=Open(); using var tx=c.BeginTransaction(); var state=Load(c,tx,requested.ShopId);
        if(state.Revision!=requested.Revision)throw new InvalidOperationException("Etsy eşleştirme listesi değişti; yenileyin.");
        if(matches.Count==0||matches.Any(m=>!m.CanMatch||m.ListingId is not >0)||matches.GroupBy(m=>m.ProductId).Any(g=>g.Count()>1))throw new InvalidOperationException("Geçerli ve benzersiz eşleştirmeleri seçin.");
        foreach(var match in matches)
        {
            using var cmd=c.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id"; cmd.Parameters.AddWithValue("$id",match.ProductId);
            var product=JsonSerializer.Deserialize<CatalogProduct>(cmd.ExecuteScalar() as string ?? throw new InvalidOperationException("Ürün silinmiş."))!;
            if(product.Sku!=match.Sku||!state.Listings.Any(l=>l.ListingId==match.ListingId)||state.Profiles.Any(p=>p.ProductId!=match.ProductId&&p.ListingId==match.ListingId))throw new InvalidOperationException("Ürün değişti veya ilan başka ürüne bağlı; eşleştirme kaydedilmedi.");
            var profile=state.Profiles.SingleOrDefault(p=>p.ProductId==match.ProductId); if(profile==null) { profile=new() { ProductId=match.ProductId }; state.Profiles.Add(profile); } profile.ListingId=match.ListingId;
        }
        Validate(state); state.Revision++; PutState(c,tx,state); tx.Commit();
    }
}
