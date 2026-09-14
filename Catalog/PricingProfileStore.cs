using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// The only two fields a pricing formula may read from - deliberately closed, not a
/// free-text field name, so a typo or a made-up field can never be "silently missing".
public enum PricingSourceField { Cost, Price }

public sealed class PricingProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Formula { get; set; } = "x";
    public PricingSourceField SourceField { get; set; } = PricingSourceField.Cost;
    public bool Active { get; set; } = true;
    public int Version { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// Named, reusable pricing formula definitions - distinct from PricePolicy (which is
/// one unnamed formula per channel/shop). A profile can be created/edited/deactivated
/// independently of any channel/shop and, once real callers exist, referenced from
/// multiple policies instead of re-entering the same formula everywhere. No Kritik
/// Fiyat (critical price) concept is added here - that stays deferred.
public sealed class PricingProfileStore
{
    readonly string connectionString;
    public PricingProfileStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS PricingProfiles(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Formula TEXT NOT NULL, SourceField TEXT NOT NULL, Active INTEGER NOT NULL, Version INTEGER NOT NULL, UpdatedUtc TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }

    public PricingProfile Save(PricingProfile profile)
    {
        var name = profile.Name.Trim();
        if (name.Length == 0 || name.Length > 120) throw new ArgumentException("Profil adı 1-120 karakter olmalı.");
        if (profile.Formula.Length is 0 or > 8192) throw new ArgumentException("Formül boş olamaz ve 8192 karakteri aşamaz.");
        _ = PriceFormula.Compile(profile.Formula);

        using var c = Open(); using var tx = c.BeginTransaction();
        using (var dup = c.CreateCommand())
        {
            dup.Transaction = tx; dup.CommandText = "SELECT Id FROM PricingProfiles WHERE Id<>$id AND lower(Name)=lower($name)";
            dup.Parameters.AddWithValue("$id", profile.Id); dup.Parameters.AddWithValue("$name", name);
            if (dup.ExecuteScalar() is not null) throw new InvalidOperationException("Bu isimde bir fiyat formülü profili zaten var.");
        }
        using (var find = c.CreateCommand())
        {
            find.Transaction = tx; find.CommandText = "SELECT Version FROM PricingProfiles WHERE Id=$id"; find.Parameters.AddWithValue("$id", profile.Id);
            var current = find.ExecuteScalar();
            if (current is not null && Convert.ToInt32(current, CultureInfo.InvariantCulture) != profile.Version) throw new InvalidOperationException("Profil başka bir işlemde değişti; yenileyip tekrar deneyin.");
        }
        profile.Name = name; profile.Version++; profile.UpdatedUtc = DateTime.UtcNow;
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO PricingProfiles(Id,Name,Formula,SourceField,Active,Version,UpdatedUtc) VALUES($id,$name,$formula,$source,$active,$version,$updated) ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,Formula=excluded.Formula,SourceField=excluded.SourceField,Active=excluded.Active,Version=excluded.Version,UpdatedUtc=excluded.UpdatedUtc";
        cmd.Parameters.AddWithValue("$id", profile.Id); cmd.Parameters.AddWithValue("$name", profile.Name); cmd.Parameters.AddWithValue("$formula", profile.Formula);
        cmd.Parameters.AddWithValue("$source", profile.SourceField.ToString()); cmd.Parameters.AddWithValue("$active", profile.Active ? 1 : 0);
        cmd.Parameters.AddWithValue("$version", profile.Version); cmd.Parameters.AddWithValue("$updated", profile.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery(); tx.Commit();
        return profile;
    }

    public IReadOnlyList<PricingProfile> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Name,Formula,SourceField,Active,Version,UpdatedUtc FROM PricingProfiles ORDER BY Name";
        using var r = cmd.ExecuteReader(); var result = new List<PricingProfile>(); while (r.Read()) result.Add(Read(r)); return result;
    }

    public PricingProfile? Find(string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Name,Formula,SourceField,Active,Version,UpdatedUtc FROM PricingProfiles WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : null;
    }

    /// Reads the formula's configured source value directly off the product - the
    /// SourceField enum makes an unrecognized/fabricated field name impossible to
    /// persist in the first place (see Save's PricingSourceField.ToString() write and
    /// the enum's closed set), so this never needs a "field not found" branch.
    public static decimal SourceValue(PricingProfile profile, CatalogProduct product) => profile.SourceField switch
    {
        PricingSourceField.Cost => product.Cost,
        PricingSourceField.Price => product.Price,
        _ => throw new InvalidOperationException("Formül kaynak alanı tanınmıyor; hesaplama durduruldu."),
    };

    public decimal Evaluate(PricingProfile profile, CatalogProduct product)
    {
        if (!profile.Active) throw new InvalidOperationException("Pasif fiyat profili ile hesaplama yapılamaz.");
        var source = SourceValue(profile, product);
        return PriceFormula.Evaluate(profile.Formula, source);
    }

    static PricingProfile Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), Name = r.GetString(1), Formula = r.GetString(2),
        SourceField = Enum.TryParse<PricingSourceField>(r.GetString(3), out var field) ? field : PricingSourceField.Cost,
        Active = r.GetInt32(4) != 0, Version = r.GetInt32(5),
        UpdatedUtc = DateTime.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };
}
