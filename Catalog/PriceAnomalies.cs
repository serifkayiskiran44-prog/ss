using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One recorded price of a product: the value, its currency, who wrote it (a feed or the operator) and when.</summary>
public sealed record PriceSnapshot(long Id, string ProductId, decimal Price, string Currency, string Origin, DateTime RecordedUtc);

/// <summary>The operator's thresholds: a jump above the percent, or above the absolute amount when that is set, is an anomaly.</summary>
public sealed record PriceAnomalyPolicy(decimal MaxPercentJump, decimal MaxAbsoluteJump)
{
    public static readonly PriceAnomalyPolicy Default = new(50m, 0m);
    public bool AbsoluteEnabled => MaxAbsoluteJump > 0;
}

/// <summary>One quarantined price move waiting for a review: what it was, what it became, by how much, its kind and state; the operator's note when reviewed.</summary>
public sealed record PriceAnomaly(long Id, string ProductId, string Sku, decimal PreviousPrice, string PreviousCurrency, decimal NewPrice, string NewCurrency, string Kind, decimal PercentChange, decimal AbsoluteChange, string Status, string Words, DateTime DetectedUtc, DateTime? ReviewedUtc, string Note)
{
    public const string JumpUp = "JUMP_UP", JumpDown = "JUMP_DOWN", CurrencyChange = "CURRENCY_CHANGE";
    public const string Pending = "PENDING", Reviewed = "REVIEWED", Dismissed = "DISMISSED";
}

/// <summary>What one observation was: a first sighting, a normal move, or an anomaly now in quarantine.</summary>
public sealed record PriceObservation(string Kind, PriceAnomaly? Anomaly, PriceSnapshot Snapshot, string Words)
{
    public const string NewProduct = "NEW_PRODUCT", Normal = "NORMAL", Anomalous = "ANOMALY";
}

/// <summary>
/// The price anomaly detector (#930). Every price a feed or the operator writes is one snapshot; the newest one
/// before it is the previous snapshot, and the move between them is judged against the operator's thresholds — a
/// percentage jump, and an absolute jump when one is set. A first sighting is a new product, nothing to compare;
/// a currency change is its own anomaly whatever the numbers; a jump above the threshold, up or down, is quarantined
/// as a review item with both prices, the change and the words. Nothing is corrected by itself: the product keeps the
/// price it was given, the review item waits for the operator to mark it reviewed or dismiss it, and readiness may
/// ask whether a product has a pending anomaly. Words carry SKUs, prices and currencies only.
/// </summary>
public sealed class PriceAnomalyStore
{
    public const int NoteLimit = 200;
    readonly string connectionString;

