using Microsoft.Data.Sqlite;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;
public enum SyncStatus { Pending, Running, Succeeded, Failed, Cancelled }
public enum SyncErrorClass { None, Network, Authentication, Mapping, Validation, RateLimit, Stale, Idempotency, Unsupported, Unknown }
public sealed record SyncRequest(string Channel,string Operation,string EntityId,string Version,string ShopId="default");
public sealed class SyncJob { public string Id {get;set;}=Guid.NewGuid().ToString("N"); public string Channel {get;set;}=""; public string ShopId {get;set;}="default"; public string Operation {get;set;}=""; public string EntityId {get;set;}=""; public string Version {get;set;}=""; public SyncStatus Status {get;set;}=SyncStatus.Pending; public int FailureCount {get;set;} public string LastError {get;set;}=""; public SyncErrorClass ErrorClass {get;set;}=SyncErrorClass.None; public DateTime UpdatedUtc {get;set;}=DateTime.UtcNow; }
/// Bounded diagnostics only (id/channel/shop identity, a short reason, detection
/// time) - never LastError, EntityId, or Version - for a SyncJobs row with an
/// unparsable persisted UpdatedUtc. See CatalogStore's CorruptProductRow for the
/// same pattern.
public sealed record CorruptSyncJob(string Id, string Channel, string ShopId, string Reason, DateTime DetectedUtc);
/// Raised by Get(id) when the row exists but its UpdatedUtc is corrupt - kept
/// distinct from the "not found" InvalidOperationException, and also raised by
/// Enqueue's exact-key readback so a corrupt existing row can never be silently
/// bypassed into creating a duplicate job for the same idempotency key.
public sealed class SyncJobCorruptException : Exception
{
    public string JobId { get; }
    public SyncJobCorruptException(string jobId, string reason) : base($"Sync işi bozuk (REVIEW_REQUIRED): {reason}") => JobId = jobId;
}
public sealed class SyncStore
{
 readonly string connectionString;
 public SyncStore(string? directory=null){directory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");Directory.CreateDirectory(directory);connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"catalog.db"),DefaultTimeout=15,Pooling=true}.ToString();using var c=OpenRaw();using(var pragma=c.CreateCommand()){pragma.CommandText="PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;PRAGMA busy_timeout=15000;CREATE TABLE IF NOT EXISTS SyncJobs(Id TEXT PRIMARY KEY, Channel TEXT NOT NULL, Operation TEXT NOT NULL, EntityId TEXT NOT NULL, Version TEXT NOT NULL, Status INTEGER NOT NULL, FailureCount INTEGER NOT NULL, LastError TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);CREATE INDEX IF NOT EXISTS IX_SyncJobs_StatusUpdated ON SyncJobs(Status,UpdatedUtc)";pragma.ExecuteNonQuery();}
  // Columns must exist before any index referencing them is (re)built - a legacy
  // SyncJobs table predating ShopId/ErrorClass previously hit "no such column:
  // ShopId" here because the unique index was built in Open() before this ran.
  EnsureColumn(c,"ShopId","TEXT NOT NULL DEFAULT 'default'");EnsureColumn(c,"ErrorClass","INTEGER NOT NULL DEFAULT 0");EnsureColumn(c,"Generation","INTEGER NOT NULL DEFAULT 0");
  EnsureUniqueKeyIndex(c);
  SchemaVersion.Ensure(c);
 }
 static void EnsureColumn(SqliteConnection c,string name,string definition){using var check=c.CreateCommand();check.CommandText="SELECT 1 FROM pragma_table_info('SyncJobs') WHERE name=$name";check.Parameters.AddWithValue("$name",name);if(check.ExecuteScalar()!=null)return;using var add=c.CreateCommand();add.CommandText=$"ALTER TABLE SyncJobs ADD COLUMN {name} {definition}";add.ExecuteNonQuery();}
 // Built once per schema migration (constructor), never on every Open(): a plain
 // connection open must not repeatedly DROP/CREATE an index. If legacy duplicate
 // rows (from before ShopId existed) violate the new unique key, fall back to a
 // non-unique index instead of crashing startup or silently deleting rows; existing
 // Enqueue callers still de-duplicate correctly for shop-scoped inserts going forward.
 static void EnsureUniqueKeyIndex(SqliteConnection c)
 {
  using(var check=c.CreateCommand()){check.CommandText="SELECT sql FROM sqlite_master WHERE type='index' AND name='UX_SyncJobs_Key'";var existing=check.ExecuteScalar() as string;if(existing!=null&&existing.Contains("UNIQUE",StringComparison.OrdinalIgnoreCase))return;}
  using(var drop=c.CreateCommand()){drop.CommandText="DROP INDEX IF EXISTS UX_SyncJobs_Key";drop.ExecuteNonQuery();}
  try{using var unique=c.CreateCommand();unique.CommandText="CREATE UNIQUE INDEX UX_SyncJobs_Key ON SyncJobs(Channel,ShopId,Operation,EntityId,Version)";unique.ExecuteNonQuery();}
  catch(SqliteException){using var fallback=c.CreateCommand();fallback.CommandText="CREATE INDEX IF NOT EXISTS UX_SyncJobs_Key ON SyncJobs(Channel,ShopId,Operation,EntityId,Version)";fallback.ExecuteNonQuery();}
 }
 SqliteConnection OpenRaw(){var c=new SqliteConnection(connectionString);c.Open();using var pragma=c.CreateCommand();pragma.CommandText="PRAGMA busy_timeout=15000";pragma.ExecuteNonQuery();return c;}
 SqliteConnection Open()=>OpenRaw();
 const string Select="SELECT Id,Channel,ShopId,Operation,EntityId,Version,Status,FailureCount,LastError,ErrorClass,UpdatedUtc FROM SyncJobs";
 public SyncJob Enqueue(SyncRequest request){if(string.IsNullOrWhiteSpace(request.Channel)||string.IsNullOrWhiteSpace(request.ShopId)||string.IsNullOrWhiteSpace(request.Operation)||string.IsNullOrWhiteSpace(request.EntityId)||string.IsNullOrWhiteSpace(request.Version))throw new InvalidOperationException("Sync işi için kanal, mağaza, işlem, varlık ve sürüm zorunlu.");using var c=Open();using var find=c.CreateCommand();find.CommandText="SELECT Id,Channel,ShopId,Operation,EntityId,Version,Status,FailureCount,LastError,ErrorClass,UpdatedUtc FROM SyncJobs WHERE Channel=$channel AND ShopId=$shop AND Operation=$operation AND EntityId=$entity AND Version=$version";find.Parameters.AddWithValue("$channel",request.Channel.Trim().ToLowerInvariant());find.Parameters.AddWithValue("$shop",request.ShopId.Trim());find.Parameters.AddWithValue("$operation",request.Operation);find.Parameters.AddWithValue("$entity",request.EntityId);find.Parameters.AddWithValue("$version",request.Version);
  using(var r=find.ExecuteReader())if(r.Read()){if(!TryRead(r,out var existing,out var corrupt))throw new SyncJobCorruptException(corrupt!.Id,corrupt.Reason);return existing!;}
  var job=new SyncJob{Channel=request.Channel.Trim().ToLowerInvariant(),ShopId=request.ShopId.Trim(),Operation=request.Operation,EntityId=request.EntityId,Version=request.Version};using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO SyncJobs(Id,Channel,ShopId,Operation,EntityId,Version,Status,FailureCount,LastError,ErrorClass,UpdatedUtc) VALUES($id,$channel,$shop,$operation,$entity,$version,$status,0,'',0,$updated)";cmd.Parameters.AddWithValue("$id",job.Id);cmd.Parameters.AddWithValue("$channel",job.Channel);cmd.Parameters.AddWithValue("$shop",job.ShopId);cmd.Parameters.AddWithValue("$operation",job.Operation);cmd.Parameters.AddWithValue("$entity",job.EntityId);cmd.Parameters.AddWithValue("$version",job.Version);cmd.Parameters.AddWithValue("$status",(int)job.Status);cmd.Parameters.AddWithValue("$updated",job.UpdatedUtc.ToString("O"));cmd.ExecuteNonQuery();return job;}
 static bool TryRead(SqliteDataReader r,out SyncJob? job,out CorruptSyncJob? corrupt)
 {
  job=null;corrupt=null;var id=r.GetString(0);var channel=r.GetString(1);var shop=r.GetString(2);
  if(!TryParseUtc(r.GetString(10),out var updated)){corrupt=new(id,channel,shop,"Malformed UpdatedUtc timestamp",DateTime.UtcNow);return false;}
  job=new(){Id=id,Channel=channel,ShopId=shop,Operation=r.GetString(3),EntityId=r.GetString(4),Version=r.GetString(5),Status=(SyncStatus)r.GetInt32(6),FailureCount=r.GetInt32(7),LastError=r.GetString(8),ErrorClass=(SyncErrorClass)r.GetInt32(9),UpdatedUtc=updated};
  return true;
 }
 // Only ever a format/parse failure - never conflated with a DB-busy/locked
 // SqliteException, which is raised by the surrounding command, not this parse.
 static bool TryParseUtc(string value,out DateTime result)=>DateTime.TryParse(value,null,System.Globalization.DateTimeStyles.RoundtripKind,out result);
 public SyncJob Get(string id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=Select+" WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);using var r=cmd.ExecuteReader();if(!r.Read())throw new InvalidOperationException("Sync işi bulunamadı.");if(!TryRead(r,out var job,out var corrupt))throw new SyncJobCorruptException(id,corrupt!.Reason);return job!;}
 /// Bounded diagnostics for every row whose UpdatedUtc failed to parse - never the
 /// raw LastError/EntityId/Version.
 public IReadOnlyList<CorruptSyncJob> CorruptJobs(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=Select;using var r=cmd.ExecuteReader();var result=new List<CorruptSyncJob>();while(r.Read())if(!TryRead(r,out _,out var corrupt))result.Add(corrupt!);return result;}
 /// Re-checked before the claim UPDATE so a corrupt row (unreliable UpdatedUtc)
 /// can never be claimed/started even though the UPDATE...WHERE itself only keys
 /// off Id/Status and never parses the timestamp.
 bool RowIsHealthy(string id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=Select+" WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);using var r=cmd.ExecuteReader();return r.Read()&&TryRead(r,out _,out _);}
 public bool TryStart(string id) => TryStart(id, out _);
 /// The returned generation identifies this specific Running episode: it bumps
 /// every time a job is claimed out of Pending. A completion (Succeed/Fail) that
 /// quotes back this generation can only apply while the job is still on this
 /// exact episode - see the generation-aware Succeed/Fail overloads, which close
 /// the abandoned-run recovery race (Running A -> abandoned -> Pending -> Running
 /// B -> A's late completion arrives): A's generation no longer matches once B
 /// has claimed the job, even though Status is Running again for B.
 public bool TryStart(string id, out long generation)
 {
  generation=0; if(!RowIsHealthy(id))return false;
  using var c=Open();using var cmd=c.CreateCommand();
  cmd.CommandText="UPDATE SyncJobs SET Status=$running,UpdatedUtc=$updated,Generation=Generation+1 WHERE Id=$id AND Status=$pending";
  cmd.Parameters.AddWithValue("$running",(int)SyncStatus.Running);cmd.Parameters.AddWithValue("$pending",(int)SyncStatus.Pending);cmd.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$id",id);
  if(cmd.ExecuteNonQuery()!=1)return false;
  using var sel=c.CreateCommand();sel.CommandText="SELECT Generation FROM SyncJobs WHERE Id=$id";sel.Parameters.AddWithValue("$id",id);
  generation=Convert.ToInt64(sel.ExecuteScalar());
  return true;
 }
 public bool Cancel(string id){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE SyncJobs SET Status=$cancelled,LastError=$error,ErrorClass=0,UpdatedUtc=$updated WHERE Id=$id AND Status IN ($pending,$running)";cmd.Parameters.AddWithValue("$cancelled",(int)SyncStatus.Cancelled);cmd.Parameters.AddWithValue("$pending",(int)SyncStatus.Pending);cmd.Parameters.AddWithValue("$running",(int)SyncStatus.Running);cmd.Parameters.AddWithValue("$error","Kullanıcı tarafından iptal edildi; yeniden dispatch edilmez.");cmd.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$id",id);return cmd.ExecuteNonQuery()==1;}
 /// Candidates are re-validated in .NET (not decided by raw SQL text comparison
 /// against a corrupt UpdatedUtc) before any row is actually recovered, so a
 /// corrupt Running row can never be swept back into Pending based on an
 /// unreliable/garbage timestamp accidentally satisfying the cutoff comparison.
 public int RecoverAbandonedRunning(TimeSpan lease, DateTime? nowUtc = null)
 {
  if(lease<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(lease));var now=nowUtc??DateTime.UtcNow;var cutoff=now-lease;
  using var c=Open();
  var candidateIds=new List<string>();
  using(var select=c.CreateCommand()){select.CommandText=Select+" WHERE Status=$running";select.Parameters.AddWithValue("$running",(int)SyncStatus.Running);using var r=select.ExecuteReader();while(r.Read())if(TryRead(r,out var job,out _)&&job!.UpdatedUtc<cutoff)candidateIds.Add(job.Id);}
  if(candidateIds.Count==0)return 0;
  using var tx=c.BeginTransaction();var recovered=0;
  foreach(var id in candidateIds){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE SyncJobs SET Status=$pending,LastError=$error,ErrorClass=$class,UpdatedUtc=$updated WHERE Id=$id AND Status=$running";cmd.Parameters.AddWithValue("$pending",(int)SyncStatus.Pending);cmd.Parameters.AddWithValue("$running",(int)SyncStatus.Running);cmd.Parameters.AddWithValue("$error","Önceki çalışma sonlandı; iş güvenli yeniden deneme için kuyruğa alındı.");cmd.Parameters.AddWithValue("$class",(int)SyncErrorClass.Unknown);cmd.Parameters.AddWithValue("$updated",now.ToString("O"));cmd.Parameters.AddWithValue("$id",id);recovered+=cmd.ExecuteNonQuery();}
  tx.Commit();return recovered;
 }
 /// Terminal transition, CAS'd on Status=Running (and, with the generation
 /// overload, the exact Running episode) so a late/duplicate completion callback
 /// can never overwrite Cancelled or any other terminal state. Returns false
 /// (rather than throwing) when the job exists but the transition was rejected as
 /// stale - only a genuinely missing id still throws, matching every other
 /// id-keyed method's contract.
 public bool Succeed(string id) => Succeed(id, null);
 public bool Succeed(string id, long generation) => Succeed(id, (long?)generation);
 bool Succeed(string id, long? generation)
 {
  using var c=Open();using var cmd=c.CreateCommand();
  cmd.CommandText = generation.HasValue
   ? "UPDATE SyncJobs SET Status=$status,LastError='',ErrorClass=0,UpdatedUtc=$updated WHERE Id=$id AND Status=$running AND Generation=$generation"
   : "UPDATE SyncJobs SET Status=$status,LastError='',ErrorClass=0,UpdatedUtc=$updated WHERE Id=$id AND Status=$running";
  cmd.Parameters.AddWithValue("$status",(int)SyncStatus.Succeeded);cmd.Parameters.AddWithValue("$running",(int)SyncStatus.Running);cmd.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$id",id);
  if(generation.HasValue)cmd.Parameters.AddWithValue("$generation",generation.Value);
  if(cmd.ExecuteNonQuery()==1)return true;
  return RowExistsOrThrow(c,id);
 }
 /// Same CAS discipline as Succeed, but a job can legitimately fail before ever
 /// being started (a caller that Enqueues and immediately Fails without going
 /// through TryStart), so the no-generation overload accepts Pending or Running -
 /// both are pre-terminal - while still excluding Cancelled/Succeeded/Failed.
 public bool Fail(string id,string error) => Fail(id, null, error);
 public bool Fail(string id, long generation, string error) => Fail(id, (long?)generation, error);
 bool Fail(string id, long? generation, string error)
 {
  var safe=Redact(error);var classification=Classify(safe);
  using var c=Open();using var cmd=c.CreateCommand();
  cmd.CommandText = generation.HasValue
   ? "UPDATE SyncJobs SET Status=$status,FailureCount=FailureCount+1,LastError=$error,ErrorClass=$class,UpdatedUtc=$updated WHERE Id=$id AND Status=$running AND Generation=$generation"
   : "UPDATE SyncJobs SET Status=$status,FailureCount=FailureCount+1,LastError=$error,ErrorClass=$class,UpdatedUtc=$updated WHERE Id=$id AND Status IN ($pending,$running)";
  cmd.Parameters.AddWithValue("$status",(int)SyncStatus.Failed);cmd.Parameters.AddWithValue("$error",safe);cmd.Parameters.AddWithValue("$class",(int)classification);cmd.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$id",id);
  if(generation.HasValue){cmd.Parameters.AddWithValue("$running",(int)SyncStatus.Running);cmd.Parameters.AddWithValue("$generation",generation.Value);}
  else{cmd.Parameters.AddWithValue("$pending",(int)SyncStatus.Pending);cmd.Parameters.AddWithValue("$running",(int)SyncStatus.Running);}
  if(cmd.ExecuteNonQuery()==1)return true;
  return RowExistsOrThrow(c,id);
 }
 static bool RowExistsOrThrow(SqliteConnection c,string id)
 {
  using var exists=c.CreateCommand();exists.CommandText="SELECT 1 FROM SyncJobs WHERE Id=$id";exists.Parameters.AddWithValue("$id",id);
  if(exists.ExecuteScalar() is null)throw new InvalidOperationException("Sync işi bulunamadı.");
  return false;
 }
 public void Retry(string id){var job=Get(id);if(job.Status!=SyncStatus.Failed)throw new InvalidOperationException("Yalnızca başarısız sync işleri tekrarlanabilir.");if(!IsRetryable(job.ErrorClass))throw new InvalidOperationException($"{job.ErrorClass} sınıfındaki sync işi otomatik tekrar denenemez; veriyi düzeltip yeni önizleme oluşturun.");if(job.FailureCount>=3)throw new InvalidOperationException("Sync işi üç başarısız denemeden sonra durduruldu.");using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE SyncJobs SET Status=$status,LastError='',ErrorClass=0,UpdatedUtc=$updated WHERE Id=$id";cmd.Parameters.AddWithValue("$status",(int)SyncStatus.Pending);cmd.Parameters.AddWithValue("$updated",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}
 public static TimeSpan RetryDelay(int failureCount){if(failureCount<1)throw new ArgumentOutOfRangeException(nameof(failureCount));return TimeSpan.FromSeconds(Math.Min(300,Math.Pow(2,failureCount)*5));}
 /// A malformed UpdatedUtc must never crash the whole read - the row is excluded
 /// from the healthy result and reported only via CorruptJobs(); detection re-runs
 /// from the row's own stored text every call, so it stays stable across a
 /// restart without a separate tracking table.
 public IReadOnlyList<SyncJob> List(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=Select+" ORDER BY UpdatedUtc DESC";using var r=cmd.ExecuteReader();var result=new List<SyncJob>();while(r.Read())if(TryRead(r,out var job,out _))result.Add(job!);return result;}
 public static SyncErrorClass Classify(string error){var text=error.ToLowerInvariant();if(text.Contains("429")||text.Contains("rate")||text.Contains("istek sınır"))return SyncErrorClass.RateLimit;if(text.Contains("401")||text.Contains("403")||text.Contains("auth")||text.Contains("yetki")||text.Contains("credential"))return SyncErrorClass.Authentication;if(text.Contains("timeout")||text.Contains("zaman aş")||text.Contains("network")||text.Contains("ağ"))return SyncErrorClass.Network;if(text.Contains("mapping")||text.Contains("eşleş"))return SyncErrorClass.Mapping;if(text.Contains("stale")||text.Contains("önizleme")||text.Contains("güncelliğini"))return SyncErrorClass.Stale;if(text.Contains("duplicate")||text.Contains("idempot")||text.Contains("zaten"))return SyncErrorClass.Idempotency;if(text.Contains("validation")||text.Contains("geçersiz")||text.Contains("zorunlu")||text.Contains("eksik"))return SyncErrorClass.Validation;if(text.Contains("blocked")||text.Contains("desteklenmiyor"))return SyncErrorClass.Unsupported;return SyncErrorClass.Unknown;}
 public static bool IsRetryable(SyncErrorClass classification)=>classification is SyncErrorClass.Network or SyncErrorClass.RateLimit or SyncErrorClass.Authentication or SyncErrorClass.Unknown;
 // Delegates to the shared AuditStore.Sanitize redactor, which masks the secret
 // VALUE (key=value, query string, Authorization Bearer/Basic). The previous
 // implementation here only replaced the key NAME (e.g. "api_key" -> "[redacted]")
 // and left the actual secret value in LastError untouched.
 static string Redact(string value)=>TrMarketplaceHubDesktop.AuditStore.Sanitize(value).Trim();
 public static string Explain(SyncErrorClass c)=>c switch { SyncErrorClass.Network=>"Ağ bağlantısı veya zaman aşımı; tekrar deneyin.", SyncErrorClass.Authentication=>"Yetkilendirme geçersiz veya süresi dolmuş; bağlantıyı yenileyin.", SyncErrorClass.RateLimit=>"İstek sınırına ulaşıldı; bekleyip tekrar deneyin.", SyncErrorClass.Mapping=>"Alan veya kategori eşleşmesi eksik.", SyncErrorClass.Validation=>"Gönderilen veri doğrulama kurallarını karşılamıyor.", SyncErrorClass.Stale=>"Önizleme güncel değil; yenileyin.", SyncErrorClass.Idempotency=>"Aynı işlem daha önce kaydedildi.", SyncErrorClass.Unsupported=>"Bu işlem connector tarafından desteklenmiyor.", _=>"Beklenmeyen senkronizasyon hatası." };}

