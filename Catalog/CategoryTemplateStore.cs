using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed class CategoryFieldTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CategoryId { get; set; } = "";
    public string Channel { get; set; } = "local";
    public string ShopId { get; set; } = "default";
    public int Version { get; set; } = 1;
    public string? Brand { get; set; }
    public string? Currency { get; set; }
    public decimal? VatRate { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed record CategoryTemplateFieldChange(string Field, string OldValue, string NewValue);
public sealed record CategoryTemplateImpactRow(string ProductId, string Sku, DateTime ProductUpdatedUtc, IReadOnlyList<CategoryTemplateFieldChange> Changes);
public sealed record CategoryTemplateImpactPreview(CategoryFieldTemplate Template, IReadOnlyList<CategoryTemplateImpactRow> Changed, int UnaffectedCount)
{
    public int TotalInCategory => Changed.Count + UnaffectedCount;
}
public sealed record CategoryTemplateApplyResult(int Applied, IReadOnlyList<string> StaleProductIds);
/// Bounded diagnostics only (id/category/channel/shop identity, a short reason,
/// detection time) - never Brand/Currency/VatRate - for a CategoryFieldTemplates
/// row with an unparsable persisted UpdatedUtc. See CatalogStore's
/// CorruptProductRow for the same pattern.
public sealed record CorruptCategoryTemplate(string Id, string CategoryId, string Channel, string ShopId, string Reason, DateTime DetectedUtc);
/// Raised by Get(...) when the row exists but its UpdatedUtc is corrupt - kept
/// distinct from returning null (which still means "no such template"), so a
/// caller (and PreviewImpact/ApplyApproved, which only ever see what Get/List
/// hand them) can never mistake "needs repair" for "not configured".
public sealed class CategoryTemplateCorruptException : Exception
{
    public string TemplateId { get; }
    public CategoryTemplateCorruptException(string templateId, string reason) : base($"Kategori şablonu bozuk (REVIEW_REQUIRED): {reason}") => TemplateId = templateId;
}

/// Versioned per-category, per-channel/shop default field values (Brand/Currency/
/// VatRate only - the fields CatalogProduct actually has; this does not introduce
/// variant mapping or a second product-field engine). Applying a template to
/// existing products always goes through PreviewImpact -> explicit approval ->
/// ApplyApproved, reusing CatalogStore.SaveProduct's existing optimistic-concurrency
/// guard per product rather than a bulk unguarded UPDATE.
public sealed class CategoryTemplateStore
{
    readonly string connectionString;

    public CategoryTemplateStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS CategoryFieldTemplates(
                Id TEXT PRIMARY KEY, CategoryId TEXT NOT NULL, Channel TEXT NOT NULL, ShopId TEXT NOT NULL,
                Version INTEGER NOT NULL, Brand TEXT NULL, Currency TEXT NULL, VatRate REAL NULL, UpdatedUtc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_CategoryFieldTemplates_Scope ON CategoryFieldTemplates(CategoryId,Channel,ShopId);
            """;
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }

    static void Validate(CategoryFieldTemplate t)
    {
        if (string.IsNullOrWhiteSpace(t.CategoryId)) throw new InvalidOperationException("Şablon bir kategoriye bağlı olmalı.");
        if (string.IsNullOrWhiteSpace(t.Channel) || string.IsNullOrWhiteSpace(t.ShopId)) throw new InvalidOperationException("Kanal ve mağaza zorunlu.");
        if (t.Brand is { Length: > 200 }) throw new InvalidOperationException("Marka en fazla 200 karakter olabilir.");
        if (t.Currency is not null && t.Currency.Trim().Length != 3) throw new InvalidOperationException("Para birimi 3 karakter olmalı.");
        if (t.VatRate is < 0 or > 100) throw new InvalidOperationException("KDV oranı 0-100 arasında olmalı.");
    }

    /// Create-or-update by (CategoryId,Channel,ShopId): Version increments on every
    /// real update so callers can detect a template changed under them.
    public CategoryFieldTemplate Save(CategoryFieldTemplate template)
    {
        Validate(template);
        using var c = Open(); using var tx = c.BeginTransaction();
        // Re-read through TryRead (not a raw Id/Version-only projection) so a corrupt
        // existing row under this (CategoryId,Channel,ShopId) scope is rejected here
        // rather than silently version-bumped/overwritten by a normal Save.
        using var find = c.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT Id,CategoryId,Channel,ShopId,Version,Brand,Currency,VatRate,UpdatedUtc FROM CategoryFieldTemplates WHERE CategoryId=$cat AND Channel=$channel AND ShopId=$shop";
        find.Parameters.AddWithValue("$cat", template.CategoryId); find.Parameters.AddWithValue("$channel", template.Channel.Trim().ToLowerInvariant()); find.Parameters.AddWithValue("$shop", template.ShopId.Trim());
        string? existingId = null; var nextVersion = 1;
        using (var r = find.ExecuteReader()) if (r.Read()) { if (!TryRead(r, out var existing, out var corrupt)) throw new CategoryTemplateCorruptException(corrupt!.Id, corrupt.Reason); existingId = existing!.Id; nextVersion = existing.Version + 1; }
        template.Id = existingId ?? template.Id; template.Version = nextVersion; template.UpdatedUtc = DateTime.UtcNow;
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO CategoryFieldTemplates(Id,CategoryId,Channel,ShopId,Version,Brand,Currency,VatRate,UpdatedUtc) VALUES($id,$cat,$channel,$shop,$version,$brand,$currency,$vat,$updated) ON CONFLICT(Id) DO UPDATE SET Version=excluded.Version,Brand=excluded.Brand,Currency=excluded.Currency,VatRate=excluded.VatRate,UpdatedUtc=excluded.UpdatedUtc";
        cmd.Parameters.AddWithValue("$id", template.Id); cmd.Parameters.AddWithValue("$cat", template.CategoryId); cmd.Parameters.AddWithValue("$channel", template.Channel.Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$shop", template.ShopId.Trim()); cmd.Parameters.AddWithValue("$version", template.Version);
        cmd.Parameters.AddWithValue("$brand", (object?)template.Brand ?? DBNull.Value); cmd.Parameters.AddWithValue("$currency", (object?)template.Currency?.Trim().ToUpperInvariant() ?? DBNull.Value); cmd.Parameters.AddWithValue("$vat", (object?)template.VatRate ?? DBNull.Value); cmd.Parameters.AddWithValue("$updated", template.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery(); tx.Commit();
        return template;
    }

    /// A corrupt target row throws CategoryTemplateCorruptException rather than
    /// returning null, so "needs repair" is never confused with "not configured".
    public CategoryFieldTemplate? Get(string categoryId, string channel, string shopId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,CategoryId,Channel,ShopId,Version,Brand,Currency,VatRate,UpdatedUtc FROM CategoryFieldTemplates WHERE CategoryId=$cat AND Channel=$channel AND ShopId=$shop";
        cmd.Parameters.AddWithValue("$cat", categoryId); cmd.Parameters.AddWithValue("$channel", channel.Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$shop", shopId.Trim());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        if (!TryRead(r, out var template, out var corrupt)) throw new CategoryTemplateCorruptException(corrupt!.Id, corrupt.Reason);
        return template;
    }

    /// A malformed UpdatedUtc must never crash the whole read - the row is excluded
    /// from the healthy result and reported only via CorruptTemplates(); detection
    /// re-runs from the row's own stored text every call, so it stays stable across
    /// a restart without a separate tracking table. PreviewImpact/ApplyApproved only
    /// ever operate on a template handed to them by Get/List, so this alone keeps a
    /// corrupt row out of any preview/apply flow.
    public IReadOnlyList<CategoryFieldTemplate> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,CategoryId,Channel,ShopId,Version,Brand,Currency,VatRate,UpdatedUtc FROM CategoryFieldTemplates ORDER BY CategoryId,Channel,ShopId";
        using var r = cmd.ExecuteReader(); var result = new List<CategoryFieldTemplate>(); while (r.Read()) if (TryRead(r, out var template, out _)) result.Add(template!); return result;
    }

    /// Bounded diagnostics for every row whose UpdatedUtc failed to parse - never
    /// the raw Brand/Currency/VatRate.
    public IReadOnlyList<CorruptCategoryTemplate> CorruptTemplates()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,CategoryId,Channel,ShopId,Version,Brand,Currency,VatRate,UpdatedUtc FROM CategoryFieldTemplates";
        using var r = cmd.ExecuteReader(); var result = new List<CorruptCategoryTemplate>(); while (r.Read()) if (!TryRead(r, out _, out var corrupt)) result.Add(corrupt!); return result;
    }

    static bool TryRead(SqliteDataReader r, out CategoryFieldTemplate? template, out CorruptCategoryTemplate? corrupt)
    {
        template = null; corrupt = null; var id = r.GetString(0); var categoryId = r.GetString(1); var channel = r.GetString(2); var shopId = r.GetString(3);
        if (!TryParseUtc(r.GetString(8), out var updated)) { corrupt = new(id, categoryId, channel, shopId, "Malformed UpdatedUtc timestamp", DateTime.UtcNow); return false; }
        template = new()
        {
            Id = id, CategoryId = categoryId, Channel = channel, ShopId = shopId, Version = r.GetInt32(4),
            Brand = r.IsDBNull(5) ? null : r.GetString(5), Currency = r.IsDBNull(6) ? null : r.GetString(6), VatRate = r.IsDBNull(7) ? null : r.GetDecimal(7),
            UpdatedUtc = updated,
        };
        return true;
    }

    /// Only ever a format/parse failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised by the surrounding command, not this parse.
    static bool TryParseUtc(string value, out DateTime result) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);

    /// Every product currently in the template's category, with the exact field-level
    /// old -> new diff the template would apply. Products whose fields already match
    /// the template are counted but not listed as a change (nothing to apply to them).
    public CategoryTemplateImpactPreview PreviewImpact(CategoryFieldTemplate template, TaxonomyStore taxonomy, CatalogStore catalog)
    {
        var category = taxonomy.List(TaxonomyKind.Category).FirstOrDefault(x => x.Id == template.CategoryId) ?? throw new InvalidOperationException("Şablonun bağlı olduğu kategori bulunamadı.");
        var products = catalog.Products().Where(p => string.Equals(p.Category, category.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var changed = new List<CategoryTemplateImpactRow>(); var unaffected = 0;
        foreach (var product in products)
        {
            var changes = new List<CategoryTemplateFieldChange>();
            if (template.Brand is { } brand && !string.Equals(product.Brand, brand, StringComparison.OrdinalIgnoreCase)) changes.Add(new("Brand", product.Brand, brand));
            if (template.Currency is { } currency && !string.Equals(product.Currency, currency, StringComparison.OrdinalIgnoreCase)) changes.Add(new("Currency", product.Currency, currency));
            if (template.VatRate is { } vat && product.VatRate != vat) changes.Add(new("VatRate", product.VatRate.ToString(CultureInfo.InvariantCulture), vat.ToString(CultureInfo.InvariantCulture)));
            if (changes.Count > 0) changed.Add(new(product.Id, product.Sku, product.UpdatedUtc, changes));
            else unaffected++;
        }
        return new(template, changed, unaffected);
    }

    /// Applies only the rows in `preview` whose product hasn't changed since the
    /// preview was built (ProductUpdatedUtc still matches); a stale product is
    /// skipped and reported, never overwritten blind. Uses CatalogStore.SaveProduct
    /// per row, so its own validation/version/lock-field rules stay the single owner
    /// of what a product update may do - this does not bypass them.
    public CategoryTemplateApplyResult ApplyApproved(CategoryTemplateImpactPreview preview, bool approved, CatalogStore catalog)
    {
        if (!approved) throw new InvalidOperationException("Şablon uygulaması için önizleme onayı gerekli.");
        var applied = 0; var stale = new List<string>();
        var current = catalog.Products().ToDictionary(p => p.Id);
        foreach (var row in preview.Changed)
        {
            if (!current.TryGetValue(row.ProductId, out var product) || product.UpdatedUtc != row.ProductUpdatedUtc) { stale.Add(row.ProductId); continue; }
            foreach (var change in row.Changes)
            {
                switch (change.Field)
                {
                    case "Brand": product.Brand = change.NewValue; break;
                    case "Currency": product.Currency = change.NewValue; break;
                    case "VatRate": product.VatRate = decimal.Parse(change.NewValue, CultureInfo.InvariantCulture); break;
                }
            }
            try { catalog.SaveProduct(product); applied++; }
            catch (InvalidOperationException) { stale.Add(row.ProductId); }
        }
        return new(applied, stale);
    }
}
