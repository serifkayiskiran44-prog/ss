using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
    sealed record GlobalHit(string Type, string Label, string Detail, string Route);

    void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K)
        {
            GlobalSearchBox.Focus(); GlobalSearchBox.SelectAll(); e.Handled = true; return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.NumPad1) { Navigate("dashboard"); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D2 or Key.NumPad2) { Navigate("products"); e.Handled = true; return; }
        if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None) { RefreshProducts(); e.Handled = true; }
    }

    void GlobalSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { GlobalSearch_Click(sender, e); e.Handled = true; }
    }

    void GlobalSearch_Click(object? sender, RoutedEventArgs e)
    {
        var query = GlobalSearchBox.Text.Trim();
        if (query.Length < 2) { StatusText.Text = "Arama için en az 2 karakter girin."; GlobalSearchBox.Focus(); return; }
        var comparison = StringComparison.CurrentCultureIgnoreCase;
        var hits = new List<GlobalHit>();
        foreach (var product in store.Search(query, 0, 30).Items)
            hits.Add(new("Ürün", $"{product.Sku} · {product.Name}", $"Barkod: {product.Barcode} · {product.Brand} · {product.Category}", "products"));
        foreach (var order in new OrdersStore(dataDirectory).ReadAll().Where(x => $"{x.Marketplace} {x.ShopId} {x.OrderId} {x.TrackingNumbers} {string.Join(' ', x.Items.Select(i => i.Sku))}".Contains(query, comparison)).Take(30))
            hits.Add(new("Sipariş", $"{order.Marketplace} / {order.OrderId}", $"Mağaza: {order.ShopId} · {order.RawStatus}", "orders"));
        foreach (var plan in new ChannelProductsStore(dataDirectory).List().Where(x => $"{x.ChannelId} {x.ShopId} {x.ProductId} {x.ListingId}".Contains(query, comparison)).Take(30))
            hits.Add(new("İlan", $"{plan.ChannelId} / {plan.ListingId}", $"Ürün: {plan.ProductId} · Mağaza: {plan.ShopId}", routes.ContainsKey(plan.ChannelId) ? plan.ChannelId : "listing-matrix"));
        foreach (var connection in new MarketplaceConnectionStore(dataDirectory).List().Where(x => $"{x.Channel} {x.ShopId} {x.DisplayName}".Contains(query, comparison)).Take(30))
            hits.Add(new("Mağaza", $"{connection.Channel} / {connection.DisplayName}", $"Kimlik: {connection.ShopId} · Durum: {connection.Status}", "connections"));
        ShowGlobalResults(query, hits.Take(80).ToList());
    }

    void ShowGlobalResults(string query, IReadOnlyList<GlobalHit> hits)
    {
        var window = new Window { Owner = this, Title = $"Arama · {query}", Width = 720, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResize };
        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new TextBlock { Text = hits.Count == 0 ? "Eşleşme bulunamadı." : $"{hits.Count} eşleşme · bir sonuca tıklayarak ilgili ekrana git", Margin = new Thickness(3, 3, 3, 10), Foreground = System.Windows.Media.Brushes.DarkSlateGray };
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top); root.Children.Add(header);
        var list = new ListBox { BorderThickness = new Thickness(0) };
        foreach (var hit in hits)
        {
            var button = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Background = System.Windows.Media.Brushes.White, Foreground = System.Windows.Media.Brushes.DarkSlateGray, Content = new StackPanel { Children = { new TextBlock { Text = $"{hit.Type}  ·  {hit.Label}", FontWeight = FontWeights.SemiBold }, new TextBlock { Text = hit.Detail, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 3, 0, 0) } } } };
            button.Click += (_, _) => { window.Close(); Navigate(hit.Route); };
            list.Items.Add(button);
        }
        root.Children.Add(list); window.Content = root; window.ShowDialog();
    }
}
