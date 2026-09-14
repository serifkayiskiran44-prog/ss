using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
    FrameworkElement BuildTaxonomy()
    {
        var taxonomy = new TaxonomyStore(dataDirectory);
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 1200 };
        panel.Children.Add(Heading("Kategori, marka ve özellik merkezi"));
        panel.Children.Add(Hint("Yerel sözlük, kanal/mağaza eşlemeleri ve eksik/stale durumları tek ekranda izlenir. Öneriler yalnız isim normalizasyonuna dayanır; canlı marketplace yazımı yapılmaz."));
        var kind = new ComboBox { ItemsSource = Enum.GetValues<TaxonomyKind>(), SelectedItem = TaxonomyKind.Category, Width = 180 };
        var marketplace = new TextBox { Text = "etsy", Width = 120 };
        var shop = new TextBox { Text = "default", Width = 140 };
        var search = new TextBox { Width = 180, ToolTip = "Yerel ad, değer veya harici anahtar ara" };
        var name = new TextBox { Width = 180 }; var value = new TextBox { Width = 180 }; var external = new TextBox { Width = 220 };
        var active = new CheckBox { Content = "Etkin", IsChecked = true, Margin = new Thickness(4) };
        string? editingId = null;
        var externalBatch = new TextBox { Width = 420, Height = 65, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, ToolTip = "Her satıra bir harici kategori/marka adı" };
        var status = Hint("");
        var localGrid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 230, EnableRowVirtualization = true };
        var mappingGrid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 250, EnableRowVirtualization = true };
        var historyGrid = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 200, EnableRowVirtualization = true };
        IReadOnlyList<TaxonomySuggestion> pendingSuggestions = [];
        void Refresh()
        {
            var selected = (TaxonomyKind)kind.SelectedItem!; var market = marketplace.Text.Trim(); var shopId = shop.Text.Trim();
            var local = taxonomy.List(selected).Where(x => string.IsNullOrWhiteSpace(search.Text) || $"{x.Name} {x.Value}".Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
            localGrid.ItemsSource = local; var views = taxonomy.MappingViews(selected, market, shopId, search.Text.Trim()); mappingGrid.ItemsSource = views; historyGrid.ItemsSource = taxonomy.History(selected, market, shopId);
            var mapped = views.Count(x => x.Status == "MAPPED");
            var missing = views.Count(x => x.Status == "MISSING");
            var stale = views.Count(x => x.Status == "STALE");
            var invalid = views.Count(x => x.Status == "INVALID");
            status.Text = $"{local.Count} yerel kayıt · {mapped} eşleşmiş · {missing} eksik · {stale} stale · {invalid} geçersiz";
        }
        void ClearEditor() { editingId = null; name.Clear(); value.Clear(); active.IsChecked = true; }
        kind.SelectionChanged += (_, _) => { ClearEditor(); Refresh(); }; marketplace.TextChanged += (_, _) => Refresh(); shop.TextChanged += (_, _) => Refresh(); search.TextChanged += (_, _) => Refresh();
        localGrid.SelectionChanged += (_, _) => { if (localGrid.SelectedItem is TaxonomyEntry selected) { editingId = selected.Id; name.Text = selected.Name; value.Text = selected.Value; active.IsChecked = selected.Active; } };
        var save = Button("Yerel kaydı ekle / güncelle", () =>
        {
            var entry = new TaxonomyEntry { Kind = (TaxonomyKind)kind.SelectedItem!, Name = name.Text, Value = value.Text, Active = active.IsChecked == true };
            if (editingId is not null) entry.Id = editingId;
            taxonomy.Save(entry); ClearEditor(); Refresh();
        });
        var clearEdit = Button("Yeni kayıt", () => ClearEditor());
        var delete = Button("Seçili kaydı sil", () =>
        {
            if (localGrid.SelectedItem is not TaxonomyEntry selected) throw new InvalidOperationException("Önce silinecek kaydı listeden seçin.");
            var usage = taxonomy.Usage((TaxonomyKind)kind.SelectedItem!, selected);
            var prompt = usage.Total > 0
                ? $"'{selected.Name}' {usage.Products} üründe ve {usage.Mappings} kanal eşlemesinde kullanılıyor; silinemez. Bunun yerine pasife almak ister misiniz?"
                : $"'{selected.Name}' hiçbir üründe veya eşlemede kullanılmıyor. Kalıcı olarak silinsin mi?";
            if (usage.Total > 0)
            {
                if (MessageBox.Show(prompt, "Kayıt kullanımda", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                { selected.Active = false; taxonomy.Save(selected); ClearEditor(); Refresh(); status.Text = "Kayıt kullanımda olduğu için silinmedi; pasife alındı."; }
                return;
            }
            if (MessageBox.Show(prompt, "Kaydı sil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            taxonomy.Delete((TaxonomyKind)kind.SelectedItem!, selected.Id); ClearEditor(); Refresh(); status.Text = "Kayıt silindi.";
        });
        var map = Button("Harici anahtarı eşle", () => { if (localGrid.SelectedItem is not TaxonomyEntry entry) throw new InvalidOperationException("Önce yerel listeden kayıt seçin."); taxonomy.Map((TaxonomyKind)kind.SelectedItem!, external.Text, entry.Id, marketplace.Text, shop.Text); external.Clear(); Refresh(); });
        var suggest = Button("İsim önerisi oluştur", () => { pendingSuggestions = taxonomy.SuggestBulk((TaxonomyKind)kind.SelectedItem!, externalBatch.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)); mappingGrid.ItemsSource = pendingSuggestions; var suggested = pendingSuggestions.Count(x => x.Status == "SUGGESTED"); var unmatched = pendingSuggestions.Count(x => x.Status == "UNMATCHED"); status.Text = $"{suggested} öneri · {unmatched} eşleşmeyen; henüz yazılmadı."; });
        var bulk = Button("Toplu eşlemeyi önizle / onayla", () => { if (pendingSuggestions.Count == 0) throw new InvalidOperationException("Önce harici anahtar listesinden öneri oluşturun."); var text = string.Join("\n", pendingSuggestions.Select(x => $"{x.ExternalKey} → {x.LocalName ?? "eşleşme yok"}")); if (MessageBox.Show(text + "\n\nSadece önerilen eşleşmeler yerel veritabanına yazılsın mı?", "Eşleme önizlemesi", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return; taxonomy.MapBulk((TaxonomyKind)kind.SelectedItem!, marketplace.Text, shop.Text, pendingSuggestions, true); Refresh(); status.Text += " · Toplu eşleme uygulandı."; });
        var row = new WrapPanel(); row.Children.Add(new TextBlock { Text = "Tür", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) }); row.Children.Add(kind); row.Children.Add(new TextBlock { Text = "Kanal", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) }); row.Children.Add(marketplace); row.Children.Add(new TextBlock { Text = "Mağaza", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) }); row.Children.Add(shop); row.Children.Add(new TextBlock { Text = "Ara", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) }); row.Children.Add(search); panel.Children.Add(row);
        var localRow = new WrapPanel(); localRow.Children.Add(new TextBlock { Text = "Yerel ad", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) }); localRow.Children.Add(name); localRow.Children.Add(new TextBlock { Text = "Değer", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) }); localRow.Children.Add(value); localRow.Children.Add(active); localRow.Children.Add(save); localRow.Children.Add(clearEdit); localRow.Children.Add(delete); panel.Children.Add(localRow);
        panel.Children.Add(Hint("Listeden bir kayıt seçmek düzenleme moduna geçer (aynı kayıt güncellenir); 'Yeni kayıt' formu temizler. Kullanımda olan kayıt silinemez, yalnız pasife alınabilir."));
        var mappingRow = new WrapPanel(); mappingRow.Children.Add(new TextBlock { Text = "Harici anahtar", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4) }); mappingRow.Children.Add(external); mappingRow.Children.Add(map); panel.Children.Add(mappingRow);
        var bulkRow = new StackPanel(); bulkRow.Children.Add(new TextBlock { Text = "Toplu öneri listesi (satır başına bir değer)", Margin = new Thickness(4) }); bulkRow.Children.Add(externalBatch); var bulkButtons = new WrapPanel(); bulkButtons.Children.Add(suggest); bulkButtons.Children.Add(bulk); bulkRow.Children.Add(bulkButtons); panel.Children.Add(bulkRow);
        var tabs = new TabControl(); tabs.Items.Add(new TabItem { Header = "Yerel sözlük", Content = localGrid }); tabs.Items.Add(new TabItem { Header = "Kanal / mağaza eşleme", Content = mappingGrid }); tabs.Items.Add(new TabItem { Header = "Eşleme geçmişi", Content = historyGrid }); panel.Children.Add(tabs); panel.Children.Add(status); Refresh(); return Scroll(panel);
    }
}
