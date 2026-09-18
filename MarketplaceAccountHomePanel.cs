using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record MarketplaceAccountCard(
    string ConnectionId,
    string Channel,
    string ChannelName,
    string ShopId,
    string DisplayName,
    string Status,
    int LinkedProductCount,
    int PendingCount,
    int ErrorCount);

public sealed class MarketplaceAccountHomePanel : UserControl, IDisposable
{
    readonly string? directory;
    readonly Action<string> openConnection;
    readonly WrapPanel cards = new() { Name = "MarketplaceAccountCards", Orientation = Orientation.Horizontal };
    readonly ContentControl workspace = new() { Name = "MarketplaceAccountWorkspace" };
    readonly TextBlock empty = new()
    {
        Text = "Etkin mağaza bağlantısı yok. Ayarlar → Mağaza bağlantıları bölümünden bir hesap ekleyin.",
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.SlateGray,
        Margin = new Thickness(8)
    };

    public string? CurrentConnectionId { get; private set; }

    public MarketplaceAccountHomePanel(string? directory = null, Action<string>? openConnection = null)
    {
        this.directory = directory;
        this.openConnection = openConnection ?? OpenWorkspace;
        var root = new DockPanel { Margin = new Thickness(12) };
        var heading = new StackPanel { Margin = new Thickness(4, 2, 4, 12) };
        heading.Children.Add(new TextBlock { Text = "Pazaryeri hesapları", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(23, 54, 70)) });
        heading.Children.Add(new TextBlock { Text = "Her kart ayrı bir mağaza hesabıdır. Ürünler, planlar ve geçmiş yalnız seçili hesap için açılır.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 5, 0, 0) });
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var cardScroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 260 };
        DockPanel.SetDock(cardScroll, Dock.Top);
        root.Children.Add(cardScroll);
        root.Children.Add(workspace);
        Content = root;
        Refresh();
    }

    public void Refresh()
    {
        var connections = new MarketplaceConnectionStore(directory).List(false).Where(x => x.Enabled).ToArray();
        var bindings = new ProductChannelBindingStore(directory);
        cards.Children.Clear();
        var index = 0;
        foreach (var connection in connections)
        {
            var rows = bindings.List(connectionId: connection.Id);
            var model = new MarketplaceAccountCard(
                connection.Id,
                connection.Channel,
                MarketplaceConnectionCatalog.Get(connection.Channel).Name,
                connection.ShopId,
                connection.DisplayName,
                connection.Status,
                rows.Count,
                rows.Count(x => IsPending(x.State)),
                rows.Count(x => IsError(x.State)));
            var button = new Button
            {
                Name = "MarketplaceAccountCard_" + index++,
                Tag = connection.Id,
                DataContext = model,
                Width = 275,
                MinHeight = 128,
                Margin = new Thickness(5),
                Padding = new Thickness(14),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = CardBody(model)
            };
            button.Click += (_, _) => openConnection((string)button.Tag);
            cards.Children.Add(button);
        }
        if (connections.Length == 0) cards.Children.Add(empty);
        if (CurrentConnectionId is not null && connections.All(x => x.Id != CurrentConnectionId))
        {
            CurrentConnectionId = null;
            if (workspace.Content is IDisposable disposable) disposable.Dispose();
            workspace.Content = null;
        }
        if (workspace.Content is MarketplaceWorkspaceHost host) host.RefreshAccounts();
    }

    public void OpenFirst(string channel)
    {
        var connection = new MarketplaceConnectionStore(directory).List(false)
            .FirstOrDefault(x => x.Enabled && x.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{MarketplaceConnectionCatalog.Get(channel).Name} için etkin mağaza bağlantısı bulunamadı.");
        openConnection(connection.Id);
    }

    void OpenWorkspace(string connectionId)
    {
        if (workspace.Content is IDisposable disposable) disposable.Dispose();
        var host = MarketplaceWorkspaceHost.Create(connectionId, directory);
        workspace.Content = host;
        CurrentConnectionId = host.ConnectionId;
    }

    public void Dispose()
    {
        if (workspace.Content is IDisposable disposable) disposable.Dispose();
        workspace.Content = null;
        CurrentConnectionId = null;
    }

    static FrameworkElement CardBody(MarketplaceAccountCard model)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = model.DisplayName, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"{model.ChannelName} · {model.ShopId}", Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = model.Status, Margin = new Thickness(0, 5, 0, 0), Foreground = Brushes.SlateGray });
        panel.Children.Add(new TextBlock { Text = $"Bağlı {model.LinkedProductCount}  ·  Bekleyen {model.PendingCount}  ·  Hata {model.ErrorCount}", Margin = new Thickness(0, 9, 0, 0), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    static bool IsPending(string state) => state.Contains("pending", StringComparison.OrdinalIgnoreCase)
        || state.Contains("unmatched", StringComparison.OrdinalIgnoreCase)
        || state.Contains("candidate", StringComparison.OrdinalIgnoreCase)
        || state.Contains("review", StringComparison.OrdinalIgnoreCase);

    static bool IsError(string state) => state.Contains("error", StringComparison.OrdinalIgnoreCase)
        || state.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || state.Contains("conflict", StringComparison.OrdinalIgnoreCase);
}
