namespace TrMarketplaceHubDesktop;

/// <param name="Classified">A column that carries personal or quasi-personal data: hidden by default, offered only while the PII policy allows, and masked in every value.</param>
public sealed record ReportColumn(string Key, string Label, string Group, bool Classified = false, double Width = 120);

public sealed record ReportColumnChoice(ReportColumn Column, bool Visible, double Width);

public sealed record ReportColumnLayout(IReadOnlyList<ReportColumnChoice> Columns)
{
    public IReadOnlyList<ReportColumn> Visible => Columns.Where(c => c.Visible).Select(c => c.Column).ToList();
    public IReadOnlyList<string> VisibleKeys => Columns.Where(c => c.Visible).Select(c => c.Column.Key).ToList();
    public int HiddenClassified => Columns.Count(c => c.Column.Classified && !c.Visible);
}

public sealed record ReportColumnGroup(string Group, IReadOnlyList<ReportColumnChoice> Items);

/// <summary>
/// The report result's columns (#849): the schema a report allows (labels and groups a person recognises, the
/// classified columns marked), the operator's order / visibility / widths persisted per report through the
/// shared grid-layout codec (keys not headers, DIP widths), and the rules that keep a persisted layout honest --
/// a column the schema no longer has is dropped, a column the schema gained appears at its default position, a
/// classified column is hidden by default and stays hidden while the policy forbids it, and at least one column
/// always shows. The chooser's search and grouping are pure functions over a layout.
/// </summary>
public static class ReportColumns
{
    public const string PreferencePrefix = "report-columns:";
    public const double MinWidth = 40;
    public const double MaxWidth = 600;
    public const string PolicyClosedWord = "politika kapalı";

    public static readonly IReadOnlyList<ReportColumn> OrdersSchema = new ReportColumn[]
    {
        new("OrderId", "Sipariş no", "Kimlik", Width: 130), new("ShopId", "Mağaza", "Kimlik", Width: 100),
        new("Status", "Teslimat durumu", "Durum", Width: 150),
        new("Price", "Tutar", "Finans", Width: 90), new("Currency", "Para birimi", "Finans", Width: 80),
        new("UpdatedUtc", "Güncelleme (UTC)", "Zaman", Width: 150),
        new("Tracking", "Kargo takip (maskeli)", "Kargo", Classified: true, Width: 160),
    };

    public static IReadOnlyList<ReportColumn> SchemaFor(ReportDefinition definition) => definition is not null && definition.Key == ReportRunner.OrdersCsvKey ? OrdersSchema : Array.Empty<ReportColumn>();
    public static string PreferenceKey(string reportKey) => PreferencePrefix + (reportKey ?? "").Trim().ToLowerInvariant();

    /// <summary>Every column visible at its default width except the classified ones, which start hidden whatever the policy says.</summary>
    public static ReportColumnLayout Default(IEnumerable<ReportColumn> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new(schema.Select(c => new ReportColumnChoice(c, !c.Classified, c.Width)).ToList());
    }

