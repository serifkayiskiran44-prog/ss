namespace TrMarketplaceHubDesktop;

public sealed record OrderColumnPreset(string Key, string Label, IReadOnlyList<string> Columns);

/// <summary>
/// Column presets for the order list (#834), built only from columns the grid really has (binding paths, as the
/// #792 layout codec keys them): operations, shipping and a read-only finance view. A preset decides visibility
/// and order; widths stay whatever they are. Columns that would carry customer PII are never part of a preset and
/// are collapsed under every preset -- today the grid has none, and the rule keeps it that way if one is added.
/// A column the preset names but the grid no longer has is skipped; a column the grid gained after the preset was
/// written stays hidden under the preset and shows under the custom layout, at its default position.
/// </summary>
public static class OrderColumnPresets
{
    public const string CustomKey = "custom";
    public const string CustomLabel = "Özel";
    public const string PresetPreferenceKey = "layout:orders:preset";
    public const string CustomLayoutPreferenceKey = "layout:orders:custom";

    public static readonly IReadOnlyList<string> PiiKeys = new[] { "CustomerName", "BuyerName", "BuyerEmail", "Email", "Phone", "Address", "ShippingAddress", "BillingAddress" };

    public static readonly IReadOnlyList<OrderColumnPreset> Presets = new[]
    {
        new OrderColumnPreset("operations", "Operasyon", new[] { "Marketplace", "ShopId", "OrderId", "RawStatus", "PaymentStatus", "StockDecisionLabel", "DeliveryLabel", "SlaLabel", "UrgencyLabel" }),
        new OrderColumnPreset("shipping", "Kargo", new[] { "OrderId", "ShopId", "DeliveryLabel", "SlaLabel", "Carriers", "TrackingNumbers", "SyncLabel" }),
        new OrderColumnPreset("finance", "Finans (salt okunur)", new[] { "Marketplace", "ShopId", "OrderId", "PaymentStatus", "TotalLabel", "Source", "SyncLabel" }),
    };

    public static IReadOnlyList<(string Key, string Label)> Options => new[] { (CustomKey, CustomLabel) }.Concat(Presets.Select(p => (p.Key, p.Label))).ToList();

    public static bool IsPii(string key) => PiiKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static OrderColumnPreset? Find(string? key) => Presets.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

    /// <summary>The layout a preset means for the grid as it is now; null for an unknown preset (the caller keeps what it has).</summary>
    public static DataGridLayoutState? Resolve(string presetKey, IReadOnlyList<string> liveKeys, IReadOnlyList<DataGridColumnLayout>? currentWidths = null)
    {
        ArgumentNullException.ThrowIfNull(liveKeys);
        var preset = Find(presetKey);
        if (preset is null) return null;
        var live = liveKeys.Where(k => !string.IsNullOrEmpty(k)).Distinct(StringComparer.Ordinal).ToList();
        var width = (currentWidths ?? Array.Empty<DataGridColumnLayout>()).GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Width, StringComparer.Ordinal);
        var ordered = preset.Columns.Where(live.Contains).Where(k => !IsPii(k)).ToList();
        foreach (var k in live) if (!ordered.Contains(k, StringComparer.Ordinal)) ordered.Add(k);
        var columns = ordered.Select((k, i) => new DataGridColumnLayout(k, i, width.TryGetValue(k, out var w) ? w : 0, preset.Columns.Contains(k, StringComparer.Ordinal) && !IsPii(k))).ToList();
        return new(DataGridLayoutCodec.CurrentVersion, columns, "", false);
    }
}

/// <summary>
/// Which layout the order grid shows and what is remembered: the chosen preset key, and -- separately -- the
/// user's own custom layout, which a preset never overwrites. Selecting a preset applies it and records the key;
/// selecting "Özel" restores the custom layout; any column change the user makes switches to custom and becomes
/// the custom layout. Both survive a restart because both live in the preference store.
/// </summary>
public sealed class OrderColumnPresetController
{
    readonly Func<string, string?> get; readonly Action<string, string> set;

    public OrderColumnPresetController(Func<string, string?> get, Action<string, string> set)
    {
        this.get = get ?? throw new ArgumentNullException(nameof(get)); this.set = set ?? throw new ArgumentNullException(nameof(set));
        var saved = get(OrderColumnPresets.PresetPreferenceKey);
        Current = OrderColumnPresets.Find(saved) is not null ? saved! : OrderColumnPresets.CustomKey;
    }

    public string Current { get; private set; }

    public DataGridLayoutState? CustomLayout => DataGridLayoutCodec.Deserialize(get(OrderColumnPresets.CustomLayoutPreferenceKey));

    /// <summary>What to apply when the grid is built: the remembered preset, else the remembered custom layout, else nothing.</summary>
    public DataGridLayoutState? Initial(IReadOnlyList<string> liveKeys, IReadOnlyList<DataGridColumnLayout>? currentWidths = null)
        => Current == OrderColumnPresets.CustomKey ? CustomLayout : OrderColumnPresets.Resolve(Current, liveKeys, currentWidths);

    /// <summary>Choose a preset (recorded) or the custom layout; the custom layout on disk is untouched either way.</summary>
    public DataGridLayoutState? Select(string key, IReadOnlyList<string> liveKeys, IReadOnlyList<DataGridColumnLayout>? currentWidths = null)
    {
        ArgumentNullException.ThrowIfNull(liveKeys);
        if (key != OrderColumnPresets.CustomKey && OrderColumnPresets.Find(key) is null) throw new ArgumentException("Bilinmeyen kolon ön ayarı.", nameof(key));
        Current = key; set(OrderColumnPresets.PresetPreferenceKey, key);
        return key == OrderColumnPresets.CustomKey ? CustomLayout : OrderColumnPresets.Resolve(key, liveKeys, currentWidths);
    }

    /// <summary>A change the user made to the columns: it is theirs, so it becomes the custom layout and the grid is in custom mode.</summary>
    public void UserEdited(DataGridLayoutState layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Current = OrderColumnPresets.CustomKey;
        set(OrderColumnPresets.PresetPreferenceKey, OrderColumnPresets.CustomKey);
        set(OrderColumnPresets.CustomLayoutPreferenceKey, DataGridLayoutCodec.Serialize(layout));
    }
}
