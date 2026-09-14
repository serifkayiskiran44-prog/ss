using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>A resolution remembered for a store: the line evidence (a SKU, or a title key when the line has no SKU) that maps to one product from now on. Written only by an operator's resolution.</summary>
public sealed record OrderLineMappingRule(string Marketplace, string ShopId, string Sku, string TitleKey, string ProductId, DateTime CreatedUtc, string Note);

/// <summary>What one order line maps to: exactly one product by SKU, a remembered resolution, several candidates (ambiguous), or none (missing) — with the candidates and the evidence in words.</summary>
public sealed record OrderLineJudgement(string Kind, string ProductId, IReadOnlyList<string> CandidateIds, string Evidence)
{
    public const string Exact = "EXACT", Ambiguous = "AMBIGUOUS", Missing = "MISSING", Resolved = "RESOLVED";
    public bool Linked => Kind is Exact or Resolved && ProductId.Length > 0;
}

/// <summary>One review in the queue: the line (order, index, SKU, title), what was found, the candidates, the evidence, open until resolved.</summary>
public sealed record OrderLineReview(long Id, string Marketplace, string ShopId, string OrderId, int LineIndex, string Sku, string Title, string Kind, string ProductId, IReadOnlyList<string> CandidateIds, string Evidence, string State, DateTime SeenUtc, DateTime? ResolvedUtc, string Note)
{
    public const string Open = "OPEN", Closed = "RESOLVED";
    public string Words => $"{OrderId} satır {(LineIndex + 1).ToString(CultureInfo.InvariantCulture)}: {(Sku.Length > 0 ? "SKU " + Sku : "SKU yok")} · \"{Title}\" · {Kind} · {Evidence}" + (CandidateIds.Count > 0 ? $" · {CandidateIds.Count.ToString(CultureInfo.InvariantCulture)} aday" : "");
}

public sealed record OrderLineScanResult(int Lines, int Exact, int Resolved, int Ambiguous, int Missing, int Opened, int Closed, string Words);

