using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public sealed record MarketplaceConnectionListRow(MarketplaceConnection Connection)
{
    public string ChannelName => MarketplaceConnectionCatalog.Get(Connection.Channel).Name;
    public string DisplayName => Connection.DisplayName;
    public string ShopId => Connection.ShopId;
    public string EnabledText => Connection.Enabled ? "Etkin" : "Devre dışı";
    public string StatusText => MarketplaceStatusText.ToTurkish(Connection.Status);
    public string LastError => MarketplaceConnectionStore.Redact(Connection.LastError);
}

public static class MarketplaceConnectionsPanel
{
    public static FrameworkElement Create(string? dataDirectory = null, Action<string>? navigate = null, Action? connectionsChanged = null)
    {
        var store = new MarketplaceConnectionStore(dataDirectory);
        var rows = new ObservableCollection<MarketplaceConnectionListRow>(VisibleConnections(store));
        var grid = new DataGrid { Name = "MarketplaceConnectionList", ItemsSource = rows, AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, MinHeight = 360, CanUserAddRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Pazaryeri", Binding = new System.Windows.Data.Binding("ChannelName"), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Mağaza", Binding = new System.Windows.Data.Binding("DisplayName"), Width = 190 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Mağaza kimliği", Binding = new System.Windows.Data.Binding("ShopId"), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Kullanım", Binding = new System.Windows.Data.Binding("EnabledText"), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Bağlantı durumu", Binding = new System.Windows.Data.Binding("StatusText"), Width = 155 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Son hata", Binding = new System.Windows.Data.Binding("LastError"), Width = 260 });

