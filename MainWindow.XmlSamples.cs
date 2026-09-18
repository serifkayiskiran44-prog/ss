using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
    IReadOnlyList<IReadOnlyDictionary<string, string>> xmlSamples = [];
    int xmlSampleIndex;
    readonly TextBlock xmlSampleStatus = Hint("Alanların örnek değerleri için XML'i indirin.");
    readonly List<(MappingEntry Entry, ComboBox Picker, TextBlock Sample, Image? Image)> sampleViews = [];
    readonly HttpClient thumbnailHttp = SafeRemoteHttp.CreateClient(TimeSpan.FromSeconds(12));
    readonly SemaphoreSlim thumbnailSlots = new(3);
    readonly Dictionary<string, BitmapSource> thumbnailCache = new();
    CancellationTokenSource thumbnailCancellation = new();

    static void BindModeVisibility(FrameworkElement panel, string mode)
    {
        var style = new Style(panel.GetType()); style.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        var trigger = new DataTrigger { Binding = new Binding("PriceMode"), Value = mode };
        trigger.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible)); style.Triggers.Add(trigger); panel.Style = style;
    }

    void BuildXmlDefaults()
    {
        sourceRules.Children.Add(Heading("Kategori ve varsayılanlar"));
        var products = store.Products();
        var taxonomy = new TaxonomyStore(dataDirectory);
        var categories = products.Select(p => p.Category).Concat(taxonomy.List(TaxonomyKind.Category).Select(p => p.Name)).Where(v => v.Length > 0).Distinct().Order().ToList();
        var brands = products.Select(p => p.Brand).Concat(taxonomy.List(TaxonomyKind.Brand).Select(p => p.Name)).Where(v => v.Length > 0).Distinct().Order().ToList();
        void Choice(string label, string path, List<string> values)
        {
            var picker = new ComboBox { IsEditable = true, ItemsSource = values, MaxDropDownHeight = 240, MinHeight = 25 };
            picker.SetBinding(ComboBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            CompactField(sourceRules, label, picker);
        }
        Choice("Varsayılan kategori", "DefaultCategory", categories); Choice("Sabit kategori", "FixedCategory", categories);
        Choice("Varsayılan marka", "DefaultBrand", brands); Choice("Sabit marka", "FixedBrand", brands);
        CompactBoundField(sourceRules, "Varsayılan KDV %", "DefaultVatRate");
        Choice("Varsayılan alış dövizi", "CostCurrency", ["TRY", "USD", "EUR", "GBP"]);
        sourceRules.Children.Add(Hint("Varsayılan: XML alanı boşsa. Sabit: bu kaynaktaki tüm ürünlere uygulanır. Kategori 1–5 soldan seçilir."));
    }

    void BuildXmlSampleToolbar()
    {
        var bar = new WrapPanel();
        bar.Children.Add(Button("‹ Önceki örnek", () => MoveXmlSample(-1)));
        bar.Children.Add(Button("Sonraki örnek ›", () => MoveXmlSample(1)));
        bar.Children.Add(Button("Örnekleri yenile", RefreshXmlSamples)); bar.Children.Add(xmlSampleStatus);
        sourceGeneral.Children.Add(bar);
    }

    void StopThumbnailRequests()
    {
        thumbnailCancellation.Cancel(); thumbnailCancellation.Dispose(); thumbnailCancellation = new();
    }

    void ResetXmlSamples() { StopThumbnailRequests(); xmlSamples = []; xmlSampleIndex = 0; }

    void RefreshXmlSamples()
    {
        if (xml.Length == 0 || source?.Location != loadedLocation) throw new InvalidOperationException("Önce bu kaynağın XML'ini indirin.");
        xmlSamples = XmlCatalog.FieldSamples(xml, itemPath.Text.Trim()); xmlSampleIndex = 0; RenderXmlSamples();
    }

    void MoveXmlSample(int step)
    {
        if (xmlSamples.Count == 0) return;
        xmlSampleIndex = (xmlSampleIndex + step + xmlSamples.Count) % xmlSamples.Count; RenderXmlSamples();
    }

    void AddXmlMapping(MappingEntry entry)
    {
        var cell = new StackPanel { Margin = new Thickness(2, 2, 8, 2) };
        var picker = new ComboBox { ItemsSource = xmlPaths, IsEditable = true, IsTextSearchEnabled = true, MinHeight = 25, MaxDropDownHeight = 280, Padding = new Thickness(2), ToolTip = "XML alanını ok düğmesinden seçin. Boş bırakılan alan alınmaz." };
        picker.SetBinding(ComboBox.TextProperty, new Binding("Path") { Source = entry, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        CompactField(cell, entry.Label, picker, 94);
        var sample = new TextBlock { FontSize = 10, Foreground = Brushes.SlateGray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(98, 0, 2, 2), MinHeight = 15 };
        Image? image = null;
        if (XmlFieldDefinitions.All.Any(d => d.Key == entry.Key && d.IsImage))
        {
            image = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform, Margin = new Thickness(4) };
            var line = new DockPanel(); DockPanel.SetDock(image, System.Windows.Controls.Dock.Right); line.Children.Add(image); line.Children.Add(sample); cell.Children.Add(line);
        }
        else cell.Children.Add(sample);
        sampleViews.Add((entry, picker, sample, image));
        picker.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => RenderXmlSample(picker)));
        var index=xmlMappingFields.Children.Count; if(index%3==0)xmlMappingFields.RowDefinitions.Add(new(){Height=GridLength.Auto}); Grid.SetColumn(cell,index%3); Grid.SetRow(cell,index/3); xmlMappingFields.Children.Add(cell);
    }

    void RenderXmlSamples()
    {
        StopThumbnailRequests();
        xmlSampleStatus.Text = xmlSamples.Count == 0 ? "Örnek değerler için XML'i indirin." : $"Örnek ürün {xmlSampleIndex + 1} / {xmlSamples.Count}";
        foreach (var view in sampleViews) RenderXmlSample(view.Picker);
    }

    void RenderXmlSample(ComboBox picker)
    {
        var view = sampleViews.FirstOrDefault(v => ReferenceEquals(v.Picker, picker)); if (view.Sample == null) return;
        var value = xmlSamples.Count == 0 ? "" : XmlCatalog.SampleValue(xmlSamples[xmlSampleIndex], picker.Text);
        view.Sample.Text = value.Length > 0 ? value : picker.Text.Length == 0 ? view.Entry.Key is "SourceProductId" or "BrandId" or "CategoryId1" ? "Seçilmezse sıralı ID otomatik üretilir" : "Alan seçilmedi" : "Örnekte değer yok"; view.Sample.ToolTip = value;
        if (view.Image == null) return;
        var url = value.Split(" | ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        view.Image.Source = null; view.Image.Tag = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https") _ = ShowXmlThumbnailAsync(view.Image, url, thumbnailCancellation.Token);
    }

    async Task ShowXmlThumbnailAsync(Image target, string url, CancellationToken token, string? productId=null)
    {
        try
        {
            if (!thumbnailCache.TryGetValue(url, out var bitmap))
            {
                await thumbnailSlots.WaitAsync(token);
                try
                {
                    var local=productId==null?null:new ProductAssetCache(dataDirectory).Find(productId,url);
                    var bytes = local==null?await XmlThumbnailLoader.ReadAsync(thumbnailHttp, url, token):await File.ReadAllBytesAsync(local,token);
                    bitmap = await Task.Run(() => { using var stream = new MemoryStream(bytes); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 80; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image; }, token);
                    if (thumbnailCache.Count >= 40) thumbnailCache.Clear(); thumbnailCache[url] = bitmap;
                }
                finally { thumbnailSlots.Release(); }
            }
            if (!token.IsCancellationRequested && Equals(target.Tag, url)) { target.Source = bitmap; target.ToolTip = "XML'deki ürün görseli"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!token.IsCancellationRequested && Equals(target.Tag, url)) target.ToolTip = "Görsel önizlemesi alınamadı; XML adresi örnek satırında gösteriliyor."; }
    }
}
