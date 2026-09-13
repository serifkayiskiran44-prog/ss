using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public static class LocaleSettingsPanel
{
    /// <param name="editState">#854: the app's settings edit state; the form reports its unsaved fields by label and clears them on save or load.</param>
    public static FrameworkElement Create(string? directory = null, SettingsEditState? editState = null)
    {
        var store = new LocaleSettingsStore(directory); var root = new DockPanel { Margin = Spacing.Section }; var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(Text("Döviz, vergi ve yerel ayarlar", TextRole.SectionTitle)); top.Children.Add(Text("Mağaza bazında hedef para birimi, sayı/tarih kültürü ve KDV metadata'sını tek yerde tutun. Bu merkez muhasebe/hakediş hesaplaması yapmaz; yalnız ürün, XML, Excel ve fiyat preview doğrulamasına kaynak olur."));
        var form = new WrapPanel(); top.Children.Add(form); var channel = new TextBox { Text = "etsy", Width = 110 }; var shop = new TextBox { Text = "default", Width = 130 }; var currency = new ComboBox { ItemsSource = LocaleSettings.SupportedCurrencies.ToArray(), SelectedItem = "TRY", Width = 80 }; var culture = new ComboBox { ItemsSource = LocaleSettings.SupportedCultures.ToArray(), SelectedItem = "tr-TR", Width = 90 }; var vat = new TextBox { Text = "20", Width = 60 }; var date = new TextBox { Text = "dd.MM.yyyy", Width = 110 };
        foreach (var pair in new[] { ("Kanal", (Control)channel), ("Mağaza", shop), ("Döviz", currency), ("Kültür", culture), ("KDV %", vat), ("Tarih", date) }) { form.Children.Add(new TextBlock { Text = pair.Item1, Margin = new Thickness(4, 7, 2, 0) }); form.Children.Add(pair.Item2); }
        var status = Text(""); status.Tag = "locale-status";
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 240, SelectionMode = DataGridSelectionMode.Single }; foreach (var c in new[] { ("Kanal", "Channel", 90d), ("Mağaza", "ShopId", 130d), ("Döviz", "Currency", 70d), ("Kültür", "CultureName", 90d), ("KDV %", "VatRate", 65d), ("Tarih", "DatePattern", 110d), ("Sürüm", "Version", 60d), ("Güncelleme", "UpdatedUtc", 150d) }) grid.Columns.Add(GridColumns.Text(c.Item1, c.Item2, c.Item3)); root.Children.Add(grid);
        // Errors land in the status line (redacted), never in a modal.
        Button Button(string text, Action action) { var button = new Button { Content = text, Margin = Spacing.Control }; button.Click += (_, _) => { try { action(); } catch (Exception error) { status.Text = MarketplaceConnectionStore.Redact(error.Message); } }; return button; }
        // #854: the form's unsaved fields, by label; a snapshot after load and after a successful save clears them.
        var tracker = editState?.Form("locale").Track("Kanal", () => channel.Text).Track("Mağaza", () => shop.Text).Track("Döviz", () => currency.SelectedItem?.ToString()).Track("Kültür", () => culture.SelectedItem?.ToString()).Track("KDV %", () => vat.Text).Track("Tarih", () => date.Text);
        foreach (var box in new[] { channel, shop, vat, date }) box.TextChanged += (_, _) => tracker?.Recompute();
        foreach (var combo in new[] { currency, culture }) combo.SelectionChanged += (_, _) => tracker?.Recompute();
        var copyChannel = new TextBox { Text = "etsy", Width = 100 }; var copyShop = new TextBox { Text = "default", Width = 120 }; var copyToChannel = new TextBox { Text = "ebay", Width = 100 }; var copyToShop = new TextBox { Text = "default", Width = 120 }; var actions = new WrapPanel();
        // #856: the row this form loaded (or last saved) and its version; a save that would overwrite a newer version of that row is a conflict, not a save.
        int? loadedVersion = null; var loadedKey = "";
        string Key() => channel.Text.Trim().ToLowerInvariant() + "/" + shop.Text.Trim();
        actions.Children.Add(Button("Kaydet", () =>
        {
            var selectedCulture = culture.SelectedItem?.ToString() ?? "tr-TR"; if (!decimal.TryParse(vat.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.GetCultureInfo(selectedCulture), out var rate)) throw new InvalidOperationException($"KDV oranı {selectedCulture} kültüründe sayı olmalı.");
            var current = store.Get(channel.Text, shop.Text);
            if (current is not null && loadedVersion is int seen && string.Equals(loadedKey, Key(), StringComparison.Ordinal) && current.Version != seen)
            {
                Reload();
                var detail = $"{current.Channel}/{current.ShopId} yerel ayarı bu formda yüklenen sürümden sonra değiştirildi (sürüm {seen} → {current.Version}); Seçileni yükle ile yeniden yükleyin, sonra kaydedin.";
                editState?.Report(new SettingsIssue(SettingsIssueKind.SaveConflict, "locale", "Kayıt çakışması", detail, SeverityLevel.Blocking));
                throw new InvalidOperationException("Kayıt çakışması: " + detail);
            }
            var saved = store.Save(new StoreLocaleSettings { Channel = channel.Text, ShopId = shop.Text, Currency = currency.SelectedItem?.ToString() ?? "TRY", CultureName = selectedCulture, VatRate = rate, DatePattern = date.Text });
            status.Text = $"{saved.Channel}/{saved.ShopId} ayarı kaydedildi · sürüm {saved.Version}"; Reload(); loadedVersion = saved.Version; loadedKey = saved.Channel + "/" + saved.ShopId; tracker?.Snapshot(); editState?.NotifySaved("locale");
        }));
        actions.Children.Add(Button("Seçileni yükle", () => { if (grid.SelectedItem is not StoreLocaleSettings selected) throw new InvalidOperationException("Önce ayar seçin."); channel.Text = selected.Channel; shop.Text = selected.ShopId; currency.SelectedItem = selected.Currency; culture.SelectedItem = selected.CultureName; vat.Text = selected.VatRate.ToString(System.Globalization.CultureInfo.InvariantCulture); date.Text = selected.DatePattern; loadedVersion = selected.Version; loadedKey = selected.Channel + "/" + selected.ShopId; tracker?.Snapshot(); editState?.Resolve(SettingsIssueKind.SaveConflict, "locale"); }));
        actions.Children.Add(Button("Kopyala", () => { var copied = store.Copy(copyChannel.Text, copyShop.Text, copyToChannel.Text, copyToShop.Text); status.Text = $"{copied.Channel}/{copied.ShopId} ayarı kopyalandı ve audit'e yazıldı."; Reload(); })); top.Children.Add(actions);
        var copy = new WrapPanel(); copy.Children.Add(new TextBlock { Text = "Kaynak", Margin = new Thickness(4, 7, 2, 0) }); copy.Children.Add(copyChannel); copy.Children.Add(copyShop); copy.Children.Add(new TextBlock { Text = "Hedef", Margin = new Thickness(10, 7, 2, 0) }); copy.Children.Add(copyToChannel); copy.Children.Add(copyToShop); top.Children.Add(copy); top.Children.Add(status);
        void Reload() => grid.ItemsSource = store.List(); Reload(); tracker?.Snapshot(); return root;
    }
    static TextBlock Text(string value, TextRole role = TextRole.Body) => TextStyles.Apply(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = Spacing.BodyBlock, Foreground = Brushes.DarkSlateGray }, role);
}
