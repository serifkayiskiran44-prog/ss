using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TrMarketplaceHubDesktop;

public static class ChannelListingMatrixPanel
{
    public static FrameworkElement Create(string? directory, Action<string>? navigate = null)
    {
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 1450 };
        panel.Children.Add(Heading("Kanal yayın durumu ve ürün matrisi"));
        panel.Children.Add(Hint("Ürün × kanal × mağaza görünümünde ürün eşleştirmesini, işlem durumunu ve sonraki adımı karşılaştırın. Bu ekran yalnızca yerel plan/önizleme okur; canlı ürün, stok veya fiyat yazmaz."));
        var query = new TextBox { Width = 240, ToolTip = "SKU, ürün, kanal, ilan veya hata ara" };
        var channel = new TextBox { Width = 130, ToolTip = "Kanal filtresi (etsy, ebay...)" };
        var shop = new TextBox { Width = 140, ToolTip = "Mağaza filtresi" };
        var statusFilter = new ComboBox { Width = 145, ItemsSource = new[] { "Tümü", "MISSING", "ERROR", "STALE", "AUTH_ERROR", "PENDING", "SYNCED", "DRAFT" }, SelectedIndex = 0 };
        var status = Hint("");
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 560, EnableRowVirtualization = true, EnableColumnVirtualization = false, SelectionMode = DataGridSelectionMode.Single };
        void Column(string header, string path, double width) => grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
        Column("Ürün / SKU", "ProductName", 2); Column("Kanal", "ChannelName", 1); Column("Mağaza", "ShopId", 1); Column("Eşleştirme", "EslemeDurumu", 1.2); Column("İlan ID", "ListingId", 1); Column("İşlem", "EsitlemeDurumu", 1); Column("Bağlantı", "BaglantiDurumu", 1.25); Column("Ne yapılmalı?", "YapilacakIs", 3);
        IReadOnlyList<ChannelListingMatrixRow> all = [];
        void RefreshGrid() { var filtered = ChannelListingMatrixService.Filter(all, query.Text, statusFilter.SelectedItem?.ToString() ?? "Tümü", channel.Text, shop.Text); grid.ItemsSource = filtered; status.Text = $"{filtered.Count:N0} satır · {filtered.Count(x => x.MappingStatus == "MISSING")} eşleştirme eksik · {filtered.Count(x => x.MappingStatus == "ERROR")} işlem hatası · {filtered.Count(x => x.AuthStatus == "AUTH_ERROR")} bağlantı uyarısı"; }
        async Task RefreshAsync() { status.Text = "Matris hazırlanıyor…"; all = await Task.Run(() => new ChannelListingMatrixService(directory).Build()); RefreshGrid(); }
        var refresh = AsyncButton("Matrisi yenile", RefreshAsync);
        var product = Button("Ürün havuzuna git", () => navigate?.Invoke("products"));
        var channelOpen = Button("Seçili kanala git", () => { if (grid.SelectedItem is not ChannelListingMatrixRow row) throw new InvalidOperationException("Önce matristen satır seçin."); navigate?.Invoke(row.Channel); });
        var bar = new WrapPanel(); bar.Children.Add(new TextBlock { Text = "Ara", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(query); bar.Children.Add(new TextBlock { Text = "Kanal", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(channel); bar.Children.Add(new TextBlock { Text = "Mağaza", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(shop); bar.Children.Add(statusFilter); bar.Children.Add(refresh); bar.Children.Add(product); bar.Children.Add(channelOpen); panel.Children.Add(bar); panel.Children.Add(grid); panel.Children.Add(status);
        query.TextChanged += (_, _) => RefreshGrid(); channel.TextChanged += (_, _) => RefreshGrid(); shop.TextChanged += (_, _) => RefreshGrid(); statusFilter.SelectionChanged += (_, _) => RefreshGrid(); _ = RefreshAsync();
        return Scroll(panel);
    }
    static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 12) };
    static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(87, 112, 125)), Margin = new Thickness(4, 8, 4, 8) };
    static Button Button(string text, Action action) { var button = new Button { Content = text, Margin = new Thickness(3) }; button.Click += (_, _) => { try { action(); } catch (Exception error) { MessageBox.Show(MarketplaceConnectionStore.Redact(error.Message), "Yayın matrisi", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return button; }
    static Button AsyncButton(string text, Func<Task> action) { var button = new Button { Content = text, Margin = new Thickness(3) }; button.Click += async (_, _) => { try { button.IsEnabled = false; await action(); } catch (Exception error) { MessageBox.Show(MarketplaceConnectionStore.Redact(error.Message), "Yayın matrisi", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { button.IsEnabled = true; } }; return button; }
    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
}