    public PriceAnomalyStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS PriceSnapshots(Id INTEGER PRIMARY KEY AUTOINCREMENT, ProductId TEXT NOT NULL, Price TEXT NOT NULL, Currency TEXT NOT NULL, Origin TEXT NOT NULL, RecordedUtc TEXT NOT NULL);"
            + "CREATE INDEX IF NOT EXISTS IX_PriceSnapshots_Product ON PriceSnapshots(ProductId, Id);"
            + "CREATE TABLE IF NOT EXISTS PriceAnomalies(Id INTEGER PRIMARY KEY AUTOINCREMENT, ProductId TEXT NOT NULL, Sku TEXT NOT NULL, PreviousPrice TEXT NOT NULL, PreviousCurrency TEXT NOT NULL, NewPrice TEXT NOT NULL, NewCurrency TEXT NOT NULL, Kind TEXT NOT NULL, PercentChange TEXT NOT NULL, AbsoluteChange TEXT NOT NULL, Status TEXT NOT NULL, Words TEXT NOT NULL, DetectedUtc TEXT NOT NULL, ReviewedUtc TEXT NULL, Note TEXT NOT NULL DEFAULT '');"
            + "CREATE TABLE IF NOT EXISTS PriceAnomalyPolicy(Id INTEGER PRIMARY KEY CHECK(Id = 1), MaxPercentJump TEXT NOT NULL, MaxAbsoluteJump TEXT NOT NULL, UpdatedUtc TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    public PriceAnomalyPolicy GetPolicy()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT MaxPercentJump, MaxAbsoluteJump FROM PriceAnomalyPolicy WHERE Id=1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(D(r.GetString(0)), D(r.GetString(1))) : PriceAnomalyPolicy.Default;
    }

    public PriceAnomalyPolicy SavePolicy(decimal maxPercentJump, decimal maxAbsoluteJump, DateTime nowUtc)
    {
        if (maxPercentJump <= 0 || maxPercentJump > 1000) throw new ArgumentException("Yüzde eşiği 0'dan büyük, en çok 1000 olmalı.");
        if (maxAbsoluteJump < 0) throw new ArgumentException("Mutlak eşik negatif olamaz; 0 kapalı demektir.");
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO PriceAnomalyPolicy(Id, MaxPercentJump, MaxAbsoluteJump, UpdatedUtc) VALUES(1, $p, $a, $t) ON CONFLICT(Id) DO UPDATE SET MaxPercentJump=excluded.MaxPercentJump, MaxAbsoluteJump=excluded.MaxAbsoluteJump, UpdatedUtc=excluded.UpdatedUtc";
        cmd.Parameters.AddWithValue("$p", S(maxPercentJump)); cmd.Parameters.AddWithValue("$a", S(maxAbsoluteJump)); cmd.Parameters.AddWithValue("$t", T(nowUtc)); cmd.ExecuteNonQuery();
        return new(maxPercentJump, maxAbsoluteJump);
    }

    /// <summary>Records the product's current price as a snapshot and judges the move from the previous one; an anomaly is written to the review queue. The product itself is never changed.</summary>
    public PriceObservation Observe(CatalogProduct product, string origin, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product);
        var currency = (product.Currency ?? "").Trim().ToUpperInvariant(); var who = string.IsNullOrWhiteSpace(origin) ? FieldProvenance.ManualKind : origin.Trim();
        var policy = GetPolicy();
        using var c = Open(); using var tx = c.BeginTransaction();
        var previous = Latest(c, tx, product.Id);
        long id;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "INSERT INTO PriceSnapshots(ProductId, Price, Currency, Origin, RecordedUtc) VALUES($id, $p, $c, $o, $t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$id", product.Id); cmd.Parameters.AddWithValue("$p", S(product.Price)); cmd.Parameters.AddWithValue("$c", currency); cmd.Parameters.AddWithValue("$o", who); cmd.Parameters.AddWithValue("$t", T(nowUtc));
            id = (long)cmd.ExecuteScalar()!;
        }
        var snapshot = new PriceSnapshot(id, product.Id, product.Price, currency, who, nowUtc);
        if (previous is null) { tx.Commit(); return new(PriceObservation.NewProduct, null, snapshot, $"{product.Sku}: ilk fiyat gözlemi {Money(product.Price, currency)}; karşılaştırılacak önceki değer yok"); }
        string? kind = null; decimal percent = 0m, absolute = Math.Abs(product.Price - previous.Price);
        if (!string.Equals(previous.Currency, currency, StringComparison.Ordinal)) kind = PriceAnomaly.CurrencyChange;
        else
        {
            percent = previous.Price == 0m ? (product.Price == 0m ? 0m : 100m) : decimal.Round(absolute / previous.Price * 100m, 2, MidpointRounding.AwayFromZero);
            if (percent > policy.MaxPercentJump || (policy.AbsoluteEnabled && absolute > policy.MaxAbsoluteJump)) kind = product.Price > previous.Price ? PriceAnomaly.JumpUp : PriceAnomaly.JumpDown;
        }
        if (kind is null) { tx.Commit(); return new(PriceObservation.Normal, null, snapshot, $"{product.Sku}: {Money(previous.Price, previous.Currency)} → {Money(product.Price, currency)} (%{P(percent)}); eşik içinde"); }
        var words = kind == PriceAnomaly.CurrencyChange
            ? $"{product.Sku}: para birimi değişti {Money(previous.Price, previous.Currency)} → {Money(product.Price, currency)}; inceleme bekliyor, otomatik düzeltme yok"
            : $"{product.Sku}: fiyat {(kind == PriceAnomaly.JumpUp ? "sıçradı" : "düştü")} {Money(previous.Price, previous.Currency)} → {Money(product.Price, currency)} (%{P(percent)}, {Money(absolute, currency)}); eşik %{P(policy.MaxPercentJump)}{(policy.AbsoluteEnabled ? " / " + Money(policy.MaxAbsoluteJump, currency) : "")}; inceleme bekliyor, otomatik düzeltme yok";
        long anomalyId;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO PriceAnomalies(ProductId, Sku, PreviousPrice, PreviousCurrency, NewPrice, NewCurrency, Kind, PercentChange, AbsoluteChange, Status, Words, DetectedUtc, ReviewedUtc, Note) VALUES($id, $sku, $pp, $pc, $np, $nc, $k, $pct, $abs, $st, $w, $t, NULL, ''); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$id", product.Id); cmd.Parameters.AddWithValue("$sku", (product.Sku ?? "").Trim()); cmd.Parameters.AddWithValue("$pp", S(previous.Price)); cmd.Parameters.AddWithValue("$pc", previous.Currency); cmd.Parameters.AddWithValue("$np", S(product.Price)); cmd.Parameters.AddWithValue("$nc", currency);
            cmd.Parameters.AddWithValue("$k", kind); cmd.Parameters.AddWithValue("$pct", S(percent)); cmd.Parameters.AddWithValue("$abs", S(absolute)); cmd.Parameters.AddWithValue("$st", PriceAnomaly.Pending); cmd.Parameters.AddWithValue("$w", words); cmd.Parameters.AddWithValue("$t", T(nowUtc));
            anomalyId = (long)cmd.ExecuteScalar()!;
        }
        tx.Commit();
        var anomaly = new PriceAnomaly(anomalyId, product.Id, (product.Sku ?? "").Trim(), previous.Price, previous.Currency, product.Price, currency, kind, percent, absolute, PriceAnomaly.Pending, words, nowUtc, null, "");
        return new(PriceObservation.Anomalous, anomaly, snapshot, words);
    }

    public IReadOnlyList<PriceSnapshot> History(string productId, int limit = 50)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductId, Price, Currency, Origin, RecordedUtc FROM PriceSnapshots WHERE ProductId=$id ORDER BY Id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$id", productId ?? ""); cmd.Parameters.AddWithValue("$n", Math.Max(1, limit));
        using var r = cmd.ExecuteReader(); var result = new List<PriceSnapshot>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), D(r.GetString(2)), r.GetString(3), r.GetString(4), U(r.GetString(5))));
        return result;
    }

    /// <summary>The review queue, newest first; a status narrows it (PENDING by default).</summary>
    public IReadOnlyList<PriceAnomaly> List(string? status = PriceAnomaly.Pending)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, ProductId, Sku, PreviousPrice, PreviousCurrency, NewPrice, NewCurrency, Kind, PercentChange, AbsoluteChange, Status, Words, DetectedUtc, ReviewedUtc, Note FROM PriceAnomalies" + (status is null ? "" : " WHERE Status=$s") + " ORDER BY Id DESC";
        if (status is not null) cmd.Parameters.AddWithValue("$s", status);
        using var r = cmd.ExecuteReader(); var result = new List<PriceAnomaly>();
        while (r.Read()) result.Add(Read(r));
        return result;
    }

    public bool HasPending(string productId) => List(PriceAnomaly.Pending).Any(a => a.ProductId == productId);

    /// <summary>Marks a pending anomaly reviewed with the operator's note (the product is not touched) — or dismissed.</summary>
    public PriceAnomaly Review(long id, string? note, DateTime nowUtc, bool dismiss = false)
    {
        var cleanNote = AuditStore.Redact((note ?? "").Trim()); if (cleanNote.Length > NoteLimit) cleanNote = cleanNote[..NoteLimit];
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE PriceAnomalies SET Status=$s, ReviewedUtc=$t, Note=$n WHERE Id=$id AND Status=$pending";
        cmd.Parameters.AddWithValue("$s", dismiss ? PriceAnomaly.Dismissed : PriceAnomaly.Reviewed); cmd.Parameters.AddWithValue("$t", T(nowUtc)); cmd.Parameters.AddWithValue("$n", cleanNote); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$pending", PriceAnomaly.Pending);
        if (cmd.ExecuteNonQuery() == 0) throw new InvalidOperationException("Bekleyen fiyat anomalisi bulunamadı; zaten incelenmiş ya da yok.");
        return List(null).Single(a => a.Id == id);
    }

    static PriceSnapshot? Latest(SqliteConnection c, SqliteTransaction tx, string productId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id, ProductId, Price, Currency, Origin, RecordedUtc FROM PriceSnapshots WHERE ProductId=$id ORDER BY Id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$id", productId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(r.GetInt64(0), r.GetString(1), D(r.GetString(2)), r.GetString(3), r.GetString(4), U(r.GetString(5))) : null;
    }

    static PriceAnomaly Read(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), D(r.GetString(3)), r.GetString(4), D(r.GetString(5)), r.GetString(6), r.GetString(7), D(r.GetString(8)), D(r.GetString(9)), r.GetString(10), r.GetString(11), U(r.GetString(12)), r.IsDBNull(13) ? null : U(r.GetString(13)), r.GetString(14));
    static string Money(decimal value, string currency) => value.ToString("0.##", CultureInfo.InvariantCulture) + (currency.Length > 0 ? " " + currency : "");
    static string S(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    static string P(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    static decimal D(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);
    static string T(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTime U(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
