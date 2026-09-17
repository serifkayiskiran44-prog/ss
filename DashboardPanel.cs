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
        var notificationGroup = new GroupBox { Header = "Hata / bildirim merkezi", Margin = new Thickness(4), Padding = new Thickness(8) };
        var notifications = new StackPanel(); notificationGroup.Content = notifications; panel.Children.Add(notificationGroup);
        var trendGroup = new GroupBox { Header = "Son 14 gün yerel raporları", Margin = new Thickness(4), Padding = new Thickness(8) };
        var trends = new DataGrid { Height = 250, IsReadOnly = true, AutoGenerateColumns = false, EnableRowVirtualization = true };
        AddColumn(trends, "Tarih", "DateLabel", 130); AddColumn(trends, "Sipariş", "Orders", 100); AddColumn(trends, "Mevcut toplam stok", "StockLabel", 170);
        trendGroup.Content = trends; panel.Children.Add(trendGroup);

        var service = new DashboardDataService(directory);
        async Task RefreshAsync()
        {
            refresh.IsEnabled = false; status.Text = "Yerel veri kaynakları okunuyor…";
            try
            {
                var snapshot = await Task.Run(service.Load);
                cards.Children.Clear();
                AddCard(cards, "Aktif ürün", snapshot.ActiveProducts.ToString("N0"), "products", navigate); AddCard(cards, "Stokta olmayan", snapshot.OutOfStockProducts.ToString("N0"), "products", navigate); AddCard(cards, "XML kaynağı", snapshot.XmlSources.ToString("N0"), "xml", navigate); AddCard(cards, "XML hata", snapshot.FailedSyncs.ToString("N0"), "xml", navigate); AddCard(cards, "BizimHesap uyarısı", snapshot.ConnectionIssues.ToString("N0"), "bizimhesap", navigate); AddCard(cards, "Ayarlar", "Aç", "settings", navigate);
                channels.ItemsSource = snapshot.Connections.Select(x => new { x.Channel, x.ShopId, x.Status, LastTestLabel = x.LastTestUtc?.ToLocalTime().ToString("g") ?? "—", x.LastError }).ToList();
                notifications.Children.Clear(); foreach (var item in snapshot.Notifications) AddNotification(notifications, item, navigate);
                trends.ItemsSource = snapshot.OrderTrend.Select(x => new { DateLabel = x.Date.ToString("dd.MM.yyyy"), x.Orders, StockLabel = x.CurrentStock < 0 ? "—" : x.CurrentStock.ToString("N0") }).ToList();
                var ops = OperationsSummaryService.From(snapshot);
                status.Text = ops.HasAction ? $"{snapshot.TotalProducts:N0} toplam ürün · {snapshot.PendingSyncs:N0} bekleyen/çalışan sync · Açık sipariş {ops.OpenOrders:N0} · Sync hata {ops.FailedSyncs:N0} · Son XML: {snapshot.LastXmlStatus} ({snapshot.LastXmlUtc?.ToLocalTime().ToString("g") ?? "yok"})" : ops.EmptyState.Length > 0 ? ops.EmptyState : $"{snapshot.TotalProducts:N0} toplam ürün · Açık uyarı yok · {snapshot.GeneratedUtc.ToLocalTime():g}";
            }
            catch (Exception error)
            {
                status.Text = MarketplaceConnectionStore.Redact(error.Message);
            }
            finally { refresh.IsEnabled = true; }
        }
        refresh.Click += async (_, _) => await RefreshAsync();
        _ = RefreshAsync();
        return root;
    }

    static void AddColumn(DataGrid grid, string header, string path, double width) => grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = width });

    static void AddCard(Panel parent, string label, string value, string route, Action<string> navigate)
    {
        var button = new Button { Content = new StackPanel { Children = { new TextBlock { Text = label, FontSize = 12 }, new TextBlock { Text = value, FontSize = 25, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 0) } } }, Focusable = true, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.White, Foreground = Brushes.DarkSlateGray, BorderBrush = new SolidColorBrush(Color.FromRgb(220, 227, 234)), MinHeight = 85, Margin = new Thickness(4) };
        button.SetValue(AutomationProperties.NameProperty, label);
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
