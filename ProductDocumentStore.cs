using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

/// <summary>One compliance document of a product: its metadata and where its bytes sit in the store — never the path it was picked from.</summary>
public sealed record ProductDocument(string Id, string ProductId, string Kind, string DisplayName, string Extension, long Bytes, string Sha256, int Revision, DateTime? ExpiresOn, string Origin, DateTime AddedUtc);

/// <summary>A document with its state as of a moment: OK, EXPIRING (inside the warning window), EXPIRED, MISSING (the file is gone) or CORRUPT (the file does not verify).</summary>
public sealed record ProductDocumentState(ProductDocument Document, string Status, string Words)
{
    public const string Ok = "OK", Expiring = "EXPIRING", Expired = "EXPIRED", Missing = "MISSING", Corrupt = "CORRUPT";
    public bool IsUsable => Status is Ok or Expiring;
}

public static class ProductDocumentKinds
{
    public static readonly IReadOnlyList<(string Key, string Label)> All = new[] { ("safety", "Güvenlik bilgi formu"), ("manufacturer", "Üretici beyanı"), ("certificate", "Uygunluk sertifikası"), ("other", "Diğer belge") };
    public static bool IsKnown(string? key) => All.Any(k => k.Key.Equals((key ?? "").Trim(), StringComparison.Ordinal));
    public static string Label(string? key) => All.FirstOrDefault(k => k.Key.Equals((key ?? "").Trim(), StringComparison.Ordinal)).Label ?? "Belge";
}

