using System.Text.Json;

namespace TrMarketplaceHubDesktop;

/// <summary>How a stored preference was read: nothing stored; the current schema; an older schema brought forward (and re-saved); or a fallback to defaults because the record is corrupt, from a future version, or could not be migrated.</summary>
public enum PreferenceReadOutcome { Missing, Current, Migrated, Fallback }

public sealed record PreferenceRead(PreferenceReadOutcome Outcome, string? Payload, int Version, string? Diagnostic);

/// <summary>One family of persisted UI preferences: its key prefix, its current schema version, and how an older payload is brought forward (null when it cannot be).</summary>
public sealed record PreferenceFamily(string Name, string KeyPrefix, int CurrentVersion, Func<int, string, string?> Migrate);

/// <summary>
/// View preference schema versioning (#875). Every persisted UI preference — a grid layout, a density, hidden
/// columns, the last route, the sidebar, the recent source, the dashboard's store filter, the orders split and
/// presets, the report columns — is written inside an envelope that names its schema version. A record written
/// before the envelope existed is version 0 and is brought forward by its family's migration and re-saved; a record
/// from a future version or a corrupt envelope falls back to the defaults with a diagnostic that names the key and
/// the reason, never the payload; a payload that carries personal data or a secret is refused before it is written.
/// The store keys keep their store scope; the schema is per family, not per store.
/// </summary>
public static class PreferenceSchema
{
    public const string SchemaField = "schema";
    public const string PayloadField = "payload";
    public const string PiiRefused = "Tercih kaydına kişisel veri veya gizli değer yazılmaz.";

    static string? Identity(int from, string payload) => payload; // version 0 (a bare value) carries the same payload as version 1

    static readonly List<PreferenceFamily> families = new()
    {
        new("product-layout", "layout:products", 1, Identity),
        new("product-density", "density:products", 1, Identity),
        new("product-columns", "columns:products", 1, Identity),
        new("last-route", "last-route", 1, Identity),
        new("sidebar", "shell:sidebar", 1, Identity),
        new("recent-source", "xml:last-source", 1, Identity),
        new("dashboard-store", "filter:dashboard-store", 1, Identity),
        new("order-preset", "layout:orders:preset", 1, Identity),
        new("order-custom-layout", "layout:orders:custom", 1, Identity),
        new("order-split", "layout:orders:split", 1, Identity),
        new("report-columns", "report-columns:", 1, Identity),
    };
    static readonly List<string> diagnostics = new();
    static readonly object gate = new();

    public static IReadOnlyList<PreferenceFamily> Families { get { lock (gate) return families.ToList(); } }

    /// <summary>Registers a family (a screen with its own preference); a later registration with the same prefix replaces the earlier one.</summary>
    public static void Register(PreferenceFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        lock (gate) { families.RemoveAll(f => f.KeyPrefix == family.KeyPrefix); families.Add(family); }
    }

    /// <summary>The family a key belongs to by its prefix; an unregistered key is version 1 with the identity migration, so nothing is refused for being new.</summary>
    public static PreferenceFamily FamilyFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (gate) return families.Where(f => key.StartsWith(f.KeyPrefix, StringComparison.Ordinal)).OrderByDescending(f => f.KeyPrefix.Length).FirstOrDefault() ?? new PreferenceFamily("unregistered", key, 1, Identity);
    }

    /// <summary>Fallbacks since start-up, as "key: reason" — the key and the reason only, never a payload.</summary>
    public static IReadOnlyList<string> Diagnostics { get { lock (gate) return diagnostics.ToList(); } }
    public static void ClearDiagnostics() { lock (gate) diagnostics.Clear(); }

    public static string Wrap(int version, string payload) => JsonSerializer.Serialize(new Envelope(version, payload ?? ""));

    /// <summary>Reads an envelope: true only for a JSON object with a numeric schema and a string payload.</summary>
    public static bool TryUnwrap(string raw, out int version, out string payload)
    {
        version = 0; payload = "";
        if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart()[0] != '{') return false;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty(SchemaField, out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out version)) return false;
            if (!document.RootElement.TryGetProperty(PayloadField, out var body) || body.ValueKind != JsonValueKind.String) return false;
            payload = body.GetString() ?? ""; return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Whether a raw value claims to be an envelope (so a broken one is corrupt rather than a bare legacy value).</summary>
    static bool ClaimsEnvelope(string raw) => raw.TrimStart().StartsWith('{') && raw.Contains($"\"{SchemaField}\"", StringComparison.Ordinal);

    public static PreferenceRead Inspect(UiPreferenceStore store, string key)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(key);
        var family = FamilyFor(key);
        string? raw;
        try { raw = store.Get(key); }
        catch (Exception ex) { return Fallback(key, "okunamadı: " + AuditStore.Redact(ex.Message)); }
        if (raw is null) return new PreferenceRead(PreferenceReadOutcome.Missing, null, 0, null);
        int version; string payload;
        if (TryUnwrap(raw, out version, out payload)) { }
        else if (ClaimsEnvelope(raw)) return Fallback(key, "bozuk kayıt zarfı");
        else { version = 0; payload = raw; }
        if (version > family.CurrentVersion) return Fallback(key, $"gelecek sürüm {version} (bu sürüm {family.CurrentVersion} okur)");
        if (version == family.CurrentVersion) return new PreferenceRead(PreferenceReadOutcome.Current, payload, version, null);
        string? migrated;
        try { migrated = family.Migrate(version, payload); }
        catch (Exception ex) { return Fallback(key, $"sürüm {version} taşınamadı: " + AuditStore.Redact(ex.Message)); }
        if (migrated is null) return Fallback(key, $"sürüm {version} taşınamadı");
        try { store.Set(key, Wrap(family.CurrentVersion, migrated)); } catch (Exception) { /* the next read migrates again */ }
        return new PreferenceRead(PreferenceReadOutcome.Migrated, migrated, family.CurrentVersion, null);
    }

    /// <summary>The payload for a key, brought forward when older; null when nothing is stored or the record fell back to the defaults.</summary>
    public static string? Read(UiPreferenceStore store, string key) => Inspect(store, key).Payload;

    /// <summary>Writes a payload inside the family's current envelope; a payload that carries personal data or a secret is refused.</summary>
    public static void Write(UiPreferenceStore store, string key, string payload)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(payload);
        if (!string.Equals(AuditStore.Redact(payload), payload, StringComparison.Ordinal)) throw new InvalidOperationException(PiiRefused);
        store.Set(key, Wrap(FamilyFor(key).CurrentVersion, payload));
    }

    static PreferenceRead Fallback(string key, string reason)
    {
        lock (gate) { diagnostics.Add($"{key}: {reason}"); if (diagnostics.Count > 50) diagnostics.RemoveAt(0); }
        return new PreferenceRead(PreferenceReadOutcome.Fallback, null, -1, reason);
    }

    sealed record Envelope(int schema, string payload);
}
