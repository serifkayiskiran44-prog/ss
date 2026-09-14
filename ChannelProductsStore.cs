using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;
namespace TrMarketplaceHubDesktop;
public sealed class ChannelProductsStore
{
 readonly string connectionString;
 public ChannelProductsStore(string? directory=null)
 {
  directory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");Directory.CreateDirectory(directory);
  connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"channel_products.db")}.ToString();
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="CREATE TABLE IF NOT EXISTS ChannelPlans(ChannelId TEXT NOT NULL,ShopId TEXT NOT NULL,ProductId TEXT NOT NULL,Json TEXT NOT NULL,Version INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(ChannelId,ShopId,ProductId))";cmd.ExecuteNonQuery();
  EnsureColumn(c,"Version","INTEGER NOT NULL DEFAULT 0");
 }
 static void EnsureColumn(SqliteConnection c,string name,string definition){using var check=c.CreateCommand();check.CommandText="SELECT 1 FROM pragma_table_info('ChannelPlans') WHERE name=$name";check.Parameters.AddWithValue("$name",name);if(check.ExecuteScalar()!=null)return;using var add=c.CreateCommand();add.CommandText=$"ALTER TABLE ChannelPlans ADD COLUMN {name} {definition}";add.ExecuteNonQuery();}
 SqliteConnection Open(){var c=new SqliteConnection(connectionString);c.Open();return c;}
 static void Identity(string channel,string shop,string product){if(string.IsNullOrWhiteSpace(channel)||string.IsNullOrWhiteSpace(shop)||string.IsNullOrWhiteSpace(product))throw new ArgumentException("Kanal, mağaza anahtarı ve merkez ürün kimliği zorunlu.");}
 public ChannelProductPlan? Find(string channel,string shop,string product)
 {
  Identity(channel,shop,product);using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Json,Version FROM ChannelPlans WHERE ChannelId=$channel AND ShopId=$shop AND ProductId=$product";cmd.Parameters.AddWithValue("$channel",channel.Trim().ToLowerInvariant());cmd.Parameters.AddWithValue("$shop",shop.Trim());cmd.Parameters.AddWithValue("$product",product);
  using var r=cmd.ExecuteReader();if(!r.Read())return null;return ReadPlan(r);
 }
 static ChannelProductPlan? ReadPlan(SqliteDataReader r)
 {
  var plan=JsonSerializer.Deserialize<ChannelProductPlan>(r.GetString(0));if(plan is null)return null;
  plan.Version=r.GetInt32(1);return plan;
 }
 public IReadOnlyList<ChannelProductPlan> List(string? channel=null,string? shop=null,string? product=null)
 {
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Json,Version FROM ChannelPlans WHERE ($channel='' OR ChannelId=$channel) AND ($shop='' OR ShopId=$shop) AND ($product='' OR ProductId=$product) ORDER BY ChannelId,ShopId,ProductId";cmd.Parameters.AddWithValue("$channel",channel?.Trim().ToLowerInvariant()??"");cmd.Parameters.AddWithValue("$shop",shop?.Trim()??"");cmd.Parameters.AddWithValue("$product",product?.Trim()??"");using var r=cmd.ExecuteReader();var result=new List<ChannelProductPlan>();while(r.Read()){var plan=ReadPlan(r);if(plan is not null)result.Add(plan);}return result;
 }
 static void ValidatePlan(ChannelProductPlan plan)
 {
  Identity(plan.ChannelId,plan.ShopId,plan.ProductId);
  if(plan.PlannedPrice<0||plan.PlannedStock<0)throw new ArgumentException("Plan fiyatı ve stok negatif olamaz.");
  if(plan.Currency is null||plan.Currency.Length!=3||!plan.Currency.All(c=>c>='A'&&c<='Z'))throw new ArgumentException("Para birimini üç büyük harfle girin (USD, EUR, RUB, TRY).");
  if(!string.IsNullOrEmpty(plan.ListingUrl)&&(!Uri.TryCreate(plan.ListingUrl,UriKind.Absolute,out var uri)||(uri.Scheme!="https"&&uri.Scheme!="http")||!string.IsNullOrEmpty(uri.UserInfo)))throw new ArgumentException("İlan bağlantısı geçerli bir HTTP/HTTPS adresi olmalı.");
 }
 /// plan.Version must equal the currently-stored version (0 for "no plan yet") -
 /// the same optimistic-concurrency contract used elsewhere in this app (e.g.
 /// TaxonomyEntry/PricingProfile). A stale/deleted/newly-created plan under the
 /// same key is rejected rather than silently overwritten.
 public void Save(ChannelProductPlan plan)
 {
  ValidatePlan(plan);
  plan.ChannelId=plan.ChannelId.Trim().ToLowerInvariant();plan.ShopId=plan.ShopId.Trim();
  using var c=Open();using var tx=c.BeginTransaction();
  SaveWithinTransaction(c,tx,plan);
  tx.Commit();
 }
 /// All-or-nothing commit for a bulk channel-mapping batch: every plan is
 /// version-checked and written inside one transaction, so a single stale/
 /// mismatched row rolls back the whole batch instead of leaving a partial
 /// commit (#2597) - and each row's version check closes the same stale-plan
 /// window Save() closes for a single row (#2598).
 public void SaveBatch(IReadOnlyList<ChannelProductPlan> plans)
 {
  if(plans.Count==0)return;
  foreach(var plan in plans)ValidatePlan(plan);
  using var c=Open();using var tx=c.BeginTransaction();
  foreach(var plan in plans){plan.ChannelId=plan.ChannelId.Trim().ToLowerInvariant();plan.ShopId=plan.ShopId.Trim();SaveWithinTransaction(c,tx,plan);}
  tx.Commit();
 }
 /// Never mutates the caller's `plan` object: a preview line's ChannelPlan must
 /// stay frozen at the version it was built against, so re-applying the same
 /// (now stale) preview a second time is rejected by the version check rather
 /// than silently "succeeding" against a version this call itself just bumped.
 static void SaveWithinTransaction(SqliteConnection c,SqliteTransaction tx,ChannelProductPlan plan)
 {
  int currentVersion;
  using(var find=c.CreateCommand())
  {
   find.Transaction=tx;find.CommandText="SELECT Version FROM ChannelPlans WHERE ChannelId=$channel AND ShopId=$shop AND ProductId=$product";
   find.Parameters.AddWithValue("$channel",plan.ChannelId);find.Parameters.AddWithValue("$shop",plan.ShopId);find.Parameters.AddWithValue("$product",plan.ProductId);
   var current=find.ExecuteScalar();currentVersion=current is null?0:Convert.ToInt32(current);
   if(currentVersion!=plan.Version)throw new InvalidOperationException($"Kanal planı ({plan.ChannelId}/{plan.ShopId}/{plan.ProductId}) önizlemeden sonra değişti; yeni önizleme alınmalı.");
  }
  var nextVersion=currentVersion+1;
  var stored=new ChannelProductPlan{ChannelId=plan.ChannelId,ShopId=plan.ShopId,ProductId=plan.ProductId,ListingId=plan.ListingId,ListingUrl=plan.ListingUrl,TargetCategory=plan.TargetCategory,PlannedPrice=plan.PlannedPrice,Currency=plan.Currency,PlannedStock=plan.PlannedStock,Notes=plan.Notes,UpdatedUtc=DateTime.UtcNow,Version=nextVersion};
  using var cmd=c.CreateCommand();cmd.Transaction=tx;
  cmd.CommandText="INSERT INTO ChannelPlans(ChannelId,ShopId,ProductId,Json,Version) VALUES($channel,$shop,$product,$json,$version) ON CONFLICT(ChannelId,ShopId,ProductId) DO UPDATE SET Json=excluded.Json,Version=excluded.Version";
  cmd.Parameters.AddWithValue("$channel",plan.ChannelId);cmd.Parameters.AddWithValue("$shop",plan.ShopId);cmd.Parameters.AddWithValue("$product",plan.ProductId);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(stored));cmd.Parameters.AddWithValue("$version",nextVersion);
  cmd.ExecuteNonQuery();
 }
}
