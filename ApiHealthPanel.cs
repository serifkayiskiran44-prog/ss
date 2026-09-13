using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class ApiHealthPanel
{
    public static FrameworkElement Create(string? directory = null, Action<string>? navigate = null)
    {
        var health = new ApiHealthStore(directory);
        var connections = new MarketplaceConnectionStore(directory);
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(Text("API bağlantı sağlığı", 22));
        top.Children.Add(Text("Auth, erişilebilirlik, rate-limit, son başarılı istek ve güvenli hata sınıflarını tek görünümde izleyin. Salt okunur testler Mağaza bağlantıları ekranındaki mevcut resmi connector akışından çalışır; bu merkez endpoint uydurmaz ve credential göstermez."));
        var bar = new WrapPanel(); top.Children.Add(bar);
        var query = new TextBox { Width = 220, ToolTip = "Kanal, mağaza, durum veya hata ara" };
        var state = new ComboBox { Width = 150, ItemsSource = new[] { "Tümü", "HEALTHY", "NOT_CONFIGURED", "LIVE_API_BLOCKED", "AUTH_ERROR", "RATE_LIMITED", "SERVER_ERROR", "NETWORK_ERROR", "TIMEOUT", "CANCELLED", "CLIENT_ERROR", "UNKNOWN" }, SelectedIndex = 0 };
        var refresh = Button("Yenile"); var openConnections = Button("Salt okunur test ekranı");
        bar.Children.Add(query); bar.Children.Add(state); bar.Children.Add(refresh); bar.Children.Add(openConnections);
        var summary = Text(""); top.Children.Add(summary);
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, EnableRowVirtualization = true, MinHeight = 300 };
        VirtualizingPanel.SetIsVirtualizing(grid, true); VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        foreach (var column in new[] { ("Kanal", "Channel", 100d), ("Mağaza", "ShopId", 120d), ("Durum", "State", 130d), ("Auth", "AuthStatus", 90d), ("HTTP", "HttpStatus", 60d), ("Hata sınıfı", "ErrorClass", 110d), ("Rate limit", "RateLimitSummary", 100d), ("Son başarılı", "LastSuccessUtc", 155d), ("Backoff", "BackoffSummary", 145d), ("Son hata", "LastError", 310d) }) grid.Columns.Add(new DataGridTextColumn { Header = column.Item1, Binding = new Binding(column.Item2), Width = column.Item3 });
        // #815: the shared status tooltip on every health row -- state, the last error as the reason (raw bodies
        // hidden by the template), last change, the connection as the source, and a next step that fits the state.
        grid.LoadingRow += (_, e) =>
        {
            if (e.Row.Item is not ApiHealthRecord record) return;
            var next = record.State.ToUpperInvariant() switch
            {
                "HEALTHY" => "",
                "NOT_CONFIGURED" => "Bağlantı ayarlarını Mağaza bağlantıları ekranından tamamlayın.",
                "AUTH_ERROR" => "Mağaza bağlantısından yetkiyi yenileyin.",
                "RATE_LIMITED" => "Backoff süresi dolana kadar bekleyin; istek göndermeyin.",
                "LIVE_API_BLOCKED" => StatusTooltip.NextActionFor("unsupported"),
                _ => StatusTooltip.NextActionFor("error"),
            };
            var tooltip = StatusTooltip.Compose(new StatusTooltipContent(record.State, record.LastError, record.UpdatedUtc == default ? null : record.UpdatedUtc.UtcDateTime, $"{record.Channel} / {record.ShopId}", next), DateTime.UtcNow);
            e.Row.ToolTip = tooltip;
            ToolTipService.SetShowsToolTipOnKeyboardFocus(e.Row, true);
            System.Windows.Automation.AutomationProperties.SetHelpText(e.Row, tooltip);
        };
        root.Children.Add(grid);
        var detail = Text("Bir bağlantı seçin."); top.Children.Add(detail);
        var actions = new WrapPanel(); var openChannel = Button("Kanal ekranına git"); actions.Children.Add(openChannel); top.Children.Add(actions);
        ApiHealthRecord? selected = null;
        void Reload()
        {
            foreach (var connection in connections.List()) health.EnsureConnection(connection.Channel, connection.ShopId, connection.Status, connection.LastError);
            var selectedState = state.SelectedItem?.ToString() == "Tümü" ? null : state.SelectedItem?.ToString();
            grid.ItemsSource = health.List(query.Text, selectedState);
            var totals = health.Summary();
            summary.Text = $"{totals.Total:N0} bağlantı · Sağlıklı {totals.Healthy} · Auth hatası {totals.AuthErrors} · Rate-limit {totals.RateLimited} · Diğer hata {totals.OtherErrors} · Backoff {totals.BackingOff}";
        }
        grid.SelectionChanged += (_, _) =>
        {
            selected = grid.SelectedItem as ApiHealthRecord;
            detail.Text = selected is null ? "Bir bağlantı seçin." : $"{selected.Channel} / {selected.ShopId}\nDurum: {selected.State} · Auth: {selected.AuthStatus} · HTTP: {selected.HttpStatus?.ToString() ?? "-"}\nSon deneme: {selected.LastAttemptUtc.ToLocalTime():g} · Son başarılı: {selected.LastSuccessUtc?.ToLocalTime().ToString("g") ?? "yok"}\nRate-limit: {selected.RateLimitSummary} · Reset: {selected.RateLimitResetUtc?.ToLocalTime().ToString("g") ?? "yok"}\nÖnerilen bekleme: {selected.BackoffSummary}\n{selected.LastError}";
        };
        query.TextChanged += (_, _) => Reload(); state.SelectionChanged += (_, _) => Reload(); refresh.Click += (_, _) => Reload(); openConnections.Click += (_, _) => navigate?.Invoke("connections");
        openChannel.Click += (_, _) =>
        {
            if (selected is null) { detail.Text = "Önce bağlantı seçin."; return; }
            navigate?.Invoke(MarketplaceConnectionCatalog.All.FirstOrDefault(x => x.Id.Equals(selected.Channel, StringComparison.OrdinalIgnoreCase))?.RouteKey ?? "connections");
        };
        Reload(); return root;
    }
    static TextBlock Text(string value, int size = 12) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 4, 3, 7), Foreground = Brushes.DarkSlateGray };
    static Button Button(string text) => new() { Content = text, Margin = new Thickness(3, 5, 3, 5) };
}
