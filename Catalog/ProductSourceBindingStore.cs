using System.IO;
using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>
/// Stores, per product and field group, which source feeds it (manual or one XML source).
/// Lives in its own database next to the catalog; it never changes catalog products.
/// </summary>
public sealed class ProductSourceBindingStore
{
    readonly string connectionString;
    readonly CatalogStore catalog;

    public ProductSourceBindingStore(string directory, CatalogStore catalog)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "product-sources.db"), DefaultTimeout = 15, Pooling = true
        }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;PRAGMA busy_timeout=15000;" +
            "CREATE TABLE IF NOT EXISTS ProductSourceBindings(ProductId TEXT NOT NULL, FieldGroup INTEGER NOT NULL, Kind INTEGER NOT NULL, " +
            "SourceId TEXT NOT NULL, Enabled INTEGER NOT NULL, Version INTEGER NOT NULL, UpdatedUtc TEXT NOT NULL, PRIMARY KEY(ProductId, FieldGroup));";
        cmd.ExecuteNonQuery();
        SchemaVersion.Ensure(c);
    }

    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }

    static readonly ProductFieldGroup[] Groups = Enum.GetValues<ProductFieldGroup>();

    /// <summary>One row per field group; groups that were never stored are returned as Manual with version 0.</summary>
    public IReadOnlyList<ProductSourceBinding> Get(string productId)
    {
        var stored = new Dictionary<ProductFieldGroup, ProductSourceBinding>();
        if (!string.IsNullOrWhiteSpace(productId))
        {
            using var c = Open();
            foreach (var row in ReadRows(c, null, productId)) stored[row.Group] = row;
        }
        return Groups.Select(g => stored.TryGetValue(g, out var row)
            ? row : new ProductSourceBinding(productId ?? "", g, ProductSourceKind.Manual, "", true, 0, default)).ToList();
    }

    /// <summary>Compare-and-swap: the stored version must equal <paramref name="expectedVersion"/>.</summary>
    public ProductSourceBinding Save(ProductSourceBinding binding, long expectedVersion)
    {
        if (binding is null) throw new ArgumentNullException(nameof(binding));
        if (!Enum.IsDefined(binding.Group) || !Enum.IsDefined(binding.Kind)) throw new InvalidOperationException("Geçersiz alan grubu veya kaynak türü.");
        if (!catalog.Products().Any(p => p.Id == binding.ProductId)) throw new InvalidOperationException("Ürün bulunamadı.");
        var sourceId = (binding.SourceId ?? "").Trim();
        if (binding.Kind == ProductSourceKind.Manual)
        {
            if (sourceId.Length != 0) throw new InvalidOperationException("Elle girilen alan bir XML kaynağı gösteremez.");
        }
        else
        {
            var source = catalog.Sources().FirstOrDefault(s => s.Id == sourceId);
            if (source is null) throw new InvalidOperationException("XML kaynağı bulunamadı.");
            if (!source.Enabled) throw new InvalidOperationException("XML kaynağı kapalı.");
        }
        using var c = Open();
        using var tx = c.BeginTransaction();
        var current = ReadRows(c, tx, binding.ProductId).FirstOrDefault(r => r.Group == binding.Group)?.Version ?? 0;
        if (current != expectedVersion) throw new InvalidOperationException("Kaynak bağı başka bir işlemle değişti; yenileyip tekrar deneyin.");
        var saved = binding with { SourceId = sourceId, Version = current + 1, UpdatedUtc = DateTime.UtcNow };
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO ProductSourceBindings(ProductId,FieldGroup,Kind,SourceId,Enabled,Version,UpdatedUtc) VALUES($p,$g,$k,$s,$e,$v,$u) " +
            "ON CONFLICT(ProductId,FieldGroup) DO UPDATE SET Kind=$k,SourceId=$s,Enabled=$e,Version=$v,UpdatedUtc=$u";
        Bind(cmd, saved); cmd.ExecuteNonQuery();
        tx.Commit();
        return saved;
    }

    /// <summary>Manual is always usable; an XML binding needs to be enabled and its source must still exist and be enabled.</summary>
    public bool IsUsable(ProductSourceBinding binding)
    {
        if (binding is null || !binding.Enabled) return false;
        if (binding.Kind == ProductSourceKind.Manual) return true;
        return catalog.Sources().Any(s => s.Id == binding.SourceId && s.Enabled);
    }

    /// <summary>
    /// Translates the sources recorded on catalog products into bindings. Only groups that have no stored row are
    /// written, so re-running it (or running it after a user change) never overwrites anything. Returns rows written.
    /// </summary>
    public int MigrateFromCatalog()
    {
        var sources = catalog.Sources().Select(s => s.Id).ToHashSet();
        var written = 0;
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var product in catalog.Products())
        {
            var existing = ReadRows(c, tx, product.Id).Select(r => r.Group).ToHashSet();
            foreach (var group in Groups)
            {
                if (existing.Contains(group)) continue;
                var recorded = group switch
                {
                    ProductFieldGroup.Content => string.Equals(product.SourceKind, "xml", StringComparison.OrdinalIgnoreCase),
                    ProductFieldGroup.Price => string.Equals(product.PriceSource, "xml", StringComparison.OrdinalIgnoreCase),
                    _ => string.Equals(product.StockSource, "xml", StringComparison.OrdinalIgnoreCase)
                };
                var xml = recorded && !string.IsNullOrWhiteSpace(product.SourceId) && sources.Contains(product.SourceId);
                if (!xml && !recorded) continue;          // plain manual product: the implicit default already says Manual
                var row = new ProductSourceBinding(product.Id, group, xml ? ProductSourceKind.Xml : ProductSourceKind.Manual,
                    xml ? product.SourceId : "", true, 1, DateTime.UtcNow);
                using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO ProductSourceBindings(ProductId,FieldGroup,Kind,SourceId,Enabled,Version,UpdatedUtc) VALUES($p,$g,$k,$s,$e,$v,$u)";
                Bind(cmd, row); cmd.ExecuteNonQuery(); written++;
            }
        }
        tx.Commit();
        return written;
    }

    /// <summary>Deletes stored rows whose product no longer exists. Returns rows removed.</summary>
    public int RemoveOrphans()
    {
        var alive = catalog.Products().Select(p => p.Id).ToHashSet();
        using var c = Open();
        using var tx = c.BeginTransaction();
        var orphans = ReadRows(c, tx, null).Where(r => !alive.Contains(r.ProductId)).ToList();
        foreach (var row in orphans)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM ProductSourceBindings WHERE ProductId=$p AND FieldGroup=$g";
            cmd.Parameters.AddWithValue("$p", row.ProductId); cmd.Parameters.AddWithValue("$g", (int)row.Group);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return orphans.Count;
    }

    static void Bind(SqliteCommand cmd, ProductSourceBinding row)
    {
        cmd.Parameters.AddWithValue("$p", row.ProductId); cmd.Parameters.AddWithValue("$g", (int)row.Group);
        cmd.Parameters.AddWithValue("$k", (int)row.Kind); cmd.Parameters.AddWithValue("$s", row.SourceId);
        cmd.Parameters.AddWithValue("$e", row.Enabled ? 1 : 0); cmd.Parameters.AddWithValue("$v", row.Version);
        cmd.Parameters.AddWithValue("$u", row.UpdatedUtc.ToString("O"));
    }

    static List<ProductSourceBinding> ReadRows(SqliteConnection c, SqliteTransaction? tx, string? productId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT ProductId,FieldGroup,Kind,SourceId,Enabled,Version,UpdatedUtc FROM ProductSourceBindings" +
                          (productId is null ? "" : " WHERE ProductId=$p");
        if (productId is not null) cmd.Parameters.AddWithValue("$p", productId);
        using var r = cmd.ExecuteReader();
        var rows = new List<ProductSourceBinding>();
        while (r.Read())
        {
            if (!Enum.IsDefined(typeof(ProductFieldGroup), r.GetInt32(1)) || !Enum.IsDefined(typeof(ProductSourceKind), r.GetInt32(2))) continue;
            DateTime.TryParse(r.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind, out var updated);
            rows.Add(new ProductSourceBinding(r.GetString(0), (ProductFieldGroup)r.GetInt32(1), (ProductSourceKind)r.GetInt32(2),
                r.GetString(3), r.GetInt32(4) != 0, r.GetInt64(5), updated));
        }
        return rows;
    }
}
