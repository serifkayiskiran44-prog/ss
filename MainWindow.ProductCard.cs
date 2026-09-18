using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
    sealed class EditableChannelPrice
    {
        public string Channel { get; set; } = "";
        public decimal SalePrice { get; set; }
        public decimal? ListPrice { get; set; }
        public string Currency { get; set; } = "TRY";
    }

    void OpenProductCard(bool create)
    {
        var product = create ? new CatalogProduct { Currency = "TRY", CostCurrency = "TRY", Active = true } : Clone(SelectedProduct());
        var tabs = new TabControl { DataContext = product, Margin = new Thickness(10) };
        var columns = new Grid(); for (var i = 0; i < 3; i++) columns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var identity = new StackPanel(); var pricing = new StackPanel(); var stock = new StackPanel();
        foreach (var (panel, index) in new[] { (identity, 0), (pricing, 1), (stock, 2) }) { var scroll = Scroll(panel); Grid.SetColumn(scroll, index); columns.Children.Add(scroll); }
        identity.Children.Add(Heading("Ürün bilgileri"));
        var idBox=CompactBoundField(identity,"Ürün ID","ProductIdLabel");idBox.SetBinding(TextBox.TextProperty,new Binding("ProductIdLabel"){Mode=BindingMode.OneWay});idBox.IsReadOnly=true;
        var sku = CompactBoundField(identity, "Stok kodu / SKU", "Sku"); sku.IsReadOnly = !create;
        var name = CompactBoundField(identity, "Ürün adı", "Name"); name.MaxLength = 500;
        Flag(identity, "Aktif ürün", "Active");
        var all = store.Products();
        void Choice(Panel parent, string label, string path, IEnumerable<string> values)
        {
            var choice = new ComboBox { ItemsSource = values.Where(v => v.Length > 0).Distinct().Order().ToList(), IsEditable = true, MaxDropDownHeight = 250 };
            choice.SetBinding(ComboBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); CompactField(parent, label, choice);
        }
        Choice(identity, "Kategori", "Category", all.Select(p => p.Category)); Choice(identity, "Marka", "Brand", all.Select(p => p.Brand));
        var brandId=CompactBoundField(identity,"Marka ID","BrandIdLabel");brandId.SetBinding(TextBox.TextProperty,new Binding("BrandIdLabel"){Mode=BindingMode.OneWay});brandId.IsReadOnly=true;var categoryId=CompactBoundField(identity,"Kategori ID","CategoryIdLabel");categoryId.SetBinding(TextBox.TextProperty,new Binding("CategoryIdLabel"){Mode=BindingMode.OneWay});categoryId.IsReadOnly=true;
        identity.Children.Add(Heading("Ek bilgiler"));
        CompactBoundField(identity, "Barkod", "Barcode").IsReadOnly = !create;
        CompactBoundField(identity, "GTIN / EAN", "Gtin"); CompactBoundField(identity, "MPN / ASIN", "Mpn");
        CompactBoundField(identity, "Fatura adı", "InvoiceName"); CompactBoundField(identity, "Alt başlık", "Subtitle"); CompactBoundField(identity, "Raf / konum", "Shelf");
        foreach (var definition in XmlFieldDefinitions.All.Where(d => d.IsAttribute && d.Key is not ("BrandId" or "CategoryId1" or "CategoryId2" or "CategoryId3" or "CategoryId4" or "CategoryId5")))
        {
            var box = new TextBox { Text = product.XmlAttributes.GetValueOrDefault(definition.Key, ""), MinHeight = 25 };
            box.TextChanged += (_, _) => { if (string.IsNullOrWhiteSpace(box.Text)) product.XmlAttributes.Remove(definition.Key); else product.XmlAttributes[definition.Key] = box.Text; };
            CompactField(identity, definition.Label, box);
        }
        var expiry = new DatePicker(); expiry.SetBinding(DatePicker.SelectedDateProperty, new Binding("ExpiresOn") { Mode = BindingMode.TwoWay }); CompactField(identity, "Miad", expiry);
        pricing.Children.Add(Heading("Fiyatlar")); CompactBoundField(pricing, "Alış fiyatı", "Cost"); Choice(pricing, "Alış para birimi", "CostCurrency", ["TRY", "USD", "EUR", "GBP"]);
        CompactBoundField(pricing, "Satış fiyatı", "Price"); Choice(pricing, "Satış para birimi", "Currency", ["TRY", "USD", "EUR", "GBP"]); CompactBoundField(pricing, "KDV %", "VatRate");
        var prices = XmlCategoryRules.Channels.Select(channel => { var price = product.ChannelPrices.GetValueOrDefault(channel); return new EditableChannelPrice { Channel = channel, SalePrice = price?.SalePrice ?? product.Price, ListPrice = price?.ListPrice, Currency = price?.Currency ?? product.Currency }; }).ToList();
        var priceGrid = new DataGrid { ItemsSource = prices, IsReadOnly = false, AutoGenerateColumns = false, CanUserAddRows = false, Height = 310, RowHeight = 28 };
        Column(priceGrid, "Pazaryeri", "Channel", 105); priceGrid.Columns[0].IsReadOnly = true;
        EditableColumn(priceGrid, "Satış", "SalePrice", 85); EditableColumn(priceGrid, "Üstü çizili", "ListPrice", 90); EditableColumn(priceGrid, "Döviz", "Currency", 65);
        pricing.Children.Add(new GroupBox { Header = "Pazaryeri fiyatları", Content = priceGrid }); pricing.Children.Add(Hint("Üstü çizili fiyat boş veya 0 ise kullanılmaz. XML'in fiyatı değiştirmemesi için Fiyatlar değişmesin seçeneğini işaretleyin."));
        stock.Children.Add(Heading("Stok")); CompactBoundField(stock, "Ürün adedi", "Stock");
        stock.Children.Add(Heading("XML güncelleme koruması"));
        Flag(stock, "Ürün adı değişmesin", "LockName"); Flag(stock, "Açıklama değişmesin", "LockDescription"); Flag(stock, "Fiyatlar değişmesin", "LockPrice"); Flag(stock, "Stok değişmesin", "LockStock"); Flag(stock, "Görseller değişmesin", "LockImages");
        stock.Children.Add(Hint("İşaretli alanlar sonraki XML aktarımında korunur."));
        stock.Children.Add(Heading("Kaynak")); stock.Children.Add(Hint(create ? "Manuel ürün. ID'ler kaydedildiğinde üretilecek." : $"Ürün ID: {product.ProductIdLabel}\nKaynak: {store.Sources().FirstOrDefault(s => s.Id == product.SourceId)?.Name ?? product.SourceKind}\nGüncelleme: {product.UpdatedUtc.ToLocalTime():g}"));
        tabs.Items.Add(new TabItem { Header = "Genel", Content = columns });

        var media = new StackPanel(); var description = CompactBoundField(media, "Açıklama", "Description"); description.Height = 110; description.AcceptsReturn = true; description.TextWrapping = TextWrapping.Wrap;
        media.Children.Add(Hint("Görseller kayıttan sonra bu bilgisayardaki ürüne özel arşive kopyalanır. Ürün silinince uygulamanın kopyaları temizlenir; kaynak bağlantılar ve orijinal dosyalar korunur."));
        var imageRows = new List<TextBox>(); var urls = ProductImages(product).ToList(); var mediaCancellation = new CancellationTokenSource();
        for (var i = 0; i < Math.Max(9, urls.Count); i++)
        {
            var row = new DockPanel { Margin = new Thickness(4) }; var image = new Image { Width = 64, Height = 64, Margin = new Thickness(8) }; DockPanel.SetDock(image, System.Windows.Controls.Dock.Right); row.Children.Add(image);
            var box = new TextBox { Text = urls.ElementAtOrDefault(i) ?? "", MinHeight = 28, VerticalAlignment = VerticalAlignment.Center }; imageRows.Add(box);
            var label = new TextBlock { Text = $"Görsel {i + 1}", Width = 80, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(label, System.Windows.Controls.Dock.Left); row.Children.Add(label); row.Children.Add(box); media.Children.Add(row);
            void Load() { image.Tag = box.Text.Trim(); image.Source = null; if (Uri.TryCreate(box.Text, UriKind.Absolute, out var uri) && uri.Scheme == "https") _ = ShowXmlThumbnailAsync(image, box.Text.Trim(), mediaCancellation.Token, create?null:product.Id); }
            box.LostKeyboardFocus += (_, _) => Load(); Load();
        }
        tabs.Items.Add(new TabItem { Header = "Resimler ve açıklama", Content = Scroll(media) });
        var channels = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, ItemsSource = create ? null : MarketplaceProductPanelModel.Build(product.Id, dataDirectory) };
        foreach (var col in new[] { ("Pazaryeri", "Channel", 150d), ("Mağaza", "ShopId", 130d), ("Durum", "Durum", 180d), ("Eşleştirme", "MappingId", 190d), ("Ne yapılmalı?", "Aciklama", 430d) }) Column(channels, col.Item1, col.Item2, col.Item3);
        tabs.Items.Add(new TabItem { Header = "Pazaryeri durumları", Content = channels });
        Window? dialog = null; var actions = new WrapPanel(); var message = Hint("");
        actions.Children.Add(Button("Kaydet", () =>
        {
            priceGrid.CommitEdit(DataGridEditingUnit.Cell, true); priceGrid.CommitEdit(DataGridEditingUnit.Row, true); ValidBindings(tabs);
            if (prices.Any(p => p.SalePrice < 0 || p.ListPrice < 0 || p.ListPrice is > 0 && p.ListPrice < p.SalePrice || !LocaleSettings.SupportedCurrencies.Contains(p.Currency))) throw new InvalidOperationException("Pazaryeri fiyatı veya para birimi geçersiz; üstü çizili fiyat satıştan düşük olamaz.");
            product.ChannelPrices = prices.ToDictionary(p => p.Channel, p => new XmlChannelPrice(p.SalePrice, p.ListPrice == 0 ? null : p.ListPrice, p.Currency));
            product.ImageUrls = string.Join(" | ", imageRows.Select(b => b.Text.Trim()).Where(v => v.Length > 0).Distinct());
            if (create) store.CreateManual(product); else store.SaveProduct(product);
            RefreshProducts(); Log($"Ürün kaydedildi: {product.ProductIdLabel} • {product.Name}"); dialog!.Close();
        }));
        actions.Children.Add(Button("Vazgeç", () => dialog!.Close())); actions.Children.Add(message);
        var root = new DockPanel(); DockPanel.SetDock(actions, System.Windows.Controls.Dock.Bottom); root.Children.Add(actions); root.Children.Add(tabs);
        dialog = CreateXmlWindow(create ? "Yeni ürün — varyantsız" : "Ürün kartı — " + product.Name, root, 1400, 850);
        try { dialog.ShowDialog(); } finally { mediaCancellation.Cancel(); mediaCancellation.Dispose(); }
    }
}
