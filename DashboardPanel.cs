using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrMarketplaceHubDesktop;

public static class DashboardPanel
{
    public static FrameworkElement Create(string? directory, Action<string> navigate)
    {
        var root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
        var panel = new StackPanel();
        root.Content = panel;
        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(4, 4, 4, 12) };
        var title = new TextBlock { Text = "Genel bakış", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.DarkSlateGray, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(title, Dock.Left);
        toolbar.Children.Add(title);
        var refresh = new Button { Content = "Durumu yenile", HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(refresh, Dock.Right);
        toolbar.Children.Add(refresh);
        panel.Children.Add(toolbar);
        var status = new TextBlock { Text = "Yerel veriler yükleniyor…", Foreground = Brushes.DarkSlateGray, Margin = new Thickness(4, 0, 4, 10) };
        panel.Children.Add(status);
        var cards = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(cards);
        var channelGroup = new GroupBox { Header = "Kanal / mağaza sağlığı", Margin = new Thickness(4), Padding = new Thickness(8) };
        var channels = new DataGrid { Height = 220, IsReadOnly = true, AutoGenerateColumns = false, EnableRowVirtualization = true };
        AddColumn(channels, "Kanal", "Channel", 100); AddColumn(channels, "Mağaza", "ShopId", 120); AddColumn(channels, "Durum", "Status", 170); AddColumn(channels, "Son test", "LastTestLabel", 150); AddColumn(channels, "Hata", "LastError", 300);
        channelGroup.Content = channels;
        panel.Children.Add(channelGroup);
        var anomalyGroup = new GroupBox { Header = "Anomaliler", Margin = new Thickness(4), Padding = new Thickness(8) };
        var anomalies = new StackPanel(); anomalyGroup.Content = anomalies; panel.Children.Add(anomalyGroup);
        var notificationGroup = new GroupBox { Header = "Hata / bildirim merkezi", Margin = new Thickness(4), Padding = new Thickness(8) };
        var notifications = new StackPanel(); notificationGroup.Content = notifications; panel.Children.Add(notificationGroup);
        var trendGroup = new GroupBox { Header = "Son 14 gün yerel raporları", Margin = new Thickness(4), Padding = new Thickness(8) };
        var trends = new DataGrid { Height = 250, IsReadOnly = true, AutoGenerateColumns = false, EnableRowVirtualization = true };
        AddColumn(trends, "Tarih", "DateLabel", 130); AddColumn(trends, "Sipariş", "Orders", 100); AddColumn(trends, "Mevcut toplam stok", "StockLabel", 170);
        trendGroup.Content = trends; panel.Children.Add(trendGroup);

        var service = new DashboardDataService(directory);
        DashboardSnapshot? lastSnapshot = null;
        static string RouteFor(string key) => key switch { "products" or "out-of-stock" => "products", "orders" => "orders", "sync" => "sync", "xml" => "xml", _ => "connections" };
        // Navigation uses the short-lived revision-aware cache (#783); the explicit "Durumu yenile" click is
        // the user asking for an authoritative re-read and always bypasses it.
        async Task RefreshAsync(bool force)
        {
            refresh.IsEnabled = false; status.Text = "Yerel veri kaynakları okunuyor…";
            try
            {
                var snapshot = await Task.Run(() => service.Load(bypassCache: force));
                cards.Children.Clear();
                lastSnapshot = snapshot;
                // #807: every card carries its own data time, coverage and fresh/stale state.
                foreach (var kpi in DashboardKpiFreshness.ForSnapshot(snapshot, DateTime.UtcNow))
                    AddCard(cards, kpi.Title, kpi.Value, RouteFor(kpi.Key), navigate, kpi.Freshness);
                foreach (var quick in new[] { ("Kategoriler / markalar", "taxonomy"), ("Excel işlemleri", "excel"), ("Raporlar", "reports"), ("Mesaj / hata merkezi", "messages"), ("Ayarlar", "settings") }) AddCard(cards, quick.Item1, "Aç", quick.Item2, navigate);
                ShowAnomalies(anomalies, snapshot, navigate);
                channels.ItemsSource = snapshot.Connections.Select(x => new { x.Channel, x.ShopId, x.Status, LastTestLabel = x.LastTestUtc?.ToLocalTime().ToString("g") ?? "—", x.LastError }).ToList();
                notifications.Children.Clear(); foreach (var item in snapshot.Notifications) AddNotification(notifications, item, navigate);
                trends.ItemsSource = snapshot.OrderTrend.Select(x => new { DateLabel = x.Date.ToString("dd.MM.yyyy"), x.Orders, StockLabel = x.CurrentStock < 0 ? "—" : x.CurrentStock.ToString("N0") }).ToList();
                var ops = OperationsSummaryService.From(snapshot);
                status.Text = ops.HasAction ? $"{snapshot.TotalProducts:N0} toplam ürün · {snapshot.PendingSyncs:N0} bekleyen/çalışan sync · Açık sipariş {ops.OpenOrders:N0} · Sync hata {ops.FailedSyncs:N0} · Son XML: {snapshot.LastXmlStatus} ({snapshot.LastXmlUtc?.ToLocalTime().ToString("g") ?? "yok"})" : ops.EmptyState.Length > 0 ? ops.EmptyState : $"{snapshot.TotalProducts:N0} toplam ürün · Açık uyarı yok · {snapshot.GeneratedUtc.ToLocalTime():g}";
            }
            catch (Exception error)
            {
                status.Text = MarketplaceConnectionStore.Redact(error.Message);
                // #807: a refresh that failed must not leave the previous figures looking current.
                if (lastSnapshot is { } previous)
                {
                    cards.Children.Clear();
                    foreach (var kpi in DashboardKpiFreshness.ForSnapshot(previous, DateTime.UtcNow))
                        AddCard(cards, kpi.Title, kpi.Value, RouteFor(kpi.Key), navigate, DashboardKpiFreshness.AfterRefreshFailure(kpi.Freshness));
                }
            }
            finally { refresh.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await RefreshAsync(force: true);
        _ = RefreshAsync(force: false);
        return root;
    }

    // #808: the tracked anomaly states as cards carrying impact, age and the next action, ordered by severity.
    static void ShowAnomalies(Panel parent, DashboardSnapshot snapshot, Action<string> navigate)
    {
        parent.Children.Clear();
        var view = DashboardAnomalies.Project(new DashboardAnomalyInput
        {
            Scope = "tüm mağazalar",
            OversellRiskProducts = snapshot.OversellRiskProducts, OversellOldestUtc = snapshot.OversellOldestUtc,
            StaleSources = snapshot.StaleSources, StaleSourceOldestUtc = snapshot.StaleSourceOldestUtc,
            FailedSyncJobs = snapshot.FailedSyncs, FailedSyncOldestUtc = snapshot.FailedSyncOldestUtc,
            UnmappedOrders = snapshot.StockWaitingOrders, UnmappedOrderOldestUtc = snapshot.UnmappedOrderOldestUtc,
        }, DateTime.UtcNow);
        parent.Children.Add(new TextBlock { Text = view.Headline, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 6) });
        foreach (var card in view.Cards)
        {
            var critical = card.Severity == DashboardAnomalies.Critical;
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = $"{(critical ? "✖" : "⚠")} {card.Title} · {card.Count:N0}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(critical ? Color.FromRgb(190, 52, 52) : Color.FromRgb(160, 82, 22)) });
            body.Children.Add(new TextBlock { Text = card.Impact, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(87, 112, 125)) });
            body.Children.Add(new TextBlock { Text = $"{card.Age} · kapsam: {card.Scope}", TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(126, 146, 158)) });
            var go = new Button { Content = card.NextAction, Tag = card.Route, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
            go.Click += (_, _) => navigate((string)go.Tag);
            body.Children.Add(go);
            var border = new Border { BorderBrush = new SolidColorBrush(critical ? Color.FromRgb(190, 52, 52) : Color.FromRgb(214, 226, 235)), BorderThickness = new Thickness(critical ? 2 : 1), Padding = new Thickness(8), Margin = new Thickness(2, 3, 2, 3), Child = body };
            AutomationProperties.SetName(border, $"{card.Title}, {card.Count}, {card.Age}");
            parent.Children.Add(border);
        }
    }

    static void AddColumn(DataGrid grid, string header, string path, double width) => grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = width });

    static void AddCard(Panel parent, string label, string value, string route, Action<string> navigate, DashboardKpiFreshnessInfo? freshness = null)
    {
        var content = new StackPanel { Children = { new TextBlock { Text = label, FontSize = 12 }, new TextBlock { Text = value, FontSize = 25, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 0) } } };
        if (freshness is not null)
        {
            // Words, not just colour: a stale card must read as stale on a monochrome or high-contrast display.
            content.Children.Add(new TextBlock { Text = (freshness.IsStale ? "⚠ " : "") + freshness.Label, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(freshness.IsStale ? Color.FromRgb(160, 82, 22) : Color.FromRgb(87, 112, 125)) });
            content.Children.Add(new TextBlock { Text = "Kapsam: " + freshness.Scope, FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(126, 146, 158)) });
        }
        var button = new Button { Content = content, Focusable = true, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.White, Foreground = Brushes.DarkSlateGray, BorderBrush = new SolidColorBrush(freshness?.IsStale == true ? Color.FromRgb(196, 132, 22) : Color.FromRgb(220, 227, 234)), MinHeight = 85, Margin = new Thickness(4) };
        button.SetValue(AutomationProperties.NameProperty, freshness is null ? label : $"{label}: {value}, {freshness.Label}, kapsam {freshness.Scope}");
        if (route != "dashboard") button.Click += (_, _) => navigate(route);
        parent.Children.Add(button);
    }

    static void AddNotification(Panel parent, DashboardNotification item, Action<string> navigate)
    {
        var row = new DockPanel { Margin = new Thickness(2, 3, 2, 3), LastChildFill = true };
        var open = new Button { Content = "Aç", Tag = item.RouteKey, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 0, 0) };
        open.Click += (_, _) => navigate((string)open.Tag);
        DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
        row.Children.Add(new TextBlock { Text = $"{item.Severity} · {item.Title}\n{item.Detail}", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(4) });
        parent.Children.Add(row);
    }
}
