using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

public enum NesQueueState { Planned, Ready, Running, RetryWaiting, Blocked, Unknown, Completed, Cancelled }
public enum NesQueueStep { PrepareDocument, SendInvoice, AwaitNumber, DownloadPdf, UploadMarketplace, Finish }

public sealed record NesQueueRequest(
    string Marketplace, string ShopId, string OrderId, string PackageId,
    NesDocumentKind Kind, bool UploadMarketplace, string ReceiverAlias,
    string OrderFingerprint);

public sealed class NesQueueItem
{
    public string Id { get; set; } = "";
    public string Marketplace { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string PackageId { get; set; } = "";
    public NesDocumentKind Kind { get; set; }
    public bool UploadMarketplace { get; set; }
    public string ReceiverAlias { get; set; } = "";
    public string OrderFingerprint { get; set; } = "";
    public NesQueueState State { get; set; }
    public NesQueueStep Step { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public string LeaseToken { get; set; } = "";
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string NesUuid { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public string PdfPath { get; set; } = "";
    public string PdfSha256 { get; set; } = "";
    public string LastError { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed record CorruptNesQueueRow(string Id, string Reason, DateTimeOffset DetectedUtc);

public sealed class NesInvoiceQueueStore
{
    readonly string connectionString;

    public NesInvoiceQueueStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "nes-invoice-queue.db"),
            DefaultTimeout = 15,
            Pooling = true
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS nes_invoice_queue(
              id TEXT NOT NULL PRIMARY KEY,
              marketplace TEXT NOT NULL,
              shop_id TEXT NOT NULL,
              order_id TEXT NOT NULL,
              package_id TEXT NOT NULL,
              kind INTEGER NOT NULL,
              upload_marketplace INTEGER NOT NULL,
              receiver_alias TEXT NOT NULL,
              order_fingerprint TEXT NOT NULL,
              state INTEGER NOT NULL,
              step INTEGER NOT NULL,
              attempt_count INTEGER NOT NULL,
              next_attempt_utc TEXT NULL,
              lease_token TEXT NOT NULL,
              lease_until_utc TEXT NULL,
              nes_uuid TEXT NOT NULL,
              invoice_number TEXT NOT NULL,
              pdf_path TEXT NOT NULL,
              pdf_sha256 TEXT NOT NULL,
              last_error TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              updated_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_nes_invoice_queue_order
              ON nes_invoice_queue(marketplace, shop_id, order_id);
            CREATE INDEX IF NOT EXISTS ix_nes_invoice_queue_next
              ON nes_invoice_queue(state, next_attempt_utc, created_utc);
            """;
        command.ExecuteNonQuery();
    }

    SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=15000";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public NesQueueItem Enqueue(NesQueueRequest request, DateTimeOffset now)
    {
        Validate(request);
        var id = Guid.NewGuid().ToString("N");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO nes_invoice_queue
                (id,marketplace,shop_id,order_id,package_id,kind,upload_marketplace,receiver_alias,
                 order_fingerprint,state,step,attempt_count,next_attempt_utc,lease_token,lease_until_utc,
                 nes_uuid,invoice_number,pdf_path,pdf_sha256,last_error,created_utc,updated_utc)
                VALUES($id,$marketplace,$shop,$order,$package,$kind,$upload,$alias,$fingerprint,
                       $state,$step,0,$next,'',NULL,'','','','','',$created,$updated)
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$marketplace", request.Marketplace.Trim());
            insert.Parameters.AddWithValue("$shop", request.ShopId.Trim());
            insert.Parameters.AddWithValue("$order", request.OrderId.Trim());
            insert.Parameters.AddWithValue("$package", request.PackageId.Trim());
            insert.Parameters.AddWithValue("$kind", (int)request.Kind);
            insert.Parameters.AddWithValue("$upload", request.UploadMarketplace ? 1 : 0);
            insert.Parameters.AddWithValue("$alias", request.ReceiverAlias.Trim());
            insert.Parameters.AddWithValue("$fingerprint", request.OrderFingerprint.Trim());
            insert.Parameters.AddWithValue("$state", (int)NesQueueState.Ready);
            insert.Parameters.AddWithValue("$step", (int)NesQueueStep.PrepareDocument);
            insert.Parameters.AddWithValue("$next", Format(now));
            insert.Parameters.AddWithValue("$created", Format(now));
            insert.Parameters.AddWithValue("$updated", Format(now));
            insert.ExecuteNonQuery();
        }
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = SelectColumns + " WHERE marketplace=$marketplace AND shop_id=$shop AND order_id=$order";
        select.Parameters.AddWithValue("$marketplace", request.Marketplace.Trim());
        select.Parameters.AddWithValue("$shop", request.ShopId.Trim());
        select.Parameters.AddWithValue("$order", request.OrderId.Trim());
        using var reader = select.ExecuteReader();
        if (!reader.Read() || !TryRead(reader, out var result, out _))
            throw new InvalidOperationException("NES kuyruğu kaydı okunamadı.");
        transaction.Commit();
        return result!;
    }

    public IReadOnlyList<NesQueueItem> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " ORDER BY created_utc,id";
        using var reader = command.ExecuteReader();
        var result = new List<NesQueueItem>();
        while (reader.Read()) if (TryRead(reader, out var item, out _)) result.Add(item!);
        return result;
    }

    public IReadOnlyList<CorruptNesQueueRow> CorruptRows()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " ORDER BY created_utc,id";
        using var reader = command.ExecuteReader();
        var result = new List<CorruptNesQueueRow>();
        while (reader.Read()) if (!TryRead(reader, out _, out var reason))
            result.Add(new(reader.IsDBNull(0) ? "" : reader.GetString(0), reason, DateTimeOffset.UtcNow));
        return result;
    }

    public bool TryClaimNext(DateTimeOffset now, TimeSpan leaseDuration, out NesQueueItem? claimed)
    {
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        string? id = null;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = """
                SELECT id FROM nes_invoice_queue
                WHERE (
                  (state IN ($planned,$ready,$retry) AND (next_attempt_utc IS NULL OR next_attempt_utc<=$now))
                  OR (state=$running AND lease_until_utc IS NOT NULL AND lease_until_utc<=$now)
                )
                ORDER BY created_utc,id LIMIT 1
                """;
            find.Parameters.AddWithValue("$planned", (int)NesQueueState.Planned);
            find.Parameters.AddWithValue("$ready", (int)NesQueueState.Ready);
            find.Parameters.AddWithValue("$retry", (int)NesQueueState.RetryWaiting);
            find.Parameters.AddWithValue("$running", (int)NesQueueState.Running);
            find.Parameters.AddWithValue("$now", Format(now));
            id = find.ExecuteScalar() as string;
        }
        if (id is null) { transaction.Commit(); claimed = null; return false; }
        var token = Guid.NewGuid().ToString("N");
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE nes_invoice_queue SET state=$state,lease_token=$token,lease_until_utc=$until,updated_utc=$now WHERE id=$id";
            update.Parameters.AddWithValue("$state", (int)NesQueueState.Running);
            update.Parameters.AddWithValue("$token", token);
            update.Parameters.AddWithValue("$until", Format(now.Add(leaseDuration)));
            update.Parameters.AddWithValue("$now", Format(now));
            update.Parameters.AddWithValue("$id", id);
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("NES kuyruğu lease kaydı alınamadı.");
        }
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = SelectColumns + " WHERE id=$id";
        select.Parameters.AddWithValue("$id", id);
        using var reader = select.ExecuteReader();
        if (!reader.Read() || !TryRead(reader, out claimed, out _)) throw new InvalidOperationException("NES kuyruğu lease kaydı okunamadı.");
        transaction.Commit();
        return true;
    }

    public void Advance(string id, string leaseToken, NesQueueStep step, NesQueueState state, DateTimeOffset now) =>
        LeaseUpdate(id, leaseToken,
            "state=$state,step=$step,next_attempt_utc=$next,lease_token='',lease_until_utc=NULL,last_error='',updated_utc=$now",
            command => { command.Parameters.AddWithValue("$state", (int)state); command.Parameters.AddWithValue("$step", (int)step); command.Parameters.AddWithValue("$next", Format(now)); command.Parameters.AddWithValue("$now", Format(now)); });

    public void ScheduleRetry(string id, string leaseToken, TimeSpan delay, string error, DateTimeOffset now)
    {
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        if (delay > TimeSpan.FromHours(24)) delay = TimeSpan.FromHours(24);
        LeaseUpdate(id, leaseToken,
            "state=$state,attempt_count=attempt_count+1,next_attempt_utc=$next,lease_token='',lease_until_utc=NULL,last_error=$error,updated_utc=$now",
            command => { command.Parameters.AddWithValue("$state", (int)NesQueueState.RetryWaiting); command.Parameters.AddWithValue("$next", Format(now.Add(delay))); command.Parameters.AddWithValue("$error", MarketplaceConnectionStore.Redact(error ?? "")); command.Parameters.AddWithValue("$now", Format(now)); });
    }

    public void Block(string id, string leaseToken, string error, DateTimeOffset now) => Terminal(id, leaseToken, NesQueueState.Blocked, error, now);
    public void MarkUnknown(string id, string leaseToken, string error, DateTimeOffset now) => Terminal(id, leaseToken, NesQueueState.Unknown, error, now);
    public void Complete(string id, string leaseToken, DateTimeOffset now) => Terminal(id, leaseToken, NesQueueState.Completed, "", now);
    public void Cancel(string id, string leaseToken, DateTimeOffset now) => Terminal(id, leaseToken, NesQueueState.Cancelled, "", now);

    void Terminal(string id, string leaseToken, NesQueueState state, string error, DateTimeOffset now) =>
        LeaseUpdate(id, leaseToken,
            "state=$state,step=$step,next_attempt_utc=NULL,lease_token='',lease_until_utc=NULL,last_error=$error,updated_utc=$now",
            command => { command.Parameters.AddWithValue("$state", (int)state); command.Parameters.AddWithValue("$step", (int)NesQueueStep.Finish); command.Parameters.AddWithValue("$error", MarketplaceConnectionStore.Redact(error ?? "")); command.Parameters.AddWithValue("$now", Format(now)); });

    void LeaseUpdate(string id, string leaseToken, string assignments, Action<SqliteCommand> bind)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(leaseToken)) throw new ArgumentException("Kuyruk kimliği ve lease anahtarı gerekli.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE nes_invoice_queue SET " + assignments + " WHERE id=$id AND lease_token=$token AND state=$running";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$token", leaseToken);
        command.Parameters.AddWithValue("$running", (int)NesQueueState.Running);
        bind(command);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("NES kuyruğu lease anahtarı geçersiz veya süresi dolmuş.");
    }

    static void Validate(NesQueueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Marketplace) || string.IsNullOrWhiteSpace(request.ShopId) ||
            string.IsNullOrWhiteSpace(request.OrderId) || string.IsNullOrWhiteSpace(request.OrderFingerprint))
            throw new ArgumentException("Pazaryeri, mağaza, sipariş ve sipariş parmak izi gerekli.");
        if (!Enum.IsDefined(request.Kind)) throw new ArgumentOutOfRangeException(nameof(request.Kind));
    }

    static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static bool ParseNullable(SqliteDataReader reader, int ordinal, out DateTimeOffset? value)
    {
        if (reader.IsDBNull(ordinal)) { value = null; return true; }
        if (DateTimeOffset.TryParseExact(reader.GetString(ordinal), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) { value = parsed; return true; }
        value = null; return false;
    }

    static bool TryRead(SqliteDataReader reader, out NesQueueItem? item, out string reason)
    {
        item = null; reason = "";
        try
        {
            var stateValue = reader.GetInt32(9);
            var stepValue = reader.GetInt32(10);
            var kindValue = reader.GetInt32(5);
            if (!Enum.IsDefined(typeof(NesQueueState), stateValue)) { reason = "geçersiz state değeri"; return false; }
            if (!Enum.IsDefined(typeof(NesQueueStep), stepValue)) { reason = "geçersiz step değeri"; return false; }
            if (!Enum.IsDefined(typeof(NesDocumentKind), kindValue)) { reason = "geçersiz kind değeri"; return false; }
            if (!ParseNullable(reader, 12, out var next)) { reason = "geçersiz next_attempt_utc değeri"; return false; }
            if (!ParseNullable(reader, 14, out var leaseUntil)) { reason = "geçersiz lease_until_utc değeri"; return false; }
            if (!DateTimeOffset.TryParseExact(reader.GetString(20), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created)) { reason = "geçersiz created_utc değeri"; return false; }
            if (!DateTimeOffset.TryParseExact(reader.GetString(21), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updated)) { reason = "geçersiz updated_utc değeri"; return false; }
            item = new NesQueueItem
            {
                Id = reader.GetString(0), Marketplace = reader.GetString(1), ShopId = reader.GetString(2), OrderId = reader.GetString(3),
                PackageId = reader.GetString(4), Kind = (NesDocumentKind)kindValue, UploadMarketplace = reader.GetInt32(6) != 0,
                ReceiverAlias = reader.GetString(7), OrderFingerprint = reader.GetString(8), State = (NesQueueState)stateValue,
                Step = (NesQueueStep)stepValue, AttemptCount = reader.GetInt32(11), NextAttemptUtc = next,
                LeaseToken = reader.GetString(13), LeaseUntilUtc = leaseUntil, NesUuid = reader.GetString(15),
                InvoiceNumber = reader.GetString(16), PdfPath = reader.GetString(17), PdfSha256 = reader.GetString(18),
                LastError = reader.GetString(19), CreatedUtc = created, UpdatedUtc = updated
            };
            return true;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or SqliteException)
        {
            reason = "kuyruk satırı okunamadı";
            return false;
        }
    }

    const string SelectColumns = """
        SELECT id,marketplace,shop_id,order_id,package_id,kind,upload_marketplace,receiver_alias,
               order_fingerprint,state,step,attempt_count,next_attempt_utc,lease_token,lease_until_utc,
               nes_uuid,invoice_number,pdf_path,pdf_sha256,last_error,created_utc,updated_utc
        FROM nes_invoice_queue
        """;
}
