using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record MissingSourceCase(string Supplier, string ProductId, DateTimeOffset FirstMissingUtc, DateTimeOffset LastSeenUtc, string State, int ExistingStock, decimal ExistingPrice, bool ListingPreserved);
public sealed record MissingSourcePolicy(TimeSpan GracePeriod, int MassMissingThreshold = 100);
public sealed record DeactivatePreview(string Supplier, string ProductId, string Key, string Reason);
public sealed record QuarantineAudit(string Supplier, string ProductId, string Action, DateTimeOffset AtUtc, string Reason);

/// <summary>Durable quarantine for products absent from a complete supplier feed.</summary>
public sealed class SourceMissingQuarantine
{
    readonly object gate = new();
    readonly Dictionary<(string Supplier, string Product), MissingSourceCase> cases = new();
    readonly List<QuarantineAudit> audits = new();
    readonly string? connectionString;

    public SourceMissingQuarantine(string? directory = null)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "source-health.db") }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SourceMissingCases(
              Supplier TEXT NOT NULL, ProductId TEXT NOT NULL, FirstMissingUtc TEXT NOT NULL,
              LastSeenUtc TEXT NOT NULL, State TEXT NOT NULL, ExistingStock INTEGER NOT NULL,
              ExistingPrice TEXT NOT NULL, ListingPreserved INTEGER NOT NULL,
              PRIMARY KEY(Supplier,ProductId));
            CREATE TABLE IF NOT EXISTS SourceMissingAudits(
              Id INTEGER PRIMARY KEY AUTOINCREMENT, Supplier TEXT NOT NULL, ProductId TEXT NOT NULL,
              Action TEXT NOT NULL, AtUtc TEXT NOT NULL, Reason TEXT NOT NULL);
            """;
        command.ExecuteNonQuery(); Load();
    }
    public IReadOnlyList<QuarantineAudit> Audits { get { lock (gate) return audits.ToArray(); } }

    public MissingSourceCase Observe(string supplier, string productId, bool present, int existingStock, decimal existingPrice, DateTimeOffset now, MissingSourcePolicy policy)
    {
        if (string.IsNullOrWhiteSpace(supplier) || string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("Kaynak ve ürün kimliği zorunludur.");
        if (policy.GracePeriod < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(policy));
        lock (gate)
        {
            var key = (Supplier: supplier.Trim(), Product: productId.Trim());
            if (present)
            {
                var recovered = cases.Remove(key); if (recovered) { AddAudit(new(key.Supplier, key.Product, "LOCAL_RECOVERY", now, "source-returned")); DeleteCase(key); }
                return new(key.Supplier, key.Product, now, now, "PRESENT", existingStock, existingPrice, true);
            }
            var old = cases.TryGetValue(key, out var value) ? value : new(key.Supplier, key.Product, now, now, "WARNING", existingStock, existingPrice, true);
            var state = now - old.FirstMissingUtc >= policy.GracePeriod ? "PENDING_ACTION" : "WARNING";
            var item = old with { State = state, ExistingStock = old.ExistingStock == 0 ? existingStock : old.ExistingStock, ExistingPrice = old.ExistingPrice == 0 ? existingPrice : old.ExistingPrice, ListingPreserved = true };
            cases[key] = item; PersistCase(item); return item;
        }
    }
    public IReadOnlyList<MissingSourceCase> List(string? supplier = null, string? state = null) { lock (gate) return cases.Values.Where(x => (supplier is null || x.Supplier.Equals(supplier, StringComparison.OrdinalIgnoreCase)) && (state is null || x.State.Equals(state, StringComparison.OrdinalIgnoreCase))).ToArray(); }
    public DeactivatePreview PreviewDeactivate(MissingSourceCase item, string reason) => new(item.Supplier, item.ProductId, $"{item.Supplier}|{item.ProductId}|{item.FirstMissingUtc:O}", reason);
    public QuarantineAudit ApplyLocalDeactivate(DeactivatePreview preview, bool approved) { if (!approved) throw new InvalidOperationException("EXPLICIT_APPROVAL_REQUIRED"); lock (gate) { var audit = new QuarantineAudit(preview.Supplier, preview.ProductId, "LOCAL_DEACTIVATE", DateTimeOffset.UtcNow, preview.Reason); AddAudit(audit); return audit; } }
    public static bool IsMassMissing(int missingCount, MissingSourcePolicy policy) => missingCount >= policy.MassMissingThreshold;

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString!);
    void Load()
    {
        if (connectionString is null) return; using var connection = Open();
        using (var command = connection.CreateCommand()) { command.CommandText = "SELECT Supplier,ProductId,FirstMissingUtc,LastSeenUtc,State,ExistingStock,ExistingPrice,ListingPreserved FROM SourceMissingCases"; using var reader = command.ExecuteReader(); while (reader.Read()) cases[(reader.GetString(0), reader.GetString(1))] = new(reader.GetString(0), reader.GetString(1), ParseUtc(reader.GetString(2)), ParseUtc(reader.GetString(3)), reader.GetString(4), reader.GetInt32(5), decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture), reader.GetInt32(7) != 0); }
        using (var command = connection.CreateCommand()) { command.CommandText = "SELECT Supplier,ProductId,Action,AtUtc,Reason FROM SourceMissingAudits ORDER BY Id"; using var reader = command.ExecuteReader(); while (reader.Read()) audits.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseUtc(reader.GetString(3)), reader.GetString(4))); }
    }
    void PersistCase(MissingSourceCase item)
    {
        if (connectionString is null) return; using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO SourceMissingCases(Supplier,ProductId,FirstMissingUtc,LastSeenUtc,State,ExistingStock,ExistingPrice,ListingPreserved) VALUES($supplier,$product,$first,$last,$state,$stock,$price,$preserved) ON CONFLICT(Supplier,ProductId) DO UPDATE SET FirstMissingUtc=excluded.FirstMissingUtc,LastSeenUtc=excluded.LastSeenUtc,State=excluded.State,ExistingStock=excluded.ExistingStock,ExistingPrice=excluded.ExistingPrice,ListingPreserved=excluded.ListingPreserved"; command.Parameters.AddWithValue("$supplier", item.Supplier); command.Parameters.AddWithValue("$product", item.ProductId); command.Parameters.AddWithValue("$first", item.FirstMissingUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$last", item.LastSeenUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$state", item.State); command.Parameters.AddWithValue("$stock", item.ExistingStock); command.Parameters.AddWithValue("$price", item.ExistingPrice.ToString(CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$preserved", item.ListingPreserved ? 1 : 0); command.ExecuteNonQuery();
    }
    void DeleteCase((string Supplier, string Product) key) { if (connectionString is null) return; using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM SourceMissingCases WHERE Supplier=$supplier AND ProductId=$product"; command.Parameters.AddWithValue("$supplier", key.Supplier); command.Parameters.AddWithValue("$product", key.Product); command.ExecuteNonQuery(); }
    void AddAudit(QuarantineAudit audit) { audits.Add(audit); if (connectionString is null) return; using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO SourceMissingAudits(Supplier,ProductId,Action,AtUtc,Reason) VALUES($supplier,$product,$action,$at,$reason)"; command.Parameters.AddWithValue("$supplier", audit.Supplier); command.Parameters.AddWithValue("$product", audit.ProductId); command.Parameters.AddWithValue("$action", audit.Action); command.Parameters.AddWithValue("$at", audit.AtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$reason", audit.Reason); command.ExecuteNonQuery(); }
    static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
