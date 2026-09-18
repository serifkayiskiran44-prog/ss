using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class MarketplaceConnectionsPanel
{
    public static FrameworkElement Create(string? dataDirectory = null, Action<string>? navigate = null, Action? connectionsChanged = null)
    {
        var store = new MarketplaceConnectionStore(dataDirectory);
        string migrationNotice;
        try
        {
            var outcomes = new MarketplaceConnectionMigration(dataDirectory).ImportLegacy()
                .Where(x => x.State is MarketplaceConnectionMigrationState.Imported or MarketplaceConnectionMigrationState.Failed)
                .Select(x => $"{MarketplaceConnectionCatalog.Get(x.Channel).Name}: {x.Detail}");
            migrationNotice = string.Join("\n", outcomes);
        }
        catch (Exception error) { migrationNotice = "Eski bağlantı içe aktarılamadı: " + MarketplaceConnectionStore.Redact(error.Message); }
        var rows = new ObservableCollection<MarketplaceConnection>(store.List());
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, MinHeight = 260 };
        grid.Columns.Add(new DataGridTextColumn { Header = "Kanal", Binding = new System.Windows.Data.Binding("Channel"), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Mağaza", Binding = new System.Windows.Data.Binding("DisplayName"), Width = 190 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Mağaza kimliği", Binding = new System.Windows.Data.Binding("ShopId"), Width = 130 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Etkin", Binding = new System.Windows.Data.Binding("Enabled"), Width = 55 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Durum", Binding = new System.Windows.Data.Binding("Status"), Width = 155 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Son hata", Binding = new System.Windows.Data.Binding("LastError"), Width = 260 });

        var channel = new ComboBox { ItemsSource = MarketplaceConnectionCatalog.All, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = "etsy", Width = 180 };
        var shop = new TextBox { Text = "default", Width = 180 };
        var display = new TextBox { Width = 220 };
        var enabled = new CheckBox { Content = "Bağlantı etkin", IsChecked = true };
        var selectedId = "";
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(4, 8, 4, 8) };
        var capability = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(4, 8, 4, 8) };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(4, 8, 4, 8) };
        result.Text = migrationNotice;
        var accountTabs = new TabControl { Name = "MarketplaceAccountTabs", Margin = new Thickness(0, 0, 0, 10), MinHeight = 175 };
        var health = new ApiHealthStore(dataDirectory);
        foreach (var connection in rows) health.EnsureConnection(connection.Channel, connection.ShopId, connection.Status, connection.LastError);
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
            if (item is null) { shop.Text = "default"; display.Text = ""; enabled.IsChecked = true; status.Text = "Yeni mağaza kaydı."; }
            else { channel.SelectedValue = item.Channel; shop.Text = item.ShopId; display.Text = item.DisplayName; enabled.IsChecked = item.Enabled; status.Text = $"Durum: {item.Status}\nSon test: {item.LastTestUtc?.ToLocalTime().ToString("g") ?? "yok"}\n{item.LastError}"; }
            UpdateCapabilities();
        }
        void UpdateCapabilities()
        {
            var definition = channel.SelectedItem as MarketplaceConnectionDefinition;
            if (definition is null) { capability.Text = ""; return; }
            var labels = new[]
            {
                (MarketplaceOperation.ProductsRead, "Ürün okuma"), (MarketplaceOperation.OrdersRead, "Sipariş okuma"),
                (MarketplaceOperation.StockWrite, "Stok yazma"), (MarketplaceOperation.PriceWrite, "Fiyat yazma"),
                (MarketplaceOperation.Shipment, "Kargo")
            };
            capability.Text = "Yetenekler: " + string.Join(" · ", labels.Select(x => definition.Capabilities.Supports(x.Item1) ? x.Item2 : x.Item2 + " yok"));
            if (definition.LiveApiBlocked) capability.Text += "\nCanlı API: LIVE_API_BLOCKED / NOT_CONFIGURED";
        }
        void Reload()
        {
            rows.Clear(); foreach (var item in store.List()) rows.Add(item);
            RebuildAccountTabs();
            if (selectedId.Length > 0) grid.SelectedItem = rows.FirstOrDefault(x => x.Id == selectedId);
            connectionsChanged?.Invoke();
        }
        void RebuildAccountTabs()
        {
            accountTabs.Items.Clear();
            var index = 0;
            foreach (var account in rows)
            {
                var tab = BuildAccountTab(account, index++);
                accountTabs.Items.Add(tab);
            }
        }
        channel.SelectionChanged += (_, _) => UpdateCapabilities();
        grid.SelectionChanged += (_, _) => Show(grid.SelectedItem as MarketplaceConnection);
        accountTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != accountTabs || accountTabs.SelectedItem is not TabItem tab || tab.Tag is not string id) return;
            var account = rows.FirstOrDefault(x => x.Id == id);
            if (account is not null) { grid.SelectedItem = account; Show(account); }
        };
        channel.SelectedIndex = 0; UpdateCapabilities(); Show(null); RebuildAccountTabs();

        var form = new StackPanel { Margin = new Thickness(12) };
        form.Children.Add(new TextBlock { Text = "Hesaplar", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 4, 4, 10) });
        form.Children.Add(accountTabs);
        form.Children.Add(new TextBlock { Text = "Seçili mağaza bağlantısı", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 4, 4, 10) });
        AddLabel(form, "Kanal", channel); AddLabel(form, "Mağaza kimliği", shop); AddLabel(form, "Görünen ad", display); form.Children.Add(enabled);
        form.Children.Add(capability); form.Children.Add(status);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        actions.Children.Add(Button("Yeni", () => Show(null)));
        actions.Children.Add(Button("Kaydet", () =>
        {
            var saved = store.Save(channel.SelectedValue?.ToString() ?? "", shop.Text, display.Text, enabled.IsChecked == true, selectedId);
            selectedId = saved.Id; result.Text = "Mağaza metadatası kaydedildi. Gizli bilgiler bu ekrana veya SQLite'a yazılmaz."; Reload(); grid.SelectedItem = rows.First(x => x.Id == saved.Id); Show(saved);
        }));
        actions.Children.Add(Button("Etkinliği değiştir", () =>
        {
            if (string.IsNullOrWhiteSpace(selectedId)) throw new InvalidOperationException("Önce kayıtlı mağaza seçin.");
            var next = !(grid.SelectedItem as MarketplaceConnection)?.Enabled ?? true; store.SetEnabled(selectedId, next); Reload(); result.Text = next ? "Bağlantı etkinleştirildi." : "Bağlantı pasife alındı.";
        }));
        actions.Children.Add(Button("Devre dışı bırak (geçmişi koru)", () =>
        {
            if (string.IsNullOrWhiteSpace(selectedId)) throw new InvalidOperationException("Önce kayıtlı mağaza seçin.");
            store.Deactivate(selectedId); Reload(); result.Text = "Mağaza devre dışı bırakıldı; metadata ve geçmiş sağlık kaydı korundu.";
        }));
        actions.Children.Add(AsyncButton("Salt okunur bağlantı testi", async () =>
        {
            if (grid.SelectedItem is not MarketplaceConnection item) throw new InvalidOperationException("Önce kayıtlı mağaza seçin.");
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
        form.Children.Add(new TextBlock { Text = "Kanal kartından ilgili ürün/sipariş/sync ekranına geçiş:", Margin = new Thickness(4, 12, 4, 4), FontWeight = FontWeights.SemiBold });
        var links = new WrapPanel();
        foreach (var definition in MarketplaceConnectionCatalog.All.Where(x => x.RouteKey is not null))
        {
            var target = definition.RouteKey!;
            links.Children.Add(Button(definition.Name, () => navigate?.Invoke(target)));
            links.Children.Add(Button("API belgeleri", () => Process.Start(new ProcessStartInfo(definition.DocumentationUrl) { UseShellExecute = true })));
        }
        form.Children.Add(links);
        var layout = new Grid { Margin = new Thickness(12) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });
        layout.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(formScroll, 1); layout.Children.Add(formScroll);
        return layout;
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

    static TabItem BuildAccountTab(MarketplaceConnection account, int index)
    {
        var definition = MarketplaceConnectionCatalog.Get(account.Channel);
        var body = new StackPanel { Margin = new Thickness(8) };
        body.Children.Add(new CheckBox
        {
            Name = "MarketplaceAccountActive_" + index,
            Content = "Active / Etkin",
            IsChecked = account.Active,
            IsHitTestVisible = false,
            Focusable = false
        });
        body.Children.Add(new TextBlock { Text = $"{definition.Name} · {account.ShopId} · {account.Status}", Margin = new Thickness(2, 5, 2, 8), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(SettingsSection("Bağlantı", "Hesap kimliği ve şifreli erişim bilgileri bu bağlantıya özeldir."));
        body.Children.Add(CapabilitySection("Ürün kuralları", definition, index, new[]
        {
            (MarketplaceOperation.ProductsRead, "Ürün okuma"),
            (MarketplaceOperation.PriceWrite, "Fiyat yazma"),
            (MarketplaceOperation.StockWrite, "Stok yazma")
        }));
        body.Children.Add(CapabilitySection("Sipariş kuralları", definition, index, new[] { (MarketplaceOperation.OrdersRead, "Sipariş okuma") }));
        body.Children.Add(CapabilitySection("Senkronizasyon", definition, index, new[] { (MarketplaceOperation.Shipment, "Kargo") }));
        return new TabItem { Header = account.DisplayName, Tag = account.Id, Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
    }

    static GroupBox SettingsSection(string header, string explanation) => new()
    {
        Header = header,
        Margin = new Thickness(2, 3, 2, 3),
        Padding = new Thickness(6),
        Content = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkSlateGray }
    };

    static GroupBox CapabilitySection(string header, MarketplaceConnectionDefinition definition, int index, IEnumerable<(MarketplaceOperation Operation, string Label)> operations)
    {
        var panel = new WrapPanel();
        foreach (var (operation, label) in operations)
        {
            var supported = definition.Capabilities.Supports(operation);
            panel.Children.Add(new CheckBox
            {
                Name = $"MarketplaceCapability_{operation}_{index}",
                Content = supported ? label : label + " · desteklenmiyor",
                IsChecked = supported,
                IsEnabled = supported,
                IsHitTestVisible = false,
                Focusable = false,
                ToolTip = supported ? "Bu hesap türü tarafından desteklenir." : "Bu kanal bağdaştırıcısı bu özelliği desteklemiyor.",
                Margin = new Thickness(6, 4, 6, 4)
            });
        }
        return new GroupBox { Header = header, Margin = new Thickness(2, 3, 2, 3), Padding = new Thickness(4), Content = panel };
    }

    static void AddLabel(Panel panel, string label, UIElement control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(4, 7, 4, 0) }); panel.Children.Add(control); }
    static Button Button(string text, Action action) { var button = new Button { Content = text, Margin = new Thickness(3) }; button.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Bağlantı yönetimi", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return button; }
    static Button AsyncButton(string text, Func<Task> action) { var button = new Button { Content = text, Margin = new Thickness(3) }; button.Click += async (_, _) => { try { button.IsEnabled = false; await action(); } catch (Exception e) { MessageBox.Show(MarketplaceConnectionStore.Redact(e.Message), "Bağlantı yönetimi", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { button.IsEnabled = true; } }; return button; }
}
