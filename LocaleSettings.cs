using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed class StoreLocaleSettings
{
    public string Channel { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string Currency { get; set; } = "TRY";
    public string CultureName { get; set; } = "tr-TR";
    public decimal VatRate { get; set; } = 20;
    public string DatePattern { get; set; } = "dd.MM.yyyy";
    public int Version { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public static class LocaleSettings
{
    public static IReadOnlySet<string> SupportedCurrencies { get; } = new HashSet<string>(new[] { "TRY", "USD", "EUR", "GBP" }, StringComparer.OrdinalIgnoreCase);
    public static IReadOnlySet<string> SupportedCultures { get; } = new HashSet<string>(new[] { "tr-TR", "en-US", "de-DE", "en-GB" }, StringComparer.OrdinalIgnoreCase);
    public static CultureInfo Culture(StoreLocaleSettings settings) => CultureInfo.GetCultureInfo(settings.CultureName);
    public static class Format
    {
        public static bool TryParseDecimal(string value, StoreLocaleSettings settings, out decimal result) => decimal.TryParse(value, NumberStyles.Number, Culture(settings), out result);
        public static decimal ParseDecimal(string value, StoreLocaleSettings settings) => TryParseDecimal(value, settings, out var result) ? result : throw new FormatException($"Sayı {settings.CultureName} kültüründe geçerli değil.");
        public static string Decimal(decimal value, StoreLocaleSettings settings) => value.ToString("N2", Culture(settings));
        public static string Date(DateTime value, StoreLocaleSettings settings) => value.ToString(settings.DatePattern, Culture(settings));
    }
    public static void Validate(StoreLocaleSettings settings)
    {
        settings.Currency = settings.Currency.Trim().ToUpperInvariant(); settings.CultureName = settings.CultureName.Trim(); settings.DatePattern = settings.DatePattern.Trim();
        if (string.IsNullOrWhiteSpace(settings.Channel) || string.IsNullOrWhiteSpace(settings.ShopId)) throw new ArgumentException("Kanal ve mağaza zorunlu.");
        if (!SupportedCurrencies.Contains(settings.Currency)) throw new ArgumentException("Desteklenmeyen para birimi. TRY, USD, EUR veya GBP kullanın.");
        if (!SupportedCultures.Contains(settings.CultureName)) throw new ArgumentException("Desteklenmeyen sayı/tarih kültürü.");
        if (settings.VatRate is < 0 or > 100) throw new ArgumentException("KDV oranı 0 ile 100 arasında olmalı.");
        if (string.IsNullOrWhiteSpace(settings.DatePattern) || settings.DatePattern.Length > 40) throw new ArgumentException("Tarih biçimi geçersiz.");
        _ = DateTime.UtcNow.ToString(settings.DatePattern, Culture(settings));
    }
}

/// Bounded diagnostics only (channel/shop identity, a short reason, detection
/// time) - never Currency/CultureName/DatePattern - for a StoreLocaleSettings row
/// with an unparsable/mistyped persisted field. See CatalogStore's
/// CorruptProductRow for the same pattern.
public sealed record CorruptLocaleSettingsRow(string Channel, string ShopId, string Reason, DateTime DetectedUtc);

/// Raised by Get(channel,shopId) when the row exists but is corrupt - kept
/// distinct from returning null (which means "no such row"), so a caller can
/// never mistake "needs explicit repair" for "not configured yet".
public sealed class LocaleSettingsCorruptException : Exception
{
    public string Channel { get; } public string ShopId { get; }
    public LocaleSettingsCorruptException(string channel, string shopId, string reason) : base($"Yerel ayar kaydı bozuk (REVIEW_REQUIRED): {reason}") { Channel = channel; ShopId = shopId; }
}

public sealed class LocaleSettingsStore
{
    readonly string connectionString;
    readonly string directory;
    public LocaleSettingsStore(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(this.directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(this.directory, "locale-settings.db") }.ToString();
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS StoreLocaleSettings(Channel TEXT NOT NULL,ShopId TEXT NOT NULL,Currency TEXT NOT NULL,CultureName TEXT NOT NULL,VatRate REAL NOT NULL,DatePattern TEXT NOT NULL,Version INTEGER NOT NULL,UpdatedUtc TEXT NOT NULL,PRIMARY KEY(Channel,ShopId));"; command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    /// A corrupt target row throws LocaleSettingsCorruptException rather than
    /// returning null, so "needs repair" is never confused with "not configured".
    public StoreLocaleSettings? Get(string channel, string shopId)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Channel,ShopId,Currency,CultureName,VatRate,DatePattern,Version,UpdatedUtc FROM StoreLocaleSettings WHERE Channel=$channel AND ShopId=$shop"; command.Parameters.AddWithValue("$channel", channel.Trim().ToLowerInvariant()); command.Parameters.AddWithValue("$shop", shopId.Trim());
        using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        if (!TryRead(r, out var settings, out var corrupt)) throw new LocaleSettingsCorruptException(corrupt!.Channel, corrupt.ShopId, corrupt.Reason);
        return settings;
    }
    /// A malformed row must never crash the whole read - it is excluded from the
    /// healthy result and reported only via CorruptSettings(); detection re-runs
    /// from the row's own stored bytes every call, so it stays stable across a
    /// restart without a separate tracking table.
    public IReadOnlyList<StoreLocaleSettings> List() { using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Channel,ShopId,Currency,CultureName,VatRate,DatePattern,Version,UpdatedUtc FROM StoreLocaleSettings ORDER BY Channel,ShopId"; using var r = command.ExecuteReader(); var rows = new List<StoreLocaleSettings>(); while (r.Read()) if (TryRead(r, out var settings, out _)) rows.Add(settings!); return rows; }
    /// Bounded diagnostics for every row with an unparsable/mistyped field - never
    /// the raw Currency/CultureName/DatePattern.
    public IReadOnlyList<CorruptLocaleSettingsRow> CorruptSettings() { using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Channel,ShopId,Currency,CultureName,VatRate,DatePattern,Version,UpdatedUtc FROM StoreLocaleSettings"; using var r = command.ExecuteReader(); var rows = new List<CorruptLocaleSettingsRow>(); while (r.Read()) if (!TryRead(r, out _, out var corrupt)) rows.Add(corrupt!); return rows; }
    public StoreLocaleSettings Save(StoreLocaleSettings settings)
    {
        LocaleSettings.Validate(settings); settings.Channel = settings.Channel.Trim().ToLowerInvariant(); settings.ShopId = settings.ShopId.Trim(); var existing = Get(settings.Channel, settings.ShopId); var nextVersion = Math.Max(existing?.Version ?? 0, settings.Version) + 1; using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO StoreLocaleSettings(Channel,ShopId,Currency,CultureName,VatRate,DatePattern,Version,UpdatedUtc) VALUES($channel,$shop,$currency,$culture,$vat,$date,$version,$updated) ON CONFLICT(Channel,ShopId) DO UPDATE SET Currency=excluded.Currency,CultureName=excluded.CultureName,VatRate=excluded.VatRate,DatePattern=excluded.DatePattern,Version=excluded.Version,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$channel", settings.Channel); command.Parameters.AddWithValue("$shop", settings.ShopId); command.Parameters.AddWithValue("$currency", settings.Currency); command.Parameters.AddWithValue("$culture", settings.CultureName); command.Parameters.AddWithValue("$vat", settings.VatRate); command.Parameters.AddWithValue("$date", settings.DatePattern); command.Parameters.AddWithValue("$version", nextVersion); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery(); return Get(settings.Channel, settings.ShopId)!;
    }
    public StoreLocaleSettings Copy(string sourceChannel, string sourceShop, string targetChannel, string targetShop)
    {
        var source = Get(sourceChannel, sourceShop) ?? throw new InvalidOperationException("Kopyalanacak mağaza yerel ayarı bulunamadı."); var copy = new StoreLocaleSettings { Channel = targetChannel, ShopId = targetShop, Currency = source.Currency, CultureName = source.CultureName, VatRate = source.VatRate, DatePattern = source.DatePattern }; var saved = Save(copy); new AuditStore(directory).Append(new AuditEvent { Module = "locale", Action = "copy", Marketplace = targetChannel, ShopId = targetShop, Outcome = "Succeeded", Detail = $"{sourceChannel}/{sourceShop} ayarı kopyalandı." }); return saved;
    }
    static bool TryRead(SqliteDataReader r, out StoreLocaleSettings? settings, out CorruptLocaleSettingsRow? corrupt)
    {
        settings = null; corrupt = null; var channel = r.GetString(0); var shop = r.GetString(1);
        // SQLite is dynamically typed: a non-numeric TEXT value stored (by accident
        // or corruption) into an INTEGER/REAL-declared column does not throw on
        // read - sqlite3_column_int()/_double() silently coerce it to 0. Reject by
        // the reader's own storage-class type instead of relying on a cast to throw.
        decimal vatRate;
        switch (r.GetValue(4)) { case long l: vatRate = l; break; case double d: vatRate = (decimal)d; break; default: corrupt = new(channel, shop, "Malformed VatRate value", DateTime.UtcNow); return false; }
        int version;
        switch (r.GetValue(6)) { case long lv when lv is >= int.MinValue and <= int.MaxValue: version = (int)lv; break; default: corrupt = new(channel, shop, "Malformed Version value", DateTime.UtcNow); return false; }
        if (!TryParseUtc(r.GetString(7), out var updated)) { corrupt = new(channel, shop, "Malformed UpdatedUtc timestamp", DateTime.UtcNow); return false; }
        settings = new() { Channel = channel, ShopId = shop, Currency = r.GetString(2), CultureName = r.GetString(3), VatRate = vatRate, DatePattern = r.GetString(5), Version = version, UpdatedUtc = updated };
        return true;
    }
    /// Only ever a format/type failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised by the surrounding command, not this parse.
    static bool TryParseUtc(string value, out DateTime result) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
}
