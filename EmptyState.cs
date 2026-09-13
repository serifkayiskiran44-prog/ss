using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>Why a list is empty: there is nothing at all, a filter or search hides what exists, or this session may not see what exists.</summary>
public enum EmptyStateKind { TrueEmpty, FilteredEmpty, Hidden }

/// <summary>An empty state as every workspace shows it: a text glyph, a title, a reason, and at most one action — a route to open or a local action key — never an action this build cannot perform.</summary>
public sealed record EmptyStateView(EmptyStateKind Kind, string Glyph, string Title, string Reason, string ActionLabel, string Route, string ActionKey)
{
    public bool HasAction => ActionLabel.Length > 0 && (Route.Length > 0 || ActionKey.Length > 0);
}

/// <summary>
/// The empty-state standard (#886): no decorative image — a text glyph, a title, the reason and one action, the
/// same shape on every workspace. A filtered empty (a search or filter hides existing rows) is told apart from a
/// true empty (there are no rows) and from a hidden empty (this session may not see the rows), because each asks
/// for a different next step: clear the filter, set something up, or connect a store. An action is offered only
/// when this build can perform it: a route the shell does not register drops the button and keeps the diagnosis.
/// </summary>
public static class EmptyState
{
    public const string Tag = "empty-state";
    public const string TitleTag = "empty-state-title";
    public const string ReasonTag = "empty-state-reason";
    public const string ActionTag = "empty-state-action";
    public const string ClearFilter = "clear-filter";
    public const string NewSource = "new-source";
    public const int MaxTitle = 80;
    public const int MaxReason = 300;

    public static string GlyphFor(EmptyStateKind kind) => kind switch { EmptyStateKind.FilteredEmpty => "⌕", EmptyStateKind.Hidden => "⊘", _ => "▢" };

    /// <summary>A state whose action opens a route — dropped when the route is not one this build has.</summary>
    public static EmptyStateView Compose(EmptyStateKind kind, string title, string reason, string actionLabel = "", string route = "", Func<string, bool>? routeExists = null)
    {
        var target = (route ?? "").Trim();
        var offered = actionLabel is { Length: > 0 } && target.Length > 0 && (routeExists?.Invoke(target) ?? false);
        return new EmptyStateView(kind, GlyphFor(kind), Clean(title, MaxTitle), Clean(reason, MaxReason), offered ? Clean(actionLabel, MaxTitle) : "", offered ? target : "", "");
    }

    /// <summary>A state whose action is the workspace's own (clear its filter, add its first record).</summary>
    public static EmptyStateView Local(EmptyStateKind kind, string title, string reason, string actionLabel = "", string actionKey = "")
    {
        var key = (actionKey ?? "").Trim();
        var offered = actionLabel is { Length: > 0 } && key.Length > 0;
        return new EmptyStateView(kind, GlyphFor(kind), Clean(title, MaxTitle), Clean(reason, MaxReason), offered ? Clean(actionLabel, MaxTitle) : "", "", offered ? key : "");
    }

    public static EmptyStateView Products(bool filtered, Func<string, bool> routeExists) => filtered
        ? Local(EmptyStateKind.FilteredEmpty, "Bu aramaya uyan ürün yok", "Arama metni veya seçili filtreler hiçbir ürünü eşleştirmiyor; ürünler havuzda duruyor.", "Aramayı ve filtreleri temizle", ClearFilter)
        : Compose(EmptyStateKind.TrueEmpty, "Henüz ürün yok", "Havuz boş. Bir XML kaynağı tanımlayıp önizleyin; içe aktarma önizleme olmadan başlamaz.", "XML kaynaklarını aç", "xml", routeExists);

    public static EmptyStateView Orders(bool filtered, Func<string, bool> routeExists) => filtered
        ? Local(EmptyStateKind.FilteredEmpty, "Bu filtreye uyan sipariş yok", "Arama metni, durum, mağaza veya aciliyet filtresi hiçbir siparişi eşleştirmiyor; siparişler kayıtlı.", "Filtreleri temizle", ClearFilter)
        : Compose(EmptyStateKind.TrueEmpty, "Henüz sipariş yok", "Siparişler bağlı mağazalardan okunur. Bir mağaza bağlantısı ekleyip salt okunur testini çalıştırın ve siparişleri çekin.", "Mağaza bağlantılarını aç", "connections", routeExists);

    public static EmptyStateView Sources() => Local(EmptyStateKind.TrueEmpty, "Henüz XML kaynağı yok", "Tedarikçi beslemesinin adresini bir kaynak olarak kaydedin; şifre kaynakla birlikte dışa aktarılmaz.", "Yeni kaynak", NewSource);

    public static EmptyStateView Preview(bool filtered) => filtered
        ? Local(EmptyStateKind.FilteredEmpty, "Filtreye uyan önizleme satırı yok", "Önizleme satırları var; seçili önem, alan veya değişiklik filtresi hepsini gizliyor. Filtre çubuğundan seçimi genişletin.")
        : Local(EmptyStateKind.TrueEmpty, "Önizleme yok", "Önce kaynağın XML'ini okuyun, sonra önizleyin; içe aktarma önizlemesiz başlamaz.");