        var channel = new ComboBox { Name = "MarketplaceNewChannel", ItemsSource = MarketplaceConnectionCatalog.All, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = "trendyol", MinWidth = 220 };
        var shop = new TextBox { Name = "MarketplaceShopId", MinWidth = 220 };
        var display = new TextBox { Name = "MarketplaceDisplayName", MinWidth = 220 };
        var enabled = new CheckBox { Content = "Mağaza etkin", IsChecked = true, Margin = new Thickness(4, 10, 4, 4) };
        var selectedId = "";
        var editorTitle = new TextBlock { Text = "Yeni mağaza ekle", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 2, 4, 8) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(4, 8, 4, 8) };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(4, 8, 4, 8) };
        var editor = new Border { Name = "MarketplaceStoreEditor", Visibility = Visibility.Collapsed, Padding = new Thickness(16), BorderBrush = new SolidColorBrush(Color.FromRgb(210, 222, 228)), BorderThickness = new Thickness(1), Background = Brushes.White };
        var health = new ApiHealthStore(dataDirectory);
        foreach (var row in rows) health.EnsureConnection(row.Connection.Channel, row.Connection.ShopId, row.Connection.Status, row.Connection.LastError);
        // AllowAutoRedirect=false: this client sends credential-bearing headers
        // (x-api-key/Bearer via EtsyHttp.AddHeaders) that HttpClientHandler does not
        // strip on redirect the way it strips Authorization - the default
        // auto-follow would resend those secrets to whatever host a malicious or
        // compromised 3xx response names. A 3xx now surfaces directly as a non-2xx
        // status, which the connector's own IsSuccessStatusCode check already
        // treats as a failed test, without ever issuing the second request.
        var capture = new ApiHealthCaptureHandler { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } };
        var http = new HttpClient(capture) { Timeout = TimeSpan.FromSeconds(30) };

        void Show(MarketplaceConnection? item)
        {
            selectedId = item?.Id ?? "";
            editor.Visibility = Visibility.Visible;
            editorTitle.Text = item is null ? "Yeni mağaza ekle" : "Mağaza ayarları";
            if (item is null)
            {
                channel.IsEnabled = true; channel.SelectedValue = "trendyol"; shop.Text = ""; display.Text = ""; enabled.IsChecked = true;
                status.Text = "Pazaryerini seçin, mağaza kimliğini ve görünen adı girin.";
            }
            else
            {
                channel.SelectedValue = item.Channel; channel.IsEnabled = false; shop.Text = item.ShopId; display.Text = item.DisplayName; enabled.IsChecked = item.Enabled;
                status.Text = $"Durum: {MarketplaceStatusText.ToTurkish(item.Status)}\nSon kontrol: {item.LastTestUtc?.ToLocalTime().ToString("g") ?? "Henüz yapılmadı"}" +
                    (string.IsNullOrWhiteSpace(item.LastError) ? "" : "\n" + MarketplaceConnectionStore.Redact(item.LastError));
            }
        }
        void Reload()
        {
            rows.Clear(); foreach (var item in VisibleConnections(store)) rows.Add(item);
            if (selectedId.Length > 0) grid.SelectedItem = rows.FirstOrDefault(x => x.Connection.Id == selectedId);
            connectionsChanged?.Invoke();
        }
        grid.SelectionChanged += (_, _) => { if (grid.SelectedItem is MarketplaceConnectionListRow row) Show(row.Connection); };

        var form = new StackPanel { Margin = new Thickness(12) };
        form.Children.Add(editorTitle);
        AddLabel(form, "Pazaryeri", channel); AddLabel(form, "Mağaza / satıcı kimliği", shop); AddLabel(form, "Ekranda görünecek mağaza adı", display); form.Children.Add(enabled);
        form.Children.Add(new TextBlock { Text = "API anahtarları mağaza kaydından sonra güvenli bağlantı ekranında girilir ve Windows kullanıcı hesabında şifreli saklanır.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(4, 10, 4, 4) });
        form.Children.Add(status);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        var save = Button("Kaydet", () =>
        {
            var saved = store.Save(channel.SelectedValue?.ToString() ?? "", shop.Text, display.Text, enabled.IsChecked == true, selectedId);
            selectedId = saved.Id; result.Text = "Mağaza kaydedildi. Şimdi API bilgilerini girip bağlantıyı kontrol edebilirsiniz."; Reload(); grid.SelectedItem = rows.First(x => x.Connection.Id == saved.Id); Show(saved);
        });
        save.Name = "MarketplaceSaveStore"; actions.Children.Add(save);
        actions.Children.Add(Button("API bilgilerini gir", () =>
        {
            var item = RequireSelected();
            OpenCredentialSettings(item, dataDirectory, navigate);
        }));
        actions.Children.Add(Button("Mağazayı aç", () =>
        {
            var item = RequireSelected(); navigate?.Invoke(item.Channel);
        }));
        actions.Children.Add(Button("Devre dışı bırak", () =>
        {
            if (string.IsNullOrWhiteSpace(selectedId)) throw new InvalidOperationException("Önce kayıtlı mağaza seçin.");
            store.Deactivate(selectedId); Reload(); result.Text = "Mağaza devre dışı bırakıldı. Ürün bağlantıları ve geçmiş korundu.";
        }));
        actions.Children.Add(AsyncButton("Salt okunur bağlantı testi", async () =>
        {
            var item = RequireSelected();
            if (health.ShouldDefer(item.Channel, item.ShopId, DateTimeOffset.UtcNow))
            {
                var deferred = health.Get(item.Channel, item.ShopId);
                result.Text = $"Bağlantı testi ertelendi: rate-limit/backoff etkin ({deferred?.BackoffSummary}).";
                return;
            }
            if (!item.Enabled) throw new InvalidOperationException("DISABLED: Devre dışı mağaza için bağlantı testi çalıştırılamaz.");
            var testedRevision = item.Revision;
            result.Text = "Salt okunur bağlantı testi çalışıyor...";
            capture.Reset();
            try
            {
                var message = await ProbeAsync(item, http, dataDirectory); health.Observe(item.Channel, item.ShopId, capture.LastObservation ?? new ApiHealthObservation { State = "HEALTHY", AuthStatus = "VALID" });
                var outcome = store.RecordTest(item.Id, testedRevision, true);
                result.Text = outcome switch { ConnectionTestApplyResult.Applied => "Bağlantı testi başarılı: " + message, ConnectionTestApplyResult.Disabled => "Bağlantı testi tamamlandı ancak mağaza bu sırada devre dışı bırakıldı; sonuç uygulanmadı.", ConnectionTestApplyResult.Stale => "Bağlantı testi tamamlandı ancak mağaza ayarları bu sırada değişti; sonuç güncelliğini yitirdiği için uygulanmadı.", _ => "Bağlantı testi tamamlandı ancak mağaza artık bulunamıyor." };
                Reload();
            }
            catch (Exception ex)
            {
                health.Observe(item.Channel, item.ShopId, capture.LastObservation ?? ApiHealthClassifier.FromException(ex));
                var outcome = store.RecordTest(item.Id, testedRevision, false, ex.Message);
                result.Text = outcome switch { ConnectionTestApplyResult.Applied => "Bağlantı testi: " + MarketplaceConnectionStore.Redact(ex.Message), ConnectionTestApplyResult.Disabled => "Bağlantı testi başarısız oldu ancak mağaza bu sırada devre dışı bırakıldı; sonuç uygulanmadı.", ConnectionTestApplyResult.Stale => "Bağlantı testi başarısız oldu ancak mağaza ayarları bu sırada değişti; sonuç güncelliğini yitirdiği için uygulanmadı.", _ => "Bağlantı testi tamamlandı ancak mağaza artık bulunamıyor." };
                Reload();
            }
        }));
        form.Children.Add(actions);
        form.Children.Add(result);
        editor.Child = form;

        MarketplaceConnection RequireSelected() => selectedId.Length == 0
            ? throw new InvalidOperationException("Önce kayıtlı mağaza seçin veya yeni mağazayı kaydedin.")
            : store.Get(selectedId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");

        var add = Button("+ Yeni mağaza ekle", () => { grid.SelectedItem = null; Show(null); });
        add.Name = "MarketplaceAddStore";
        var listPanel = new DockPanel();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        toolbar.Children.Add(add);
        toolbar.Children.Add(new TextBlock { Text = "Kayıtlı mağazayı seçerek ayarlarını ve bağlantısını yönetin.", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.SlateGray, Margin = new Thickness(10, 0, 0, 0) });
        DockPanel.SetDock(toolbar, Dock.Top); listPanel.Children.Add(toolbar); listPanel.Children.Add(grid);

        var layout = new Grid { Margin = new Thickness(12) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        layout.Children.Add(listPanel);
        var formScroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(formScroll, 1); layout.Children.Add(formScroll);
        return layout;
    }

    static IReadOnlyList<MarketplaceConnectionListRow> VisibleConnections(MarketplaceConnectionStore store) => store.List()
        .Where(connection => !IsUnusedSeededDefault(connection))
        .Select(connection => new MarketplaceConnectionListRow(connection))
        .ToArray();

    static bool IsUnusedSeededDefault(MarketplaceConnection connection) =>
        connection.Id.Equals(connection.Channel + ":default", StringComparison.OrdinalIgnoreCase) &&
        connection.ShopId.Equals("default", StringComparison.OrdinalIgnoreCase) &&
        connection.Status.Equals("NOT_CONFIGURED", StringComparison.OrdinalIgnoreCase) &&
        !connection.LastTestUtc.HasValue && string.IsNullOrWhiteSpace(connection.LastError);

    static void OpenCredentialSettings(MarketplaceConnection connection, string? dataDirectory, Action<string>? navigate)
    {
        FrameworkElement? content = connection.Channel switch
        {
            "trendyol" => new TrendyolWorkspacePanel(connection.Id, dataDirectory),
            "etsy" => new EtsyWorkspacePanel(connection.Id, dataDirectory),
            _ => null
        };
        if (content is null) { navigate?.Invoke(connection.Channel); return; }
        if (content is TrendyolWorkspacePanel trendyol) trendyol.ShowSettings();
        if (content is EtsyWorkspacePanel etsy) etsy.ShowSettings();
        var window = new Window { Title = connection.DisplayName + " — API bağlantısı", Content = content, Width = 1180, Height = 780, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(item => item.IsActive);
        if (owner is not null) window.Owner = owner;
        window.ShowDialog();
    }

    public static T? LoadCredentialsForProbe<T>(string? dataDirectory, MarketplaceConnection item)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        var stored = new MarketplaceConnectionStore(dataDirectory).Get(item.Id)
            ?? throw new InvalidOperationException("NOT_CONFIGURED: Mağaza bağlantısı bulunamadı.");
        if (!string.Equals(stored.Channel, item.Channel, StringComparison.Ordinal) ||
            !string.Equals(stored.ShopId, item.ShopId, StringComparison.Ordinal))
            throw new InvalidOperationException("WRONG_ACCOUNT: Mağaza bağlantı kimliği değişti.");
        return new MarketplaceCredentialVault(dataDirectory).Load<T>(stored.Id, stored.Channel, stored.ShopId);
    }

    static async Task<string> ProbeAsync(MarketplaceConnection item, HttpClient http, string? dataDirectory)
    {
        switch (item.Channel)
        {
            case "etsy":
                var etsy = LoadCredentialsForProbe<EtsyCredentials>(dataDirectory, item) ?? throw new InvalidOperationException("NOT_CONFIGURED: Etsy şifreli bağlantısı bulunamadı.");
                return "Etsy mağazası: " + await new EtsyConnector(http).TestAsync(etsy);
            case "trendyol":
                var trendyol = LoadCredentialsForProbe<TrendyolSettings>(dataDirectory, item) ?? throw new InvalidOperationException("NOT_CONFIGURED: Trendyol şifreli bağlantısı bulunamadı.");
                await new TrendyolConnection().TestReadOnlyAsync(trendyol);
                return "Trendyol satıcı erişimi doğrulandı.";
            case "ebay":
                var ebay = new EbaySettingsStore().Load() ?? throw new InvalidOperationException("NOT_CONFIGURED: eBay şifreli ayarı bulunamadı.");
                if (ebay.Tokens is null) throw new InvalidOperationException("NOT_CONFIGURED: eBay OAuth onayı tamamlanmamış.");
                return (await new EbayConnection(http).VerifyAsync(ebay.Settings, ebay.Tokens)) ? "eBay satıcı yetkisi doğrulandı." : "eBay satıcı yetkisi eksik.";
            case "ozon":
                var ozon = new OzonSettingsStore().Load() ?? throw new InvalidOperationException("NOT_CONFIGURED: Ozon şifreli ayarı bulunamadı.");
                return $"Ozon ürün okuma yetkisi doğrulandı ({await new OzonConnection(http).ReadProductCountAsync(ozon)} ürün).";
            default: throw new InvalidOperationException("LIVE_API_BLOCKED: Bu kanal için doğrulanmış salt okunur API bağlantısı yapılandırılmadı.");
        }
    }

    static void AddLabel(Panel panel, string label, UIElement control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(4, 7, 4, 0) }); panel.Children.Add(control); }
    static Button Button(string text, Action action) { var button = new Button { Content = text, Margin = new Thickness(3) }; button.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Bağlantı yönetimi", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return button; }
    static Button AsyncButton(string text, Func<Task> action) { var button = new Button { Content = text, Margin = new Thickness(3) }; button.Click += async (_, _) => { try { button.IsEnabled = false; await action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Bağlantı yönetimi", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { button.IsEnabled = true; } }; return button; }
}
