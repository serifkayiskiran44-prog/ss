using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

/// <summary>The canonical customer of a store: one record per identity key (the e-mail, else the phone), created from the first snapshot, changed only on purpose, counting its orders. Shown masked.</summary>
public sealed record CanonicalCustomer(string Marketplace, string ShopId, string Key, string Name, string Email, string Phone, string Address, DateTime CreatedUtc, DateTime UpdatedUtc, int Version, int Orders)
{
    public string MaskedWords => $"{PiiReveal.MaskName(Name)} · {PiiReveal.MaskEmail(Email)} · {PiiReveal.MaskPhone(Phone)} · {PiiReveal.MaskAddress(Address)}";
}

/// <summary>The link between one order's customer snapshot and the canonical record: the outcome when it was made, the snapshot's fingerprint, the duplicate candidate when one was seen, the words (masked, keys abbreviated).</summary>
public sealed record CustomerLink(string Marketplace, string ShopId, string OrderId, string CustomerKey, string Outcome, string SnapshotFingerprint, string CandidateKey, DateTime LinkedUtc, string Words)
{
    public const string New = "NEW", Existing = "EXISTING", DuplicateCandidate = "DUPLICATE_CANDIDATE", Unkeyed = "UNKEYED";
}

/// <summary>
/// Order–customer relationship integrity (#943). An order's customer snapshot (#841, kept apart from the order) is
/// linked, within the store's scope, to one canonical customer record keyed by the e-mail (else the phone): the
/// first snapshot creates the record, later ones find it and count the order, and a later change of the canonical
/// record never rewrites an order's snapshot — what the order was placed with stays what it was. A snapshot whose
/// identity is new but whose phone, or name and address, match another record of the store is linked to its own
/// record and flagged a duplicate candidate: merging is the operator's decision, never silent. A snapshot without
/// an e-mail or a phone gets no canonical record and says so. Every word shown is masked; keys are hashes, shown
/// abbreviated; the audit trail carries key prefixes and outcomes, never a value.
/// </summary>
public static class CustomerIdentity
{
    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();
    public static string NormalizePhone(string? phone) => new string((phone ?? "").Where(char.IsDigit).ToArray());
    static string NormalizeText(string? value) => string.Join(' ', (value ?? "").Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The identity key within a store: the e-mail, else a phone of at least seven digits; empty when neither.</summary>
    public static string KeyFor(string marketplace, string shopId, string? email, string? phone)
    {
        var e = NormalizeEmail(email); var p = NormalizePhone(phone);
        var identity = e.Length > 0 ? "email:" + e : p.Length >= 7 ? "phone:" + p : "";
        return identity.Length == 0 ? "" : Hash((marketplace ?? "").Trim().ToLowerInvariant() + "|" + (shopId ?? "").Trim() + "|" + identity)[..16];
    }

    /// <summary>The snapshot's content fingerprint, for change detection only.</summary>
    public static string Fingerprint(OrderCustomer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        return Hash(string.Join("|", (customer.Name ?? "").Trim(), NormalizeEmail(customer.Email), NormalizePhone(customer.Phone), (customer.Address ?? "").Trim()))[..16];
    }

    /// <summary>Whether two records look like one person under another identity: the same phone, or the same name and address (both present).</summary>
    public static bool LooksLikeTheSamePerson(string? nameA, string? addressA, string? phoneA, string? nameB, string? addressB, string? phoneB)
    {
        // The same number often carries a leading trunk zero or a country code depending on who typed it; the last
        // seven digits are the subscriber number, so comparing those catches "0532 123 45 67" and "+90 532 123 45 67" as one phone.
        var pa = NormalizePhone(phoneA); var pb = NormalizePhone(phoneB);
        if (pa.Length >= 7 && pb.Length >= 7 && pa[^7..] == pb[^7..]) return true;
        var na = NormalizeText(nameA); var aa = NormalizeText(addressA);
        return na.Length > 0 && aa.Length > 0 && na == NormalizeText(nameB) && aa == NormalizeText(addressB);
    }

    public static string Short(string key) => key.Length <= 8 ? key : key[..8];
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>The canonical customers and the order links, in orders.db beside the orders and their snapshots.</summary>
public sealed class CustomerIdentityStore
{
    readonly string directory; readonly string connectionString;

    public CustomerIdentityStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory); this.directory = directory;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "orders.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS customers(marketplace TEXT NOT NULL, shop TEXT NOT NULL, key TEXT NOT NULL, payload TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, version INTEGER NOT NULL, orders INTEGER NOT NULL, PRIMARY KEY(marketplace, shop, key)); CREATE TABLE IF NOT EXISTS order_customer_links(marketplace TEXT NOT NULL, shop TEXT NOT NULL, order_id TEXT NOT NULL, customer_key TEXT NOT NULL, outcome TEXT NOT NULL, snapshot_fp TEXT NOT NULL, candidate_key TEXT NOT NULL DEFAULT '', linked_utc TEXT NOT NULL, PRIMARY KEY(marketplace, shop, order_id))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() { var c = SqliteConnectionPolicy.Open(connectionString); using var pragma = c.CreateCommand(); pragma.CommandText = "PRAGMA busy_timeout=15000"; pragma.ExecuteNonQuery(); return c; }

    /// <summary>Links an order's customer snapshot to the store's canonical record: creating it, finding it, or flagging a duplicate candidate; a relink keeps the original outcome. The snapshot itself is never changed here.</summary>
    public CustomerLink Link(OrderCustomer snapshot, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.Marketplace) || string.IsNullOrWhiteSpace(snapshot.ShopId) || string.IsNullOrWhiteSpace(snapshot.OrderId)) throw new ArgumentException("Pazaryeri, mağaza ve sipariş numarası zorunlu.");
        var m = snapshot.Marketplace.Trim().ToLowerInvariant(); var s = snapshot.ShopId.Trim(); var o = snapshot.OrderId.Trim();
        var key = CustomerIdentity.KeyFor(m, s, snapshot.Email, snapshot.Phone); var fingerprint = CustomerIdentity.Fingerprint(snapshot);
        var at = DateTime.SpecifyKind(nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : nowUtc, DateTimeKind.Utc);
        using var c = Open(); using var tx = c.BeginTransaction();
        var previous = ReadLink(c, tx, m, s, o);
        CustomerLink link;
        if (key.Length == 0)
            link = new(m, s, o, "", CustomerLink.Unkeyed, fingerprint, "", at, "kimlik anahtarı yok (e-posta ya da telefon yok); kanonik müşteri kaydı oluşturulmadı");
        else
        {
            var canonical = ReadCustomer(c, tx, m, s, key);
            if (canonical is null)
            {
                var candidate = ReadCustomers(c, tx, m, s).FirstOrDefault(x => x.Key != key && CustomerIdentity.LooksLikeTheSamePerson(snapshot.Name, snapshot.Address, snapshot.Phone, x.Name, x.Address, x.Phone));
                canonical = new(m, s, key, (snapshot.Name ?? "").Trim(), CustomerIdentity.NormalizeEmail(snapshot.Email), (snapshot.Phone ?? "").Trim(), (snapshot.Address ?? "").Trim(), at, at, 1, 1); // the identity's own normalization (lower-cased e-mail) is what the record keeps
                WriteCustomer(c, tx, canonical);
                link = candidate is null
                    ? new(m, s, o, key, CustomerLink.New, fingerprint, "", at, $"yeni kanonik müşteri {CustomerIdentity.Short(key)}: {canonical.MaskedWords}")
                    : new(m, s, o, key, CustomerLink.DuplicateCandidate, fingerprint, candidate.Key, at, $"yeni kanonik müşteri {CustomerIdentity.Short(key)}; olası mükerrer: {CustomerIdentity.Short(candidate.Key)} (aynı telefon ya da aynı ad ve adres) — birleştirme otomatik değildir, operatörün kararıdır");
            }
            else
            {
                if (previous is null || !string.Equals(previous.CustomerKey, key, StringComparison.Ordinal)) { canonical = canonical with { Orders = canonical.Orders + 1 }; WriteCustomer(c, tx, canonical); }
                var outcome = previous is not null && string.Equals(previous.CustomerKey, key, StringComparison.Ordinal) ? previous.Outcome : CustomerLink.Existing;
                var candidateKey = previous is not null && string.Equals(previous.CustomerKey, key, StringComparison.Ordinal) ? previous.CandidateKey : "";
                link = new(m, s, o, key, outcome, fingerprint, candidateKey, at, $"mevcut kanonik müşteri {CustomerIdentity.Short(key)} ({canonical.Orders.ToString(CultureInfo.InvariantCulture)} sipariş, sürüm {canonical.Version.ToString(CultureInfo.InvariantCulture)}): {canonical.MaskedWords}" + (outcome == CustomerLink.DuplicateCandidate ? $"; olası mükerrer: {CustomerIdentity.Short(candidateKey)}" : ""));
            }
        }
        WriteLink(c, tx, link); tx.Commit();
        if (previous is null && link.Outcome is CustomerLink.New or CustomerLink.DuplicateCandidate)
            try { new AuditStore(directory).Append(new AuditEvent { Module = "orders", Action = "customer-link", Marketplace = m, ShopId = s, OrderId = o, Outcome = link.Outcome == CustomerLink.DuplicateCandidate ? "Warning" : "Info", Detail = link.Outcome == CustomerLink.DuplicateCandidate ? $"kanonik müşteri {CustomerIdentity.Short(key)} oluşturuldu; olası mükerrer {CustomerIdentity.Short(link.CandidateKey)}" : $"kanonik müşteri {CustomerIdentity.Short(key)} oluşturuldu" }); } catch (Exception) { }
        return link;
    }

    /// <summary>Changes the canonical record on purpose: a new version; the identity key stays; no order snapshot is touched.</summary>
    public CanonicalCustomer UpdateCanonical(string marketplace, string shopId, string key, string? name, string? email, string? phone, string? address, DateTime nowUtc)
    {
        var m = (marketplace ?? "").Trim().ToLowerInvariant(); var s = (shopId ?? "").Trim();
        using var c = Open(); using var tx = c.BeginTransaction();
        var existing = ReadCustomer(c, tx, m, s, (key ?? "").Trim()) ?? throw new InvalidOperationException("Kanonik müşteri kaydı bulunamadı.");
        var at = DateTime.SpecifyKind(nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : nowUtc, DateTimeKind.Utc);
        var updated = existing with { Name = (name ?? "").Trim(), Email = (email ?? "").Trim(), Phone = (phone ?? "").Trim(), Address = (address ?? "").Trim(), UpdatedUtc = at, Version = existing.Version + 1 };
        WriteCustomer(c, tx, updated); tx.Commit();
        return updated;
    }

    public CanonicalCustomer? Get(string marketplace, string shopId, string key) { using var c = Open(); return ReadCustomer(c, null, (marketplace ?? "").Trim().ToLowerInvariant(), (shopId ?? "").Trim(), (key ?? "").Trim()); }
    public CustomerLink? LinkOf(string marketplace, string shopId, string orderId) { using var c = Open(); return ReadLink(c, null, (marketplace ?? "").Trim().ToLowerInvariant(), (shopId ?? "").Trim(), (orderId ?? "").Trim()); }
    public IReadOnlyList<CanonicalCustomer> List(string marketplace, string shopId) { using var c = Open(); return ReadCustomers(c, null, (marketplace ?? "").Trim().ToLowerInvariant(), (shopId ?? "").Trim()); }
    /// <summary>The links flagged as duplicate candidates in a store: the operator's review list.</summary>
    public IReadOnlyList<CustomerLink> Candidates(string marketplace, string shopId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT marketplace, shop, order_id, customer_key, outcome, snapshot_fp, candidate_key, linked_utc FROM order_customer_links WHERE marketplace=$m AND shop=$s AND outcome=$o ORDER BY linked_utc DESC";
        cmd.Parameters.AddWithValue("$m", (marketplace ?? "").Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$s", (shopId ?? "").Trim()); cmd.Parameters.AddWithValue("$o", CustomerLink.DuplicateCandidate);
        using var r = cmd.ExecuteReader(); var result = new List<CustomerLink>();
        while (r.Read()) result.Add(Row(r));
        return result;
    }

    static CanonicalCustomer? ReadCustomer(SqliteConnection c, SqliteTransaction? tx, string m, string s, string key) => ReadCustomers(c, tx, m, s).FirstOrDefault(x => x.Key == key);
    static List<CanonicalCustomer> ReadCustomers(SqliteConnection c, SqliteTransaction? tx, string m, string s)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT payload, created_utc, updated_utc, version, orders, key FROM customers WHERE marketplace=$m AND shop=$s ORDER BY created_utc";
        cmd.Parameters.AddWithValue("$m", m); cmd.Parameters.AddWithValue("$s", s);
        using var r = cmd.ExecuteReader(); var result = new List<CanonicalCustomer>();
        while (r.Read())
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(0)) ?? new();
            result.Add(new(m, s, r.GetString(5), payload.GetValueOrDefault("Name", ""), payload.GetValueOrDefault("Email", ""), payload.GetValueOrDefault("Phone", ""), payload.GetValueOrDefault("Address", ""), U(r.GetString(1)), U(r.GetString(2)), r.GetInt32(3), r.GetInt32(4)));
        }
        return result;
    }
    static void WriteCustomer(SqliteConnection c, SqliteTransaction tx, CanonicalCustomer x)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO customers(marketplace, shop, key, payload, created_utc, updated_utc, version, orders) VALUES($m, $s, $k, $p, $c, $u, $v, $o) ON CONFLICT(marketplace, shop, key) DO UPDATE SET payload=excluded.payload, updated_utc=excluded.updated_utc, version=excluded.version, orders=excluded.orders";
        cmd.Parameters.AddWithValue("$m", x.Marketplace); cmd.Parameters.AddWithValue("$s", x.ShopId); cmd.Parameters.AddWithValue("$k", x.Key); cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(new Dictionary<string, string> { ["Name"] = x.Name, ["Email"] = x.Email, ["Phone"] = x.Phone, ["Address"] = x.Address }));
        cmd.Parameters.AddWithValue("$c", T(x.CreatedUtc)); cmd.Parameters.AddWithValue("$u", T(x.UpdatedUtc)); cmd.Parameters.AddWithValue("$v", x.Version); cmd.Parameters.AddWithValue("$o", x.Orders);
        cmd.ExecuteNonQuery();
    }
    static CustomerLink? ReadLink(SqliteConnection c, SqliteTransaction? tx, string m, string s, string o)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT marketplace, shop, order_id, customer_key, outcome, snapshot_fp, candidate_key, linked_utc FROM order_customer_links WHERE marketplace=$m AND shop=$s AND order_id=$o";
        cmd.Parameters.AddWithValue("$m", m); cmd.Parameters.AddWithValue("$s", s); cmd.Parameters.AddWithValue("$o", o);
        using var r = cmd.ExecuteReader(); return r.Read() ? Row(r) : null;
    }
    static CustomerLink Row(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), U(r.GetString(7)), r.GetString(4) switch { CustomerLink.Unkeyed => "kimlik anahtarı yok; kanonik kayıt yok", CustomerLink.DuplicateCandidate => $"kanonik {CustomerIdentity.Short(r.GetString(3))}; olası mükerrer {CustomerIdentity.Short(r.GetString(6))}", _ => $"kanonik {CustomerIdentity.Short(r.GetString(3))}" });
    static void WriteLink(SqliteConnection c, SqliteTransaction tx, CustomerLink link)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO order_customer_links(marketplace, shop, order_id, customer_key, outcome, snapshot_fp, candidate_key, linked_utc) VALUES($m, $s, $o, $k, $out, $fp, $cand, $t) ON CONFLICT(marketplace, shop, order_id) DO UPDATE SET customer_key=excluded.customer_key, outcome=excluded.outcome, snapshot_fp=excluded.snapshot_fp, candidate_key=excluded.candidate_key, linked_utc=excluded.linked_utc";
        cmd.Parameters.AddWithValue("$m", link.Marketplace); cmd.Parameters.AddWithValue("$s", link.ShopId); cmd.Parameters.AddWithValue("$o", link.OrderId); cmd.Parameters.AddWithValue("$k", link.CustomerKey); cmd.Parameters.AddWithValue("$out", link.Outcome); cmd.Parameters.AddWithValue("$fp", link.SnapshotFingerprint); cmd.Parameters.AddWithValue("$cand", link.CandidateKey); cmd.Parameters.AddWithValue("$t", T(link.LinkedUtc));
        cmd.ExecuteNonQuery();
    }
    static string T(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTime U(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
