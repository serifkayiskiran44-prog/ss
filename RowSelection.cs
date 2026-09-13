using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The selected-row standard (#862) for code-built grid styles: a selected cell takes the token highlight with
/// readable text (and the quiet inactive highlight with the primary text when the grid has no focus), and a
/// selected row carries a rule on its edge -- so a selection is never colour alone. The window's implicit
/// DataGridRow and DataGridCell styles say the same in XAML; a grid that builds its own style adds these.
/// </summary>
public static class RowSelection
{
    public static Style AddToCellStyle(Style style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var active = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        active.Setters.Add(new Setter(Control.BackgroundProperty, Frozen(DesignTokens.SelectedRowColor)));
        active.Setters.Add(new Setter(Control.ForegroundProperty, Frozen(DesignTokens.SelectedRowForegroundColor)));
        active.Setters.Add(new Setter(Control.BorderBrushProperty, Frozen(DesignTokens.SelectedRowColor)));
        style.Triggers.Add(active);
        var inactive = new MultiTrigger();
        inactive.Conditions.Add(new Condition(DataGridCell.IsSelectedProperty, true));
        inactive.Conditions.Add(new Condition(Selector.IsSelectionActiveProperty, false));
        inactive.Setters.Add(new Setter(Control.BackgroundProperty, Frozen(DesignTokens.SelectedRowInactiveColor)));
        inactive.Setters.Add(new Setter(Control.ForegroundProperty, Frozen(DesignTokens.TextPrimaryColor)));
        style.Triggers.Add(inactive);
        return style;
    }

    public static Style AddToRowStyle(Style style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, Frozen(DesignTokens.SelectedRowColor)));
        selected.Setters.Add(new Setter(Control.BorderThicknessProperty, DesignTokens.SelectedRowRule));
        style.Triggers.Add(selected);
        return style;
    }

    static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
}
