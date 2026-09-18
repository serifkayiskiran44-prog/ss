using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace TrMarketplaceHubDesktop;

public sealed record MarketplaceShopProductRules(
    bool ManageContent = false, bool ManagePrice = false, bool ManageStock = false,
    string DefaultCategoryId = "", string DefaultBrandId = "", string DefaultTemplateId = "",
    string DefaultShippingId = "", string ContentSource = "");

public sealed record MarketplaceShopOrderRules(bool Enabled = false, bool AutoAcknowledge = false, string StockLocationId = "online");
public sealed record MarketplaceShopSyncRules(bool ProductsEnabled = false, bool OrdersEnabled = false, int IntervalMinutes = 60);

public sealed record MarketplaceShopSettings(
    string ConnectionId, bool Active, MarketplaceShopProductRules ProductRules,
    MarketplaceShopOrderRules OrderRules, MarketplaceShopSyncRules Sync,
    long Revision, DateTime UpdatedUtc);

public sealed record MarketplaceShopProductRulesPatch(
    bool? ManageContent = null, bool? ManagePrice = null, bool? ManageStock = null,
    string? DefaultCategoryId = null, string? DefaultBrandId = null, string? DefaultTemplateId = null,
    string? DefaultShippingId = null, string? ContentSource = null);
public sealed record MarketplaceShopOrderRulesPatch(bool? Enabled = null, bool? AutoAcknowledge = null, string? StockLocationId = null);
public sealed record MarketplaceShopSyncRulesPatch(bool? ProductsEnabled = null, bool? OrdersEnabled = null, int? IntervalMinutes = null);
public sealed record MarketplaceShopSettingsPatch(
    bool? Active = null, MarketplaceShopProductRulesPatch? ProductRules = null,
    MarketplaceShopOrderRulesPatch? OrderRules = null, MarketplaceShopSyncRulesPatch? Sync = null);

public sealed class MarketplaceShopSettingsStore
{
    readonly MarketplaceConnectionStore connections;
    readonly MarketplaceAdapterRegistry registry;
    readonly string connectionString;

