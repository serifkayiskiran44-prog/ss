using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

public enum BizimHesapCreateAttemptStatus { Pending, Succeeded, NeedsReconciliation, Rejected }

public sealed class BizimHesapCreateReceiptStore
{
    readonly string connectionString;

    public BizimHesapCreateReceiptStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "bizimhesap-create-receipts.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS bizimhesap_create_receipts(account_key TEXT NOT NULL,product_id TEXT NOT NULL,payload_hash TEXT NOT NULL,status TEXT NOT NULL,remote_id TEXT NOT NULL,detail TEXT NOT NULL,updated_utc TEXT NOT NULL,PRIMARY KEY(account_key,product_id,payload_hash))";
        command.ExecuteNonQuery();
    }

    public bool HasBlockingAttempt(string accountKey, string productId, string payloadHash)
    {
        Validate(accountKey, productId, payloadHash);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM bizimhesap_create_receipts WHERE account_key=$account AND product_id=$product AND payload_hash=$payload";
        command.Parameters.AddWithValue("$account", accountKey); command.Parameters.AddWithValue("$product", productId); command.Parameters.AddWithValue("$payload", payloadHash);
        var value = command.ExecuteScalar() as string;
        return value is not null && !string.Equals(value, BizimHesapCreateAttemptStatus.Rejected.ToString(), StringComparison.Ordinal);
    }

    public string RemoteProductId(string accountKey, string productId)
    {
        Validate(accountKey, productId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT remote_id FROM bizimhesap_create_receipts WHERE account_key=$account AND product_id=$product AND status=$status AND remote_id<>'' ORDER BY updated_utc DESC LIMIT 1";
        command.Parameters.AddWithValue("$account", accountKey);
        command.Parameters.AddWithValue("$product", productId);
        command.Parameters.AddWithValue("$status", BizimHesapCreateAttemptStatus.Succeeded.ToString());
        return (command.ExecuteScalar() as string ?? "").Trim();
    }

    public void Begin(string accountKey, string productId, string payloadHash, DateTimeOffset now)
    {
        Validate(accountKey, productId, payloadHash);
        if (HasBlockingAttempt(accountKey, productId, payloadHash)) throw new InvalidOperationException("Bu ürün ve içerik daha önce BizimHesap'a gönderildi veya sonucu uzlaştırma bekliyor; ikinci kez gönderilmedi.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO bizimhesap_create_receipts(account_key,product_id,payload_hash,status,remote_id,detail,updated_utc) VALUES($account,$product,$payload,$status,'','',$updated) ON CONFLICT(account_key,product_id,payload_hash) DO UPDATE SET status=excluded.status,remote_id='',detail='',updated_utc=excluded.updated_utc";
        command.Parameters.AddWithValue("$account", accountKey); command.Parameters.AddWithValue("$product", productId); command.Parameters.AddWithValue("$payload", payloadHash); command.Parameters.AddWithValue("$status", BizimHesapCreateAttemptStatus.Pending.ToString()); command.Parameters.AddWithValue("$updated", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public void Complete(string accountKey, string productId, string payloadHash, BizimHesapCreateAttemptStatus status, string remoteId, string detail, DateTimeOffset now)
    {
        Validate(accountKey, productId, payloadHash);
        remoteId = (remoteId ?? "").Trim(); detail = AuditStore.Sanitize(detail ?? ""); if (remoteId.Length > 300) remoteId = remoteId[..300]; if (detail.Length > 1000) detail = detail[..1000];
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE bizimhesap_create_receipts SET status=$status,remote_id=$remote,detail=$detail,updated_utc=$updated WHERE account_key=$account AND product_id=$product AND payload_hash=$payload";
        command.Parameters.AddWithValue("$status", status.ToString()); command.Parameters.AddWithValue("$remote", remoteId); command.Parameters.AddWithValue("$detail", detail); command.Parameters.AddWithValue("$updated", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$account", accountKey); command.Parameters.AddWithValue("$product", productId); command.Parameters.AddWithValue("$payload", payloadHash);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("BizimHesap gönderim kaydı bulunamadı.");
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    static void Validate(params string[] values) { if (values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 300 || value.Any(char.IsControl))) throw new ArgumentException("Geçerli BizimHesap gönderim kimliği gerekli."); }
}
