using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The reports workspace (#846, #847): the report catalog as a card grid -- purpose, data scope, last real run,
/// saved filter count and output kinds per card -- and, beside it, the run setup of the selected report: typed
/// parameters with defaults and validation, saved filters, and either "Çalıştır" (the orders CSV the workspace
/// produces itself) or "Ekranda aç" (a report another screen owns). Store-scoped reports appear only while the
/// shell offers a store; the counts and the empty text say what is hidden and why. No report writes anywhere but
/// the file the operator picks.
/// </summary>
public static class ReportsPanel
{
    public const double CardWidth = 300;
    public const double CardMinHeight = 150;
    public const double SetupWidth = 380;

    /// <param name="choosePath">Where a runnable report's file goes; null asks with the standard save dialog.</param>
    public static FrameworkElement Create(string? directory, Action<string>? navigate = null, Func<IReadOnlyCollection<string>>? allowedStoreKeys = null, Func<ReportDefinition, string?>? choosePath = null)
    {
        var panel = new StackPanel { Margin = new Thickness(DesignTokens.SpacePage), MaxWidth = 1450 };
        panel.Children.Add(Heading("Rapor kataloğu"));
        panel.Children.Add(Hint("Bu yapının gerçekten ürettiği raporlar: amacı, okuduğu yerel veri, son çalıştırması, kayıtlı filtreleri ve çıktı türü. Kartı seçince sağda çalıştırma ayarları açılır; hiçbir rapor pazaryerine yazmaz."));
        var errorHost = new StackPanel { Visibility = Visibility.Collapsed }; var errors = new ErrorSurface(errorHost, navigate); panel.Children.Add(errorHost);
        var search = new TextBox { Tag = "report-search", Width = 260, ToolTip = "Rapor adı, amaç veya veri kaynağı ara" }; AutomationProperties.SetName(search, "Rapor ara");
        var count = Hint(""); count.Tag = "report-count";
        var empty = new TextBlock { Tag = "report-empty", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 6), Visibility = Visibility.Collapsed };
        var cards = new WrapPanel { Tag = "report-cards", Margin = new Thickness(0, 6, 0, 6) };
        // #847: the setup of the selected report lives beside the grid, rebuilt per selection, kept across card refreshes.
        var setupHost = new Border { Tag = "report-setup-host", Visibility = Visibility.Collapsed, Width = SetupWidth, BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(214, 222, 228)), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var preferences = new UiPreferenceStore(directory);
        // #848: a retry re-runs into the file the operator already chose for that report.
        var lastPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        // #849: classified result columns follow the PII reveal policy (#841); an unreadable policy allows nothing.
        var catalogStore = new Catalog.CatalogStore(directory);
        bool ClassifiedAllowed() { try { return catalogStore.GetPiiRevealPolicy().Allowed; } catch (Exception) { return false; } }
        void Refresh()
        {
            var view = ReportCatalog.Load(directory, allowedStoreKeys?.Invoke(), search.Text, DateTime.UtcNow);
            Render(cards, view, Select);
            empty.Text = view.EmptyText; empty.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            var unmatched = view.Total - view.Hidden - view.Cards.Count;
            count.Text = $"{view.Cards.Count:N0} rapor" + (view.Hidden > 0 ? $" · {view.Hidden:N0} mağaza kapsamlı rapor gizli (sunulan mağaza yok)" : "") + (unmatched > 0 ? $" · {unmatched:N0} aramaya uymadı" : "");
        }
        void Select(ReportCard card)
        {
            var definition = ReportCatalog.Find(card.Key); if (definition is null) return;
            var runnable = ReportRunner.CanRun(definition);
            // #849: the query lists on screen; the export writes the visible columns into the file the operator picks (a retry reuses it).
            Func<ReportParameterSet, IProgress<ReportRunProgressEvent>, CancellationToken, Task<ReportQueryOutcome>>? query = runnable ? async (parameters, progress, token) =>
            {
                var outcome = await ReportRunner.QueryAsync(directory, definition, parameters, allowedStoreKeys?.Invoke(), token, progress);
                Refresh();
                return outcome;
            } : null;
            Func<ReportResult, IReadOnlyList<string>, IProgress<ReportRunProgressEvent>, CancellationToken, bool, Task<string?>>? export = runnable ? async (result, columns, progress, token, retry) =>
            {
                var path = retry && lastPaths.TryGetValue(definition.Key, out var last) ? last : choosePath is not null ? choosePath(definition) : AskPath(panel, definition);
                if (string.IsNullOrWhiteSpace(path)) return null;
                lastPaths[definition.Key] = path;
                var outcome = await ReportRunner.ExportAsync(directory, result, columns, path, token, progress);
                Refresh();
                return outcome.Message;
            } : null;
            setupHost.Child = ReportParameterPanel.Build(new ReportParameterPanel.Context(definition, () => allowedStoreKeys?.Invoke(), preferences, navigate, query, export, ClassifiedAllowed));
            setupHost.Visibility = Visibility.Visible;
        }
        var refresh = Button(errors, "Yenile", Refresh);
        var bar = new WrapPanel(); bar.Children.Add(new TextBlock { Text = "Ara", Margin = Spacing.Inline, VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(search); bar.Children.Add(refresh);
        var left = new StackPanel(); left.Children.Add(bar); left.Children.Add(count); left.Children.Add(empty); left.Children.Add(cards);
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0); Grid.SetColumn(setupHost, 1); layout.Children.Add(left); layout.Children.Add(setupHost);
        panel.Children.Add(layout);
        search.TextChanged += (_, _) => { try { errors.Clear(); Refresh(); } catch (Exception error) { errors.Show(error); } };
        try { Refresh(); } catch (Exception error) { errors.Show(error); }
        return Scroll(panel);
    }

    /// <summary>Lays the cards out: one fixed-width (DIP) focusable button per card, title trimmed on the card and whole in the tooltip and accessible name, the last run carrying the severity glyph so it reads without colour.</summary>
    public static void Render(Panel host, ReportCatalogView view, Action<ReportCard> open)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(view); ArgumentNullException.ThrowIfNull(open);
        host.Children.Clear(); var hc = SeverityStyle.IsHighContrast;
        foreach (var card in view.Cards)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Tag = "report-card-title", Text = card.DisplayTitle, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 44 });
            body.Children.Add(new TextBlock { Text = card.Purpose, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6), Opacity = 0.9 });
            body.Children.Add(Line(card.ScopeText));
            var level = card.LastRunState switch { ReportRunState.Succeeded => SeverityLevel.Success, ReportRunState.Failed => SeverityLevel.Blocking, ReportRunState.Cancelled => SeverityLevel.Warning, _ => SeverityLevel.Info };
            var style = SeverityStyle.For(level, hc);
            body.Children.Add(new TextBlock { Tag = "report-card-run", Text = $"{style.Glyph} {card.LastRunText}", TextWrapping = TextWrapping.Wrap, Foreground = SeverityStyle.AccentBrush(level, hc) });
            body.Children.Add(Line(card.SavedFiltersText)); body.Children.Add(Line(card.OutputsText));
            var button = new Button { Tag = card, Content = body, Width = CardWidth, MinHeight = CardMinHeight, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(10), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, ToolTip = card.Title + Environment.NewLine + card.Purpose + Environment.NewLine + "Seçmek için Enter." };
            AutomationProperties.SetName(button, card.AccessibleName); AutomationProperties.SetHelpText(button, card.Purpose); ToolTipService.SetShowsToolTipOnKeyboardFocus(button, true);
            var current = card; button.Click += (_, _) => open(current);
            host.Children.Add(button);
        }
    }

    static string? AskPath(FrameworkElement owner, ReportDefinition definition)
    {
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = $"{definition.Key}-{DateTime.Now:yyyyMMdd-HHmm}.csv", AddExtension = true };
        var window = Window.GetWindow(owner);
        return (window is null ? dialog.ShowDialog() : dialog.ShowDialog(window)) == true ? dialog.FileName : null;
    }
    static TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
    static TextBlock Heading(string text) => TextStyles.Apply(new TextBlock { Text = text, Margin = Spacing.TitleBlock }, TextRole.SectionTitle);
    static TextBlock Hint(string text) => TextStyles.Apply(new TextBlock { Text = text, Margin = Spacing.HintBlock }, TextRole.Hint);
    static Button Button(ErrorSurface errors, string text, Action action) { var button = new Button { Content = text, Margin = Spacing.Control }; button.Click += (_, _) => { try { errors.Clear(); action(); } catch (Exception error) { errors.Show(error); } }; return button; }
    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
}