    public MarketplaceShopSettingsStore(string? directory = null, MarketplaceAdapterRegistry? registry = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connections = new(directory);
        this.registry = registry ?? MarketplaceAdapterRegistry.Default;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15 }.ToString();
        using var database = Open(); using var command = database.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS MarketplaceShopSettings(ConnectionId TEXT PRIMARY KEY,Revision INTEGER NOT NULL,Json TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    public MarketplaceShopSettings Load(string connectionId)
    {
        var connection = RequireOperational(connectionId);
        using var database = Open(); using var command = database.CreateCommand();
        command.CommandText = "SELECT Revision,Json FROM MarketplaceShopSettings WHERE ConnectionId=$connection";
        command.Parameters.AddWithValue("$connection", connection.Id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return Default(connection.Id);
        try
        {
            var state = JsonSerializer.Deserialize<MarketplaceShopSettings>(reader.GetString(1)) ?? throw new InvalidDataException();
            if (state.ConnectionId != connection.Id || state.Revision != reader.GetInt64(0) || state.Revision < 1)
                throw new InvalidDataException();
            Validate(state);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
        {
            throw new InvalidDataException("Mağaza ayarları bozuk (REVIEW_REQUIRED).", ex);
        }
    }

    public MarketplaceShopSettings Save(MarketplaceShopSettings proposed, long expectedRevision, long expectedConnectionRevision)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        if (expectedRevision < 0 || expectedConnectionRevision < 0) throw new ArgumentOutOfRangeException();
        Validate(proposed);
        using var database = Open(); using var transaction = database.BeginTransaction(deferred: false);
        MarketplaceConnection current;
        using (var readConnection = database.CreateCommand())
        {
            readConnection.Transaction = transaction;
            readConnection.CommandText = "SELECT Channel,ShopId,DisplayName,Enabled,Status,LastTestUtc,LastError,Revision FROM MarketplaceConnections WHERE Id=$id";
            readConnection.Parameters.AddWithValue("$id", proposed.ConnectionId);
            using var reader = readConnection.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(3) != 1 || reader.GetInt64(7) != expectedConnectionRevision)
                throw new InvalidOperationException("Mağaza hesabı değişti veya devre dışı; ayarları yenileyin.");
            DateTime? lastTestUtc = null;
            if (!reader.IsDBNull(5))
            {
                if (!DateTime.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    throw new InvalidOperationException("Mağaza bağlantısı bozuk; ayarlar kaydedilemez.");
                lastTestUtc = parsed;
            }
            current = new(proposed.ConnectionId, reader.GetString(0), reader.GetString(1), reader.GetString(2), true, reader.GetString(4), lastTestUtc, reader.GetString(6), reader.GetInt64(7));
        }
        if (!MarketplaceOperationalAccounts.IsEligible(current, connections))
            throw new InvalidOperationException("Operasyonel olmayan mağaza ayarları değiştirilemez.");
        ValidateCapabilities(proposed, registry.Get(current.Channel).Capabilities);
        long actualRevision;
        using (var revision = database.CreateCommand())
        {
            revision.Transaction = transaction;
            revision.CommandText = "SELECT Revision FROM MarketplaceShopSettings WHERE ConnectionId=$connection";
            revision.Parameters.AddWithValue("$connection", proposed.ConnectionId);
            var value = revision.ExecuteScalar(); actualRevision = value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        if (actualRevision != expectedRevision) throw new InvalidOperationException("Mağaza ayarları değişti; ekranı yenileyin.");
        var saved = proposed with { Revision = actualRevision + 1, UpdatedUtc = DateTime.UtcNow };
        using (var write = database.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = "INSERT INTO MarketplaceShopSettings(ConnectionId,Revision,Json) VALUES($connection,$revision,$json) ON CONFLICT(ConnectionId) DO UPDATE SET Revision=excluded.Revision,Json=excluded.Json";
            write.Parameters.AddWithValue("$connection", saved.ConnectionId); write.Parameters.AddWithValue("$revision", saved.Revision);
            write.Parameters.AddWithValue("$json", JsonSerializer.Serialize(saved)); write.ExecuteNonQuery();
        }
        transaction.Commit(); return saved;
    }

    public MarketplaceShopSettings SavePatch(string connectionId, MarketplaceShopSettingsPatch patch, long expectedRevision, long expectedConnectionRevision)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var current = Load(connectionId);
        if (current.Revision != expectedRevision) throw new InvalidOperationException("Mağaza ayarları değişti; ekranı yenileyin.");
        var productPatch = patch.ProductRules;
        var orderPatch = patch.OrderRules;
        var syncPatch = patch.Sync;
        var next = current with
        {
            Active = patch.Active ?? current.Active,
            ProductRules = productPatch is null ? current.ProductRules : current.ProductRules with
            {
                ManageContent = productPatch.ManageContent ?? current.ProductRules.ManageContent,
                ManagePrice = productPatch.ManagePrice ?? current.ProductRules.ManagePrice,
                ManageStock = productPatch.ManageStock ?? current.ProductRules.ManageStock,
                DefaultCategoryId = PreserveBlank(productPatch.DefaultCategoryId, current.ProductRules.DefaultCategoryId),
                DefaultBrandId = PreserveBlank(productPatch.DefaultBrandId, current.ProductRules.DefaultBrandId),
                DefaultTemplateId = PreserveBlank(productPatch.DefaultTemplateId, current.ProductRules.DefaultTemplateId),
                DefaultShippingId = PreserveBlank(productPatch.DefaultShippingId, current.ProductRules.DefaultShippingId),
                ContentSource = PreserveBlank(productPatch.ContentSource, current.ProductRules.ContentSource)
            },
            OrderRules = orderPatch is null ? current.OrderRules : current.OrderRules with
            {
                Enabled = orderPatch.Enabled ?? current.OrderRules.Enabled,
                AutoAcknowledge = orderPatch.AutoAcknowledge ?? current.OrderRules.AutoAcknowledge,
                StockLocationId = PreserveBlank(orderPatch.StockLocationId, current.OrderRules.StockLocationId)
            },
            Sync = syncPatch is null ? current.Sync : current.Sync with
            {
                ProductsEnabled = syncPatch.ProductsEnabled ?? current.Sync.ProductsEnabled,
                OrdersEnabled = syncPatch.OrdersEnabled ?? current.Sync.OrdersEnabled,
                IntervalMinutes = syncPatch.IntervalMinutes ?? current.Sync.IntervalMinutes
            }
        };
        return Save(next, expectedRevision, expectedConnectionRevision);
    }

    MarketplaceConnection RequireOperational(string connectionId)
    {
        MarketplaceConnection? current;
        try { current = connections.Get(connectionId); }
        catch (MarketplaceConnectionCorruptException) { throw new InvalidOperationException("Mağaza bağlantısı bozuk; ayarlar açılamaz."); }
        if (current is null || !MarketplaceOperationalAccounts.IsEligible(current, connections))
            throw new InvalidOperationException("Etkin ve operasyonel mağaza bağlantısı gerekli.");
        return current;
    }

    static void Validate(MarketplaceShopSettings state)
    {
        if (string.IsNullOrWhiteSpace(state.ConnectionId) || state.ConnectionId.Length > 512 || state.Revision < 0)
            throw new InvalidOperationException("Mağaza ayar kimliği geçersiz.");
        if (state.Sync.IntervalMinutes is < 5 or > 10080) throw new InvalidOperationException("Senkron aralığı 5–10080 dakika olmalı.");
        foreach (var value in new[] { state.ProductRules.DefaultCategoryId, state.ProductRules.DefaultBrandId, state.ProductRules.DefaultTemplateId, state.ProductRules.DefaultShippingId, state.ProductRules.ContentSource, state.OrderRules.StockLocationId })
            if (value.Length > 256 || value.Any(char.IsControl)) throw new InvalidOperationException("Mağaza kural değeri geçersiz.");
    }

    static void ValidateCapabilities(MarketplaceShopSettings state, MarketplaceCapabilities capabilities)
    {
        var product = state.ProductRules;
        if (product.ManageContent && !capabilities.Supports(MarketplaceOperation.ContentWrite) ||
            product.ManagePrice && !capabilities.Supports(MarketplaceOperation.PriceWrite) ||
            product.ManageStock && !capabilities.Supports(MarketplaceOperation.StockWrite) ||
            product.DefaultCategoryId.Length > 0 && !SupportsCategory(capabilities) ||
            product.DefaultBrandId.Length > 0 && !capabilities.Supports(MarketplaceOperation.BrandWrite) ||
            product.DefaultTemplateId.Length > 0 && !SupportsTemplate(capabilities) ||
            product.DefaultShippingId.Length > 0 && !SupportsShipping(capabilities) ||
            product.ContentSource.Length > 0 && !capabilities.Supports(MarketplaceOperation.ContentWrite))
            throw new InvalidOperationException("Adapter bu ürün kuralını desteklemiyor.");
        if ((state.OrderRules.Enabled || state.OrderRules.AutoAcknowledge || state.Sync.OrdersEnabled) &&
            !capabilities.Supports(MarketplaceOperation.OrdersRead))
            throw new InvalidOperationException("Adapter sipariş kurallarını desteklemiyor.");
        if (state.Sync.ProductsEnabled && !capabilities.Supports(MarketplaceOperation.ProductsRead))
            throw new InvalidOperationException("Adapter ürün senkronunu desteklemiyor.");
    }

    internal static bool SupportsCategory(MarketplaceCapabilities capabilities) =>
        capabilities.Supports(MarketplaceOperation.CategoryWrite) || capabilities.Supports(MarketplaceOperation.TaxonomyWrite);
    internal static bool SupportsTemplate(MarketplaceCapabilities capabilities) =>
        capabilities.Supports(MarketplaceOperation.DeliveryWrite) || capabilities.Supports(MarketplaceOperation.PropertiesWrite) || capabilities.Supports(MarketplaceOperation.ListingCreate);
    internal static bool SupportsShipping(MarketplaceCapabilities capabilities) =>
        capabilities.Supports(MarketplaceOperation.ShippingWrite) || capabilities.Supports(MarketplaceOperation.DeliveryWrite);

    static MarketplaceShopSettings Default(string connectionId) => new(connectionId, true, new(), new(), new(), 0, DateTime.MinValue);
    static string PreserveBlank(string? proposed, string current) => string.IsNullOrWhiteSpace(proposed) ? current : proposed.Trim();
    SqliteConnection Open() { var database = new SqliteConnection(connectionString); database.Open(); return database; }
}

public sealed class MarketplaceShopSettingsPanel : UserControl
{
    readonly string connectionId;
    readonly MarketplaceConnectionStore connections;
    readonly MarketplaceShopSettingsStore store;
    readonly TabControl sections = new() { Name = "MarketplaceShopSettingsSections" };
    readonly CheckBox active = new() { Content = "Bu hesap için mağaza kuralları etkin" };
    readonly CheckBox content = new() { Content = "İçeriği varsayılan olarak yönet" };
    readonly CheckBox price = new() { Content = "Fiyatı varsayılan olarak yönet" };
    readonly CheckBox stock = new() { Content = "Stoku varsayılan olarak yönet" };
    readonly TextBox category = new() { Name = "MarketplaceDefaultCategory" }, brand = new() { Name = "MarketplaceDefaultBrand" },
        template = new() { Name = "MarketplaceDefaultTemplate" }, shipping = new() { Name = "MarketplaceDefaultShipping" },
        source = new() { Name = "MarketplaceContentSource" };
    readonly CheckBox orders = new() { Content = "Sipariş kuralları etkin" }, acknowledge = new() { Content = "Siparişi otomatik kabul et" };
    readonly TextBox location = new();
    readonly CheckBox syncProducts = new() { Content = "Ürün okumayı zamanla" }, syncOrders = new() { Content = "Sipariş okumayı zamanla" };
    readonly TextBox interval = new();
    readonly TextBlock status = new() { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap };
    MarketplaceShopSettings state;
    MarketplaceConnection connection;
    readonly MarketplaceCapabilities capabilities;

    public MarketplaceShopSettingsPanel(string connectionId, string? directory = null, MarketplaceAdapterRegistry? registry = null)
    {
        this.connectionId = connectionId; connections = new(directory); registry ??= MarketplaceAdapterRegistry.Default; store = new(directory, registry);
        connection = connections.Get(connectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
        capabilities = registry.Get(connection.Channel).Capabilities;
        state = store.Load(connectionId);
        var root = new DockPanel { Margin = new Thickness(6) };
        sections.Items.Add(new TabItem { Header = "Active", Content = Section(active) });
        sections.Items.Add(new TabItem { Header = "Connection", Content = Section(new TextBlock { Text = $"{connection.DisplayName}\n{connection.Channel} / {connection.ShopId}\nDurum: {connection.Status}", TextWrapping = TextWrapping.Wrap }) });
        sections.Items.Add(new TabItem { Header = "Product rules", Content = ProductSection() });
        sections.Items.Add(new TabItem { Header = "Order rules", Content = OrderSection(), IsEnabled = capabilities.Supports(MarketplaceOperation.OrdersRead) });
        sections.Items.Add(new TabItem { Header = "Sync", Content = SyncSection() });
        root.Children.Add(sections);
        var footer = new WrapPanel(); var save = new Button { Name = "MarketplaceSaveShopSettings", Content = "Hesap ayarlarını kaydet", Margin = new Thickness(4), Padding = new Thickness(10, 5, 10, 5) };
        save.Click += (_, _) => Save(); footer.Children.Add(save); footer.Children.Add(status); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Content = root; ApplyCapabilities(); Fill();
    }

    void Save()
    {
        try
        {
            if (!int.TryParse(interval.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var minutes)) throw new InvalidOperationException("Senkron aralığı sayı olmalı.");
            state = store.SavePatch(connectionId, new(
                Active: active.IsChecked == true,
                ProductRules: new(content.IsChecked == true, price.IsChecked == true, stock.IsChecked == true,
                    category.Text, brand.Text, template.Text, shipping.Text, source.Text),
                OrderRules: new(orders.IsChecked == true, acknowledge.IsChecked == true, location.Text),
                Sync: new(syncProducts.IsChecked == true, syncOrders.IsChecked == true, minutes)),
                state.Revision, connection.Revision);
            connection = connections.Get(connectionId) ?? throw new InvalidOperationException("Mağaza bağlantısı bulunamadı.");
            status.Text = $"Ayarlar kaydedildi · revision {state.Revision}"; Fill();
        }
        catch (Exception ex) { status.Text = MarketplaceConnectionStore.Redact(ex.Message); }
    }

    void Fill()
    {
        active.IsChecked = state.Active; content.IsChecked = state.ProductRules.ManageContent; price.IsChecked = state.ProductRules.ManagePrice; stock.IsChecked = state.ProductRules.ManageStock;
        category.Text = state.ProductRules.DefaultCategoryId; brand.Text = state.ProductRules.DefaultBrandId; template.Text = state.ProductRules.DefaultTemplateId; shipping.Text = state.ProductRules.DefaultShippingId; source.Text = state.ProductRules.ContentSource;
        orders.IsChecked = state.OrderRules.Enabled; acknowledge.IsChecked = state.OrderRules.AutoAcknowledge; location.Text = state.OrderRules.StockLocationId;
        syncProducts.IsChecked = state.Sync.ProductsEnabled; syncOrders.IsChecked = state.Sync.OrdersEnabled; interval.Text = state.Sync.IntervalMinutes.ToString(CultureInfo.CurrentCulture);
    }

    UIElement ProductSection()
    {
        var panel = new StackPanel(); panel.Children.Add(content); panel.Children.Add(price); panel.Children.Add(stock);
        Field(panel, "Varsayılan kategori/taksonomi", category); Field(panel, "Varsayılan marka", brand); Field(panel, "Varsayılan şablon", template); Field(panel, "Varsayılan kargo", shipping); Field(panel, "İçerik kaynağı", source); return Section(panel);
    }
    UIElement OrderSection() { var panel = new StackPanel(); panel.Children.Add(orders); panel.Children.Add(acknowledge); Field(panel, "Stok konumu", location); return Section(panel); }
    UIElement SyncSection() { var panel = new StackPanel(); panel.Children.Add(syncProducts); panel.Children.Add(syncOrders); Field(panel, "Aralık (dakika)", interval); return Section(panel); }
    void ApplyCapabilities()
    {
        content.IsEnabled = source.IsEnabled = capabilities.Supports(MarketplaceOperation.ContentWrite);
        price.IsEnabled = capabilities.Supports(MarketplaceOperation.PriceWrite);
        stock.IsEnabled = capabilities.Supports(MarketplaceOperation.StockWrite);
        category.IsEnabled = MarketplaceShopSettingsStore.SupportsCategory(capabilities);
        brand.IsEnabled = capabilities.Supports(MarketplaceOperation.BrandWrite);
        template.IsEnabled = MarketplaceShopSettingsStore.SupportsTemplate(capabilities);
        shipping.IsEnabled = MarketplaceShopSettingsStore.SupportsShipping(capabilities);
        orders.IsEnabled = acknowledge.IsEnabled = location.IsEnabled = capabilities.Supports(MarketplaceOperation.OrdersRead);
        syncProducts.IsEnabled = capabilities.Supports(MarketplaceOperation.ProductsRead);
        syncOrders.IsEnabled = capabilities.Supports(MarketplaceOperation.OrdersRead);
        interval.IsEnabled = syncProducts.IsEnabled || syncOrders.IsEnabled;
    }
    static UIElement Section(UIElement child) => new ScrollViewer { Content = new Border { Padding = new Thickness(12), Child = child } };
    static void Field(Panel panel, string label, TextBox input) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 2) }); input.MinWidth = 220; panel.Children.Add(input); }
}