/// <summary>
/// The order line product mapping review queue (#944). An order line names a product by SKU and title; until now a
/// SKU that matched several products or none was silently unmapped, and nothing ever asked a person. The mapper
/// links a line only on certainty: exactly one product with that SKU (EXACT), or a resolution an operator recorded
/// for this store and this evidence (RESOLVED). A SKU on several products is AMBIGUOUS, a SKU on none — or no SKU —
/// is MISSING; both carry their evidence (the products sharing the SKU, the products whose title matches) into the
/// review queue, where an operator resolves the line to one product; the resolution becomes a rule for the store,
/// so the same evidence maps from then on, and the review closes. A line that later maps by itself closes its
/// review too. Nothing is linked silently and no wrong product is ever chosen for a person. SKUs, titles, counts
/// and product ids only — never a customer.
/// </summary>
public static class OrderLineMapper
{
    public static string TitleKey(string? title) => string.Join(' ', new string((title ?? "").Trim().ToUpperInvariant().Normalize().Where(c => !char.IsPunctuation(c)).ToArray()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static OrderLineJudgement Judge(string marketplace, string shopId, OrderItem line, IReadOnlyList<CatalogProduct> products, IReadOnlyList<OrderLineMappingRule> rules)
    {
        ArgumentNullException.ThrowIfNull(line); ArgumentNullException.ThrowIfNull(products); ArgumentNullException.ThrowIfNull(rules);
        var m = (marketplace ?? "").Trim().ToLowerInvariant(); var s = (shopId ?? "").Trim(); var sku = (line.Sku ?? "").Trim(); var titleKey = TitleKey(line.Title);
        var rule = rules.FirstOrDefault(r => r.Marketplace == m && r.ShopId == s && (sku.Length > 0 ? r.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase) : r.Sku.Length == 0 && r.TitleKey == titleKey && titleKey.Length > 0));
        if (rule is not null && products.Any(p => p.Id == rule.ProductId)) return new(OrderLineJudgement.Resolved, rule.ProductId, new[] { rule.ProductId }, $"çözülmüş eşleme kuralı ({(sku.Length > 0 ? "SKU " + sku : "başlık")} → ürün)");
        if (sku.Length > 0)
        {
            var bySku = products.Where(p => (p.Sku ?? "").Trim().Equals(sku, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToList();
            if (bySku.Count == 1) return new(OrderLineJudgement.Exact, bySku[0], bySku, "SKU birebir tek ürünle eşleşti");
            if (bySku.Count > 1) return new(OrderLineJudgement.Ambiguous, "", bySku, $"SKU {sku} {bySku.Count.ToString(CultureInfo.InvariantCulture)} üründe var; kesin eşleme yok, inceleme gerekli");
        }
        var byTitle = titleKey.Length == 0 ? new List<string>() : products.Where(p => TitleKey(p.Name) == titleKey).Select(p => p.Id).ToList();
        return new(OrderLineJudgement.Missing, "", byTitle, (sku.Length > 0 ? $"SKU {sku} katalogda yok" : "satırda SKU yok") + (byTitle.Count > 0 ? $"; başlığı {byTitle.Count.ToString(CultureInfo.InvariantCulture)} ürünle aynı (kanıt, bağlantı değil)" : "; başlık eşleşmesi de yok"));
    }
}

/// <summary>The reviews and the rules, in orders.db beside the orders.</summary>
public sealed class OrderLineReviewStore
{
    public const int NoteLimit = 200;
    readonly string directory; readonly string connectionString;

    public OrderLineReviewStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory); this.directory = directory;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "orders.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS order_line_reviews(id INTEGER PRIMARY KEY AUTOINCREMENT, marketplace TEXT NOT NULL, shop TEXT NOT NULL, order_id TEXT NOT NULL, line_index INTEGER NOT NULL, sku TEXT NOT NULL, title TEXT NOT NULL, kind TEXT NOT NULL, product_id TEXT NOT NULL DEFAULT '', candidates TEXT NOT NULL DEFAULT '', evidence TEXT NOT NULL, state TEXT NOT NULL, seen_utc TEXT NOT NULL, resolved_utc TEXT NULL, note TEXT NOT NULL DEFAULT '', UNIQUE(marketplace, shop, order_id, line_index)); CREATE TABLE IF NOT EXISTS order_line_rules(marketplace TEXT NOT NULL, shop TEXT NOT NULL, sku TEXT NOT NULL, title_key TEXT NOT NULL, product_id TEXT NOT NULL, created_utc TEXT NOT NULL, note TEXT NOT NULL DEFAULT '', PRIMARY KEY(marketplace, shop, sku, title_key))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() { var c = SqliteConnectionPolicy.Open(connectionString); using var pragma = c.CreateCommand(); pragma.CommandText = "PRAGMA busy_timeout=15000"; pragma.ExecuteNonQuery(); return c; }

    /// <summary>Judges every line of every order: opens or refreshes a review for each ambiguous or missing line, closes the review of a line that now maps by itself; links nothing.</summary>
    public OrderLineScanResult Scan(IReadOnlyList<OrderSnapshot> orders, IReadOnlyList<CatalogProduct> products, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(orders); ArgumentNullException.ThrowIfNull(products);
        var rules = Rules(); var at = Utc(nowUtc);
        int lines = 0, exact = 0, resolved = 0, ambiguous = 0, missing = 0, opened = 0, closed = 0;
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var order in orders)
        {
            var m = (order.Marketplace ?? "").Trim().ToLowerInvariant(); var s = (order.ShopId ?? "").Trim(); var o = (order.OrderId ?? "").Trim();
            for (var i = 0; i < order.Items.Count; i++)
            {
                lines++; var line = order.Items[i]; var judgement = OrderLineMapper.Judge(m, s, line, products, rules);
                var existing = ReadReview(c, tx, m, s, o, i);
                if (judgement.Linked)
                {
                    if (judgement.Kind == OrderLineJudgement.Exact) exact++; else resolved++;
                    if (existing is { State: OrderLineReview.Open }) { Write(c, tx, existing with { Kind = judgement.Kind, ProductId = judgement.ProductId, CandidateIds = judgement.CandidateIds, Evidence = judgement.Evidence, State = OrderLineReview.Closed, ResolvedUtc = at, Note = "kendiliğinden çözüldü: " + judgement.Evidence }); closed++; }
                    continue;
                }
                if (judgement.Kind == OrderLineJudgement.Ambiguous) ambiguous++; else missing++;
                if (existing is null) { Write(c, tx, new(0, m, s, o, i, (line.Sku ?? "").Trim(), AuditStore.Redact((line.Title ?? "").Trim()), judgement.Kind, "", judgement.CandidateIds, judgement.Evidence, OrderLineReview.Open, at, null, "")); opened++; }
                else if (existing.State == OrderLineReview.Open) Write(c, tx, existing with { Kind = judgement.Kind, CandidateIds = judgement.CandidateIds, Evidence = judgement.Evidence, SeenUtc = at });
                // a closed review whose line no longer maps (the rule's product gone) reopens
                else if (existing.State == OrderLineReview.Closed && existing.ProductId.Length > 0 && !products.Any(p => p.Id == existing.ProductId)) { Write(c, tx, existing with { Kind = judgement.Kind, ProductId = "", CandidateIds = judgement.CandidateIds, Evidence = judgement.Evidence + "; çözümün ürünü artık yok", State = OrderLineReview.Open, ResolvedUtc = null, SeenUtc = at }); opened++; }
            }
        }
        tx.Commit();
        return new(lines, exact, resolved, ambiguous, missing, opened, closed, $"{N(lines)} satır: {N(exact)} birebir, {N(resolved)} kuralla çözülmüş, {N(ambiguous)} belirsiz, {N(missing)} eksik; {N(opened)} inceleme açıldı, {N(closed)} kapandı — hiçbir satır sessizce bağlanmadı");
    }

    /// <summary>The operator resolves an open review to one product: the review closes and the store remembers the evidence as a rule. Audited.</summary>
    public OrderLineReview Resolve(long id, string productId, IReadOnlyList<CatalogProduct> products, string? note, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(products);
        var review = Get(id) ?? throw new InvalidOperationException("İnceleme bulunamadı.");
        if (review.State != OrderLineReview.Open) throw new InvalidOperationException("İnceleme zaten çözülmüş.");
        var product = products.FirstOrDefault(p => p.Id == (productId ?? "").Trim()) ?? throw new InvalidOperationException("Ürün bulunamadı; çözüm için katalogdaki bir ürün seçin.");
        var cleanNote = AuditStore.Redact((note ?? "").Trim()); if (cleanNote.Length > NoteLimit) cleanNote = cleanNote[..NoteLimit];
        var at = Utc(nowUtc); var titleKey = review.Sku.Length > 0 ? "" : OrderLineMapper.TitleKey(review.Title);
        using (var c = Open()) using (var tx = c.BeginTransaction())
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx; cmd.CommandText = "INSERT INTO order_line_rules(marketplace, shop, sku, title_key, product_id, created_utc, note) VALUES($m, $s, $k, $t, $p, $c, $n) ON CONFLICT(marketplace, shop, sku, title_key) DO UPDATE SET product_id=excluded.product_id, created_utc=excluded.created_utc, note=excluded.note";
                cmd.Parameters.AddWithValue("$m", review.Marketplace); cmd.Parameters.AddWithValue("$s", review.ShopId); cmd.Parameters.AddWithValue("$k", review.Sku); cmd.Parameters.AddWithValue("$t", titleKey); cmd.Parameters.AddWithValue("$p", product.Id); cmd.Parameters.AddWithValue("$c", T(at)); cmd.Parameters.AddWithValue("$n", cleanNote);
                cmd.ExecuteNonQuery();
            }
            review = review with { Kind = OrderLineJudgement.Resolved, ProductId = product.Id, State = OrderLineReview.Closed, ResolvedUtc = at, Note = cleanNote };
            Write(c, tx, review); tx.Commit();
        }
        try { new AuditStore(directory).Append(new AuditEvent { Module = "orders", Action = "line-mapping-resolve", Marketplace = review.Marketplace, ShopId = review.ShopId, OrderId = review.OrderId, ProductId = product.Id, Outcome = "Info", Detail = AuditStore.Redact($"satır {(review.LineIndex + 1).ToString(CultureInfo.InvariantCulture)} ({(review.Sku.Length > 0 ? "SKU " + review.Sku : "başlık")}) → {product.Sku}; kural kaydedildi" + (cleanNote.Length > 0 ? " · " + cleanNote : "")) }); } catch (Exception) { }
        return review;
    }

    public OrderLineReview? Get(long id) { using var c = Open(); return Read(c, null, "WHERE id=$id", ("$id", id)).FirstOrDefault(); }
    public IReadOnlyList<OrderLineReview> Open(string? marketplace = null, string? shopId = null)
    {
        using var c = Open();
        return marketplace is null ? Read(c, null, "WHERE state=$st", ("$st", OrderLineReview.Open)) : Read(c, null, "WHERE state=$st AND marketplace=$m AND shop=$s", ("$st", OrderLineReview.Open), ("$m", marketplace.Trim().ToLowerInvariant()), ("$s", (shopId ?? "").Trim()));
    }
    public IReadOnlyList<OrderLineReview> All() { using var c = Open(); return Read(c, null, "", null); }

    public IReadOnlyList<OrderLineMappingRule> Rules()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT marketplace, shop, sku, title_key, product_id, created_utc, note FROM order_line_rules ORDER BY created_utc DESC";
        using var r = cmd.ExecuteReader(); var result = new List<OrderLineMappingRule>();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), U(r.GetString(5)), r.GetString(6)));
        return result;
    }

    static OrderLineReview? ReadReview(SqliteConnection c, SqliteTransaction? tx, string m, string s, string o, int index) => Read(c, tx, "WHERE marketplace=$m AND shop=$s AND order_id=$o AND line_index=$i", ("$m", m), ("$s", s), ("$o", o), ("$i", index)).FirstOrDefault();
    static List<OrderLineReview> Read(SqliteConnection c, SqliteTransaction? tx, string where, params (string Name, object Value)[]? parameters)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, marketplace, shop, order_id, line_index, sku, title, kind, product_id, candidates, evidence, state, seen_utc, resolved_utc, note FROM order_line_reviews " + where + " ORDER BY id";
        foreach (var (name, value) in parameters ?? Array.Empty<(string, object)>()) cmd.Parameters.AddWithValue(name, value);
        using var r = cmd.ExecuteReader(); var result = new List<OrderLineReview>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9).Split(',', StringSplitOptions.RemoveEmptyEntries), r.GetString(10), r.GetString(11), U(r.GetString(12)), r.IsDBNull(13) ? null : U(r.GetString(13)), r.GetString(14)));
        return result;
    }
    static void Write(SqliteConnection c, SqliteTransaction tx, OrderLineReview x)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = x.Id == 0
            ? "INSERT INTO order_line_reviews(marketplace, shop, order_id, line_index, sku, title, kind, product_id, candidates, evidence, state, seen_utc, resolved_utc, note) VALUES($m, $s, $o, $i, $k, $t, $kind, $p, $c, $e, $st, $seen, $res, $n)"
            : "UPDATE order_line_reviews SET kind=$kind, product_id=$p, candidates=$c, evidence=$e, state=$st, seen_utc=$seen, resolved_utc=$res, note=$n WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", x.Id); cmd.Parameters.AddWithValue("$m", x.Marketplace); cmd.Parameters.AddWithValue("$s", x.ShopId); cmd.Parameters.AddWithValue("$o", x.OrderId); cmd.Parameters.AddWithValue("$i", x.LineIndex); cmd.Parameters.AddWithValue("$k", x.Sku); cmd.Parameters.AddWithValue("$t", x.Title);
        cmd.Parameters.AddWithValue("$kind", x.Kind); cmd.Parameters.AddWithValue("$p", x.ProductId); cmd.Parameters.AddWithValue("$c", string.Join(",", x.CandidateIds)); cmd.Parameters.AddWithValue("$e", x.Evidence); cmd.Parameters.AddWithValue("$st", x.State); cmd.Parameters.AddWithValue("$seen", T(x.SeenUtc)); cmd.Parameters.AddWithValue("$res", x.ResolvedUtc is { } r ? T(r) : DBNull.Value); cmd.Parameters.AddWithValue("$n", x.Note);
        cmd.ExecuteNonQuery();
    }
    static DateTime Utc(DateTime at) => DateTime.SpecifyKind(at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at, DateTimeKind.Utc);
    static string T(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTime U(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
