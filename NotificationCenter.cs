using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace TrMarketplaceHubDesktop;

public sealed record AlertGroup(string Severity, string Source, string StoreKey, bool Acknowledged, IReadOnlyList<LocalNotification> Alerts)
{
    public int Count => Alerts.Count;
    public int Occurrences => Alerts.Sum(a => a.Occurrences);
    public string Title => $"{NotificationCenter.SeverityWord(Severity)} · {NotificationCenter.SourceLabel(Source)}" + (StoreKey.Length > 0 ? " · " + ReportParameters.StoreLabel(StoreKey) : "");
}

public sealed record NotificationCenterView(IReadOnlyList<AlertGroup> Unresolved, IReadOnlyList<AlertGroup> Acknowledged, IReadOnlyList<LocalNotification> Resolved, int ResolvedCount, string Headline)
{
    public bool HasUnresolved => Unresolved.Count > 0;
}

/// <summary>
/// The notification centre's grouping (#851): open alerts first, grouped by severity (error before warning before
/// info), then source, then store, newest first inside a group; the ones a person acknowledged in their own
/// section, still grouped; the resolved ones as a short tail with their count. The live findings of the dashboard
/// become ledger inputs here -- the success placeholder is not an alert -- with a fingerprint that identifies the
/// alert by severity, source, store and title, never by the detail that changes with every count.
/// </summary>
public static class NotificationCenter
{
    public const int MaxPerGroup = 20;
    public const int MaxResolvedShown = 10;

    public static int Rank(string? severity) => Normalize(severity) switch { "ERROR" => 0, "WARNING" => 1, "INFO" => 2, "SUCCESS" => 3, _ => 2 };
    // Matched on the lower-cased word: "Uyarı" carries a dotless ı whose invariant upper case is not the "I" in
    // "UYARI", so an upper-case comparison would turn every warning into information (culture trap, again).
    public static string Normalize(string? severity) => (severity ?? "").Trim().ToLowerInvariant() switch
    {
        "error" or "hata" or "blocking" or "critical" => "ERROR",
        "warning" or "uyarı" or "uyari" or "warn" => "WARNING",
        "success" or "başarılı" or "basarili" or "ok" => "SUCCESS",
        _ => "INFO",
    };
    public static string SeverityWord(string? severity) => Normalize(severity) switch { "ERROR" => "Hata", "WARNING" => "Uyarı", "SUCCESS" => "Başarılı", _ => "Bilgi" };
    public static SeverityLevel LevelOf(string? severity) => Normalize(severity) switch { "ERROR" => SeverityLevel.Blocking, "WARNING" => SeverityLevel.Warning, "SUCCESS" => SeverityLevel.Success, _ => SeverityLevel.Info };
    public static string SourceLabel(string? source) => (source ?? "").Trim().ToLowerInvariant() switch
    {
        "" => "Genel", "sync" => "Sync merkezi", "xml" => "XML kaynakları", "products" => "Ürünler", "orders" => "Siparişler", "data-quality" => "Veri kalitesi",
        "connections" => "Bağlantılar", "api-health" => "API sağlığı", "dashboard" => "Genel bakış", "reports" => "Raporlar",
        var other => ChannelName(other) is { Length: > 0 } name ? name + " bağlantısı" : other,
    };
    static string? ChannelName(string id) { try { var name = MarketplaceConnectionCatalog.Get(id).Name; return string.IsNullOrWhiteSpace(name) || string.Equals(name, id, StringComparison.Ordinal) ? null : name; } catch (Exception) { return null; } }

    public static bool IsAlert(DashboardNotification notification) => notification is not null && Normalize(notification.Severity) != "SUCCESS";

    public static AlertInput ToAlert(DashboardNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var (channel, shop) = ReportParameters.SplitStore(notification.StoreKey);
        var severity = Normalize(notification.Severity);
        return new(NotificationStore.Fingerprint(severity, notification.RouteKey, notification.StoreKey, notification.Title), severity, channel, shop, notification.Title, notification.Detail, notification.RouteKey);
    }

