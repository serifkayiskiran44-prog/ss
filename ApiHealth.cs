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
    public string BackoffSummary => BackoffUntilUtc is { } until && until > DateTimeOffset.UtcNow ? TimeDisplay.Format(until) : "-";
}

public sealed record ApiHealthSummary(int Total, int Healthy, int Blocked, int AuthErrors, int RateLimited, int OtherErrors, int BackingOff);

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
    // #2561: a numeric reset is either a small "seconds from now" delta or an absolute Unix epoch second -- the two
    // ranges never overlap in practice, so a value is read as whichever range it actually falls in, never both
    // conditions guarded by the smaller range's own bound (the bug this replaces: the outer bound was the relative
    // range's own ceiling, so a real epoch second -- always in the billions -- could never reach the epoch branch
    // beneath it). Anything outside both ranges (including epoch milliseconds, which land far past the epoch
    // ceiling) is ambiguous and is left null rather than guessed at or allowed to overflow.
    internal const long RelativeSecondsMax = 31_536_000; // one year of relative seconds, generous for any real Retry-After-style delta
    internal const long EpochSecondsMin = 1_000_000_000; // 2001-09-09 UTC -- below any real recent epoch second, safely above any real relative delta
    internal const long EpochSecondsMax = 4_102_444_800; // 2100-01-01 UTC -- generous future bound; FromUnixTimeSeconds never overflows within it

    static DateTimeOffset? ParseResetHeader(HttpResponseHeaders headers, DateTimeOffset now, params string[] names)
    {
        foreach (var name in names) if (headers.TryGetValues(name, out var values))
        {
            // #2561: a malformed value under this header name does not give up on the header -- every value offered
            // under the same name is tried in order, and only the whole name is abandoned when none of them parse.
            foreach (var raw in values)
            {
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    if (number >= 0 && number <= RelativeSecondsMax) return now.AddSeconds(number);
                    if (number >= EpochSecondsMin && number <= EpochSecondsMax) return DateTimeOffset.FromUnixTimeSeconds(number);
                    continue; // negative, or outside both ranges (e.g. epoch milliseconds): ambiguous, try the next value
                }
                if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date.ToUniversalTime();
            }
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
    SqliteConnection Open() { var c = SqliteConnectionPolicy.Open(connectionString); using var pragma = c.CreateCommand(); pragma.CommandText = "PRAGMA foreign_keys=ON;PRAGMA busy_timeout=15000;"; pragma.ExecuteNonQuery(); return c; }
    public void EnsureConnection(string channel, string shopId, string state = "NOT_CONFIGURED", string error = "")
    {
        ValidateIdentity(channel, shopId); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT OR IGNORE INTO ApiHealth(Channel,ShopId,State,AuthStatus,ErrorClass,HttpStatus,LastAttemptUtc,LastSuccessUtc,LastError,RateLimitRemaining,RateLimitLimit,RateLimitResetUtc,RetryAfterSeconds,BackoffUntilUtc,UpdatedUtc) VALUES($channel,$shop,$state,'UNKNOWN','None',NULL,$now,NULL,$error,NULL,NULL,NULL,NULL,NULL,$now)"; command.Parameters.AddWithValue("$channel", channel.Trim().ToLowerInvariant()); command.Parameters.AddWithValue("$shop", shopId.Trim()); command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$error", AuditStore.Sanitize(error)); command.ExecuteNonQuery();
    }
    public void Observe(string channel, string shopId, ApiHealthObservation observation)
    {
        ValidateIdentity(channel, shopId); var normalizedChannel = channel.Trim().ToLowerInvariant(); var normalizedShop = shopId.Trim(); var observed = (observation.ObservedUtc == default ? DateTimeOffset.UtcNow : observation.ObservedUtc).ToUniversalTime(); var lastSuccess = observation.State == "HEALTHY" ? observed.ToString("O", CultureInfo.InvariantCulture) : null; var error = observation.ErrorMessage.Length == 0 ? "" : AuditStore.Sanitize(observation.ErrorMessage); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO ApiHealth(Channel,ShopId,State,AuthStatus,ErrorClass,HttpStatus,LastAttemptUtc,LastSuccessUtc,LastError,RateLimitRemaining,RateLimitLimit,RateLimitResetUtc,RetryAfterSeconds,BackoffUntilUtc,UpdatedUtc) VALUES($channel,$shop,$state,$auth,$class,$http,$attempt,$success,$error,$remaining,$limit,$reset,$retry,$backoff,$updated) ON CONFLICT(Channel,ShopId) DO UPDATE SET State=excluded.State,AuthStatus=excluded.AuthStatus,ErrorClass=excluded.ErrorClass,HttpStatus=excluded.HttpStatus,LastAttemptUtc=excluded.LastAttemptUtc,LastSuccessUtc=COALESCE(excluded.LastSuccessUtc,ApiHealth.LastSuccessUtc),LastError=excluded.LastError,RateLimitRemaining=excluded.RateLimitRemaining,RateLimitLimit=excluded.RateLimitLimit,RateLimitResetUtc=excluded.RateLimitResetUtc,RetryAfterSeconds=excluded.RetryAfterSeconds,BackoffUntilUtc=excluded.BackoffUntilUtc,UpdatedUtc=excluded.UpdatedUtc"; command.Parameters.AddWithValue("$channel", normalizedChannel); command.Parameters.AddWithValue("$shop", normalizedShop); command.Parameters.AddWithValue("$state", observation.State); command.Parameters.AddWithValue("$auth", observation.AuthStatus); command.Parameters.AddWithValue("$class", observation.ErrorClass); command.Parameters.AddWithValue("$http", (object?)observation.HttpStatus ?? DBNull.Value); command.Parameters.AddWithValue("$attempt", observed.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$success", (object?)lastSuccess ?? DBNull.Value); command.Parameters.AddWithValue("$error", error); command.Parameters.AddWithValue("$remaining", (object?)observation.RateLimitRemaining ?? DBNull.Value); command.Parameters.AddWithValue("$limit", (object?)observation.RateLimitLimit ?? DBNull.Value); command.Parameters.AddWithValue("$reset", observation.RateLimitResetUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value); command.Parameters.AddWithValue("$retry", (object?)observation.RetryAfterSeconds ?? DBNull.Value); command.Parameters.AddWithValue("$backoff", observation.BackoffUntilUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value); command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery();
    }
    // #2560: bounded/paged read path. The panel and any decision (ShouldDefer) must never materialize the whole
    // table: Get is a single-row lookup, List takes a server-side LIMIT/OFFSET with a stable tiebreaker so paging
    // never skips or repeats a row when several share the same UpdatedUtc, Summary is one SQL aggregate query, and
    // a search string is matched literally -- a caller's own '%' or '_' is escaped, never treated as a wildcard.
    public const int DefaultPageSize = 200, MaxPageSize = 500, MaxQueryLength = 200;

    public ApiHealthRecord? Get(string channel, string shopId)
    {
        ValidateIdentity(channel, shopId);
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = "SELECT Channel,ShopId,State,AuthStatus,ErrorClass,HttpStatus,LastAttemptUtc,LastSuccessUtc,LastError,RateLimitRemaining,RateLimitLimit,RateLimitResetUtc,RetryAfterSeconds,BackoffUntilUtc,UpdatedUtc FROM ApiHealth WHERE Channel=$channel AND ShopId=$shop";
        command.Parameters.AddWithValue("$channel", channel.Trim().ToLowerInvariant()); command.Parameters.AddWithValue("$shop", shopId.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Escapes a caller's own '%', '_' and the escape character itself, so the search is a literal substring match -- never a wildcard the caller did not ask for.</summary>
    public static string EscapeLikeLiteral(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    static (string Query, string Like, string State, int Limit, int Offset) NormalizeListArgs(string? query, string? state, int? limit, int offset)
    {
        var raw = query ?? ""; if (raw.Length > MaxQueryLength) raw = raw[..MaxQueryLength]; // bounded query length (edge case)
        var cleanState = (state ?? "").Trim();
        var boundedLimit = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var boundedOffset = Math.Max(0, offset);
        return (raw.Trim(), raw.Trim().Length == 0 ? "" : $"%{EscapeLikeLiteral(raw.Trim())}%", cleanState, boundedLimit, boundedOffset);
    }

    const string ListSql = "SELECT Channel,ShopId,State,AuthStatus,ErrorClass,HttpStatus,LastAttemptUtc,LastSuccessUtc,LastError,RateLimitRemaining,RateLimitLimit,RateLimitResetUtc,RetryAfterSeconds,BackoffUntilUtc,UpdatedUtc FROM ApiHealth WHERE ($query='' OR Channel LIKE $like ESCAPE '\\' OR ShopId LIKE $like ESCAPE '\\' OR State LIKE $like ESCAPE '\\' OR AuthStatus LIKE $like ESCAPE '\\' OR ErrorClass LIKE $like ESCAPE '\\' OR LastError LIKE $like ESCAPE '\\') AND ($state='' OR State=$state) ORDER BY CASE State WHEN 'AUTH_ERROR' THEN 0 WHEN 'RATE_LIMITED' THEN 1 WHEN 'SERVER_ERROR' THEN 2 WHEN 'NETWORK_ERROR' THEN 3 WHEN 'TIMEOUT' THEN 4 WHEN 'LIVE_API_BLOCKED' THEN 5 WHEN 'NOT_CONFIGURED' THEN 6 WHEN 'HEALTHY' THEN 7 ELSE 8 END,UpdatedUtc DESC,Channel ASC,ShopId ASC LIMIT $limit OFFSET $offset";

    /// <summary>One page, bounded and deterministically ordered; a query longer than <see cref="MaxQueryLength"/> is truncated, never rejected with an error that could leak into a crash log.</summary>
    public IReadOnlyList<ApiHealthRecord> List(string? query = null, string? state = null, int? limit = null, int offset = 0)
    {
        var (value, like, cleanState, boundedLimit, boundedOffset) = NormalizeListArgs(query, state, limit, offset);
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = ListSql;
        command.Parameters.AddWithValue("$query", value); command.Parameters.AddWithValue("$like", like); command.Parameters.AddWithValue("$state", cleanState); command.Parameters.AddWithValue("$limit", boundedLimit); command.Parameters.AddWithValue("$offset", boundedOffset);
        using var reader = command.ExecuteReader(); var rows = new List<ApiHealthRecord>(); while (reader.Read()) rows.Add(Read(reader)); return rows;
    }

    /// <summary>The same page, but cancellable: honored between each row and before the query itself starts, so a superseded refresh stops promptly instead of finishing into a stale UI state.</summary>
    public async Task<IReadOnlyList<ApiHealthRecord>> ListAsync(string? query, string? state, int? limit, int offset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (value, like, cleanState, boundedLimit, boundedOffset) = NormalizeListArgs(query, state, limit, offset);
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = ListSql;
        command.Parameters.AddWithValue("$query", value); command.Parameters.AddWithValue("$like", like); command.Parameters.AddWithValue("$state", cleanState); command.Parameters.AddWithValue("$limit", boundedLimit); command.Parameters.AddWithValue("$offset", boundedOffset);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<ApiHealthRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(Read(reader));
        return rows;
    }

    public bool ShouldDefer(string channel, string shopId, DateTimeOffset? now = null) => Get(channel, shopId)?.BackoffUntilUtc is { } until && until > (now ?? DateTimeOffset.UtcNow);

    const string SummarySql = "SELECT COUNT(*), SUM(CASE WHEN State='HEALTHY' THEN 1 ELSE 0 END), SUM(CASE WHEN State='LIVE_API_BLOCKED' THEN 1 ELSE 0 END), SUM(CASE WHEN State='AUTH_ERROR' THEN 1 ELSE 0 END), SUM(CASE WHEN State='RATE_LIMITED' THEN 1 ELSE 0 END), SUM(CASE WHEN State NOT IN('HEALTHY','LIVE_API_BLOCKED','AUTH_ERROR','RATE_LIMITED') THEN 1 ELSE 0 END), SUM(CASE WHEN BackoffUntilUtc IS NOT NULL AND BackoffUntilUtc > $now THEN 1 ELSE 0 END) FROM ApiHealth";

    /// <summary>One SQL aggregate round trip -- never a full table scan into managed rows to count them.</summary>
    public ApiHealthSummary Summary(DateTimeOffset? now = null)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = SummarySql;
        command.Parameters.AddWithValue("$now", (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSummary(reader) : new(0, 0, 0, 0, 0, 0, 0);
    }

    public async Task<ApiHealthSummary> SummaryAsync(DateTimeOffset? now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = SummarySql;
        command.Parameters.AddWithValue("$now", (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSummary(reader) : new(0, 0, 0, 0, 0, 0, 0);
    }

    static ApiHealthSummary ReadSummary(SqliteDataReader r) => new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6));
    static ApiHealthRecord Read(SqliteDataReader r) => new() { Channel = r.GetString(0), ShopId = r.GetString(1), State = r.GetString(2), AuthStatus = r.GetString(3), ErrorClass = r.GetString(4), HttpStatus = r.IsDBNull(5) ? null : r.GetInt32(5), LastAttemptUtc = DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), LastSuccessUtc = r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), LastError = r.GetString(8), RateLimitRemaining = r.IsDBNull(9) ? null : r.GetInt32(9), RateLimitLimit = r.IsDBNull(10) ? null : r.GetInt32(10), RateLimitResetUtc = r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), RetryAfterSeconds = r.IsDBNull(12) ? null : r.GetInt32(12), BackoffUntilUtc = r.IsDBNull(13) ? null : DateTimeOffset.Parse(r.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), UpdatedUtc = DateTimeOffset.Parse(r.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) };
    static void ValidateIdentity(string channel, string shop) { if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(shop) || channel.Length > 80 || shop.Length > 160 || channel.Any(char.IsControl) || shop.Any(char.IsControl)) throw new ArgumentException("Kanal ve mağaza kimliği geçerli olmalı."); }
}
