using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;
namespace TrMarketplaceHubDesktop;
/// Bounded diagnostics only (identity + a short reason code + detection time) -
/// never Json/Notes/ListingUrl - for a ChannelPlans row that failed to
/// deserialize, failed domain re-validation, or whose payload identity does not
/// match its SQL primary key. Mirrors CatalogStore's CorruptProductRow pattern.
public sealed record CorruptChannelPlan(string ChannelId, string ShopId, string ProductId, string Reason, DateTime DetectedUtc);
/// Raised by Find(...) when the row exists but is corrupt/identity-mismatched -
/// kept distinct from returning null (which means "no plan yet"), so a caller
/// can never mistake "needs repair" for "not configured".
public sealed class ChannelPlanCorruptException : Exception
{
    public string ChannelId { get; }
    public string ShopId { get; }
    public string ProductId { get; }
    public ChannelPlanCorruptException(string channelId, string shopId, string productId, string reason)
        : base($"Kanal planı bozuk (REVIEW_REQUIRED): {reason}") { ChannelId = channelId; ShopId = shopId; ProductId = productId; }
}
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
 const int MaxPlanJsonBytes=1024*1024;
 const string SelectWithIdentity="SELECT ChannelId,ShopId,ProductId,Json,Version FROM ChannelPlans";
 /// Deserializes and re-validates a persisted row, checking that the payload's own
 /// identity/domain-value contract still matches its SQL primary key and current
 /// business rules. A row that fails any of these checks is never silently
 /// materialized as a normal plan and never rewritten during an ordinary read -
 /// the raw row is left untouched for an explicit repair action.
 static string? PlanCorruptionReason(string channel,string shop,string product,string json,out ChannelProductPlan? plan)
 {
  plan=null;
  if(string.IsNullOrWhiteSpace(json))return "boş kayıt";
  if(System.Text.Encoding.UTF8.GetByteCount(json)>MaxPlanJsonBytes)return "kayıt boyutu sınırı aşıyor";
  ChannelProductPlan? candidate;
  try{candidate=JsonSerializer.Deserialize<ChannelProductPlan>(json);}
  catch(JsonException){return "geçersiz JSON";}
  if(candidate is null)return "boş JSON";
  if(!string.Equals(candidate.ChannelId?.Trim().ToLowerInvariant(),channel,StringComparison.Ordinal)||!string.Equals(candidate.ShopId?.Trim(),shop,StringComparison.Ordinal)||!string.Equals(candidate.ProductId,product,StringComparison.Ordinal))return "kimlik uyuşmazlığı (yanlış kanal/mağaza/ürün)";
  if(candidate.PlannedPrice<0||candidate.PlannedStock<0)return "negatif fiyat veya stok";
  if(candidate.Currency is null||candidate.Currency.Length!=3||!candidate.Currency.All(c=>c>='A'&&c<='Z'))return "geçersiz para birimi";
  if(!string.IsNullOrEmpty(candidate.ListingUrl)&&(!Uri.TryCreate(candidate.ListingUrl,UriKind.Absolute,out var uri)||(uri.Scheme!="https"&&uri.Scheme!="http")||!string.IsNullOrEmpty(uri.UserInfo)))return "geçersiz ilan bağlantısı";
  candidate.ChannelId=channel;candidate.ShopId=shop;candidate.ProductId=product;candidate.Version=0;plan=candidate;return null;
 }
 static bool TryReadPlan(SqliteDataReader r,out ChannelProductPlan? plan,out CorruptChannelPlan? corrupt)
 {
  var channel=r.GetString(0);var shop=r.GetString(1);var product=r.GetString(2);var json=r.GetString(3);var version=r.GetInt32(4);
  var reason=PlanCorruptionReason(channel,shop,product,json,out plan);
  if(plan is not null)plan.Version=version;
  corrupt=reason is null?null:new(channel,shop,product,reason,DateTime.UtcNow);
  return reason is null;
 }
 public ChannelProductPlan? Find(string channel,string shop,string product)
 {
  Identity(channel,shop,product);using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=SelectWithIdentity+" WHERE ChannelId=$channel AND ShopId=$shop AND ProductId=$product";cmd.Parameters.AddWithValue("$channel",channel.Trim().ToLowerInvariant());cmd.Parameters.AddWithValue("$shop",shop.Trim());cmd.Parameters.AddWithValue("$product",product);
  using var r=cmd.ExecuteReader();if(!r.Read())return null;
  if(!TryReadPlan(r,out var plan,out var corrupt))throw new ChannelPlanCorruptException(corrupt!.ChannelId,corrupt.ShopId,corrupt.ProductId,corrupt.Reason);
  return plan;
 }
 public ChannelProductPlan? Find(MarketplaceConnection connection,string product)
 {
  if(connection is null)throw new ArgumentNullException(nameof(connection));
  var current=new MarketplaceConnectionStore(Path.GetDirectoryName(new SqliteConnectionStringBuilder(connectionString).DataSource)).Get(connection.Id)??throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
  if(!current.Enabled||current.Channel!=connection.Channel||current.ShopId!=connection.ShopId)throw new InvalidOperationException("Mağaza bağlantı kimliği değişti veya devre dışı.");
  return Find(current.Channel,current.ShopId,product);
 }
 public IReadOnlyList<ChannelProductPlan> List(string? channel=null,string? shop=null,string? product=null)
 {
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=SelectWithIdentity+" WHERE ($channel='' OR ChannelId=$channel) AND ($shop='' OR ShopId=$shop) AND ($product='' OR ProductId=$product) ORDER BY ChannelId,ShopId,ProductId";cmd.Parameters.AddWithValue("$channel",channel?.Trim().ToLowerInvariant()??"");cmd.Parameters.AddWithValue("$shop",shop?.Trim()??"");cmd.Parameters.AddWithValue("$product",product?.Trim()??"");using var r=cmd.ExecuteReader();var result=new List<ChannelProductPlan>();while(r.Read()){if(TryReadPlan(r,out var plan,out _))result.Add(plan!);}return result;
 }
 /// Read-only diagnostics: identity + bounded reason code only, never raw
 /// Json/Notes/ListingUrl. Corrupt rows are never auto-deleted/overwritten by
 /// List/Find; this is the only way to discover them for operator repair.
 public IReadOnlyList<CorruptChannelPlan> CorruptPlans(string? channel=null,string? shop=null,string? product=null)
 {
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=SelectWithIdentity+" WHERE ($channel='' OR ChannelId=$channel) AND ($shop='' OR ShopId=$shop) AND ($product='' OR ProductId=$product) ORDER BY ChannelId,ShopId,ProductId";cmd.Parameters.AddWithValue("$channel",channel?.Trim().ToLowerInvariant()??"");cmd.Parameters.AddWithValue("$shop",shop?.Trim()??"");cmd.Parameters.AddWithValue("$product",product?.Trim()??"");using var r=cmd.ExecuteReader();var result=new List<CorruptChannelPlan>();while(r.Read()){if(!TryReadPlan(r,out _,out var corrupt))result.Add(corrupt!);}return result;
 }
 // Purely technical storage/UI bounds - never a provider-format contract - so an
 // oversized identity/URL/JSON can never reach persistence, an HTTP header, or a
 // UI control. See #2539.
 const int MaxIdentityFieldLength=200;
 const int MaxListingUrlLength=2048;
 const int MaxTargetCategoryLength=300;
 const int MaxNotesLength=4000;
 static void ValidatePlan(ChannelProductPlan plan)
 {
  Identity(plan.ChannelId,plan.ShopId,plan.ProductId);
  if(plan.ChannelId.Trim().Length>MaxIdentityFieldLength||plan.ShopId.Trim().Length>MaxIdentityFieldLength||plan.ProductId.Length>MaxIdentityFieldLength||(plan.ListingId?.Length??0)>MaxIdentityFieldLength)throw new ArgumentException($"Kanal, mağaza, ürün veya ilan kimliği en fazla {MaxIdentityFieldLength} karakter olabilir.");
  if((plan.TargetCategory?.Length??0)>MaxTargetCategoryLength)throw new ArgumentException($"Hedef kategori en fazla {MaxTargetCategoryLength} karakter olabilir.");
  if((plan.Notes?.Length??0)>MaxNotesLength)throw new ArgumentException($"Notlar en fazla {MaxNotesLength} karakter olabilir.");
  if(plan.PlannedPrice<0||plan.PlannedStock<0)throw new ArgumentException("Plan fiyatı ve stok negatif olamaz.");
  if(plan.Currency is null||plan.Currency.Length!=3||!plan.Currency.All(c=>c>='A'&&c<='Z'))throw new ArgumentException("Para birimini üç büyük harfle girin (USD, EUR, RUB, TRY).");
  if(!string.IsNullOrEmpty(plan.ListingUrl))
  {
   if(plan.ListingUrl.Length>MaxListingUrlLength)throw new ArgumentException($"İlan bağlantısı en fazla {MaxListingUrlLength} karakter olabilir.");
   if(!Uri.TryCreate(plan.ListingUrl,UriKind.Absolute,out var uri)||(uri.Scheme!="https"&&uri.Scheme!="http")||!string.IsNullOrEmpty(uri.UserInfo))throw new ArgumentException("İlan bağlantısı geçerli bir HTTP/HTTPS adresi olmalı.");
  }
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
 public void Save(MarketplaceConnection connection,ChannelProductPlan plan)
 {
  if(connection is null)throw new ArgumentNullException(nameof(connection));
  var current=new MarketplaceConnectionStore(Path.GetDirectoryName(new SqliteConnectionStringBuilder(connectionString).DataSource)).Get(connection.Id)??throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
  if(!current.Enabled||current.Channel!=connection.Channel||current.ShopId!=connection.ShopId||plan.ChannelId.Trim().ToLowerInvariant()!=current.Channel||plan.ShopId.Trim()!=current.ShopId)
   throw new InvalidOperationException("Kanal planı başka bir mağaza hesabına ait.");
  Save(plan);
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
  var json=JsonSerializer.Serialize(stored);
  // Fail-closed before any DB write - never truncate/repair silently. This is
  // the same bound TryReadPlan already enforces on read (#2665); enforcing it
  // here too means a plan can never be written in the first place only to be
  // quarantined as corrupt the next time it's read.
  if(System.Text.Encoding.UTF8.GetByteCount(json)>MaxPlanJsonBytes)throw new ArgumentException("Kanal planı boyutu izin verilen sınırı aşıyor.");
  using var cmd=c.CreateCommand();cmd.Transaction=tx;
  cmd.CommandText="INSERT INTO ChannelPlans(ChannelId,ShopId,ProductId,Json,Version) VALUES($channel,$shop,$product,$json,$version) ON CONFLICT(ChannelId,ShopId,ProductId) DO UPDATE SET Json=excluded.Json,Version=excluded.Version";
  cmd.Parameters.AddWithValue("$channel",plan.ChannelId);cmd.Parameters.AddWithValue("$shop",plan.ShopId);cmd.Parameters.AddWithValue("$product",plan.ProductId);cmd.Parameters.AddWithValue("$json",json);cmd.Parameters.AddWithValue("$version",nextVersion);
  cmd.ExecuteNonQuery();
 }
}
