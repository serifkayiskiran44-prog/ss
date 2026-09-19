using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Hepsiburada;

namespace TrMarketplaceHubDesktop;

public sealed class HepsiburadaWorkspacePanel : UserControl, IDisposable
{
    readonly string? directory;
    readonly MarketplaceConnection connection;
    readonly MarketplaceConnectionStore connections;
    readonly MarketplaceCredentialVault vault;
    readonly TabControl sections = new() { Name = "HepsiburadaSections" };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6), Foreground = Brushes.DarkSlateGray };
    readonly TextBox merchant = new() { Name = "HepsiburadaMerchantId", MinWidth = 360, IsReadOnly = true };
    readonly PasswordBox serviceKey = new() { Name = "HepsiburadaServiceKey", MinWidth = 360 };
    readonly ComboBox environment = new() { Name = "HepsiburadaEnvironment", MinWidth = 180, ItemsSource = Enum.GetValues<HepsiburadaEnvironment>() };
    readonly TextBox userAgent = new() { Name = "HepsiburadaUserAgent", MinWidth = 360 };
    MarketplaceShopProductsPanel? productsPanel;
    CancellationTokenSource? cancellation;

    public string ConnectionId => connection.Id;
    public string AccountShopId => connection.ShopId;
    public event Action<string>? ProductCardRequested;

    public HepsiburadaWorkspacePanel(string connectionId, string? directory = null)
    {
        this.directory = directory;
        connections = new(directory);
        connection = connections.Get(connectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        if (!connection.Enabled || connection.Channel != "hepsiburada") throw new InvalidOperationException("Hepsiburada çalışma alanı için etkin bir Hepsiburada hesabı gerekli.");
        vault = new(directory);
        merchant.Text = connection.ShopId;
        environment.SelectedItem = HepsiburadaEnvironment.Production;
        userAgent.Text = "MonoBridge/1.0";

        var root = new DockPanel { Margin = new Thickness(10) };
        var header = new DockPanel { Margin = new Thickness(4, 0, 4, 8) };
        var settings = Button("Mağaza ayarları", "HepsiburadaOpenSettings", ShowSettings);
        DockPanel.SetDock(settings, Dock.Right); header.Children.Add(settings);
        header.Children.Add(new TextBlock { Text = $"{connection.DisplayName} / {connection.ShopId} · {MarketplaceStatusText.ToTurkish(connection.Status)}", FontSize = 16, FontWeight = FontWeights.SemiBold });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        sections.Items.Add(new TabItem { Header = "Hepsiburada kontrol", Content = BuildControl() });
        sections.Items.Add(new TabItem { Header = "Ayarlar", Content = BuildSettings() });
        sections.Items.Add(new TabItem { Header = "Rekabet analizi", Content = Text("Buybox ve komisyon okumaları bağlantı doğrulandıktan sonra burada gösterilecek.") });
        sections.Items.Add(new TabItem { Header = "İşlem geçmişi", Content = Text("Hepsiburada işlemleri hesap bazında burada tutulur.") });
        root.Children.Add(sections); Content = root;
        LoadCredentials();
    }

    FrameworkElement BuildControl()
    {
        var root = new DockPanel();
        var toolbar = new WrapPanel { Margin = new Thickness(4) };
        toolbar.Children.Add(Button("Hepsiburada ürünlerini oku", "HepsiburadaReadProducts", ReadProductsAsync));
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        productsPanel = new MarketplaceShopProductsPanel(connection.Id, directory);
        productsPanel.ProductCardRequested += id => ProductCardRequested?.Invoke(id);
        root.Children.Add(productsPanel);
        return root;
    }

    FrameworkElement BuildSettings()
    {
        var form = new StackPanel { Margin = new Thickness(12), MaxWidth = 760 };
        form.Children.Add(Text("Hepsiburada satıcı panelindeki mağaza kimliği ve servis anahtarı bu Windows kullanıcısı için şifrelenerek saklanır."));
        Field(form, "Mağaza / Merchant ID", merchant);
        Field(form, "Servis anahtarı", serviceKey);
        form.Children.Add(Text("Kayıtlı servis anahtarını değiştirmeyecekseniz alanı boş bırakın.", true));
        Field(form, "Ortam", environment);
        Field(form, "User-Agent", userAgent);
        var actions = new WrapPanel();
        actions.Children.Add(Button("Ayarları kaydet", "HepsiburadaSaveSettings", SaveCredentials));
        actions.Children.Add(Button("Salt okunur bağlantıyı kontrol et", "HepsiburadaTestConnection", TestConnectionAsync));
        form.Children.Add(actions);
        return new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    void LoadCredentials()
    {
        var saved = vault.Load<HepsiburadaCredentials>(connection.Id, connection.Channel, connection.ShopId);
        if (saved is null) { status.Text = "API bilgileri eksik. Ayarlar bölümünden servis anahtarını kaydedin."; return; }
        environment.SelectedItem = saved.Environment; userAgent.Text = saved.UserAgent;
        status.Text = "API bilgileri şifreli kayıtlı. Servis anahtarı ekranda gösterilmez.";
    }

    void SaveCredentials()
    {
        var previous = vault.Load<HepsiburadaCredentials>(connection.Id, connection.Channel, connection.ShopId);
        var key = serviceKey.Password.Length > 0 ? serviceKey.Password : previous?.ServiceKey ?? "";
        var value = new HepsiburadaCredentials(connection.ShopId, key, (HepsiburadaEnvironment)(environment.SelectedItem ?? HepsiburadaEnvironment.Production), userAgent.Text.Trim());
        HepsiburadaConnection.Validate(value);
        vault.Save(connection.Id, connection.Channel, connection.ShopId, value);
        serviceKey.Clear(); status.Text = "Hepsiburada bağlantı bilgileri şifreli olarak kaydedildi.";
    }

    async Task TestConnectionAsync()
    {
        var current = connections.Get(connection.Id) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        var credentials = RequireCredentials();
        status.Text = "Salt okunur bağlantı kontrol ediliyor…";
        using var http = SafeHttp();
        var identity = await new HepsiburadaConnection().TestReadOnlyAsync(credentials, http, Token);
        var outcome = connections.RecordTest(current.Id, current.Revision, true);
        status.Text = outcome == ConnectionTestApplyResult.Applied ? $"Bağlantı doğrulandı · Mağaza {identity.MerchantId} · Salt okunur" : "Bağlantı tamamlandı ancak mağaza ayarı değişti; sonuç uygulanmadı.";
    }

    async Task ReadProductsAsync()
    {
        var credentials = RequireCredentials();
        status.Text = "Hepsiburada ürünleri okunuyor…";
        using var http = SafeHttp(); using var client = new HepsiburadaApiClient(credentials, http);
        var state = await new HepsiburadaWorkspaceService(new HepsiburadaWorkspaceStore(directory)).RefreshProductsAsync(connection.Id, credentials, client, Token);
        status.Text = $"{state.Products.Count:N0} Hepsiburada ürünü okundu. Eşleştirme barkoda, ardından çelişmeyen SKU'ya göre yapılır.";
    }

    HepsiburadaCredentials RequireCredentials() => vault.Load<HepsiburadaCredentials>(connection.Id, connection.Channel, connection.ShopId)
        ?? throw new InvalidOperationException("Önce Ayarlar bölümünde Hepsiburada servis anahtarını kaydedin.");
    CancellationToken Token => (cancellation ??= new CancellationTokenSource()).Token;
    static HttpClient SafeHttp() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };
    public void ShowSettings() => sections.SelectedIndex = 1;
    public void Dispose() { cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; }

    static TextBlock Text(string value, bool muted = false) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4), Foreground = muted ? Brushes.SlateGray : Brushes.DarkSlateGray };
    static void Field(Panel panel, string label, UIElement control) { panel.Children.Add(Text(label)); panel.Children.Add(control); }
    static Button Button(string label, string name, Action action) { var button = new Button { Name = name, Content = label, Margin = new Thickness(3), Padding = new Thickness(9, 5, 9, 5) }; button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(MarketplaceConnectionStore.Redact(ex.Message), "Hepsiburada", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return button; }
    static Button Button(string label, string name, Func<Task> action) { var button = new Button { Name = name, Content = label, Margin = new Thickness(3), Padding = new Thickness(9, 5, 9, 5) }; button.Click += async (_, _) => { try { button.IsEnabled = false; await action(); } catch (Exception ex) { MessageBox.Show(MarketplaceConnectionStore.Redact(ex.Message), "Hepsiburada", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { button.IsEnabled = true; } }; return button; }
}
