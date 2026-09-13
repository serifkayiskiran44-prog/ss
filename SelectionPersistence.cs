using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TrMarketplaceHubDesktop;

/// <summary>The selection before a rebind: the selected entities' stable keys (the anchor first), and the scope (a store) the selection belongs to.</summary>
public sealed record SelectionSnapshot(IReadOnlyList<string> Keys, string? AnchorKey, string Scope)
{
    public static readonly SelectionSnapshot Empty = new(Array.Empty<string>(), null, "");
}

/// <summary>What a restore did: how many selected entities are listed again, how many are gone (deleted, filtered out, or on another page), and their keys.</summary>
public sealed record SelectionRestoreResult(int Kept, int Missing, IReadOnlyList<string> MissingKeys)
{
    public static readonly SelectionRestoreResult Nothing = new(0, 0, Array.Empty<string>());
    /// <summary>The words a list shows when part of its selection is gone; nothing when all of it is back.</summary>
    public string? Note => Missing == 0 ? null : $"{Missing.ToString(System.Globalization.CultureInfo.CurrentCulture)} seçili öğe bu listede görünmüyor (silinmiş, filtre dışı veya başka sayfada); seçimden çıkarıldı.";
}

/// <summary>
/// Selection persistence across a refresh (#873). A rebind, a page, a sort or a filter replaces a list's objects;
/// the selection is kept by the entities' stable keys, not by object identity or index: every selected entity
/// that is listed again is selected again (the anchor first, so a single-selection owner and a product card follow
/// it), the ones that are gone are dropped and counted so the list can say so in plain words, and a selection never
/// crosses a scope (a store): a snapshot taken in one store restores nothing in another.
/// </summary>
public static class SelectionPersistence
{
    public static SelectionSnapshot Capture(Selector owner, Func<object, string> keyOf, string scope = "")
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(keyOf);
        var selected = owner is MultiSelector multi ? multi.SelectedItems.Cast<object>().ToList() : owner.SelectedItem is { } one ? new List<object> { one } : new List<object>();
        var anchor = owner.SelectedItem is { } a ? keyOf(a) : null;
        var keys = new List<string>();
        if (anchor is not null) keys.Add(anchor);
        foreach (var item in selected) { var key = keyOf(item); if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key); }
        return new SelectionSnapshot(keys, anchor, scope ?? "");
    }

    public static SelectionRestoreResult Restore(Selector owner, SelectionSnapshot snapshot, Func<object, string> keyOf, string scope = "")
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(keyOf);
        if (snapshot.Keys.Count == 0) return SelectionRestoreResult.Nothing;
        if (!string.Equals(snapshot.Scope, scope ?? "", StringComparison.Ordinal)) { Clear(owner); return new SelectionRestoreResult(0, snapshot.Keys.Count, snapshot.Keys); }
        var byKey = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var item in owner.Items) { var key = keyOf(item); if (!byKey.ContainsKey(key)) byKey[key] = item; }
        var kept = snapshot.Keys.Where(byKey.ContainsKey).ToList(); var missing = snapshot.Keys.Where(k => !byKey.ContainsKey(k)).ToList();
        if (owner is MultiSelector multi)
        {
            multi.SelectedItems.Clear();
            foreach (var key in kept) multi.SelectedItems.Add(byKey[key]); // the anchor first: SelectedItem follows it
        }
        else owner.SelectedItem = kept.Count > 0 ? byKey[kept[0]] : null;
        return new SelectionRestoreResult(kept.Count, missing.Count, missing);
    }

    static void Clear(Selector owner) { if (owner is MultiSelector multi) multi.SelectedItems.Clear(); else owner.SelectedItem = null; }
}
