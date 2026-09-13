using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The reports workspace (#846): the report catalog as a card grid -- purpose, data scope, last real run, saved
/// filter count and output kinds per card. A card is a focusable button that opens the report's owner screen;
/// nothing on this page runs a report or writes anywhere. Store-scoped reports appear only while the shell offers a
/// store; the counts and the empty text say what is hidden and why.
/// </summary>
public static class ReportsPanel
{
    public const double CardWidth = 300;
    public const double CardMinHeight = 150;

    public static FrameworkElement Create(string? directory, Action<string>? navigate = null, Func<IReadOnlyCollection<string>>? allowedStoreKeys = null)
    {
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 1450 };
        panel.Children.Add(Heading("Rapor kataloğu"));
        panel.Children.Add(Hint("Bu yapının gerçekten ürettiği raporlar: amacı, okuduğu yerel veri, son çalıştırması, kayıtlı filtreleri ve çıktı türü. Kart, raporun sahibi olan ekranı açar; hiçbir rapor pazaryerine yazmaz."));
        var errorHost = new StackPanel { Visibility = Visibility.Collapsed }; var errors = new ErrorSurface(errorHost, navigate); panel.Children.Add(errorHost);
        var search = new TextBox { Tag = "report-search", Width = 260, ToolTip = "Rapor adı, amaç veya veri kaynağı ara" }; AutomationProperties.SetName(search, "Rapor ara");
        var count = Hint(""); count.Tag = "report-count";
        var empty = new TextBlock { Tag = "report-empty", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 6), Visibility = Visibility.Collapsed };
        var cards = new WrapPanel { Tag = "report-cards", Margin = new Thickness(0, 6, 0, 6) };
        void Refresh()
        {
            var view = ReportCatalog.Load(directory, allowedStoreKeys?.Invoke(), search.Text, DateTime.UtcNow);
            Render(cards, view, card => navigate?.Invoke(card.Route));
            empty.Text = view.EmptyText; empty.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            var unmatched = view.Total - view.Hidden - view.Cards.Count;
            count.Text = $"{view.Cards.Count:N0} rapor" + (view.Hidden > 0 ? $" · {view.Hidden:N0} mağaza kapsamlı rapor gizli (sunulan mağaza yok)" : "") + (unmatched > 0 ? $" · {unmatched:N0} aramaya uymadı" : "");
        }
        var refresh = Button(errors, "Yenile", Refresh);
        var bar = new WrapPanel(); bar.Children.Add(new TextBlock { Text = "Ara", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(search); bar.Children.Add(refresh);
        panel.Children.Add(bar); panel.Children.Add(count); panel.Children.Add(empty); panel.Children.Add(cards);
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
            var button = new Button { Tag = card, Content = body, Width = CardWidth, MinHeight = CardMinHeight, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(10), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top, ToolTip = card.Title + Environment.NewLine + card.Purpose + Environment.NewLine + "Açmak için Enter." };
            AutomationProperties.SetName(button, card.AccessibleName); AutomationProperties.SetHelpText(button, card.Purpose); ToolTipService.SetShowsToolTipOnKeyboardFocus(button, true);
            var current = card; button.Click += (_, _) => open(current);
            host.Children.Add(button);
        }
    }

    static TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
    static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 12) };
    static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(87, 112, 125)), Margin = new Thickness(4, 8, 4, 8) };
    static Button Button(ErrorSurface errors, string text, Action action) { var button = new Button { Content = text, Margin = new Thickness(3) }; button.Click += (_, _) => { try { errors.Clear(); action(); } catch (Exception error) { errors.Show(error); } }; return button; }
    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
}
