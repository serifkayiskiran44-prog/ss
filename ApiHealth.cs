using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace TrMarketplaceHubDesktop;

public sealed class ApiHealthObservation
{
    public string State { get; init; } = "UNKNOWN";
    public string AuthStatus { get; init; } = "UNKNOWN";
    public string ErrorClass { get; init; } = "None";
    public int? HttpStatus { get; init; }
    public string ErrorMessage { get; init; } = "";
    public int? RateLimitRemaining { get; init; }
    public int? RateLimitLimit { get; init; }
    public DateTimeOffset? RateLimitResetUtc { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public DateTimeOffset? BackoffUntilUtc { get; init; }
    public DateTimeOffset ObservedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ApiHealthRecord
{
    public string Channel { get; init; } = "";
    public string ShopId { get; init; } = "";
    public string State { get; init; } = "UNKNOWN";
    public string AuthStatus { get; init; } = "UNKNOWN";
    public string ErrorClass { get; init; } = "None";
    public int? HttpStatus { get; init; }
    public DateTimeOffset LastAttemptUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public string LastError { get; init; } = "";
    public int? RateLimitRemaining { get; init; }
    public int? RateLimitLimit { get; init; }
    public DateTimeOffset? RateLimitResetUtc { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public DateTimeOffset? BackoffUntilUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public string RateLimitSummary => RateLimitRemaining.HasValue || RateLimitLimit.HasValue
        ? $"{RateLimitRemaining?.ToString(CultureInfo.InvariantCulture) ?? "?"}/{RateLimitLimit?.ToString(CultureInfo.InvariantCulture) ?? "?"}"
        : "-";
    public string BackoffSummary => BackoffUntilUtc is { } until && until > DateTimeOffset.UtcNow ? until.ToLocalTime().ToString("g") : "-";
}

public sealed record ApiHealthSummary(int Total, int Healthy, int Blocked, int AuthErrors, int RateLimited, int OtherErrors, int BackingOff);
/// Bounded diagnostics only (channel/shop identity, a short reason, detection time) -
/// never LastError or any other health field - for a row with an unparsable
/// persisted timestamp. See CatalogStore's CorruptProductRow for the same pattern.
public sealed record CorruptApiHealthRow(string Channel, string ShopId, string Reason, DateTime DetectedUtc);

public static class ApiHealthClassifier
{
    public static ApiHealthObservation FromResponse(HttpResponseMessage response, DateTimeOffset? now = null)
    {
        var observed = now ?? DateTimeOffset.UtcNow;
        var status = (int)response.StatusCode;
        var retry = ParseRetryAfter(response.Headers, observed);
        var remaining = ParseIntHeader(response.Headers, "X-RateLimit-Remaining", "RateLimit-Remaining", "X-Rate-Limit-Remaining");
        var limit = ParseIntHeader(response.Headers, "X-RateLimit-Limit", "RateLimit-Limit", "X-Rate-Limit-Limit");
        var reset = ParseResetHeader(response.Headers, observed, "X-RateLimit-Reset", "RateLimit-Reset", "X-Rate-Limit-Reset");
        var (state, auth, errorClass) = status switch
        {
            >= 200 and < 300 => ("HEALTHY", "VALID", "None"),
            401 or 403 => ("AUTH_ERROR", "INVALID", "Authentication"),
            408 => ("TIMEOUT", "UNKNOWN", "Timeout"),
            429 => ("RATE_LIMITED", "UNKNOWN", "RateLimit"),
            >= 500 => ("SERVER_ERROR", "UNKNOWN", "Server"),
            >= 400 => ("CLIENT_ERROR", "UNKNOWN", "Client"),
            _ => ("UNKNOWN", "UNKNOWN", "Unknown")
        };
        var retrySeconds = retry.HasValue ? (int?)Math.Max(0, (int)Math.Ceiling(retry.Value.TotalSeconds)) : null;
        if (status == 429 && retrySeconds is null) retrySeconds = 60;
        var backoff = retrySeconds is { } seconds ? observed.AddSeconds(seconds) : state switch
        {
            "SERVER_ERROR" => observed.AddSeconds(30),
            "TIMEOUT" => observed.AddSeconds(30),
            _ => (DateTimeOffset?)null
        };
        var message = status is >= 200 and < 300 ? "" : AuditStore.Sanitize($"HTTP {status} {response.ReasonPhrase}".Trim());
        return new ApiHealthObservation { State = state, AuthStatus = auth, ErrorClass = errorClass, HttpStatus = status, ErrorMessage = message, RateLimitRemaining = remaining, RateLimitLimit = limit, RateLimitResetUtc = reset, RetryAfterSeconds = retrySeconds, BackoffUntilUtc = backoff, ObservedUtc = observed };
    }

    public static ApiHealthObservation FromException(Exception error, DateTimeOffset? now = null)
    {
        var observed = now ?? DateTimeOffset.UtcNow;
        var message = AuditStore.Sanitize(error.Message);
        if (message.Contains("LIVE_API_BLOCKED", StringComparison.OrdinalIgnoreCase)) return new() { State = "LIVE_API_BLOCKED", AuthStatus = "UNKNOWN", ErrorClass = "Unsupported", ErrorMessage = message, ObservedUtc = observed };
        if (message.Contains("NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase)) return new() { State = "NOT_CONFIGURED", AuthStatus = "UNKNOWN", ErrorClass = "NotConfigured", ErrorMessage = message, ObservedUtc = observed };
        var (state, errorClass, backoff) = error switch
        {
            OperationCanceledException => ("TIMEOUT", "Timeout", observed.AddSeconds(30)),
            HttpRequestException => ("NETWORK_ERROR", "Network", observed.AddSeconds(15)),
            _ => ("UNKNOWN", "Unknown", (DateTimeOffset?)null)
        };
        return new ApiHealthObservation { State = state, AuthStatus = "UNKNOWN", ErrorClass = errorClass, ErrorMessage = message, BackoffUntilUtc = backoff, ObservedUtc = observed };
    }

    public static ApiHealthObservation Blocked(string message, DateTimeOffset? now = null) => new() { State = "LIVE_API_BLOCKED", AuthStatus = "UNKNOWN", ErrorClass = "Unsupported", ErrorMessage = AuditStore.Sanitize(message), ObservedUtc = now ?? DateTimeOffset.UtcNow };

    static int? ParseIntHeader(HttpResponseHeaders headers, params string[] names)
    {
        foreach (var name in names) if (headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0) return result;
        return null;
    }
    static DateTimeOffset? ParseResetHeader(HttpResponseHeaders headers, DateTimeOffset now, params string[] names)
    {
        foreach (var name in names) if (headers.TryGetValues(name, out var values))
        {
            var raw = values.FirstOrDefault();
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0 && number <= 31_536_000) return number > 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(number) : now.AddSeconds(number);
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date.ToUniversalTime();
        }
        return null;
    }
    static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        if (headers.RetryAfter?.Delta is { } delta) return delta;
        if (headers.RetryAfter?.Date is { } date) return date - now;
        if (headers.TryGetValues("Retry-After", out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 86400) return TimeSpan.FromSeconds(seconds);
        return null;
    }
}

public sealed class ApiHealthCaptureHandler : DelegatingHandler
{
    public ApiHealthObservation? LastObservation { get; private set; }
    public void Reset() => LastObservation = null;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            LastObservation = ApiHealthClassifier.FromResponse(response);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LastObservation = new ApiHealthObservation { State = "CANCELLED", AuthStatus = "UNKNOWN", ErrorClass = "Cancelled", ErrorMessage = "İstek kullanıcı tarafından iptal edildi." };
            throw;
        }
        catch (Exception error)
        {
            LastObservation = ApiHealthClassifier.FromException(error);
            throw;
        }
    }
}

public sealed class ApiHealthStore
{
    readonly string connectionString;
    public ApiHealthStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "health.db") }.ToString();
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS ApiHealth(Channel TEXT NOT NULL,ShopId TEXT NOT NULL,State TEXT NOT NULL,AuthStatus TEXT NOT NULL,ErrorClass TEXT NOT NULL,HttpStatus INTEGER NULL,LastAttemptUtc TEXT NOT NULL,LastSuccessUtc TEXT NULL,LastError TEXT NOT NULL,RateLimitRemaining INTEGER NULL,RateLimitLimit INTEGER NULL,RateLimitResetUtc TEXT NULL,RetryAfterSeconds INTEGER NULL,BackoffUntilUtc TEXT NULL,UpdatedUtc TEXT NOT NULL,PRIMARY KEY(Channel,ShopId));CREATE INDEX IF NOT EXISTS IX_ApiHealth_State ON ApiHealth(State,UpdatedUtc DESC);"; command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public void EnsureConnection(string channel, string shopId, string state = "NOT_CONFIGURED", string error = "")
    {
        ValidateIdentity(channel, shopId); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT OR IGNORE INTO ApiHealth(Channel,ShopId,State,AuthStatus,ErrorClass,HttpStatus,LastAttemptUtc,LastSuccessUtc,LastError,RateLimitRemaining,RateLimitLimit,RateLimitResetUtc,RetryAfterSeconds,BackoffUntilUtc,UpdatedUtc) VALUES($channel,$shop,$state,'UNKNOWN','None',NULL,$now,NULL,$error,NULL,NULL,NULL,NULL,NULL,$now)"; command.Parameters.AddWithValue("$channel", channel.Trim().ToLowerInvariant()); command.Parameters.AddWithValue("$shop", shopId.Trim()); command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$error", AuditStore.Sanitize(error)); command.ExecuteNonQuery();
    }
    public void Observe(string channel, string shopId, ApiHealthObservation observation)
    {
        ValidateIdentity(channel, shopId); var normalizedChannel = channel.Trim().ToLowerInvariant(); var normalizedShop = shopId.Trim(); var observed = observation.ObservedUtc == default ? DateTimeOffset.UtcNow : observation.ObservedUtc; var lastSuccess = observation.State == "HEALTHY" ? observed.ToString("O", CultureInfo.InvariantCulture) : null; var error = observation.ErrorMessage.Length == 0 ? "" : AuditStore.Sanitize(observation.ErrorMessage); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO ApiHealth(Channel,ShopId,State,AuthStatus,ErrorClass,HttpStatus,LastAttemptUtc,LastSuccessUtc,LastError,RateLimitRemaining,RateLimitLimit,RateLimitResetUtc,RetryAfterSeconds,BackoffUntilUtc,UpdatedUtc) VALUES($channel,$shop,$state,$auth,$class,$http,$attempt,$success,$error,$remaining,$limit,$reset,$retry,$backoff,$updated) ON CONFLICT(Channel,ShopId) DO UPDATE SET State=excluded.State,AuthStatus=excluded.AuthStatus,ErrorClass=excluded.ErrorClass,HttpStatus=excluded.HttpStatus,LastAttemptUtc=excluded.LastAttemptUtc,LastSuccessUtc=COALESCE(excluded.LastSuccessUtc,ApiHealth.LastSuccessUtc),LastError=excluded.LastError,RateLimitRemaining=excluded.RateLimitRemaining,RateLimitLimit=excluded.RateLimitLimit,RateLimitResetUtc=excluded.RateLimitResetUtc,RetryAfterSeconds=excluded.RetryAfterSeconds,BackoffUntilUtc=excluded.BackoffUntilUtc,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$channel", normalizedChannel); command.Parameters.AddWithValue("$shop", normalizedShop); command.Parameters.AddWithValue("$state", observation.State); command.Parameters.AddWithValue("$auth", observation.AuthStatus); command.Parameters.AddWithValue("$class", observation.ErrorClass); command.Parameters.AddWithValue("$http", (object?)observation.HttpStatus ?? DBNull.Value); command.Parameters.AddWithValue("$attempt", observed.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$success", (object?)lastSuccess ?? DBNull.Value); command.Parameters.AddWithValue("$error", error); command.Parameters.AddWithValue("$remaining", (object?)observation.RateLimitRemaining ?? DBNull.Value); command.Parameters.AddWithValue("$limit", (object?)observation.RateLimitLimit ?? DBNull.Value); command.Parameters.AddWithValue("$reset", observation.RateLimitResetUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value); command.Parameters.AddWithValue("$retry", (object?)observation.RetryAfterSeconds ?? DBNull.Value); command.Parameters.AddWithValue("$backoff", observation.BackoffUntilUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value); command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery();
    }
    public ApiHealthRecord? Get(string channel, string shopId) => List().FirstOrDefault(x => x.Channel == channel.Trim().ToLowerInvariant() && x.ShopId == shopId.Trim());
    /// A row with any unparsable persisted timestamp is excluded from the healthy
    /// result rather than crashing the whole read; see CorruptRecords() for the
    /// bounded diagnostics covering exactly those excluded rows. Detection re-runs
    /// from each row's own stored text every call, so it stays stable across a
    /// restart without a separate tracking table.
    public IReadOnlyList<ApiHealthRecord> List(string? query = null, string? state = null)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Channel,ShopId,State,AuthStatus,ErrorClass,HttpStatus,LastAttemptUtc,LastSuccessUtc,LastError,RateLimitRemaining,RateLimitLimit,RateLimitResetUtc,RetryAfterSeconds,BackoffUntilUtc,UpdatedUtc FROM ApiHealth WHERE ($query='' OR Channel LIKE $like OR ShopId LIKE $like OR State LIKE $like OR AuthStatus LIKE $like OR ErrorClass LIKE $like OR LastError LIKE $like) AND ($state='' OR State=$state) ORDER BY CASE State WHEN 'AUTH_ERROR' THEN 0 WHEN 'RATE_LIMITED' THEN 1 WHEN 'SERVER_ERROR' THEN 2 WHEN 'NETWORK_ERROR' THEN 3 WHEN 'TIMEOUT' THEN 4 WHEN 'LIVE_API_BLOCKED' THEN 5 WHEN 'NOT_CONFIGURED' THEN 6 WHEN 'HEALTHY' THEN 7 ELSE 8 END,UpdatedUtc DESC"; var value = query?.Trim() ?? ""; command.Parameters.AddWithValue("$query", value); command.Parameters.AddWithValue("$like", $"%{value}%"); command.Parameters.AddWithValue("$state", state?.Trim() ?? ""); using var reader = command.ExecuteReader(); var rows = new List<ApiHealthRecord>(); while (reader.Read()) if (TryRead(reader, out var record, out _)) rows.Add(record!); return rows;
    }
    /// Bounded diagnostics for every row with an unparsable timestamp - never the raw
    /// LastError or any other health field.
    public IReadOnlyList<CorruptApiHealthRow> CorruptRecords()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Channel,ShopId,State,AuthStatus,ErrorClass,HttpStatus,LastAttemptUtc,LastSuccessUtc,LastError,RateLimitRemaining,RateLimitLimit,RateLimitResetUtc,RetryAfterSeconds,BackoffUntilUtc,UpdatedUtc FROM ApiHealth";
        using var reader = command.ExecuteReader(); var rows = new List<CorruptApiHealthRow>();
        while (reader.Read()) if (!TryRead(reader, out _, out var corrupt)) rows.Add(corrupt!);
        return rows;
    }
    /// Deliberately does NOT go through Get()/List(): a corrupt row must fail CLOSED
    /// (defer/back off) rather than silently disappearing the way a missing row
    /// would through the List()-based lookup - treating "corrupt backoff state" the
    /// same as "no backoff row" would let a caller retry a channel/shop we actually
    /// have no reliable rate-limit information for, risking a real ban.
    public bool ShouldDefer(string channel, string shopId, DateTimeOffset? now = null)
    {
        var normalizedChannel = channel.Trim().ToLowerInvariant(); var normalizedShop = shopId.Trim();
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = "SELECT Channel,ShopId,State,AuthStatus,ErrorClass,HttpStatus,LastAttemptUtc,LastSuccessUtc,LastError,RateLimitRemaining,RateLimitLimit,RateLimitResetUtc,RetryAfterSeconds,BackoffUntilUtc,UpdatedUtc FROM ApiHealth WHERE Channel=$channel AND ShopId=$shop";
        command.Parameters.AddWithValue("$channel", normalizedChannel); command.Parameters.AddWithValue("$shop", normalizedShop);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        if (!TryRead(reader, out var record, out _)) return true;
        return record!.BackoffUntilUtc is { } until && until > (now ?? DateTimeOffset.UtcNow);
    }
    public ApiHealthSummary Summary(DateTimeOffset? now = null)
    {
        var rows = List(); var at = now ?? DateTimeOffset.UtcNow; return new(rows.Count, rows.Count(x => x.State == "HEALTHY"), rows.Count(x => x.State == "LIVE_API_BLOCKED"), rows.Count(x => x.State == "AUTH_ERROR"), rows.Count(x => x.State == "RATE_LIMITED"), rows.Count(x => x.State is not ("HEALTHY" or "LIVE_API_BLOCKED" or "AUTH_ERROR" or "RATE_LIMITED")), rows.Count(x => x.BackoffUntilUtc is { } until && until > at));
    }
    static bool TryRead(SqliteDataReader r, out ApiHealthRecord? record, out CorruptApiHealthRow? corrupt)
    {
        record = null; corrupt = null; var channel = r.GetString(0); var shop = r.GetString(1);
        if (!TryParseOffset(r.GetString(6), out var lastAttempt)) { corrupt = new(channel, shop, "Malformed LastAttemptUtc timestamp", DateTime.UtcNow); return false; }
        DateTimeOffset? lastSuccess = null;
        if (!r.IsDBNull(7)) { if (!TryParseOffset(r.GetString(7), out var value)) { corrupt = new(channel, shop, "Malformed LastSuccessUtc timestamp", DateTime.UtcNow); return false; } lastSuccess = value; }
        DateTimeOffset? rateLimitReset = null;
        if (!r.IsDBNull(11)) { if (!TryParseOffset(r.GetString(11), out var value)) { corrupt = new(channel, shop, "Malformed RateLimitResetUtc timestamp", DateTime.UtcNow); return false; } rateLimitReset = value; }
        DateTimeOffset? backoffUntil = null;
        if (!r.IsDBNull(13)) { if (!TryParseOffset(r.GetString(13), out var value)) { corrupt = new(channel, shop, "Malformed BackoffUntilUtc timestamp", DateTime.UtcNow); return false; } backoffUntil = value; }
        if (!TryParseOffset(r.GetString(14), out var updated)) { corrupt = new(channel, shop, "Malformed UpdatedUtc timestamp", DateTime.UtcNow); return false; }
        record = new() { Channel = channel, ShopId = shop, State = r.GetString(2), AuthStatus = r.GetString(3), ErrorClass = r.GetString(4), HttpStatus = r.IsDBNull(5) ? null : r.GetInt32(5), LastAttemptUtc = lastAttempt, LastSuccessUtc = lastSuccess, LastError = r.GetString(8), RateLimitRemaining = r.IsDBNull(9) ? null : r.GetInt32(9), RateLimitLimit = r.IsDBNull(10) ? null : r.GetInt32(10), RateLimitResetUtc = rateLimitReset, RetryAfterSeconds = r.IsDBNull(12) ? null : r.GetInt32(12), BackoffUntilUtc = backoffUntil, UpdatedUtc = updated };
        return true;
    }
    /// Only ever a format/parse failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised by the surrounding command, not this parse.
    static bool TryParseOffset(string value, out DateTimeOffset result) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
    static void ValidateIdentity(string channel, string shop) { if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(shop) || channel.Length > 80 || shop.Length > 160 || channel.Any(char.IsControl) || shop.Any(char.IsControl)) throw new ArgumentException("Kanal ve mağaza kimliği geçerli olmalı."); }
}