    /// <summary>A persisted layout applied to the current schema and policy; anything untrusted falls back to the default.</summary>
    public static ReportColumnLayout Resolve(IReadOnlyList<ReportColumn> schema, string? persistedJson, bool classifiedAllowed)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var defaults = Default(schema);
        var persisted = DataGridLayoutCodec.Deserialize(persistedJson);
        if (persisted is null || schema.Count == 0) return defaults;
        var order = DataGridLayoutCodec.ResolveOrder(schema.Select(c => c.Key).ToList(), persisted.Columns);
        var byKey = schema.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var saved = persisted.Columns.GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var choices = order.Select(key =>
        {
            var column = byKey[key]; var has = saved.TryGetValue(key, out var s);
            var visible = has ? s!.Visible : !column.Classified;
            if (column.Classified && !classifiedAllowed) visible = false;
            var width = has && s!.Width >= MinWidth && s.Width <= MaxWidth ? s.Width : column.Width;
            return new ReportColumnChoice(column, visible, width);
        }).ToList();
        if (!choices.Any(c => c.Visible)) { var first = choices.FindIndex(c => !c.Column.Classified); if (first < 0) first = 0; choices[first] = choices[first] with { Visible = true }; }
        return new(choices);
    }

    public static string Persist(ReportColumnLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return DataGridLayoutCodec.Serialize(new DataGridLayoutState(DataGridLayoutCodec.CurrentVersion, layout.Columns.Select((c, i) => new DataGridColumnLayout(c.Column.Key, i, c.Width, c.Visible)).ToList(), "", false));
    }

    /// <summary>Shows or hides one column; a classified column cannot be shown while the policy forbids it, and the last visible column cannot be hidden.</summary>
    public static ReportColumnLayout Toggle(ReportColumnLayout layout, string key, bool visible, bool classifiedAllowed)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var choices = layout.Columns.ToList(); var index = choices.FindIndex(c => c.Column.Key == key); if (index < 0) return layout;
        if (visible && choices[index].Column.Classified && !classifiedAllowed) return layout;
        if (!visible && choices.Count(c => c.Visible) <= 1 && choices[index].Visible) return layout;
        choices[index] = choices[index] with { Visible = visible };
        return new(choices);
    }

    public static ReportColumnLayout Move(ReportColumnLayout layout, string key, int delta)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var choices = layout.Columns.ToList(); var index = choices.FindIndex(c => c.Column.Key == key); if (index < 0 || delta == 0) return layout;
        var target = Math.Clamp(index + delta, 0, choices.Count - 1); if (target == index) return layout;
        var item = choices[index]; choices.RemoveAt(index); choices.Insert(target, item);
        return new(choices);
    }

    /// <summary>Puts the visible columns in the order given (the grid's display order) and keeps the hidden ones where they were.</summary>
    public static ReportColumnLayout Reorder(ReportColumnLayout layout, IReadOnlyList<string> visibleKeysInOrder)
    {
        ArgumentNullException.ThrowIfNull(layout); ArgumentNullException.ThrowIfNull(visibleKeysInOrder);
        var visible = new Queue<ReportColumnChoice>(visibleKeysInOrder.Select(k => layout.Columns.FirstOrDefault(c => c.Column.Key == k && c.Visible)).Where(c => c is not null)!);
        if (visible.Count != layout.Columns.Count(c => c.Visible)) return layout;
        return new(layout.Columns.Select(c => c.Visible ? visible.Dequeue() : c).ToList());
    }

    public static ReportColumnLayout Resize(ReportColumnLayout layout, string key, double width)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!double.IsFinite(width)) return layout;
        var choices = layout.Columns.ToList(); var index = choices.FindIndex(c => c.Column.Key == key); if (index < 0) return layout;
        choices[index] = choices[index] with { Width = Math.Clamp(width, MinWidth, MaxWidth) };
        return new(choices);
    }

    /// <summary>The chooser's list: groups in order of first appearance, items that match the search by label, key or group.</summary>
    public static IReadOnlyList<ReportColumnGroup> Grouped(ReportColumnLayout layout, string? query)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var q = (query ?? "").Trim();
        var items = layout.Columns.Where(c => q.Length == 0 || $"{c.Column.Label} {c.Column.Key} {c.Column.Group}".Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        return items.GroupBy(c => c.Column.Group).Select(g => new ReportColumnGroup(g.Key, g.ToList())).ToList();
    }

    public static string Summary(ReportColumnLayout layout, bool classifiedAllowed)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var text = $"{layout.Visible.Count:N0} / {layout.Columns.Count:N0} kolon görünür";
        if (layout.HiddenClassified > 0) text += classifiedAllowed ? $" · {layout.HiddenClassified:N0} sınıflandırılmış kolon gizli" : $" · {layout.HiddenClassified:N0} sınıflandırılmış kolon {PolicyClosedWord}";
        return text;
    }
}
