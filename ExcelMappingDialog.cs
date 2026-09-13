using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>The built dialog and what a test or the page needs from it: the combos by field, the summary, the jump, the confirm and the result.</summary>
public sealed class ExcelMappingDialogView
{
    public Window Window { get; internal set; } = null!;
    public required IReadOnlyDictionary<string, ComboBox> Combos { get; init; }
    public required TextBlock Summary { get; init; }
    public required Button FirstProblem { get; init; }
    public required Button Confirm { get; init; }
    public MappingTableView? Table { get; internal set; }
    public ExcelColumnMapping? Result { get; internal set; }
}

/// <summary>
/// The generic (Excel) column mapping dialog (#833): every target field carries the same semantics the XML mapping
/// grid got in #824 -- a required marker that names its alternative ("zorunlu (veya barkod)"), the expected type, a
/// masked sample from the first data row, a status with a reason -- plus a missing count in the summary and a jump
/// that focuses the first problem's combo. The confirm is enabled only when no required group is unsatisfied and no
/// header is used twice, so a mapping that cannot import is never handed back. Long headers wrap in the label and
/// are shown in full as a tooltip; samples pass <see cref="ImportMappingTable.SafeSample"/> before they are shown.
/// </summary>
public static class ExcelMappingDialog
{
    public const string Unmapped = "(eşlenmemiş)";

    public static readonly IReadOnlyList<(string Key, string Label)> Fields = new[]
    {
        ("Sku", "SKU"), ("Name", "Ürün adı"), ("Cost", "Alış"), ("Price", "Satış"), ("Stock", "Stok"), ("Barcode", "Barkod"), ("Brand", "Marka"), ("Category", "Kategori"),
        ("Description", "Açıklama"), ("Currency", "Döviz"), ("Active", "Aktif"), ("Gtin", "GTIN"), ("ImageUrls", "Görseller"),
    };

    public static ExcelMappingDialogView Build(Window? owner, IReadOnlyList<string> headers, Func<string, string?> sampleFor, ExcelColumnMapping? existing)
    {
        ArgumentNullException.ThrowIfNull(headers); ArgumentNullException.ThrowIfNull(sampleFor);
        var content = new StackPanel { Margin = new Thickness(16) };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        var jump = new Button { Content = "İlk soruna git", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        content.Children.Add(summary); content.Children.Add(jump);
        var combos = new Dictionary<string, ComboBox>(StringComparer.Ordinal);
        var statuses = new Dictionary<string, (Border Row, TextBlock Status)>(StringComparer.Ordinal);
        var options = new[] { Unmapped }.Concat(headers).ToList();
        foreach (var (key, label) in Fields)
        {
            var row = new Border { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 2, 0, 2), BorderThickness = new Thickness(0.5) };
            var head = new DockPanel { LastChildFill = true };
            var combo = new ComboBox { ItemsSource = options, Width = 230, MaxWidth = 230, VerticalAlignment = VerticalAlignment.Top, SelectedItem = existing?.Columns.TryGetValue(key, out var column) == true && column >= 1 && column <= headers.Count ? headers[column - 1] : Unmapped };
            DockPanel.SetDock(combo, System.Windows.Controls.Dock.Right);
            var labels = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            labels.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 });
            var required = ImportMappingTable.RequiredLabel(key);
            labels.Children.Add(new TextBlock { Text = (required.Length > 0 ? required : "isteğe bağlı") + " · " + ImportMappingTable.TypeLabel(key), FontSize = 11, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 });
            head.Children.Add(combo); head.Children.Add(labels);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
            var body = new StackPanel(); body.Children.Add(head); body.Children.Add(status); row.Child = body;
            content.Children.Add(row); combos[key] = combo; statuses[key] = (row, status);
        }
        var ok = new Button { Content = "Eşlemeyi kullan", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
        ToolTipService.SetShowOnDisabled(ok, true);
        content.Children.Add(ok);

        var view = new ExcelMappingDialogView { Combos = combos, Summary = summary, FirstProblem = jump, Confirm = ok };
        void Refresh()
        {
            var hc = SeverityStyle.IsHighContrast;
            var inputs = Fields.Select(f => new MappingRowInput(f.Key, f.Label, combos[f.Key].SelectedItem is string s && s != Unmapped ? s : "")).ToList();
            var table = ImportMappingTable.Compose(inputs, headers, sampleFor);
            view.Table = table;
            summary.Text = table.Summary; summary.Foreground = SeverityStyle.AccentBrush(table.HasBlocking ? SeverityLevel.Blocking : SeverityLevel.Success, hc);
            System.Windows.Automation.AutomationProperties.SetName(summary, "Eşleme özeti: " + table.Summary);
            jump.Visibility = table.FirstProblemKey is null ? Visibility.Collapsed : Visibility.Visible; jump.Tag = table.FirstProblemKey;
            foreach (var r in table.Rows)
            {
                var level = r.Status switch { MappingRowStatus.Mapped => SeverityLevel.Success, MappingRowStatus.Optional => SeverityLevel.Info, _ => r.Status == MappingRowStatus.MissingRequired ? SeverityLevel.Blocking : SeverityLevel.Warning };
                var (rowBorder, status) = statuses[r.Key];
                status.Text = r.StatusLabel + (r.Sample.Length > 0 ? " · örnek: " + r.Sample : "") + (r.Reason.Length > 0 ? " · " + r.Reason : "");
                status.Foreground = SeverityStyle.AccentBrush(level, hc);
                rowBorder.BorderBrush = SeverityStyle.AccentBrush(level, hc); rowBorder.BorderThickness = new Thickness(r.IsProblem ? SeverityStyle.For(level, hc).BorderWeight : 0.5);
                var combo = combos[r.Key];
                combo.ToolTip = r.Path.Length > 0 ? r.Path : "Bu alan için Excel başlığı seçin";
                System.Windows.Automation.AutomationProperties.SetName(combo, $"{r.Label}: {(r.Required ? "zorunlu" : "isteğe bağlı")}, {r.StatusLabel}" + (r.Reason.Length > 0 ? ". " + r.Reason : ""));
            }
            ok.IsEnabled = !table.HasBlocking;
            ok.ToolTip = table.HasBlocking ? "Önce sorunları düzeltin: " + table.Summary : "Eşlemeyi kullan ve önizlemeyi yenile.";
        }
        foreach (var combo in combos.Values) combo.SelectionChanged += (_, _) => Refresh();
        jump.Click += (_, _) => { if (jump.Tag is string key && combos.TryGetValue(key, out var combo)) { combo.Focus(); Keyboard.Focus(combo); combo.BringIntoView(); } };
        ok.Click += (_, _) =>
        {
            if (view.Table?.HasBlocking != false) return;
            var map = new Dictionary<string, int>();
            foreach (var (key, _) in Fields) { if (combos[key].SelectedItem is not string value || value == Unmapped) continue; var index = headers.IndexOf(value); if (index >= 0) map[key] = index + 1; }
            view.Result = new ExcelColumnMapping(map);
            // Shown modally by the page: closing with a result; built by a test: the result is read directly.
            if (view.Window.IsVisible) { try { view.Window.DialogResult = true; } catch (InvalidOperationException) { view.Window.Close(); } }
        };
        view.Window = DialogShell.Create(owner, "Excel kolon eşleme", content, new DialogShell.Action[] { new("Vazgeç", IsCancel: true) }, 560, 720);
        Refresh();
        return view;
    }
}
