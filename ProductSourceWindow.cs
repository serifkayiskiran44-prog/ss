using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductSourceChoice(ProductSourceKind Kind, string SourceId, string Label);
public sealed record ProductBalanceRow(string LocationId, string LocationName, InventoryLocationKind Kind, int Quantity, long Version);
public sealed record ProductSourceChangePreview(
    string Id, string ProductId, ProductFieldGroup Group, ProductSourceKind Kind, string SourceId,
    long BindingVersion, DateTime ProductUpdatedUtc, string SourceRevision, DateTime CreatedUtc);

public sealed class ProductSourceModel
{
    readonly string? directory;
    readonly CatalogStore catalog;
    readonly ProductSourceBindingStore bindings;
    readonly InventoryLocationStore inventory;
    ProductSourceChangePreview? preview;

    public string ProductId { get; }
    public IReadOnlyList<ProductSourceBinding> Bindings { get; private set; } = Array.Empty<ProductSourceBinding>();
    public IReadOnlyList<ProductSourceChoice> Sources { get; private set; } = Array.Empty<ProductSourceChoice>();
    public IReadOnlyList<ProductBalanceRow> Balances { get; private set; } = Array.Empty<ProductBalanceRow>();
    public ProductSourceChangePreview? CurrentPreview => preview;
    public bool CanApply => preview is not null;

