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
    readonly TextBlock title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };
    readonly TextBlock detail = new() { Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap };
    readonly ContentControl body = new() { Name = "MarketplaceWorkspaceBody" };
    bool selecting;
    bool disposed;

    public string ConnectionId { get; private set; } = "";
    public IMarketplaceAdapter Adapter { get; private set; } = null!;
    public UIElement Workspace => (UIElement)body.Content;
    public event Action? AccountsRequested;
    public event Action<string>? ProductCardRequested;

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
            Padding = new Thickness(8, 5, 8, 5),
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
        detail.Text = $"{MarketplaceConnectionCatalog.Get(current.Channel).Name} · {current.ShopId} · {MarketplaceStatusText.ToTurkish(current.Status)}";
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
        foreach (var panel in Descendants(next).OfType<MarketplaceShopProductsPanel>())
            panel.ProductCardRequested += productId => ProductCardRequested?.Invoke(productId);
        if (body.Content is IDisposable prior) prior.Dispose();
        body.Content = next;
        ConnectionId = connection.Id;
        Adapter = adapter;
        title.Text = connection.DisplayName;
        detail.Text = $"{MarketplaceConnectionCatalog.Get(connection.Channel).Name} · {connection.ShopId} · {MarketplaceStatusText.ToTurkish(connection.Status)}";
        RefreshAccounts();
    }

    UIElement CreateWorkspace(MarketplaceConnection connection) => connection.Channel switch
    {
        "trendyol" => new TrendyolWorkspacePanel(connection.Id, directory),
        "etsy" => new EtsyWorkspacePanel(connection.Id, directory),
        "hepsiburada" => new HepsiburadaWorkspacePanel(connection.Id, directory),
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

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var nested in Descendants(child)) yield return nested;
    }

    DockPanel BuildHeader()
    {
        var row = new DockPanel();
        var accounts = new Button { Name = "MarketplaceShowAccounts", Content = "Mağazalar", Margin = new Thickness(6, 3, 3, 3), Padding = new Thickness(10, 5, 10, 5) };
        accounts.Click += (_, _) => AccountsRequested?.Invoke();
        DockPanel.SetDock(accounts, Dock.Right);
        row.Children.Add(accounts);
        DockPanel.SetDock(switcher, Dock.Right);
        row.Children.Add(switcher);
        var identity = new StackPanel();
        identity.Children.Add(title);
        identity.Children.Add(detail);
        row.Children.Add(identity);
        return row;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (body.Content is IDisposable disposable) disposable.Dispose();
        body.Content = null;
    }
}