    public static NotificationCenterView Build(IEnumerable<LocalNotification> alerts, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        var all = alerts.ToList();
        static IReadOnlyList<AlertGroup> Groups(IEnumerable<LocalNotification> items, bool acknowledged) => items
            .GroupBy(a => (Severity: Normalize(a.Severity), Source: (a.Source ?? "").Trim().ToLowerInvariant(), Store: a.StoreKey))
            .Select(g => new AlertGroup(g.Key.Severity, g.Key.Source, g.Key.Store, acknowledged, g.OrderByDescending(a => a.AtUtc).ThenBy(a => a.Title, StringComparer.CurrentCulture).ToList()))
            .OrderBy(g => Rank(g.Severity)).ThenBy(g => SourceLabel(g.Source), StringComparer.CurrentCulture).ThenBy(g => g.StoreKey, StringComparer.Ordinal).ToList();
        var unresolved = Groups(all.Where(a => a.IsOpen && !a.Acknowledged), false);
        var acknowledged = Groups(all.Where(a => a.IsOpen && a.Acknowledged), true);
        var resolvedAll = all.Where(a => !a.IsOpen).OrderByDescending(a => a.ResolvedUtc ?? a.AtUtc).ToList();
        var open = unresolved.Sum(g => g.Count); var errors = unresolved.Where(g => g.Severity == "ERROR").Sum(g => g.Count); var warnings = unresolved.Where(g => g.Severity == "WARNING").Sum(g => g.Count); var infos = open - errors - warnings;
        var parts = new List<string>();
        if (errors > 0) parts.Add($"{errors:N0} hata"); if (warnings > 0) parts.Add($"{warnings:N0} uyarı"); if (infos > 0) parts.Add($"{infos:N0} bilgi");
        var headline = open == 0 ? "Açık uyarı yok" : $"{open:N0} açık uyarı: {string.Join(", ", parts)}";
        var ackCount = acknowledged.Sum(g => g.Count); if (ackCount > 0) headline += $" · {ackCount:N0} onaylandı"; if (resolvedAll.Count > 0) headline += $" · {resolvedAll.Count:N0} çözüldü";
        return new(unresolved, acknowledged, resolvedAll.Take(MaxResolvedShown).ToList(), resolvedAll.Count, headline);
    }
}

