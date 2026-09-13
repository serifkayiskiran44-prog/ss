using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>What a restore did: nothing because the keyboard was elsewhere, the same entity again, the row that took its place, or the list itself.</summary>
public enum FocusRestoreOutcome { Untouched, Same, Neighbour, Owner }

/// <summary>Where the keyboard was inside a list before a refresh: the entity's stable key, the cell's column, the row's index; Inside is false when the keyboard was elsewhere.</summary>
public sealed record FocusSnapshot(string? Key, int ColumnIndex, int Index, bool Inside)
{
    public static readonly FocusSnapshot Elsewhere = new(null, -1, -1, false);
}

/// <summary>
/// Focus restore after an async refresh (#872). A refresh rebinds a list and the framework drops the keyboard on
/// the floor; this puts it back where it logically was: the same entity's row and cell when it is still listed,
/// the row at the same index (or the last) when the entity is gone, the list itself when it is empty — and it
/// leaves the keyboard alone when it was elsewhere (a person typing in the search box that caused the refresh).
/// Selection is the owner's business; this moves only the keyboard.
/// </summary>
public static class FocusRestore
{
    public static FocusSnapshot Capture(ItemsControl owner, Func<object, string> keyOf)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(keyOf);
        if (Keyboard.FocusedElement is not DependencyObject focused || !IsInside(focused, owner)) return FocusSnapshot.Elsewhere;
        var cell = Ancestor<DataGridCell>(focused, owner);
        object? item = Ancestor<DataGridRow>(focused, owner)?.Item ?? Ancestor<ListBoxItem>(focused, owner)?.DataContext;
        var index = item is null ? -1 : owner.Items.IndexOf(item);
        return new FocusSnapshot(item is null ? null : keyOf(item), cell?.Column?.DisplayIndex ?? -1, index, true);
    }

    public static FocusRestoreOutcome Restore(ItemsControl owner, FocusSnapshot snapshot, Func<object, string> keyOf)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(keyOf);
        if (!snapshot.Inside) return FocusRestoreOutcome.Untouched;
        owner.UpdateLayout();
        var items = owner.Items.Cast<object>().ToList();
        object? target = snapshot.Key is null ? null : items.FirstOrDefault(i => keyOf(i) == snapshot.Key);
        var outcome = FocusRestoreOutcome.Same;
        if (target is null && items.Count > 0) { target = items[Math.Clamp(Math.Max(snapshot.Index, 0), 0, items.Count - 1)]; outcome = FocusRestoreOutcome.Neighbour; }
        if (target is null) { owner.Focus(); return FocusRestoreOutcome.Owner; }
        if (owner is DataGrid grid)
        {
            grid.ScrollIntoView(target); grid.UpdateLayout();
            if (grid.ItemContainerGenerator.ContainerFromItem(target) is DataGridRow row)
            {
                var column = grid.Columns.FirstOrDefault(c => c.DisplayIndex == snapshot.ColumnIndex && c.Visibility == Visibility.Visible) ?? grid.Columns.FirstOrDefault(c => c.Visibility == Visibility.Visible);
                if (column?.GetCellContent(row)?.Parent is DataGridCell cell && cell.Focus()) return outcome;
                if (row.Focus()) return outcome;
            }
        }
        else if (owner is ListBox list)
        {
            list.ScrollIntoView(target); list.UpdateLayout();
            var container = list.ItemContainerGenerator.ContainerFromItem(target) as ListBoxItem ?? Descendants(list).OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.DataContext, target) || ReferenceEquals(i.Content, target));
            if (container is not null && container.Focus()) return outcome;
        }
        owner.Focus(); return FocusRestoreOutcome.Owner;
    }

    static bool IsInside(DependencyObject node, DependencyObject owner)
    {
        for (var current = node; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current)) if (ReferenceEquals(current, owner)) return true;
        return false;
    }

    static T? Ancestor<T>(DependencyObject node, DependencyObject owner) where T : DependencyObject
    {
        for (var current = node; current is not null && !ReferenceEquals(current, owner); current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current)) if (current is T hit) return hit;
        return null;
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }
}
