using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public sealed class MarketplaceWorkspaceHost : UserControl, IDisposable
{
    readonly string? directory;
    readonly MarketplaceConnectionStore connections;
    readonly MarketplaceAdapterRegistry registry;
    readonly ComboBox switcher = new() { Name = "MarketplaceAccountSwitcher", MinWidth = 240, DisplayMemberPath = nameof(MarketplaceConnection.DisplayName), SelectedValuePath = nameof(MarketplaceConnection.Id) };
    readonly TextBlock title = new() { FontSize = 19, FontWeight = FontWeights.SemiBold };
    readonly TextBlock detail = new() { Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap };
    readonly WrapPanel commands = new() { Margin = new Thickness(0, 7, 0, 0) };
    readonly ContentControl body = new() { Name = "MarketplaceWorkspaceBody" };
    bool selecting;
    bool disposed;

    public string ConnectionId { get; private set; } = "";
    public IMarketplaceAdapter Adapter { get; private set; } = null!;
    public UIElement Workspace => (UIElement)body.Content;

    MarketplaceWorkspaceHost(string connectionId, string? directory, MarketplaceAdapterRegistry registry)
    {
        this.directory = directory;
        this.registry = registry;
        connections = new MarketplaceConnectionStore(directory);
        var root = new DockPanel();
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(240, 245, 247)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(215, 226, 230)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12),
            Child = BuildHeader()
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;
        switcher.SelectionChanged += (_, _) =>
        {
            if (selecting || switcher.SelectedValue is not string id || id == ConnectionId) return;
            Rebind(id);
        };
        Rebind(connectionId);
    }

    public static MarketplaceWorkspaceHost Create(string connectionId, string? directory = null, MarketplaceAdapterRegistry? registry = null) =>
        new(connectionId, directory, registry ?? MarketplaceAdapterRegistry.Default);

    public void RefreshAccounts()
    {
        var operational = MarketplaceOperationalAccounts.List(connections).ToArray();
        var current = operational.FirstOrDefault(x => x.Id == ConnectionId)
            ?? throw new InvalidOperationException("Seçili mağaza bağlantısı operasyonel değil, devre dışı bırakıldı veya kaldırıldı.");
        title.Text = current.DisplayName;
        detail.Text = $"{MarketplaceConnectionCatalog.Get(current.Channel).Name} · {current.ShopId} · {current.Status}";
        selecting = true;
        try
        {
            switcher.ItemsSource = operational;
            switcher.SelectedValue = ConnectionId;
        }
        finally { selecting = false; }
    }

    void Rebind(string connectionId)
    {
        var connection = connections.Get(connectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        if (!MarketplaceOperationalAccounts.IsEligible(connection, connections))
            throw new InvalidOperationException("Operasyonel olmayan mağaza çalışma alanı açılamaz.");
        var adapter = registry.Get(connection.Channel);
        var next = CreateWorkspace(connection);
        if (body.Content is IDisposable prior) prior.Dispose();
        body.Content = next;
        ConnectionId = connection.Id;
        Adapter = adapter;
        title.Text = connection.DisplayName;
        detail.Text = $"{MarketplaceConnectionCatalog.Get(connection.Channel).Name} · {connection.ShopId} · {connection.Status}";
        RebuildCommands(adapter.Capabilities);
        RefreshAccounts();
    }

    UIElement CreateWorkspace(MarketplaceConnection connection) => connection.Channel switch
    {
        "trendyol" => new TrendyolWorkspacePanel(connection.Id, directory),
        "etsy" => new EtsyWorkspacePanel(connection.Id, directory),
        _ => new Border
        {
            Padding = new Thickness(18),
            Child = new TextBlock
            {
                Text = $"{MarketplaceConnectionCatalog.Get(connection.Channel).Name} için hesap kabuğu hazır. Bu kanalın uzman ürün çalışma alanı henüz bağlanmadı.",
                TextWrapping = TextWrapping.Wrap
            }
        }
    };

    StackPanel BuildHeader()
    {
        var panel = new StackPanel();
        var row = new DockPanel();
        DockPanel.SetDock(switcher, Dock.Right);
        row.Children.Add(switcher);
        var identity = new StackPanel();
        identity.Children.Add(title);
        identity.Children.Add(detail);
        row.Children.Add(identity);
        panel.Children.Add(row);
        panel.Children.Add(commands);
        return panel;
    }

    void RebuildCommands(MarketplaceCapabilities capabilities)
    {
        commands.Children.Clear();
        foreach (var operation in Enum.GetValues<MarketplaceOperation>().Where(capabilities.Supports))
        {
            commands.Children.Add(new Button
            {
                Name = "MarketplaceOperation_" + operation,
                Content = OperationLabel(operation),
                IsHitTestVisible = false,
                Focusable = false,
                Margin = new Thickness(3)
            });
        }
    }

    static string OperationLabel(MarketplaceOperation operation) => operation switch
    {
        MarketplaceOperation.ProductsRead => "Ürünleri oku",
        MarketplaceOperation.OrdersRead => "Siparişleri oku",
        MarketplaceOperation.StockWrite => "Stok yönetimi",
        MarketplaceOperation.PriceWrite => "Fiyat yönetimi",
        MarketplaceOperation.Shipment => "Kargo yönetimi",
        _ => operation.ToString()
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (body.Content is IDisposable disposable) disposable.Dispose();
        body.Content = null;
    }
}
