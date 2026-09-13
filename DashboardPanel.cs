using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrMarketplaceHubDesktop;

public static class DashboardPanel
{
    public static FrameworkElement Create(string? directory, Action<string> navigate, Action<DrillRequest>? drill = null, Action<string>? storeChanged = null, Func<string, bool>? routeExists = null)
    {
        var root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
        var panel = new StackPanel();
        root.Content = panel;
        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(4, 4, 4, 12) };
        var title = new TextBlock { Text = "Genel bakış", FontSize = DesignTokens.TextSectionTitleSize, FontWeight = DesignTokens.FontWeightTitle, Foreground = Brushes.DarkSlateGray, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(title, Dock.Left);
        toolbar.Children.Add(title);
        // #809: store filter, persisted per data directory and resolved against the offered list.
        var storeFilter = new ComboBox { Width = 220, DisplayMemberPath = "Label", Margin = new Thickness(0, 0, 8, 0), ToolTip = "Panoyu bir mağazaya daralt" };
        DockPanel.SetDock(storeFilter, Dock.Right);
        var refresh = new Button { Content = "Durumu yenile", HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(refresh, Dock.Right);
        toolbar.Children.Add(refresh);
        toolbar.Children.Add(storeFilter);
        panel.Children.Add(toolbar);
        var status = new TextBlock { Text = "Yerel veriler yükleniyor…", Foreground = Brushes.DarkSlateGray, Margin = new Thickness(4, 0, 4, 10) };
        panel.Children.Add(status);
        // #811: why the board is empty, shown above the figures it would otherwise fill with zeros.
        // #816: a failed refresh is a banner with a retry, above the figures it could not refresh.
        var errorHost = new StackPanel { Visibility = Visibility.Collapsed };
        var errors = new ErrorSurface(errorHost, navigate);
        panel.Children.Add(errorHost);
        var onboarding = new StackPanel { Margin = new Thickness(4, 0, 4, 10) };
        panel.Children.Add(onboarding);
        var cards = new UniformGrid { Columns = 3, Margin = Spacing.BelowSection };
        panel.Children.Add(cards);
        var channelGroup = new GroupBox { Header = "Kanal / mağaza sağlığı", Margin = Spacing.Inline, Padding = new Thickness(8) };
        var channels = new DataGrid { Height = 220, IsReadOnly = true, AutoGenerateColumns = false, EnableRowVirtualization = true };
        var rowTooltip = new Style(typeof(DataGridRow));
        rowTooltip.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Tooltip")));
        rowTooltip.Setters.Add(new Setter(ToolTipService.ShowsToolTipOnKeyboardFocusProperty, true));
        rowTooltip.Setters.Add(new Setter(AutomationProperties.HelpTextProperty, new System.Windows.Data.Binding("Tooltip")));
        channels.RowStyle = RowSelection.AddToRowStyle(FocusStyles.AddTo(rowTooltip));
        AddColumn(channels, "Kanal", "Channel", 100); AddColumn(channels, "Mağaza", "ShopId", 120); AddColumn(channels, "Durum", "Status", 170); AddColumn(channels, "Son test", "LastTestLabel", 150); AddColumn(channels, "Hata", "LastError", 300);
        channelGroup.Content = channels;
        panel.Children.Add(channelGroup);
        var anomalyGroup = new GroupBox { Header = "Anomaliler", Margin = Spacing.Inline, Padding = new Thickness(8) };
        var anomalies = new StackPanel(); anomalyGroup.Content = anomalies; panel.Children.Add(anomalyGroup);
        var notificationGroup = new GroupBox { Header = "Hata / bildirim merkezi", Margin = Spacing.Inline, Padding = new Thickness(8) };
        var notifications = new StackPanel(); notificationGroup.Content = notifications; panel.Children.Add(notificationGroup);
        var trendGroup = new GroupBox { Header = "Son 14 gün yerel raporları", Margin = Spacing.Inline, Padding = new Thickness(8) };
        var trends = new DataGrid { Height = 250, IsReadOnly = true, AutoGenerateColumns = false, EnableRowVirtualization = true };
        AddColumn(trends, "Tarih", "DateLabel", 130); AddColumn(trends, "Sipariş", "Orders", 100); AddColumn(trends, "Mevcut toplam stok", "StockLabel", 170);
        trendGroup.Content = trends; panel.Children.Add(trendGroup);

        var service = new DashboardDataService(directory);
        var preferences = PreferenceSchema.OpenStore(directory);
        var latencyStore = new LatencyStore(directory);
        var alerts = new NotificationStore(directory); var snoozes = new AlertSnoozeStore(directory);
        void RenderAlerts() => RenderAlertsFrom(alerts.List(), snoozes.Active(DateTime.UtcNow));
        // #874: an acknowledgement or a snooze shows at once (the list re-rendered with the change applied locally), the store is asked to
        // keep it, and a refusal brings the stored state back with the error on the dashboard's own error surface, with a retry.
        void RenderAlertsFrom(IReadOnlyList<LocalNotification> list, IReadOnlyList<AlertSnooze> active)
        {
            var now = DateTime.UtcNow;
            // #852: snoozes come from their own store (fingerprint and scope only); a snooze is set from the row's chooser and lifted from the snoozed list.
            NotificationCenterPanel.Render(notifications, NotificationCenter.Build(list, active, now), navigate,
                id => Mutate($"ack:{id}", () => RenderAlertsFrom(list.Select(a => a.Id == id ? a with { Acknowledged = true } : a).ToList(), active), () => alerts.Acknowledge(id), () => RenderAlertsFrom(list, active)),
                id => Mutate($"unack:{id}", () => RenderAlertsFrom(list.Select(a => a.Id == id ? a with { Acknowledged = false } : a).ToList(), active), () => alerts.Unacknowledge(id), () => RenderAlertsFrom(list, active)), now,
                (id, option) => { var alert = list.FirstOrDefault(a => a.Id == id); var chosen = AlertSnoozeRules.Option(option); if (alert is null || chosen is null) return; Mutate($"snooze:{alert.Fingerprint}", () => RenderAlertsFrom(list.Where(a => a.Fingerprint != alert.Fingerprint).ToList(), active), () => snoozes.Snooze(alert.Fingerprint, alert.Source, alert.StoreKey, alert.Severity, chosen.Duration, DateTime.UtcNow), () => RenderAlertsFrom(list, active)); },
                fingerprint => Mutate($"unsnooze:{fingerprint}", () => RenderAlertsFrom(list, active.Where(s => s.Fingerprint != fingerprint).ToList()), () => snoozes.Clear(fingerprint), () => RenderAlertsFrom(list, active)));
        }
        // The revert redraws from the inputs already in hand, never from a fresh read: a store that refuses a write may refuse the read that follows it.
        void Mutate(string key, Action apply, Action commit, Action revert)
        {
            var report = OptimisticMutation.Run(key, apply, commit, revert,
                reportError: text => errors.Show(new InvalidOperationException(text), retry: () => { Mutate(key, apply, commit, revert); return Task.CompletedTask; }, sourceRoute: "dashboard", sourceLabel: "Bildirimler"));
            if (report.Outcome == MutationOutcome.Committed) { errors.Clear(); RenderAlerts(); }
        }
        DashboardSnapshot? lastSnapshot = null;
        var storeScope = "tüm mağazalar";
        var applyingStoreFilter = false;
        // #810: a card drills through with the board's scope and the entity it was counting; the offered keys
        // travel with the request so the window can refuse a link naming a store this session cannot use.
        var storeKey = DashboardStoreFilter.AllStoresKey;
        var offeredStoreKeys = new List<string>();
        void Drill(string route, string title, string entityKind, string entityId, string entityLabel)
        {
            if (drill is null) { navigate(route); return; }
            drill(new DrillRequest(new DrillTarget(route, title, storeKey, entityKind, entityId, entityLabel), offeredStoreKeys.ToList()));
        }
        static string RouteFor(string key) => key switch { "products" or "out-of-stock" => "products", "orders" => "orders", "sync" => "sync", "xml" => "xml", _ => "connections" };
        // Navigation uses the short-lived revision-aware cache (#783); the explicit "Durumu yenile" click is
        // the user asking for an authoritative re-read and always bypasses it.
        async Task RefreshAsync(bool force)
        {
            CommandState.Apply(refresh, DisabledReason.Busy("Yenileme sürüyor.")); status.Text = "Yerel veri kaynakları okunuyor…";
            var latency = UiLatency.Begin(latencyStore, UiLatency.DashboardView, force ? LatencyPhase.Refresh : LatencyPhase.Load, storeKey);
            try
            {
                var snapshot = await Task.Run(() => service.Load(bypassCache: force));
                errors.Clear();
                cards.Children.Clear();
                lastSnapshot = snapshot;
                // #807: every card carries its own data time, coverage and fresh/stale state.
                foreach (var kpi in DashboardKpiFreshness.ForSnapshot(snapshot, DateTime.UtcNow))
                {
                    var current = kpi;
                    AddCard(cards, kpi.Title, kpi.Value, RouteFor(kpi.Key), _ => Drill(RouteFor(current.Key), current.Title, "kpi", current.Key, current.Title), kpi.Freshness);
                }
                // #813: a quick card only for a screen the shell actually registers -- "reports" has no route today,
                // and a card that navigates nowhere is the same broken promise #811 refuses in the empty state.
                var known = routeExists ?? (_ => true);
                foreach (var quick in new[] { ("Kategoriler / markalar", "taxonomy"), ("Excel işlemleri", "excel"), ("Raporlar", "reports"), ("Mesaj / hata merkezi", "messages"), ("Ayarlar", "settings") }.Where(q => known(q.Item2))) AddCard(cards, quick.Item1, "Aç", quick.Item2, navigate);
                applyingStoreFilter = true;
                try
                {
                    var options = DashboardStoreFilter.Options(snapshot.Connections.Select(c => new DashboardStoreCandidate(c.Channel, c.ShopId, c.DisplayName, c.Enabled)).ToList());
                    var selection = DashboardStoreFilter.Resolve(PreferenceSchema.Read(preferences, DashboardStoreFilter.PreferenceKey), options);
                    storeFilter.ItemsSource = options;
                    storeFilter.SelectedItem = options.FirstOrDefault(o => o.Key == selection.Selected.Key) ?? options[0];
                    storeScope = selection.Selected.Scope;
                    storeKey = selection.Selected.Key;
                    offeredStoreKeys = options.Select(o => o.Key).Where(k => k != DashboardStoreFilter.AllStoresKey).ToList();
                    if (selection.FellBack)
                    {
                        // The saved store is gone or switched off: say so and stop pointing at it.
                        try { PreferenceSchema.Write(preferences, DashboardStoreFilter.PreferenceKey, DashboardStoreFilter.AllStoresKey); } catch (Exception saveError) { System.Diagnostics.Debug.WriteLine(saveError.Message); }
                        status.Text = selection.Notice;
                    }
                }
                finally { applyingStoreFilter = false; }
                ShowEmptyState(onboarding, snapshot, storeKey, storeScope, navigate, routeExists ?? (_ => true));
                ShowAnomalies(anomalies, snapshot, card => Drill(card.Route, card.Title, "anomaly", card.Key, card.Title), storeScope);
                // #815: every connection row carries the shared status tooltip (status, reason, last change, source,
                // next action), bound through the row style so it also shows on keyboard focus.
                channels.ItemsSource = snapshot.Connections.Select(x => new
                {
                    x.Channel, x.ShopId, x.Status, LastTestLabel = TimeDisplay.Format(x.LastTestUtc), x.LastError,
                    Tooltip = StatusTooltip.Compose(new StatusTooltipContent(x.Status, x.LastError, x.LastTestUtc, $"{x.Channel} / {x.ShopId}",
                        string.Equals(x.Status, "CONNECTED_READ_ONLY", StringComparison.OrdinalIgnoreCase) ? "" : "Bağlantıyı Mağaza bağlantıları ekranından yeniden test edin."), DateTime.UtcNow),
                }).ToList();
                // #851: the notification centre is the persisted alert ledger -- the live findings are synced in (new / repeated /
                // reopened / resolved) and shown grouped by severity, source and store, the acknowledged ones apart.
                alerts.Sync(snapshot.Notifications.Where(NotificationCenter.IsAlert).Select(NotificationCenter.ToAlert), DateTime.UtcNow);
                RenderAlerts();
                trends.ItemsSource = snapshot.OrderTrend.Select(x => new { DateLabel = x.Date.ToString("dd.MM.yyyy"), x.Orders, StockLabel = x.CurrentStock < 0 ? "—" : x.CurrentStock.ToString("N0") }).ToList();
                var ops = OperationsSummaryService.From(snapshot);
                status.Text = ops.HasAction ? $"{snapshot.TotalProducts:N0} toplam ürün · {snapshot.PendingSyncs:N0} bekleyen/çalışan sync · Açık sipariş {ops.OpenOrders:N0} · Sync hata {ops.FailedSyncs:N0} · Son XML: {snapshot.LastXmlStatus} ({TimeDisplay.Format(snapshot.LastXmlUtc, missing: "yok")})" : ops.EmptyState.Length > 0 ? ops.EmptyState : $"{snapshot.TotalProducts:N0} toplam ürün · Açık uyarı yok · {TimeDisplay.Format(snapshot.GeneratedUtc)}";
                latency.Complete(snapshot.Connections.Count);
            }
            catch (Exception error)
            {
                latency.Fail();
                status.Text = MarketplaceConnectionStore.Redact(error.Message);
                errors.Show(error, () => RefreshAsync(force: true));
                // #807: a refresh that failed must not leave the previous figures looking current.
                if (lastSnapshot is { } previous)
                {
                    cards.Children.Clear();
                    foreach (var kpi in DashboardKpiFreshness.ForSnapshot(previous, DateTime.UtcNow))
                        AddCard(cards, kpi.Title, kpi.Value, RouteFor(kpi.Key), navigate, DashboardKpiFreshness.AfterRefreshFailure(kpi.Freshness));
                }
            }
            finally { CommandState.Apply(refresh, null); }
        }
        storeFilter.SelectionChanged += async (_, _) =>
        {
            if (applyingStoreFilter || storeFilter.SelectedItem is not DashboardStoreOption chosen) return;
            try { PreferenceSchema.Write(preferences, DashboardStoreFilter.PreferenceKey, chosen.Key); } catch (Exception saveError) { System.Diagnostics.Debug.WriteLine(saveError.Message); }
            // #810: the trail was dug through the previous store's data, so the window drops it before the reload.
            storeChanged?.Invoke(chosen.Key);
            await RefreshAsync(force: false);
        };
        refresh.Click += async (_, _) => await RefreshAsync(force: true);
        _ = RefreshAsync(force: false);
        return root;
    }

    // #808: the tracked anomaly states as cards carrying impact, age and the next action, ordered by severity.
    static void ShowAnomalies(Panel parent, DashboardSnapshot snapshot, Action<DashboardAnomalyCard> open, string scope)
    {
        parent.Children.Clear();
        var view = DashboardAnomalies.Project(new DashboardAnomalyInput
        {
            Scope = scope,
            OversellRiskProducts = snapshot.OversellRiskProducts, OversellOldestUtc = snapshot.OversellOldestUtc,
            StaleSources = snapshot.StaleSources, StaleSourceOldestUtc = snapshot.StaleSourceOldestUtc,
            FailedSyncJobs = snapshot.FailedSyncs, FailedSyncOldestUtc = snapshot.FailedSyncOldestUtc,
            UnmappedOrders = snapshot.StockWaitingOrders, UnmappedOrderOldestUtc = snapshot.UnmappedOrderOldestUtc,
        }, DateTime.UtcNow);
        parent.Children.Add(new TextBlock { Text = view.Headline, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 6) });
        foreach (var card in view.Cards)
        {
            var critical = card.Severity == DashboardAnomalies.Critical;
            var style = SeverityStyle.For(SeverityStyle.FromAnomaly(card.Severity), SeverityStyle.IsHighContrast);
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = $"{style.Glyph} {card.Title} · {card.Count:N0}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = SeverityStyle.AccentBrush(style.Level, SeverityStyle.IsHighContrast) });
            body.Children.Add(new TextBlock { Text = card.Impact, TextWrapping = TextWrapping.Wrap, FontSize = DesignTokens.TextCaptionSize, Foreground = new SolidColorBrush(Color.FromRgb(87, 112, 125)) });
            body.Children.Add(new TextBlock { Text = $"{card.Age} · kapsam: {card.Scope}", TextWrapping = TextWrapping.Wrap, FontSize = DesignTokens.TextCaptionSize, Foreground = new SolidColorBrush(DesignTokens.TextMutedColor) });
            var go = new Button { Content = card.NextAction, Tag = card.Route, Margin = Spacing.AboveInline, Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
            var drilled = card;
            go.Click += (_, _) => open(drilled);
            body.Children.Add(go);
            var border = new Border { BorderBrush = SeverityStyle.AccentBrush(style.Level, SeverityStyle.IsHighContrast), BorderThickness = new Thickness(style.BorderWeight), Padding = new Thickness(8), Margin = new Thickness(2, 3, 2, 3), Child = body };
            AutomationProperties.SetName(border, $"{card.Title}, {card.Count}, {card.Age}");
            parent.Children.Add(border);
        }
    }

    // #811: one cause, one sentence, one safe next step -- and no button when the screen it needs is missing.
    static void ShowEmptyState(Panel parent, DashboardSnapshot snapshot, string storeKey, string scope, Action<string> navigate, Func<string, bool> routeExists)
    {
        parent.Children.Clear();
        var filtered = storeKey != DashboardStoreFilter.AllStoresKey;
        var visible = filtered ? snapshot.Connections.Count(c => DashboardStoreFilter.KeyFor(c.Channel, c.ShopId) == storeKey) : snapshot.Connections.Count;
        var state = DashboardEmptyState.Evaluate(new DashboardEmptyInput
        {
            Connections = snapshot.Connections.Count,
            Sources = snapshot.XmlSources,
            SourcesEverRun = snapshot.SourcesEverRun,
            SourcesWithSuccessfulFeed = snapshot.SourcesWithSuccessfulFeed,
            Products = snapshot.TotalProducts,
            Orders = snapshot.OpenOrders,
            Filtered = filtered,
            VisibleRecords = visible,
            ScopeLabel = scope,
        }, routeExists);
        if (!state.IsEmpty) { parent.Visibility = Visibility.Collapsed; return; }
        EmptyStatePanel.Render(parent, EmptyState.Dashboard(state), navigate); // #886: the ladder in the shared shape
    }

    static void AddColumn(DataGrid grid, string header, string path, double width) => grid.Columns.Add(GridColumns.Text(header, path, width));

    static void AddCard(Panel parent, string label, string value, string route, Action<string> navigate, DashboardKpiFreshnessInfo? freshness = null)
    {
        var content = new StackPanel { Children = { new TextBlock { Text = label, FontSize = DesignTokens.TextCaptionSize }, new TextBlock { Text = value, FontSize = DesignTokens.TextKpiSize, FontWeight = DesignTokens.FontWeightKpi, Margin = new Thickness(0, 6, 0, 0) } } };
        if (freshness is not null)
        {
            // Words, not just colour: a stale card must read as stale on a monochrome or high-contrast display.
            content.Children.Add(new TextBlock { Text = (freshness.IsStale ? "⚠ " : "") + freshness.Label, FontSize = DesignTokens.TextCaptionSize, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(freshness.IsStale ? Color.FromRgb(160, 82, 22) : Color.FromRgb(87, 112, 125)) });
            content.Children.Add(new TextBlock { Text = "Kapsam: " + freshness.Scope, FontSize = DesignTokens.TextCaptionSize, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(DesignTokens.TextMutedColor) });
        }
        var button = new Button { Content = content, Focusable = true, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.White, Foreground = Brushes.DarkSlateGray, BorderBrush = new SolidColorBrush(freshness?.IsStale == true ? SeverityStyle.For(SeverityLevel.Warning, false).Accent : Color.FromRgb(220, 227, 234)), MinHeight = 85, Margin = Spacing.Inline };
        button.SetValue(AutomationProperties.NameProperty, freshness is null ? label : $"{label}: {value}, {freshness.Label}, kapsam {freshness.Scope}");
        if (route != "dashboard") button.Click += (_, _) => navigate(route);
        parent.Children.Add(button);
    }

}
