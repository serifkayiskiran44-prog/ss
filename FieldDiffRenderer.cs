using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The one before/after renderer (#832): every row is a focusable, keyboard-reachable border whose glyph, word and
/// border weight say added / removed / changed / unchanged before any colour does; long values wrap; the automation
/// name reads the whole row. Used by the product content preview and the XML row diff alike.
/// </summary>
public static class FieldDiffRenderer
{
    public static StackPanel Render(IReadOnlyList<FieldDiffRow> rows, bool highContrast)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var panel = new StackPanel();
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Local);
        panel.Children.Add(new TextBlock { Text = FieldDiff.Summary(rows), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var row in rows)
        {
            var p = FieldDiff.Present(row.Kind, highContrast);
            var accent = new SolidColorBrush(p.Accent);
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = $"{p.Glyph} {row.Field} · {p.Word}", FontWeight = FontWeights.SemiBold, Foreground = accent, TextWrapping = TextWrapping.Wrap });
            if (row.Kind == DiffKind.Unchanged)
                body.Children.Add(new TextBlock { Text = row.After, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
            else
            {
                if (row.Kind != DiffKind.Added) body.Children.Add(new TextBlock { Text = "Önce: " + row.Before + (row.BeforeTruncated ? " (kısaltıldı)" : ""), TextWrapping = TextWrapping.Wrap });
                if (row.Kind != DiffKind.Removed) body.Children.Add(new TextBlock { Text = "Sonra: " + row.After + (row.AfterTruncated ? " (kısaltıldı)" : ""), TextWrapping = TextWrapping.Wrap });
            }
            var border = new Border { BorderBrush = accent, BorderThickness = new Thickness(p.BorderWeight, p.BorderWeight, p.BorderWeight, p.BorderWeight), Padding = new Thickness(8, 4, 8, 4), Margin = Spacing.BelowInline, Child = body, Focusable = true, Tag = row.Kind };
            FocusStyles.MakeFocusable(border);
            System.Windows.Automation.AutomationProperties.SetName(border, $"{row.Field}: {p.Word}. " + (row.Kind == DiffKind.Unchanged ? row.After : $"Önce {row.Before}. Sonra {row.After}."));
            panel.Children.Add(border);
        }
        return panel;
    }
}
