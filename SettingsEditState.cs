namespace TrMarketplaceHubDesktop;

public sealed record DirtyField(string Label, bool Secret)
{
    /// <summary>What a badge or tooltip may say about the field: its label, and for a secret only that it is one.</summary>
    public string Display => Secret ? $"{Label} (gizli)" : Label;
}

public sealed record DirtySection(string EntryKey, IReadOnlyList<DirtyField> Fields, DateTime SinceUtc)
{
    public string Summary => $"Kaydedilmemiş değişiklik: {string.Join(", ", Fields.Select(f => f.Display))}";
}

/// <summary>
/// The settings edit state (#854): which settings forms have unsaved changes and which of their fields -- by label,
/// never by value. A form registers its fields with a getter, snapshots on load and after a save, and recomputes
/// on every edit; a secret field is tracked by presence only, so a changed secret reads "API secret (gizli)" and
/// nothing more can leak into a badge, a tooltip or a log. The state lives in memory: a restart without a save
/// starts clean, as the forms do. The shell listens to <see cref="Changed"/> for its badges.
/// </summary>
public sealed class SettingsEditState
{
    readonly Dictionary<string, FormTracker> forms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when any section becomes dirty, clean, or changes which fields are dirty.</summary>
    public event Action? Changed;

    public FormTracker Form(string entryKey)
    {
        var key = (entryKey ?? "").Trim(); if (key.Length == 0) throw new ArgumentException("Ayar bölümü anahtarı gerekli.", nameof(entryKey));
        if (!forms.TryGetValue(key, out var form)) { form = new FormTracker(this, key); forms[key] = form; }
        return form;
    }

    public DirtySection? Dirty(string entryKey) => forms.TryGetValue((entryKey ?? "").Trim(), out var form) ? form.Dirty : null;
    public IReadOnlyList<DirtySection> All => forms.Values.Select(f => f.Dirty).Where(d => d is not null).Select(d => d!).OrderBy(d => d.SinceUtc).ThenBy(d => d.EntryKey, StringComparer.Ordinal).ToList();
    public bool Any => forms.Values.Any(f => f.Dirty is not null);

    /// <summary>Marks a section as saved or discarded: its current values become the baseline.</summary>
    public void Clear(string entryKey) { if (forms.TryGetValue((entryKey ?? "").Trim(), out var form)) form.Snapshot(); }

    internal void Raise() => Changed?.Invoke();

    public sealed class FormTracker
    {
        readonly SettingsEditState owner; readonly string key;
        readonly List<(string Label, bool Secret, Func<string> Current)> fields = new();
        readonly Dictionary<string, string> baseline = new(StringComparer.Ordinal);
        DirtySection? dirty; DateTime? since;

        internal FormTracker(SettingsEditState owner, string key) { this.owner = owner; this.key = key; }

        public string Key => key;
        public DirtySection? Dirty => dirty;

        /// <summary>Registers a field. A secret's getter is reduced to presence before it is ever compared or stored.</summary>
        public FormTracker Track(string label, Func<string?> current, bool secret = false)
        {
            ArgumentNullException.ThrowIfNull(current);
            var name = (label ?? "").Trim(); if (name.Length == 0) throw new ArgumentException("Alan etiketi gerekli.", nameof(label));
            fields.Add((name, secret, secret ? () => string.IsNullOrEmpty(Safe(current)) ? "" : "•" : () => Safe(current)));
            return this;
        }

        /// <summary>The current values become the saved ones; nothing is dirty.</summary>
        public void Snapshot()
        {
            baseline.Clear(); foreach (var field in fields) baseline[field.Label] = field.Current();
            since = null; Set(null);
        }

        /// <summary>Compares every field with the snapshot and reports the dirty ones.</summary>
        public DirtySection? Recompute()
        {
            var changed = fields.Where(f => !baseline.TryGetValue(f.Label, out var saved) || !string.Equals(saved, f.Current(), StringComparison.Ordinal)).Select(f => new DirtyField(f.Label, f.Secret)).ToList();
            if (changed.Count == 0) { since = null; Set(null); return null; }
            since ??= DateTime.UtcNow;
            Set(new DirtySection(key, changed, since.Value));
            return dirty;
        }

        void Set(DirtySection? value)
        {
            var before = dirty?.Summary; dirty = value;
            if (!string.Equals(before, value?.Summary, StringComparison.Ordinal)) owner.Raise();
        }

        static string Safe(Func<string?> get) { try { return get() ?? ""; } catch (Exception) { return ""; } }
    }
}
