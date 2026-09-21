using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

public sealed class NesRecipientAliasStore
{
    readonly string connectionString;

    public NesRecipientAliasStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "nes-recipient-aliases.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS nes_recipient_aliases(tax_number TEXT NOT NULL PRIMARY KEY,alias TEXT NOT NULL,updated_utc TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }

    public string? Get(string taxNumber)
    {
        ValidateTaxNumber(taxNumber);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT alias FROM nes_recipient_aliases WHERE tax_number=$tax";
        command.Parameters.AddWithValue("$tax", taxNumber.Trim());
        return command.ExecuteScalar() as string;
    }

    public void Save(string taxNumber, string alias, DateTimeOffset now)
    {
        ValidateTaxNumber(taxNumber);
        alias = (alias ?? "").Trim();
        if (!alias.StartsWith("urn:", StringComparison.OrdinalIgnoreCase) || alias.Length > 500 || alias.Any(char.IsControl))
            throw new ArgumentException("NES alıcı etiketi urn: ile başlamalı.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO nes_recipient_aliases(tax_number,alias,updated_utc) VALUES($tax,$alias,$updated) ON CONFLICT(tax_number) DO UPDATE SET alias=excluded.alias,updated_utc=excluded.updated_utc";
        command.Parameters.AddWithValue("$tax", taxNumber.Trim());
        command.Parameters.AddWithValue("$alias", alias);
        command.Parameters.AddWithValue("$updated", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }

    static void ValidateTaxNumber(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length is not 10 and not 11 || !value.All(char.IsDigit)) throw new ArgumentException("Alıcı VKN veya TCKN değeri 10 ya da 11 rakam olmalı.");
    }
}
