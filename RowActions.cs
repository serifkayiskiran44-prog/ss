using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>One action a row menu offers: a stable key, the label (with the count when it applies to many), what it does, why it is disabled if it is, whether a confirmation follows, and its gesture if it has one.</summary>
public sealed record RowAction(string Key, string Label, Action Execute, string? DisabledReason = null, bool Destructive = false, string? Gesture = null)
{
    public bool IsEnabled => DisabledReason is null;
    /// <summary>The label as the menu shows it: a destructive action says a confirmation follows.</summary>
    public string Header => Destructive ? Label + "…" : Label;
}

/// <summary>
/// The shared row-action menu (#870). It opens from the mouse and from the keyboard alike (the menu key, Shift+F10)
/// and, from the keyboard, sits at the focused row rather than at the mouse; it is rebuilt from the selection at
/// that moment; a disabled action says why in a tooltip that shows while disabled and never runs even if clicked;
/// the keyboard returns to the row when the menu closes. A destructive action runs only through its own guard (the
/// same method the button calls), so a menu never bypasses a confirmation. Items are hit targets in DIP.
/// </summary>
public static class RowActionMenu
{
    public const string MenuTag = "row-actions";

    public static void Attach(FrameworkElement owner, Func<IReadOnlyList<RowAction>> actionsFor, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(actionsFor);
        owner.ContextMenu = new ContextMenu { Tag = MenuTag }; // so a right-click (and the framework's own key handling) ask for one
        void Open(bool fromKeyboard)
        {
            var menu = Build(actionsFor(), onError); // rebuilt from the selection at this moment
            var target = fromKeyboard ? FocusTarget(owner) : owner;
            menu.PlacementTarget = target; menu.Placement = fromKeyboard ? PlacementMode.Bottom : PlacementMode.MousePoint;
            menu.Closed += (_, _) => { if (!ReferenceEquals(target, owner)) Keyboard.Focus(target); };
            owner.ContextMenu = menu;
            if (menu.Items.Count > 0) menu.IsOpen = true;
        }
        // The keyboard's own way in: the menu key or Shift+F10 on the owner opens the menu at the focused row, whatever the framework does with the key afterwards.
        owner.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key != Key.Apps && !(key == Key.F10 && e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift))) return;
            e.Handled = true; Open(fromKeyboard: true);
        };
        owner.ContextMenuOpening += (_, e) =>
        {
            e.Handled = true; // the menu is ours
            if (owner.ContextMenu is { IsOpen: true }) return; // the key handler already opened it
            Open(FromKeyboard(e));
        };
    }

    /// <summary>Whether an opening came from the keyboard: the framework reports no cursor position then.</summary>
    public static bool FromKeyboard(ContextMenuEventArgs e) => e.CursorLeft < 0 && e.CursorTop < 0;

    public static ContextMenu Build(IReadOnlyList<RowAction> actions, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var menu = new ContextMenu { Tag = MenuTag };
        foreach (var action in actions)
        {
            var item = new MenuItem { Header = action.Header, Tag = action.Key, InputGestureText = action.Gesture ?? "", IsEnabled = action.IsEnabled, MinHeight = DesignTokens.HitTargetMinSize, ToolTip = action.DisabledReason };
            ToolTipService.SetShowOnDisabled(item, true);
            AutomationProperties.SetName(item, action.IsEnabled ? action.Header : $"{action.Header} — {action.DisabledReason}");
            item.Click += (_, _) => { if (!action.IsEnabled) return; try { action.Execute(); } catch (Exception ex) { onError?.Invoke(ex); } };
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>The row the keyboard is in (a grid row or a list item under the owner), else the owner itself.</summary>
    public static FrameworkElement FocusTarget(FrameworkElement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var node = Keyboard.FocusedElement as DependencyObject; FrameworkElement? row = null;
        while (node is not null && !ReferenceEquals(node, owner))
        {
            if (node is DataGridRow or ListBoxItem) row = (FrameworkElement)node;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return node is null ? owner : row ?? owner;
    }
}
