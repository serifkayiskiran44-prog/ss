using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>Why and since when a field is locked; kept on the product beside the lock flag.</summary>
public sealed class LockNote
{
    public string Reason { get; set; } = "";
    public DateTime? SinceUtc { get; set; }
}

public sealed record ProductLock(string Field, string Label, bool Locked, string Reason, DateTime? SinceUtc);
public sealed record LockChange(string Field, string Label, bool Locked, string Reason);
public sealed record UnlockPreview(string Field, string Label, string Headline, IReadOnlyList<string> Lines);

/// <summary>
/// The scoped manual lock policy (#904). A lock is per field — title, description, price, stock, images — and means
/// "no feed and no bulk operation may overwrite this; only the operator may". The flags existed; this owner gives
/// each lock a reason and a moment, records every switch in the audit trail, and previews what releasing a lock
/// would let the next import do (the field's origin, the last refused decision, whether the source will run). The
/// import honours the locks through #896's decision; the bulk operations honour them here. Never a value in the
/// words — reasons are the operator's own text, redacted and capped.
/// </summary>
public static class ProductLocks
{
    public const string LockAction = "field-lock", UnlockAction = "field-unlock";
    public const int ReasonLimit = 200;
    public static readonly IReadOnlyList<(string Field, string Property, string Label)> Scopes = new[]
    {
        ("Name", "LockName", "başlık"), ("Description", "LockDescription", "açıklama"), ("Price", "LockPrice", "fiyat"), ("Stock", "LockStock", "stok"), ("ImageUrls", "LockImages", "görseller"),
    };

    public static bool IsLocked(CatalogProduct product, string field)
    {
        ArgumentNullException.ThrowIfNull(product);
        return field switch { "Name" => product.LockName, "Description" => product.LockDescription, "Price" or "Currency" => product.LockPrice, "Stock" => product.LockStock, "ImageUrls" => product.LockImages, _ => false };
    }

    public static string LabelOf(string field) { foreach (var s in Scopes) if (s.Field == field) return s.Label; return field; }

    public static IReadOnlyList<ProductLock> Of(CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return Scopes.Select(s => { var note = NoteOf(product, s.Field); return new ProductLock(s.Field, s.Label, IsLocked(product, s.Field), note?.Reason ?? "", note?.SinceUtc); }).ToArray();
    }

    /// <summary>Locks turned on or off between two records, each with the reason the edited record carries.</summary>
    public static IReadOnlyList<LockChange> Diff(CatalogProduct before, CatalogProduct after)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        return Scopes.Where(s => IsLocked(before, s.Field) != IsLocked(after, s.Field)).Select(s => new LockChange(s.Field, s.Label, IsLocked(after, s.Field), SafeReason(NoteOf(after, s.Field)?.Reason))).ToArray();
    }

    /// <summary>Applied by the store on save: a lock turned on keeps its reason and gets its moment, a lock turned off drops its note, a note without a lock is dropped. Returns the changes for the audit trail.</summary>
    public static IReadOnlyList<LockChange> Stamp(CatalogProduct before, CatalogProduct after, DateTime nowUtc)
    {
        var changes = Diff(before, after);
        foreach (var change in changes)
        {
            if (change.Locked) { after.LockReasons ??= new Dictionary<string, LockNote>(StringComparer.Ordinal); after.LockReasons[change.Field] = new LockNote { Reason = change.Reason, SinceUtc = nowUtc }; }
            else after.LockReasons?.Remove(change.Field);
        }
        if (after.LockReasons is { } notes) foreach (var key in notes.Keys.ToList()) if (!IsLocked(after, key)) notes.Remove(key);
        return changes;
    }

    /// <summary>What releasing a lock would let the next import do: the field's origin, the last refused decision, and whether the source will run.</summary>
    public static UnlockPreview PreviewUnlock(CatalogProduct product, string field, Func<string, XmlSource?> sourceById, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product); ArgumentNullException.ThrowIfNull(sourceById);
        var label = LabelOf(field);
        var origin = FieldProvenance.Of(product, field);
        var note = NoteOf(product, field);
        var home = product.SourceId.Length > 0 ? sourceById(product.SourceId) : null;
        var lines = new List<string>();
        if (note is { Reason.Length: > 0 }) lines.Add($"Kilit gerekçesi: {note.Reason}" + (note.SinceUtc is { } since ? $" ({Ago(nowUtc - since)})" : ""));
        lines.Add("Alan kökeni: " + FieldProvenance.Describe(origin, sourceById, nowUtc));
        if (origin is { Decision.Length: > 0 }) lines.Add("Son karar: " + origin.Decision);
        if (home is null) lines.Add(product.SourceId.Length == 0 ? "Bu ürünün kaynağı yok; kilit açılınca elle girilen değer yerinde kalır." : "Birincil kaynak kaydı bulunamadı; içe aktarma bu alanı yazmaz.");
        else if (!home.Enabled) lines.Add($"Kaynak {Name(home)} devre dışı; kilit açılsa da bir sonraki okuma yapılmaz.");
        else lines.Add($"Kaynak {Name(home)} bir sonraki okumada bu alanı kaynağın değeriyle yazar" + (home.AutoImport ? " (zamanlayıcı açık)." : " (elle okuma gerekir)."));
        return new(field, label, $"{Cap(label)} kilidi açılacak: alan yeniden kaynaktan güncellenebilir.", lines.Select(AuditStore.Redact).ToArray());
    }

    /// <summary>The audit row for a switch: module catalog, action field-lock / field-unlock, the product, the label and the reason.</summary>
    public static AuditEvent ToAudit(CatalogProduct product, LockChange change)
    {
        ArgumentNullException.ThrowIfNull(product); ArgumentNullException.ThrowIfNull(change);
        var detail = change.Locked ? $"{change.Label} kilitlendi" + (change.Reason.Length > 0 ? " · gerekçe: " + change.Reason : "") : $"{change.Label} kilidi açıldı";
        return new AuditEvent { Module = "catalog", Action = change.Locked ? LockAction : UnlockAction, ProductId = product.Id, Outcome = "Info", Detail = AuditStore.Redact(detail) };
    }

    public static string SafeReason(string? reason)
    {
        var clean = AuditStore.Redact((reason ?? "").Trim());
        return clean.Length > ReasonLimit ? clean[..ReasonLimit] : clean;
    }

    static LockNote? NoteOf(CatalogProduct product, string field) => product.LockReasons is { } notes && notes.TryGetValue(field, out var note) ? note : null;
    static string Name(XmlSource source) => string.IsNullOrWhiteSpace(source.Name) ? "adsız kaynak" : AuditStore.Redact(source.Name).Trim();
    static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "az önce";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} dk önce";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} sa önce";
        return $"{(int)span.TotalDays} gün önce";
    }
}