    public static EmptyStateView Reports(ReportCatalogView view, bool searching, Func<string, bool> routeExists)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Total == 0) return Compose(EmptyStateKind.TrueEmpty, "Gösterilecek rapor yok", "Bu yapı hiçbir rapor tanımı içermiyor.");
        if (view.Hidden == view.Total) return Compose(EmptyStateKind.Hidden, "Mağaza kapsamlı raporlar gizli", "Bu oturumda sunulan bir mağaza olmadığından mağaza kapsamlı raporlar gösterilmiyor; bir mağaza bağlantısı etkinleştirin.", "Mağaza bağlantılarını aç", "connections", routeExists);
        return searching
            ? Local(EmptyStateKind.FilteredEmpty, "Aramaya uyan rapor yok", "Arama metni hiçbir raporun adı, amacı veya veri kaynağıyla eşleşmiyor.", "Aramayı temizle", ClearFilter)
            : Compose(EmptyStateKind.TrueEmpty, "Gösterilecek rapor yok", "Bu oturumda sunulabilen rapor kalmadı.");
    }

    /// <summary>The dashboard's ladder (#811) in the shared shape: its reason decides the kind, its route was already checked.</summary>
    public static EmptyStateView Dashboard(DashboardEmptyStateView ladder)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        var kind = ladder.Reason == DashboardEmptyState.FilteredEmpty ? EmptyStateKind.FilteredEmpty : EmptyStateKind.TrueEmpty;
        return new EmptyStateView(kind, GlyphFor(kind), Clean(ladder.Title, MaxTitle), Clean(ladder.Detail, MaxReason), ladder.HasAction ? Clean(ladder.ActionLabel, MaxTitle) : "", ladder.HasAction ? ladder.Route : "", "");
    }

    public static EmptyStateView Matrix(bool filtered, string reason) => filtered
        ? Local(EmptyStateKind.FilteredEmpty, "Filtreye uyan ürün yok", reason, "Filtreleri temizle", ClearFilter)
        : Local(EmptyStateKind.TrueEmpty, "Gösterilecek ürün yok", reason);

    static string Clean(string? text, int max)
    {
        var clean = Regex.Replace(AuditStore.Redact(text ?? ""), @"\s+", " ").Trim();
        return clean.Length <= max ? clean : clean[..(max - 1)] + "…";
    }
}

/// <summary>Renders an empty state into a host panel: glyph + title, reason, one action; the host collapses when there is nothing to say.</summary>
public static class EmptyStatePanel
{
    public static void Render(Panel host, EmptyStateView? state, Action<string>? navigate, Action<string>? local = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.Children.Clear();
        if (state is null) { host.Visibility = Visibility.Collapsed; return; }
        host.Visibility = Visibility.Visible;
        var highContrast = SeverityStyle.IsHighContrast;
        var style = SeverityStyle.For(state.Kind == EmptyStateKind.TrueEmpty ? SeverityLevel.Warning : SeverityLevel.Info, highContrast);
        var accent = new SolidColorBrush(style.Accent);
        var body = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        // The glyph is its own block beside the title, so the title's Text stays plain for automation and tests (an inline-built TextBlock reports an empty Text).
        var heading = new DockPanel();
        var glyph = IconStyles.Glyph(state.Glyph, IconRole.Status); glyph.Foreground = accent; glyph.Margin = new Thickness(0, 0, 6, 0); glyph.VerticalAlignment = VerticalAlignment.Top; DockPanel.SetDock(glyph, System.Windows.Controls.Dock.Left); heading.Children.Add(glyph);
        var title = new TextBlock { Tag = EmptyState.TitleTag, Text = state.Title, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, FontSize = DesignTokens.TextBodySize + 1 };
        heading.Children.Add(title);
        body.Children.Add(heading);
        var reason = TextStyles.Apply(new TextBlock { Tag = EmptyState.ReasonTag, Text = state.Reason, TextWrapping = TextWrapping.Wrap, Margin = Spacing.AboveInline }, TextRole.Hint);
        body.Children.Add(reason);
        if (state.HasAction)
        {
            var action = new Button { Content = state.ActionLabel, Tag = EmptyState.ActionTag, Margin = Spacing.AboveControl, Padding = new Thickness(12, 4, 12, 4), MinHeight = DesignTokens.HitTargetMinSize, HorizontalAlignment = HorizontalAlignment.Left };
            action.Click += (_, _) => { if (state.Route.Length > 0) navigate?.Invoke(state.Route); else local?.Invoke(state.ActionKey); };
            body.Children.Add(action);
        }
        var border = new Border { Child = body, Tag = EmptyState.Tag, DataContext = state, BorderBrush = accent, BorderThickness = new Thickness(1), Background = new SolidColorBrush(style.Surface), Padding = Spacing.Section, Margin = new Thickness(4, 6, 4, 6), Focusable = false };
        AutomationProperties.SetName(border, $"{state.Title}. {state.Reason}");
        host.Children.Add(border);
    }
}