/// <summary>The centre on screen: sections, one focusable group per severity/source/store, capped rows with open / acknowledge actions, all named for the keyboard and the screen reader.</summary>
public static class NotificationCenterPanel
{
    public static void Render(Panel host, NotificationCenterView view, Action<string> navigate, Action<string> acknowledge, Action<string> unacknowledge, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(view); ArgumentNullException.ThrowIfNull(navigate); ArgumentNullException.ThrowIfNull(acknowledge); ArgumentNullException.ThrowIfNull(unacknowledge);
        host.Children.Clear(); var hc = SeverityStyle.IsHighContrast;
        var headline = new TextBlock { Tag = "alerts-headline", Text = view.Headline, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 6) };
        AutomationProperties.SetLiveSetting(headline, AutomationLiveSetting.Polite); host.Children.Add(headline);
        if (!view.HasUnresolved) host.Children.Add(new TextBlock { Tag = "alerts-empty", Text = "Açık uyarı yok. Yerel veri kaynaklarında onay bekleyen bir bulgu bulunmuyor.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 6), Opacity = 0.85 });
        var unresolved = new StackPanel { Tag = "alerts-unresolved" }; foreach (var group in view.Unresolved) unresolved.Children.Add(Group(group, navigate, acknowledge, unacknowledge, nowUtc, hc, expanded: NotificationCenter.Rank(group.Severity) <= 1)); host.Children.Add(unresolved);
        if (view.Acknowledged.Count > 0)
        {
            host.Children.Add(new TextBlock { Text = $"Onaylananlar ({view.Acknowledged.Sum(g => g.Count):N0})", FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 8, 2, 2) });
            var acknowledged = new StackPanel { Tag = "alerts-acknowledged" }; foreach (var group in view.Acknowledged) acknowledged.Children.Add(Group(group, navigate, acknowledge, unacknowledge, nowUtc, hc, expanded: false)); host.Children.Add(acknowledged);
        }
        if (view.ResolvedCount > 0)
        {
            var resolved = new Expander { Tag = "alerts-resolved", Header = $"Çözülenler ({view.ResolvedCount:N0})", IsExpanded = false, Margin = new Thickness(2, 8, 2, 2) };
            var list = new StackPanel();
            foreach (var alert in view.Resolved) list.Children.Add(new TextBlock { Text = $"✔ {alert.Title} · {NotificationCenter.SeverityWord(alert.Severity)} · çözüldü {StatusTooltip.Relative(alert.ResolvedUtc ?? alert.AtUtc, nowUtc)}" + (alert.Reopened > 0 ? $" · {alert.Reopened} kez yeniden açılmıştı" : ""), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 1, 4, 1), Opacity = 0.85 });
            if (view.ResolvedCount > view.Resolved.Count) list.Children.Add(new TextBlock { Text = $"… ve {view.ResolvedCount - view.Resolved.Count:N0} daha", Margin = new Thickness(4, 2, 4, 1), Opacity = 0.7 });
            resolved.Content = list; AutomationProperties.SetName(resolved, $"Çözülenler: {view.ResolvedCount:N0} uyarı"); host.Children.Add(resolved);
        }
    }

    static Expander Group(AlertGroup group, Action<string> navigate, Action<string> acknowledge, Action<string> unacknowledge, DateTime nowUtc, bool hc, bool expanded)
    {
        var level = NotificationCenter.LevelOf(group.Severity); var style = SeverityStyle.For(level, hc);
        var expander = new Expander { Tag = "alert-group", IsExpanded = expanded, Margin = new Thickness(2, 2, 2, 2), BorderBrush = SeverityStyle.AccentBrush(level, hc), BorderThickness = new Thickness(style.BorderWeight), Padding = new Thickness(4) };
        expander.Header = new TextBlock { Text = $"{style.Glyph} {group.Title} ({group.Count:N0}" + (group.Occurrences > group.Count ? $", ×{group.Occurrences:N0}" : "") + ")", FontWeight = FontWeights.SemiBold, Foreground = SeverityStyle.AccentBrush(level, hc), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(expander, $"{style.Word}: {group.Title}, {group.Count:N0} uyarı" + (group.Acknowledged ? ", onaylandı" : ""));
        var rows = new StackPanel();
        foreach (var alert in group.Alerts.Take(NotificationCenter.MaxPerGroup))
        {
            var row = new DockPanel { Tag = "alert-row", Margin = new Thickness(2, 3, 2, 3), LastChildFill = true }; AutomationProperties.SetAutomationId(row, alert.Id);
            var ack = new Button { Tag = group.Acknowledged ? "alert-unack" : "alert-ack", Content = group.Acknowledged ? "Geri al" : "Onayla", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(4, 0, 0, 0) }; AutomationProperties.SetAutomationId(ack, alert.Id);
            var id = alert.Id; if (group.Acknowledged) ack.Click += (_, _) => unacknowledge(id); else ack.Click += (_, _) => acknowledge(id);
            AutomationProperties.SetName(ack, (group.Acknowledged ? "Onayı geri al: " : "Onayla: ") + alert.Title);
            DockPanel.SetDock(ack, Dock.Right); row.Children.Add(ack);
            if (alert.Source.Length > 0)
            {
                var open = new Button { Tag = "alert-open", Content = "Aç", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(4, 0, 0, 0) }; AutomationProperties.SetAutomationId(open, alert.Id);
                var route = alert.Source; open.Click += (_, _) => navigate(route); AutomationProperties.SetName(open, "Aç: " + NotificationCenter.SourceLabel(route));
                DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
            }
            var meta = $"×{alert.Occurrences:N0} · {StatusTooltip.Relative(alert.AtUtc, nowUtc)}" + (alert.Reopened > 0 ? $" · yeniden açıldı ×{alert.Reopened:N0}" : "");
            // The ledger sanitizes on write; the screen redacts once more so a row built from any source still shows no secret.
            row.Children.Add(new TextBlock { Text = $"{AuditStore.Redact(alert.Title)}\n{AuditStore.Redact(alert.Detail)}\n{meta}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) });
            rows.Children.Add(row);
        }
        if (group.Count > NotificationCenter.MaxPerGroup) rows.Children.Add(new TextBlock { Tag = "alert-more", Text = $"… ve {group.Count - NotificationCenter.MaxPerGroup:N0} daha", Margin = new Thickness(6, 2, 4, 2), Opacity = 0.7 });
        expander.Content = rows;
        return expander;
    }
}