    public ProductSourceModel(string? directory, string productId)
    {
        if (string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("Ürün kimliği gerekli.", nameof(productId));
        this.directory = directory;
        catalog = new(directory);
        if (!catalog.Products().Any(product => product.Id == productId)) throw new InvalidOperationException("Ürün bulunamadı.");
        ProductId = productId;
        bindings = new(directory);
        inventory = new(directory);
        Refresh();
    }

    public void Refresh()
    {
        Bindings = bindings.Get(ProductId);
        Sources = new[] { new ProductSourceChoice(ProductSourceKind.Manual, "", "Elle yönet") }
            .Concat(catalog.Sources().Where(source => source.Enabled).OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(source => new ProductSourceChoice(ProductSourceKind.Xml, source.Id, "XML · " + source.Name))).ToArray();
        var locations = inventory.Locations().ToDictionary(location => location.Id, StringComparer.Ordinal);
        Balances = locations.Values.OrderBy(location => location.Kind).ThenBy(location => location.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(location =>
            {
                var balance = inventory.GetBalance(ProductId, location.Id);
                return new ProductBalanceRow(location.Id, location.Name, location.Kind, balance.Quantity, balance.Version);
            }).ToArray();
    }

    public ProductSourceChangePreview PreviewChange(ProductFieldGroup group, ProductSourceKind kind, string sourceId)
    {
        if (!Enum.IsDefined(group) || !Enum.IsDefined(kind)) throw new ArgumentException("Kaynak grubu veya türü geçersiz.");
        sourceId = sourceId?.Trim() ?? "";
        string sourceRevision = "";
        if (kind == ProductSourceKind.Manual)
        {
            if (sourceId.Length > 0) throw new InvalidOperationException("Elle yönetilen kaynak XML kimliği taşıyamaz.");
        }
        else
        {
            var source = catalog.Sources().SingleOrDefault(item => item.Id == sourceId && item.Enabled)
                ?? throw new InvalidOperationException("XML kaynağı bulunamadı veya devre dışı.");
            sourceRevision = CatalogStore.SourceConfigRevision(source);
        }
        var product = catalog.Products().Single(item => item.Id == ProductId);
        var current = Bindings.SingleOrDefault(binding => binding.Group == group);
        preview = new(Guid.NewGuid().ToString("N"), ProductId, group, kind, kind == ProductSourceKind.Manual ? "" : sourceId,
            current?.Version ?? 0, product.UpdatedUtc, sourceRevision, DateTime.UtcNow);
        return preview;
    }

    public ProductSourceBinding Apply(ProductSourceChangePreview value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (preview is null || value != preview || value.ProductId != ProductId)
            throw new InvalidOperationException("Önce bu ürün için güncel bir kaynak önizlemesi alın.");
        var product = catalog.Products().SingleOrDefault(item => item.Id == ProductId)
            ?? throw new InvalidOperationException("Ürün artık katalogda bulunmuyor.");
        if (product.UpdatedUtc != value.ProductUpdatedUtc)
            throw new InvalidOperationException("Ürün önizlemeden sonra değişti; yeni önizleme alın.");
        if (value.Kind == ProductSourceKind.Xml)
        {
            var source = catalog.Sources().SingleOrDefault(item => item.Id == value.SourceId && item.Enabled)
                ?? throw new InvalidOperationException("XML kaynağı silindi veya devre dışı; yeni önizleme alın.");
            if (CatalogStore.SourceConfigRevision(source) != value.SourceRevision)
                throw new InvalidOperationException("XML kaynağı önizlemeden sonra değişti; yeni önizleme alın.");
        }
        var current = bindings.Get(ProductId).SingleOrDefault(binding => binding.Group == value.Group);
        if ((current?.Version ?? 0) != value.BindingVersion)
            throw new InvalidOperationException("Kaynak seçimi önizlemeden sonra değişti; yeni önizleme alın.");
        var saved = bindings.Save(new(ProductId, value.Group, value.Kind, value.SourceId, current?.Enabled ?? true,
            current?.Version ?? 0, current?.UpdatedUtc ?? default), value.BindingVersion, value.ProductUpdatedUtc, value.SourceRevision);
        preview = null;
        Refresh();
        return saved;
    }
}

public sealed class ProductSourceWindow : Window
{
    readonly ProductSourceModel model;
    readonly ComboBox group = new() { Name = "ProductSourceGroup", MinWidth = 180, ItemsSource = Enum.GetValues<ProductFieldGroup>() };
    readonly ComboBox source = new() { Name = "ProductSourceChoice", MinWidth = 280, DisplayMemberPath = nameof(ProductSourceChoice.Label) };
    readonly Button preview = new() { Name = "ProductSourcePreviewButton", Content = "Kaynak değişikliğini önizle", IsEnabled = false };
    readonly Button apply = new() { Name = "ProductSourceApplyButton", Content = "Kaynak değişikliğini uygula", IsEnabled = false };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(4) };
    readonly DataGrid bindingGrid = new() { IsReadOnly = true, AutoGenerateColumns = false, Height = 180 };
    readonly DataGrid balanceGrid = new() { IsReadOnly = true, AutoGenerateColumns = false, Height = 180 };

    public bool IsApplyEnabled => apply.IsEnabled;

    public ProductSourceWindow(string? directory, string productId)
    {
        model = new(directory, productId);
        Title = "Ürün kaynakları ve stok bakiyeleri";
        Width = 860;
        Height = 650;
        source.ItemsSource = model.Sources;
        bindingGrid.ItemsSource = model.Bindings;
        balanceGrid.ItemsSource = model.Balances;
        Content = Build();
        group.SelectionChanged += (_, _) => UpdatePreviewState();
        source.SelectionChanged += (_, _) => UpdatePreviewState();
        preview.Click += (_, _) => Run(() =>
        {
            if (group.SelectedItem is not ProductFieldGroup field || source.SelectedItem is not ProductSourceChoice choice)
                throw new InvalidOperationException("Alan grubu ve kaynak seçin.");
            var result = model.PreviewChange(field, choice.Kind, choice.SourceId);
            apply.IsEnabled = true;
            status.Text = $"{field}: {choice.Label}. Ürün, kaynak ve kaynak seçimi revizyonları sabitlendi; satış bağlantıları değişmeyecek.";
        });
        apply.Click += (_, _) => Run(() =>
        {
            var result = model.Apply(model.CurrentPreview ?? throw new InvalidOperationException("Önizleme bulunamadı."));
            apply.IsEnabled = false;
            bindingGrid.ItemsSource = model.Bindings;
            balanceGrid.ItemsSource = model.Balances;
            status.Text = $"{result.Group} kaynağı kaydedildi.";
        });
    }

    UIElement Build()
    {
        AddColumn(bindingGrid, "Alan", nameof(ProductSourceBinding.Group), 150);
        AddColumn(bindingGrid, "Tür", nameof(ProductSourceBinding.Kind), 120);
        AddColumn(bindingGrid, "Kaynak", nameof(ProductSourceBinding.SourceId), 240);
        AddColumn(bindingGrid, "Revizyon", nameof(ProductSourceBinding.Version), 90);
        AddColumn(balanceGrid, "Konum", nameof(ProductBalanceRow.LocationName), 240);
        AddColumn(balanceGrid, "Tür", nameof(ProductBalanceRow.Kind), 140);
        AddColumn(balanceGrid, "Bakiye", nameof(ProductBalanceRow.Quantity), 100);
        AddColumn(balanceGrid, "Revizyon", nameof(ProductBalanceRow.Version), 100);
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = "İçerik, fiyat ve çevrimiçi stok kaynağı", FontSize = 20, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "Kaynak seçimi satış mağazası bağlantılarından bağımsızdır. Bakiyeler salt okunur gösterilir.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 4, 0, 8) });
        var actions = new WrapPanel(); actions.Children.Add(group); actions.Children.Add(source); actions.Children.Add(preview); actions.Children.Add(apply); root.Children.Add(actions); root.Children.Add(status);
        root.Children.Add(new TextBlock { Text = "Kaynak seçimleri", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        root.Children.Add(bindingGrid);
        root.Children.Add(new TextBlock { Text = "Çevrimiçi ve fiziksel stok", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        root.Children.Add(balanceGrid);
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    void UpdatePreviewState()
    {
        preview.IsEnabled = group.SelectedItem is ProductFieldGroup && source.SelectedItem is ProductSourceChoice;
        apply.IsEnabled = false;
    }

    void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { status.Text = MarketplaceConnectionStore.Redact(error.Message); apply.IsEnabled = false; }
    }

    static void AddColumn(DataGrid grid, string header, string path, double width) =>
        grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = width });
}
