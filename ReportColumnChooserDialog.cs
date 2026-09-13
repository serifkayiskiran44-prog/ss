using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The result column chooser (#849): the report's allowed columns, grouped, searchable, each a checkbox for
/// visibility, the selected one movable up and down from the keyboard, a reset to the schema's default, and a
/// save that hands the new layout back. A classified column is listed but disabled with the reason while the
/// PII policy is off; it can never be ticked on from here in that state.
/// </summary>
public static class ReportColumnChooserDialog
{
    public const string SaveLabel = "Kaydet";

    public static Window Build(Window? owner, ReportColumnLayout layout, bool classifiedAllowed, Action<ReportColumnLayout> onSave)
    {
        ArgumentNullException.ThrowIfNull(layout); ArgumentNullException.ThrowIfNull(onSave);
        var current = layout; var schema = layout.Columns.Select(c => c.Column).ToList();
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "Görünecek kolonları işaretleyin; seçili kolonu ok düğmeleriyle taşıyın. Sınıflandırılmış kolonlar politika izin verdiğinde açılır ve her zaman maskeli kalır.", TextWrapping = TextWrapping.Wrap, Margin = Spacing.BelowControl });
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var search = new TextBox { Tag = "report-columns-search", Width = 220, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Kolon adı, anahtar veya grup ara" }; AutomationProperties.SetName(search, "Kolon ara");
        var up = new Button { Tag = "report-columns-up", Content = "▲ Yukarı", Padding = Spacing.Chip, Margin = Spacing.RightInline }; AutomationProperties.SetName(up, "Seçili kolonu yukarı taşı"); IconStyles.ApplyIconButton(up, IconRole.Inline);
        var down = new Button { Tag = "report-columns-down", Content = "▼ Aşağı", Padding = Spacing.Chip, Margin = Spacing.RightInline }; AutomationProperties.SetName(down, "Seçili kolonu aşağı taşı"); IconStyles.ApplyIconButton(down, IconRole.Inline);
        var reset = new Button { Tag = "report-columns-reset", Content = "Varsayılan", Padding = Spacing.Chip };
        bar.Children.Add(new TextBlock { Text = "Ara", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }); bar.Children.Add(search); bar.Children.Add(up); bar.Children.Add(down); bar.Children.Add(reset);
        body.Children.Add(bar);
        var list = new ListBox { Tag = "report-columns-list", Height = 320, SelectionMode = SelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch }; AutomationProperties.SetName(list, "Rapor kolonları");
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        body.Children.Add(list);
        var summary = new TextBlock { Tag = "report-columns-summary", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }; AutomationProperties.SetLiveSetting(summary, AutomationLiveSetting.Polite);
        body.Children.Add(summary);

        void Render(string? select = null)
        {
            list.Items.Clear();
            foreach (var group in ReportColumns.Grouped(current, search.Text))
            {
                list.Items.Add(new ListBoxItem { IsEnabled = false, Focusable = false, Content = new TextBlock { Text = group.Group, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) } });
                foreach (var choice in group.Items)
                {
                    var closed = choice.Column.Classified && !classifiedAllowed;
                    var check = new CheckBox { Tag = "report-columns-item", Content = choice.Column.Label + (closed ? $" · {ReportColumns.PolicyClosedWord}" : choice.Column.Classified ? " · maskeli" : ""), IsChecked = choice.Visible, IsEnabled = !closed, Margin = new Thickness(8, 1, 0, 1) };
                    AutomationProperties.SetName(check, closed ? $"{choice.Column.Label}, {ReportColumns.PolicyClosedWord}" : choice.Column.Label);
                    var key = choice.Column.Key;
                    check.Click += (_, _) => { current = ReportColumns.Toggle(current, key, check.IsChecked == true, classifiedAllowed); check.IsChecked = current.Columns.First(c => c.Column.Key == key).Visible; summary.Text = ReportColumns.Summary(current, classifiedAllowed); };
                    var item = new ListBoxItem { Tag = key, Content = check, IsSelected = key == select };
                    list.Items.Add(item);
                }
            }
            summary.Text = ReportColumns.Summary(current, classifiedAllowed);
        }
        string? SelectedKey() => (list.SelectedItem as ListBoxItem)?.Tag as string;
        void MoveSelected(int delta) { var key = SelectedKey(); if (key is null) return; current = ReportColumns.Move(current, key, delta); Render(key); }
        up.Click += (_, _) => MoveSelected(-1);
        down.Click += (_, _) => MoveSelected(1);
        reset.Click += (_, _) => { current = ReportColumns.Default(schema); Render(SelectedKey()); };
        search.TextChanged += (_, _) => Render(SelectedKey());
        Render();
        return DialogShell.Create(owner, "Rapor kolonları", body, new DialogShell.Action[] { new("Vazgeç", IsCancel: true), new(SaveLabel, IsPrimary: true, OnClick: () => { onSave(current); return true; }) }, 520, 560);
    }
}