/// <summary>
/// Product compliance documents (#911): safety data sheets, manufacturer declarations, certificates. The metadata —
/// kind, revision, expiry, size, hash, origin, the picked file's name cleaned — lives in the catalogue database; the
/// bytes live under the data directory (documents/&lt;productId&gt;/&lt;id&gt;.&lt;ext&gt;), the way the feed cache keeps its files:
/// an id-based name, a temporary file moved into place, a hash that must verify before the file is served, and
/// nothing of the path the file was picked from. A product id is validated before it becomes a folder, an extension
/// must be on the allow-list, a file must be non-empty and within the cap, an expired document is not accepted,
/// and the same bytes under the same kind are one revision, not two. Audit rows name the kind and the revision,
/// never the file.
/// </summary>
public sealed class ProductDocumentStore
{
    public const string FolderName = "documents";
    public const long MaxBytes = 20L * 1024 * 1024;
    public const string AttachAction = "document-attach", RemoveAction = "document-remove";
    public const string DiagnosticName = "Ürün belgeleri";
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(30);
    public static readonly IReadOnlySet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".png", ".jpg", ".jpeg" };
    static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    readonly string dataDirectory; readonly string connectionString;

    public ProductDocumentStore(string dataDirectory)
    {
        this.dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        Directory.CreateDirectory(dataDirectory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDirectory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS ProductDocuments(Id TEXT PRIMARY KEY, ProductId TEXT NOT NULL, Kind TEXT NOT NULL, DisplayName TEXT NOT NULL, Extension TEXT NOT NULL, Bytes INTEGER NOT NULL, Sha256 TEXT NOT NULL, Revision INTEGER NOT NULL, ExpiresOn TEXT NOT NULL DEFAULT '', Origin TEXT NOT NULL, AddedUtc TEXT NOT NULL);CREATE INDEX IF NOT EXISTS IX_ProductDocuments_Product ON ProductDocuments(ProductId);CREATE UNIQUE INDEX IF NOT EXISTS UX_ProductDocuments_Revision ON ProductDocuments(ProductId,Kind,Revision)";
        cmd.ExecuteNonQuery();
    }

    public string Root => Path.Combine(dataDirectory, FolderName);
    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    string ProductDirectory(string productId)
    {
        if (!SafeId.IsMatch(productId ?? "")) throw new ArgumentException("Ürün kimliği belge deposu için geçersiz.", nameof(productId));
        return Path.Combine(Root, productId);
    }

    /// <summary>Attaches a picked file: its bytes copied into the store under an id-based name, its metadata recorded; only the picked file's name (cleaned) is kept, never its path.</summary>
    public ProductDocument Attach(string productId, string kind, string sourcePath, DateTime? expiresOn, string origin = FieldProvenance.ManualKind, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        var directory = ProductDirectory(productId);
        kind = (kind ?? "").Trim();
        if (!ProductDocumentKinds.IsKnown(kind)) throw new InvalidOperationException("Belge türü tanınmıyor.");
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension)) throw new InvalidOperationException("Belge biçimi desteklenmiyor; PDF, PNG veya JPEG ekleyin.");
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("Belge dosyası bulunamadı.");
        if (info.Length == 0 || info.Length > MaxBytes) throw new InvalidOperationException("Belge boş veya 20 MB sınırını aşıyor.");
        var now = nowUtc ?? DateTime.UtcNow;
        if (expiresOn is { } expiry && expiry.Date < now.Date) throw new InvalidOperationException("Belgenin geçerlilik tarihi geçmiş; süresi dolmuş belge eklenemez.");
        var bytes = File.ReadAllBytes(sourcePath); var hash = Convert.ToHexString(SHA256.HashData(bytes));
        using var c = Open(); using var tx = c.BeginTransaction();
        var siblings = List(c, tx, productId).Where(d => d.Kind == kind).ToList();
        if (siblings.FirstOrDefault(d => d.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)) is { } same) throw new InvalidOperationException($"Aynı belge zaten ekli (rev. {same.Revision.ToString(CultureInfo.CurrentCulture)}).");
        var revision = siblings.Count == 0 ? 1 : siblings.Max(d => d.Revision) + 1;
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + extension); var temporary = path + ".tmp-" + id[..8];
        File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, overwrite: true);
        var document = new ProductDocument(id, productId, kind, CleanName(Path.GetFileName(sourcePath)), extension, bytes.LongLength, hash, revision, expiresOn?.Date, (origin ?? "").Trim(), now);
        using var insert = c.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO ProductDocuments(Id,ProductId,Kind,DisplayName,Extension,Bytes,Sha256,Revision,ExpiresOn,Origin,AddedUtc) VALUES($id,$product,$kind,$name,$ext,$bytes,$sha,$rev,$expires,$origin,$added)";
        insert.Parameters.AddWithValue("$id", document.Id); insert.Parameters.AddWithValue("$product", productId); insert.Parameters.AddWithValue("$kind", kind); insert.Parameters.AddWithValue("$name", document.DisplayName); insert.Parameters.AddWithValue("$ext", extension); insert.Parameters.AddWithValue("$bytes", document.Bytes); insert.Parameters.AddWithValue("$sha", hash); insert.Parameters.AddWithValue("$rev", revision);
        insert.Parameters.AddWithValue("$expires", document.ExpiresOn is { } e ? e.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ""); insert.Parameters.AddWithValue("$origin", document.Origin); insert.Parameters.AddWithValue("$added", now.ToString("O", CultureInfo.InvariantCulture));
        insert.ExecuteNonQuery(); tx.Commit();
        return document;
    }

    public IReadOnlyList<ProductDocument> List(string productId) { using var c = Open(); return List(c, null, productId); }
    public ProductDocument? Find(string id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Id,ProductId,Kind,DisplayName,Extension,Bytes,Sha256,Revision,ExpiresOn,Origin,AddedUtc FROM ProductDocuments WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id ?? ""); using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : null; }

    /// <summary>The path of a document's bytes for the application to open — only a path inside the store, and only when the file is present and verifies; null otherwise.</summary>
    public string? PathFor(ProductDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var path = StoredPath(document);
        return File.Exists(path) && Verifies(path, document.Sha256) ? path : null;
    }

    public ProductDocumentState State(ProductDocument document, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(document);
        var path = StoredPath(document); var label = ProductDocumentKinds.Label(document.Kind); var revision = "rev. " + document.Revision.ToString(CultureInfo.CurrentCulture);
        if (!File.Exists(path)) return new(document, ProductDocumentState.Missing, $"{label} · {revision} · dosya eksik");
        if (!Verifies(path, document.Sha256)) return new(document, ProductDocumentState.Corrupt, $"{label} · {revision} · dosya bozuk (özet uyuşmuyor)");
        if (document.ExpiresOn is { } expiry)
        {
            var when = expiry.ToString("d", CultureInfo.CurrentCulture);
            if (expiry.Date < nowUtc.Date) return new(document, ProductDocumentState.Expired, $"{label} · {revision} · süresi doldu ({when})");
            if (expiry.Date - nowUtc.Date <= ExpiryWarning) return new(document, ProductDocumentState.Expiring, $"{label} · {revision} · {when} tarihinde dolacak");
            return new(document, ProductDocumentState.Ok, $"{label} · {revision} · {when} tarihine kadar geçerli");
        }
        return new(document, ProductDocumentState.Ok, $"{label} · {revision} · süresiz");
    }

    public IReadOnlyList<ProductDocumentState> States(string productId, DateTime nowUtc) => List(productId).Select(d => State(d, nowUtc)).ToList();

    /// <summary>Validation-style words for a product: an expired, missing or corrupt document is named by kind and revision.</summary>
    public IReadOnlyList<string> Findings(string productId, DateTime nowUtc) => States(productId, nowUtc).Where(s => !s.IsUsable).Select(s => "Belge: " + s.Words).ToList();

    public bool Remove(string id)
    {
        var document = Find(id); if (document is null) return false;
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM ProductDocuments WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
        TryDelete(StoredPath(document));
        return true;
    }

    /// <summary>The audit row: kind, revision, size and origin — never the file's name or path.</summary>
    public static AuditEvent ToAudit(ProductDocument document, string action)
    {
        ArgumentNullException.ThrowIfNull(document);
        var who = document.Origin.Equals(FieldProvenance.FeedKind, StringComparison.OrdinalIgnoreCase) ? "kaynaktan" : "elle";
        return new AuditEvent { Module = "catalog", Action = action, ProductId = document.ProductId, Outcome = "Info", Detail = $"{ProductDocumentKinds.Label(document.Kind)} · rev. {document.Revision.ToString(CultureInfo.InvariantCulture)} · {document.Extension} · {document.Bytes.ToString(CultureInfo.InvariantCulture)} bayt · {who}" };
    }

    /// <summary>The diagnostics line: documents, and how many are missing, corrupt or expired as of now.</summary>
    public DiagnosticCheck Check(DateTime? nowUtc = null)
    {
        try
        {
            var now = nowUtc ?? DateTime.UtcNow;
            using var c = Open(); var all = List(c, null, null);
            var states = all.Select(d => State(d, now)).ToList();
            var missing = states.Count(s => s.Status is ProductDocumentState.Missing or ProductDocumentState.Corrupt); var expired = states.Count(s => s.Status == ProductDocumentState.Expired);
            var detail = $"{all.Count.ToString("N0", CultureInfo.CurrentCulture)} belge · {missing.ToString("N0", CultureInfo.CurrentCulture)} eksik/bozuk · {expired.ToString("N0", CultureInfo.CurrentCulture)} süresi dolmuş";
            return new(DiagnosticName, missing > 0 || expired > 0 ? "WARN" : "OK", detail);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException) { return new(DiagnosticName, "ERROR", AuditStore.Sanitize(error.Message)); }
    }

    /// <summary>A picked file's name as the record may keep it: the last path segment only, control characters dropped, capped, redacted.</summary>
    public static string CleanName(string? fileName)
    {
        var name = (fileName ?? "").Replace('\\', '/'); name = name[(name.LastIndexOf('/') + 1)..];
        name = new string(name.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (name.Length > 120) name = name[..120];
        return AuditStore.Redact(name);
    }

    string StoredPath(ProductDocument document) => Path.Combine(ProductDirectory(document.ProductId), document.Id + document.Extension);

    static List<ProductDocument> List(SqliteConnection c, SqliteTransaction? tx, string? productId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id,ProductId,Kind,DisplayName,Extension,Bytes,Sha256,Revision,ExpiresOn,Origin,AddedUtc FROM ProductDocuments" + (productId is null ? "" : " WHERE ProductId=$product") + " ORDER BY Kind,Revision";
        if (productId is not null) cmd.Parameters.AddWithValue("$product", productId);
        using var r = cmd.ExecuteReader(); var rows = new List<ProductDocument>(); while (r.Read()) rows.Add(Read(r)); return rows;
    }

    static ProductDocument Read(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetString(6), r.GetInt32(7),
        DateTime.TryParseExact(r.GetString(8), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires) ? expires : null, r.GetString(9),
        DateTime.TryParse(r.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var added) ? added : DateTime.MinValue);

    static bool Verifies(string path, string sha256)
    {
        try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(sha256, StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
