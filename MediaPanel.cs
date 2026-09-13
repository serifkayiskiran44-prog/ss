using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public static class MediaPanel
{
    sealed record ProductChoice(string Id, string Label);
    // One thumbnail tile: position, state glyph and host only -- the record's URL never reaches the strip (#804).
    sealed record GalleryTile(int Position, string Glyph, string Host, string Primary);

    public static FrameworkElement Create(string? directory, Action<string>? navigate = null)
    {
        var catalog = new CatalogStore(directory);
        var media = new MediaStore(directory);
        var validation = new MediaValidationService();
        var previewHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var panel = new StackPanel { Margin = new Thickness(DesignTokens.SpacePage), MaxWidth = 1300 };
        panel.Children.Add(Heading("Ürün görsel ve medya merkezi"));
        panel.Children.Add(Hint("Görseller ürün ve kaynak bazında saklanır. URL duplicate'leri engellenir; doğrulama yalnız dosyayı okur ve marketplace'e yazmaz. XML/Excel ImageUrls alanları ilk açılışta içeri alınır."));

        var productPicker = new ComboBox { Width = 460, DisplayMemberPath = "Label", HorizontalAlignment = HorizontalAlignment.Left };
        var search = new TextBox { Width = 230, ToolTip = "SKU, ürün adı veya görsel adresi" };
        var url = new TextBox { Width = 450, ToolTip = "HTTPS veya yerel file:/// adresi" };
        var source = new TextBox { Text = "manual", Width = 160 };
        var status = Hint("");
        // #804: keyboard-navigable thumbnail strip over the same records the grid lists. Focus is visible on the
        // tile itself (a border that follows keyboard focus), and the summary beneath names position/state/host.
        IReadOnlyList<ProductMediaRecord> galleryRecords = Array.Empty<ProductMediaRecord>();
        var gallery = new ListBox { Height = 92, SelectionMode = SelectionMode.Single };
        // Deliberate copy of one image's address, in contrast to the grid's implicit row copy, which is disabled
        // below because it swept signed URLs onto the clipboard whenever an operator pressed Ctrl+C (#804).
        var copySource = new Button { Content = "Görsel adresini kopyala", Margin = Spacing.Control };
        gallery.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        gallery.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        gallery.ItemsPanel = (ItemsPanelTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><VirtualizingStackPanel Orientation='Horizontal'/></ItemsPanelTemplate>");
        gallery.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<Border Width='96' Height='68' Margin='3' Padding='4' BorderThickness='1' BorderBrush='#D6E2EB' Background='#F7FAFC'>" +
            "<StackPanel><TextBlock Text='{Binding Glyph}' FontSize='18' HorizontalAlignment='Center'/>" +
            "<TextBlock Text='{Binding Host}' FontSize='10' TextTrimming='CharacterEllipsis' HorizontalAlignment='Center'/>" +
            "<TextBlock FontSize='10' HorizontalAlignment='Center'><Run Text='#'/><Run Text='{Binding Position}'/><Run Text=' '/><Run Text='{Binding Primary}'/></TextBlock>" +
            "</StackPanel></Border></DataTemplate>");
        var gallerySummary = new TextBlock { Margin = new Thickness(3, 2, 3, 6), TextWrapping = TextWrapping.Wrap, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(87, 112, 125)) };
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 330, EnableRowVirtualization = true, ClipboardCopyMode = DataGridClipboardCopyMode.None };
        grid.Columns.Add(new DataGridTextColumn { Header = "Durum", Binding = new System.Windows.Data.Binding("Status"), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "URL", Binding = new System.Windows.Data.Binding("Url"), Width = 360 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Kaynak", Binding = new System.Windows.Data.Binding("Source"), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Sıra", Binding = new System.Windows.Data.Binding("SortOrder"), Width = 55 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Ana", Binding = new System.Windows.Data.Binding("IsPrimary"), Width = 55 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Son doğrulama", Binding = new System.Windows.Data.Binding("LastValidatedUtc"), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Hata", Binding = new System.Windows.Data.Binding("Error"), Width = 300 });
        // #797: the image and its placeholder live in one fixed box, so loading -> ready -> broken never resizes
        // anything. The placeholder is the shared ProductMediaPresentation description (glyph, word, hint).
        var preview = new System.Windows.Controls.Image { Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var previewGlyph = new TextBlock { FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 158, 171)) };
        var previewLabel = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(8, 6, 8, 0), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(87, 112, 125)) };
        var previewPlaceholder = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { previewGlyph, previewLabel } };
        var previewBox = new Border
        {
            Width = 360, Height = 260, HorizontalAlignment = HorizontalAlignment.Left,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(247, 250, 252)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(214, 226, 235)), BorderThickness = new Thickness(1),
            Child = new Grid { Children = { preview, previewPlaceholder } },
        };
        void ShowMediaState(ProductMediaRecord? record, bool loading, bool loaded, bool fromCache, bool failed = false)
        {
            var state = ProductMediaPresentation.Classify(record, loading, loaded, fromCache, failed);
            var picture = state.Key is ProductMediaPresentation.Ready or ProductMediaPresentation.Cached;
            preview.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
            previewPlaceholder.Visibility = picture ? Visibility.Collapsed : Visibility.Visible;
            previewGlyph.Text = state.Glyph; previewLabel.Text = state.Hint.Length > 0 ? state.Label + "\n" + state.Hint : state.Label;
            System.Windows.Automation.AutomationProperties.SetName(previewBox, state.Label);
        }
        ProductMediaRecord? selected = null;
        IReadOnlyList<ProductChoice> choices = [];

        void SyncCatalogImages()
        {
            foreach (var product in catalog.Products())
            {
                var sourceName = string.IsNullOrWhiteSpace(product.SourceId) ? "catalog" : "xml:" + product.SourceId;
                foreach (var imageUrl in (product.ImageUrls ?? "").Split(['|', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    try { media.Ensure(product.Id, imageUrl, sourceName); } catch (InvalidOperationException) { }
                }
            }
        }

        void RefreshProducts()
        {
            var q = search.Text.Trim();
            var products = catalog.Products().Where(p => q.Length == 0 || $"{p.Sku} {p.Barcode} {p.Name}".Contains(q, StringComparison.CurrentCultureIgnoreCase)).OrderBy(p => p.Name).ToList();
            choices = products.Select(p => new ProductChoice(p.Id, $"{p.Sku} · {p.Name}")).ToList();
            var current = productPicker.SelectedItem is ProductChoice item ? item.Id : null;
            productPicker.ItemsSource = choices;
            productPicker.SelectedItem = choices.FirstOrDefault(x => x.Id == current) ?? choices.FirstOrDefault();
            RefreshMedia();
        }

        void RefreshMedia()
        {
            var choice = productPicker.SelectedItem as ProductChoice;
            var rows = media.List(choice?.Id);
            grid.ItemsSource = rows;
            galleryRecords = rows;
            gallery.ItemsSource = rows.Select((r, i) => new GalleryTile(i + 1, ProductMediaPresentation.Classify(r, false, r.Status == MediaStatus.Ready, false).Glyph, ProductMediaPresentation.SafeSourceLabel(r.Url), r.IsPrimary ? "★" : "")).ToList();
            if (gallery.Items.Count > 0 && gallery.SelectedIndex < 0) gallery.SelectedIndex = 0;
            UpdateGallerySummary();
            status.Text = choice is null ? $"{rows.Count} görsel" : $"{rows.Count} görsel · ürün: {choice.Label}";
        }
        void UpdateGallerySummary()
        {
            gallerySummary.Text = MediaGalleryNavigation.Summary(galleryRecords, gallery.SelectedIndex);
            System.Windows.Automation.AutomationProperties.SetName(gallery, gallerySummary.Text);
        }

        async Task ShowPreviewAsync(ProductMediaRecord? record)
        {
            preview.Source = null;
            ShowMediaState(record, loading: record is not null, loaded: false, fromCache: false);
            if (record is null) return;
            try
            {
                byte[] bytes;
                var normalized = MediaStore.NormalizeUrl(record.Url);
                if (normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) bytes = await File.ReadAllBytesAsync(new Uri(normalized).LocalPath);
                else if (normalized.Length > 0) bytes = await previewHttp.GetByteArrayAsync(normalized);
                else { ShowMediaState(record, false, false, false); return; }
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); preview.Source = image;
                ShowMediaState(record, loading: false, loaded: true, fromCache: normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase));
            }
            // The transport's own message has, in this app, contained the full signed URL; the operator gets the
            // shared failure line (reason + host) instead, and the raw text is not surfaced (#797).
            catch (Exception error) when (error is HttpRequestException or IOException or NotSupportedException or UriFormatException or TaskCanceledException)
            { ShowMediaState(record, loading: false, loaded: false, fromCache: false, failed: true); status.Text = ProductMediaPresentation.FailureText(record, error.Message); }
        }

        // #804: the thumbnail strip drives the same selection the grid does, with arrow/Home/End/Enter handled
        // through the shared rules and a summary that names position, state and host -- never the URL.
        gallery.SelectionChanged += async (_, _) =>
        {
            UpdateGallerySummary();
            if (gallery.SelectedIndex >= 0 && gallery.SelectedIndex < galleryRecords.Count)
            {
                var record = galleryRecords[gallery.SelectedIndex];
                if (!ReferenceEquals(record, selected)) { grid.SelectedItem = record; selected = record; await ShowPreviewAsync(record); }
            }
        };
        gallery.PreviewKeyDown += (_, e) =>
        {
            if (MediaGalleryNavigation.IsActivation(e.Key)) { copySource.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); e.Handled = true; return; }
            var next = MediaGalleryNavigation.Next(gallery.SelectedIndex, gallery.Items.Count, e.Key);
            if (next == gallery.SelectedIndex) return;
            gallery.SelectedIndex = next;
            (gallery.ItemContainerGenerator.ContainerFromIndex(next) as ListBoxItem)?.Focus();
            gallery.ScrollIntoView(gallery.SelectedItem);
            e.Handled = true;
        };
        copySource.Click += (_, _) =>
        {
            var value = MediaGalleryNavigation.ExplicitCopyValue(selected);
            if (value.Length == 0) { status.Text = "Önce görsel seçin."; return; }
            try { Clipboard.SetText(value); status.Text = "Görsel adresi panoya kopyalandı."; }
            catch (Exception copyError) when (copyError is System.Runtime.InteropServices.COMException or InvalidOperationException) { status.Text = "Pano kullanılamadı."; }
        };
        productPicker.SelectionChanged += (_, _) => RefreshMedia();
        search.TextChanged += (_, _) => RefreshProducts();
        grid.SelectionChanged += async (_, _) => { selected = grid.SelectedItem as ProductMediaRecord; await ShowPreviewAsync(selected); };

        var refresh = Button("Yenile", () => { SyncCatalogImages(); RefreshProducts(); });
        var add = Button("Görsel ekle", () =>
        {
            var product = productPicker.SelectedItem as ProductChoice ?? throw new InvalidOperationException("Önce ürün seçin.");
            media.Add(product.Id, url.Text, source.Text); url.Clear(); RefreshMedia(); status.Text = "Görsel kaydedildi; doğrulama bekliyor.";
        });
        var validate = AsyncButton("Seçili görseli doğrula", async () =>
        {
            if (selected is null) throw new InvalidOperationException("Önce görsel seçin.");
            status.Text = "Görsel doğrulanıyor…";
            validation.Invalidate(selected.Url); var result = await validation.ValidateWithRetryAsync(selected);
            media.UpdateValidation(selected.Id, result); RefreshMedia(); status.Text = $"{result.Status}: {result.Error}";
        });
        var validateAll = AsyncButton("Ürünün tüm görsellerini doğrula", async () =>
        {
            var product = productPicker.SelectedItem as ProductChoice ?? throw new InvalidOperationException("Önce ürün seçin.");
            foreach (var row in media.List(product.Id)) { var result = await validation.ValidateWithRetryAsync(row); media.UpdateValidation(row.Id, result); }
            RefreshMedia(); status.Text = "Ürün görsellerinin doğrulaması tamamlandı.";
        });
        var primary = Button("Ana görsel yap", () => { if (selected is null) throw new InvalidOperationException("Önce görsel seçin."); media.SetPrimary(selected.Id); RefreshMedia(); });
        var remove = Button("Kaydı kaldır", () => { if (selected is null) throw new InvalidOperationException("Önce görsel seçin."); media.Delete(selected.Id); selected = null; RefreshMedia(); preview.Source = null; ShowMediaState(null, false, false, false); });
        var openProduct = Button("Ürünü düzenle", () => navigate?.Invoke("products"));

        var top = new WrapPanel(); top.Children.Add(new TextBlock { Text = "Ürün", Margin = Spacing.Inline, VerticalAlignment = VerticalAlignment.Center }); top.Children.Add(productPicker); top.Children.Add(new TextBlock { Text = "Ara", Margin = Spacing.Inline, VerticalAlignment = VerticalAlignment.Center }); top.Children.Add(search); top.Children.Add(refresh); top.Children.Add(openProduct); panel.Children.Add(top);
        var addRow = new WrapPanel(); addRow.Children.Add(new TextBlock { Text = "Adres", Margin = Spacing.Inline, VerticalAlignment = VerticalAlignment.Center }); addRow.Children.Add(url); addRow.Children.Add(new TextBlock { Text = "Kaynak", Margin = Spacing.Inline, VerticalAlignment = VerticalAlignment.Center }); addRow.Children.Add(source); addRow.Children.Add(add); panel.Children.Add(addRow);
        var actions = new WrapPanel(); actions.Children.Add(validate); actions.Children.Add(validateAll); actions.Children.Add(primary); actions.Children.Add(remove); actions.Children.Add(copySource); panel.Children.Add(actions);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) }); Grid.SetColumn(grid, 0); Grid.SetColumn(previewBox, 1); body.Children.Add(grid); body.Children.Add(previewBox);
        panel.Children.Add(gallery); panel.Children.Add(gallerySummary); panel.Children.Add(body); panel.Children.Add(status);
        ShowMediaState(null, loading: false, loaded: false, fromCache: false);
        SyncCatalogImages(); RefreshProducts();
        return Scroll(panel);
    }

    static TextBlock Heading(string text) => TextStyles.Apply(new TextBlock { Text = text, Margin = Spacing.TitleBlock }, TextRole.SectionTitle);
    static TextBlock Hint(string text) => TextStyles.Apply(new TextBlock { Text = text, Margin = Spacing.HintBlock }, TextRole.Hint);
    static Button Button(string text, Action action) { var button = new Button { Content = text, Margin = Spacing.Control }; button.Click += (_, _) => { try { action(); } catch (Exception error) { MessageBox.Show(MarketplaceConnectionStore.Redact(error.Message), "Görsel merkezi", MessageBoxButton.OK, MessageBoxImage.Warning); } }; return button; }
    static Button AsyncButton(string text, Func<Task> action) { var button = new Button { Content = text, Margin = Spacing.Control }; button.Click += async (_, _) => { try { button.IsEnabled = false; await action(); } catch (Exception error) { MessageBox.Show(MarketplaceConnectionStore.Redact(error.Message), "Görsel merkezi", MessageBoxButton.OK, MessageBoxImage.Warning); } finally { button.IsEnabled = true; } }; return button; }
    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
}
