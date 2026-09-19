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
    readonly ContentControl workspace = new() { Name = "MarketplaceAccountWorkspace", VerticalAlignment = VerticalAlignment.Stretch };
    readonly ScrollViewer chooser = new() { Name = "MarketplaceAccountChooser", VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 150 };
    readonly Button chooserToggle = new() { Name = "MarketplaceAccountChooserToggle", Content = "Mağazaları gizle", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
    readonly DockPanel heading = new() { Name = "MarketplaceAccountHeading", Margin = new Thickness(4, 2, 4, 8) };
    string? channelFilter;
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
        DockPanel.SetDock(chooserToggle, Dock.Right); heading.Children.Add(chooserToggle);
        var headingText = new StackPanel();
        headingText.Children.Add(new TextBlock { Text = "Pazaryeri hesapları", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(23, 54, 70)) });
        headingText.Children.Add(new TextBlock { Text = "Mağazayı seçin; ürün yönetimi kalan alanı kullanır.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 3, 0, 0) });
        heading.Children.Add(headingText);
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        chooser.Content = cards;
        DockPanel.SetDock(chooser, Dock.Top);
        root.Children.Add(chooser);
        root.Children.Add(workspace);
        Content = root;
        chooserToggle.Click += (_, _) => SetChooserVisible(chooser.Visibility != Visibility.Visible);
        Refresh();
    }

    public void Refresh()
    {
        var connectionStore = new MarketplaceConnectionStore(directory);
        var connections = MarketplaceOperationalAccounts.List(connectionStore, channelFilter).ToArray();
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
            button.Click += (_, _) => { openConnection((string)button.Tag); SetChooserVisible(false); };
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

    public void ShowChannel(string channel)
    {
        channelFilter = MarketplaceConnectionCatalog.Get(channel).Id;
        CloseWorkspace();
        SetChooserVisible(true);
        Refresh();
    }

    public void ShowAll()
    {
        channelFilter = null;
        SetChooserVisible(CurrentConnectionId is null);
        Refresh();
    }

    void OpenWorkspace(string connectionId)
    {
        if (workspace.Content is IDisposable disposable) disposable.Dispose();
        var host = MarketplaceWorkspaceHost.Create(connectionId, directory);
        host.AccountsRequested += () => SetChooserVisible(true);
        workspace.Content = host;
        CurrentConnectionId = host.ConnectionId;
    }

    public void Dispose()
    {
        CloseWorkspace();
    }

    void CloseWorkspace()
    {
        if (workspace.Content is IDisposable disposable) disposable.Dispose();
        workspace.Content = null;
        CurrentConnectionId = null;
    }

    void SetChooserVisible(bool visible)
    {
        heading.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        chooser.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        chooserToggle.Content = visible ? "Mağazaları gizle" : "Mağazaları göster";
    }

    static FrameworkElement CardBody(MarketplaceAccountCard model)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = model.DisplayName, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"{model.ChannelName} · {model.ShopId}", Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = MarketplaceStatusText.ToTurkish(model.Status), Margin = new Thickness(0, 5, 0, 0), Foreground = Brushes.SlateGray });
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
