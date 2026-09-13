using Microsoft.Win32;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public partial class MainWindow
{
    CancellationTokenSource? excelCts;
    readonly ExcelApplyCoordinator excelCoordinator = new();
    Button? excelApplyButton;
    Button? excelCancelButton;
    FrameworkElement BuildExcel()
    {
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 1150 };
        panel.Children.Add(Heading("Excel şablon ve aktarım merkezi"));
        panel.Children.Add(Hint("Profil; kolon eşlemelerini, başlık alias'larını, sayı/tarih kültürünü, varsayılanları ve dışa aktarım alanlarını saklar. Her dosya önce create/update/skip/error önizlemesine girer; önizleme olmadan katalog yazılmaz."));
        var profileStore = new ExcelProfileStore(dataDirectory);
        var profile = new ExcelImportProfile { Name = "Varsayılan" };
        var profileBox = new ComboBox { Width = 300, DisplayMemberPath = "Name" };
        var profileName = new TextBox { Text = profile.Name, Width = 220 };
        var culture = new TextBox { Text = profile.CultureName, Width = 150, ToolTip = "Örn. tr-TR veya en-US" };
        var aliases = new TextBox { Height = 45, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, ToolTip = "Her satır: Excel başlığı=Alan adı (örn. product_code=Sku)" };
        var defaults = new TextBox { Height = 45, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, ToolTip = "Her satır: Alan=varsayılan değer" };
        var visibleFields = new TextBox { Text = "Sku,Barcode,Name,Brand,Category,Description,Cost,Price,Currency,Stock,Active,Gtin,ImageUrls,SourceId", Width = 700, ToolTip = "Dışa aktarılacak alan anahtarları, virgülle" };
        var status = Hint("");
        var grid = new DataGrid { AutoGenerateColumns = true, Height = 330, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended };
        ExcelPreview? preview = null; ExcelDecisionPreview? decisions = null; string? selectedPath = null; ExcelColumnMapping? manualMapping = null; CatalogUndoReceipt? undo = null;

        void ApplyProfileText()
        {
            profile.Name = profileName.Text.Trim(); profile.CultureName = culture.Text.Trim(); profile.HeaderAliases = ParsePairs(aliases.Text); profile.Defaults = ParsePairs(defaults.Text); profile.VisibleFields = visibleFields.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            _ = ExcelProfileStore.Culture(profile.CultureName);
        }
        async Task RenderPreviewAsync()
        {
            if (selectedPath is null) return;
            ApplyProfileText(); decisions = await CatalogExcel.PreviewDecisionsAsync(store, selectedPath, profile, excelCts?.Token ?? CancellationToken.None); preview = decisions.Preview ?? new ExcelPreview(decisions.Rows.Where(x => x.Product is not null).Select(x => x.Product!).ToList(), decisions.Errors); grid.ItemsSource = decisions.Rows; status.Text = $"{decisions.Rows.Count} satır önizlendi · {decisions.Rows.Count(x => x.Action == "CREATE")} yeni · {decisions.Rows.Count(x => x.Action == "UPDATE")} güncellenecek · {decisions.Rows.Count(x => x.Action == "SKIP")} aynı · {decisions.Rows.Count(x => x.Action == "ERROR")} hatalı.";
        }
        void RenderPreview() => _ = RunAsync(RenderPreviewAsync);
        void LoadProfiles()
        {
            var list = profileStore.List(); profileBox.ItemsSource = list; profileBox.SelectedItem = list.FirstOrDefault(x => x.Id == profile.Id); if (profileBox.SelectedItem is null) { profileStore.Save(profile); list = profileStore.List(); profileBox.ItemsSource = list; profileBox.SelectedItem = list.FirstOrDefault(x => x.Id == profile.Id); }
        }
        profileBox.SelectionChanged += (_, _) => { if (profileBox.SelectedItem is not ExcelImportProfile selected) return; profile = selected; profileName.Text = profile.Name; culture.Text = profile.CultureName; aliases.Text = string.Join(Environment.NewLine, profile.HeaderAliases.Select(x => $"{x.Key}={x.Value}")); defaults.Text = string.Join(Environment.NewLine, profile.Defaults.Select(x => $"{x.Key}={x.Value}")); visibleFields.Text = string.Join(",", profile.VisibleFields); if (selectedPath is not null) RenderPreview(); };
        var saveProfile = Button("Profili kaydet", () => { ApplyProfileText(); profileStore.Save(profile); LoadProfiles(); status.Text = "Excel profili yerel olarak kaydedildi."; });
        var newProfile = Button("Yeni profil", () => { profile = new ExcelImportProfile { Name = "Yeni profil" }; profileName.Text = profile.Name; culture.Text = profile.CultureName; aliases.Clear(); defaults.Clear(); if (selectedPath is not null) RenderPreview(); });
        var choose = Button("Excel seç ve önizle", () => { var dialog = new OpenFileDialog { Filter = "Excel dosyası (*.xlsx)|*.xlsx" }; if (dialog.ShowDialog(this) != true) return; selectedPath = dialog.FileName; manualMapping = null; RenderPreview(); });
        var map = Button("Kolonları elle eşle", () => { if (selectedPath is null) { status.Text = "Önce Excel dosyasını seçin."; return; } var headers = CatalogExcel.Headers(selectedPath); var result = ShowMappingDialog(selectedPath, headers, manualMapping); if (result is null) return; manualMapping = result; profile.ColumnMappings = result.Columns.ToDictionary(x => x.Key, x => headers[x.Value - 1], StringComparer.OrdinalIgnoreCase); RenderPreview(); });
        var export = Button("Filtreli ürünleri dışa aktar", () => { ApplyProfileText(); var query = new TextBox { Width = 380, Text = "", ToolTip = "SKU/ürün/marka/kategori filtre metni" }; var dialog = new Window { Title = "Dışa aktarım filtresi", Width = 480, Height = 180, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner }; var ok = new Button { Content = "Excel'e aktar", Margin = new Thickness(3) }; var box = new StackPanel { Margin = new Thickness(14) }; box.Children.Add(new TextBlock { Text = "Filtre (boş: tüm ürünler)" }); box.Children.Add(query); box.Children.Add(ok); dialog.Content = box; ok.Click += (_, _) => dialog.DialogResult = true; if (dialog.ShowDialog() != true) return; var products = store.Products().Where(p => string.IsNullOrWhiteSpace(query.Text) || $"{p.Sku} {p.Barcode} {p.Name} {p.Brand} {p.Category}".Contains(query.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList(); var save = new SaveFileDialog { Filter = "Excel dosyası (*.xlsx)|*.xlsx", FileName = "urunler.xlsx" }; if (save.ShowDialog(this) != true) return; var started = DateTime.UtcNow; CatalogExcel.Export(save.FileName, products, profile.VisibleFields); new ReportRunStore(dataDirectory).Record("products-xlsx", started, ReportRunState.Succeeded, products.Count); status.Text = $"{products.Count} ürün ve seçili alanlar dışa aktarıldı."; });
        // #826: only the refused rows, stable schema, cancellable while it writes, disk errors reported as a sentence.
        CancellationTokenSource? rejectedExportCts = null;
        var cancelRejectedExport = new Button { Content = "Dışa aktarımı iptal et", Margin = new Thickness(3), Visibility = Visibility.Collapsed };
        cancelRejectedExport.Click += (_, _) => rejectedExportCts?.Cancel();
        Button errors = null!; errors = Button("Reddedilen satırları dışa aktar", () => { if (preview is null || preview.Errors.Count == 0) { status.Text = "Dışa aktarılacak reddedilen satır yok."; return; } var dialog = new SaveFileDialog { Filter = "Excel dosyası (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", FileName = "reddedilen-satirlar.xlsx" }; if (dialog.ShowDialog(this) != true) return; _ = ExportRejectedAsync(dialog.FileName, preview); });
        async Task ExportRejectedAsync(string path, ExcelPreview current)
        {
            rejectedExportCts?.Cancel(); rejectedExportCts = new CancellationTokenSource(); var token = rejectedExportCts.Token; var started = DateTime.UtcNow; var runs = new ReportRunStore(dataDirectory);
            cancelRejectedExport.Visibility = Visibility.Visible; errors.IsEnabled = false; status.Text = $"{current.Errors.Count:N0} reddedilen satır yazılıyor…";
            try
            {
                var result = await Task.Run(() => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? RejectedRowsExport.WriteCsv(path, RejectedRowsExport.Build(current.Errors), token) : CatalogExcel.ExportErrors(path, current, token), token);
                runs.Record("import-rejections", started, result.Success ? ReportRunState.Succeeded : result.Cancelled ? ReportRunState.Cancelled : ReportRunState.Failed, result.Written);
                if (result.Success) status.Text = $"{result.Written:N0} reddedilen satır dışa aktarıldı (şema v{RejectedRowsExport.SchemaVersion}).";
                else if (result.Cancelled) status.Text = result.Error;
                else Log(result.Error, NotificationSeverity.Error);
            }
            catch (OperationCanceledException) { runs.Record("import-rejections", started, ReportRunState.Cancelled, 0); status.Text = RejectedRowsExportResult.WasCancelled.Error; }
            finally { cancelRejectedExport.Visibility = Visibility.Collapsed; errors.IsEnabled = true; }
        }
        async Task ApplyExcelAsync()
        {
            if (decisions is null || selectedPath is null || decisions.Preview is null) throw new InvalidOperationException("Önce Excel profiliyle önizleme yapın.");
            var selectedProducts = grid.SelectedItems.OfType<ExcelPreviewDecision>().Where(x => x.Product is not null).Select(x => x.Product!).ToList();
            if (selectedProducts.Count == 0) throw new InvalidOperationException("Önizlemeden en az bir geçerli satır seçin.");
            var selected = selectedProducts.Select(x => decisions.Preview.Rows.IndexOf(x)).Where(x => x >= 0).ToArray();
            if (selected.Length != selectedProducts.Count) throw new InvalidOperationException("Seçili satır önizleme dışında.");
            excelCts?.Dispose(); excelCts = new CancellationTokenSource(); excelApplyButton!.IsEnabled = false; excelCancelButton!.IsEnabled = true;
            try
            {
                var source = new XmlSource { Id = "excel-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(selectedPath))).ToLowerInvariant()[..16], Name = Path.GetFileName(selectedPath), NumberCultureName = profile.CultureName };
                undo = await excelCoordinator.ApplyWithUndoAsync(store, source, decisions.Preview, selected, selectedPath, profile, excelCts.Token);
                status.Text = "Seçili Excel satırları atomik olarak uygulandı; geri alma kaydı oluşturuldu."; RefreshProducts();
            }
            finally { excelApplyButton!.IsEnabled = true; excelCancelButton!.IsEnabled = false; excelCts?.Dispose(); excelCts = null; }
        }
        excelApplyButton = Button("Seçili önizleme satırlarını uygula", () => _ = RunAsync(ApplyExcelAsync));
        excelCancelButton = Button("Excel uygulamasını iptal et", () => excelCts?.Cancel()); excelCancelButton.IsEnabled = false;
        var rollback = Button("Son Excel uygulamasını geri al", () => { if (undo is null) { status.Text = "Geri alınacak Excel işlemi yok."; return; } store.Undo(undo); undo = null; status.Text = "Son Excel uygulaması geri alındı."; RefreshProducts(); });
        var profileRow = new WrapPanel(); profileRow.Children.Add(new TextBlock { Text = "Profil", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }); profileRow.Children.Add(profileBox); profileRow.Children.Add(new TextBlock { Text = "Ad", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }); profileRow.Children.Add(profileName); profileRow.Children.Add(new TextBlock { Text = "Kültür", Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }); profileRow.Children.Add(culture); profileRow.Children.Add(saveProfile); profileRow.Children.Add(newProfile); panel.Children.Add(profileRow);
        var details = new StackPanel(); Label(details, "Başlık alias'ları", aliases); Label(details, "Varsayılan alanlar", defaults); Label(details, "Dışa aktarım görünür alanları", visibleFields); panel.Children.Add(details);
        var bar = new WrapPanel(); bar.Children.Add(export); bar.Children.Add(choose); bar.Children.Add(map); bar.Children.Add(errors); bar.Children.Add(cancelRejectedExport); bar.Children.Add(excelApplyButton); bar.Children.Add(excelCancelButton); bar.Children.Add(rollback); panel.Children.Add(bar); panel.Children.Add(grid); panel.Children.Add(status); LoadProfiles(); return Scroll(panel);
    }

    static Dictionary<string, string> ParsePairs(string text) => text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2 && x[0].Trim().Length > 0).ToDictionary(x => x[0].Trim(), x => x[1].Trim(), StringComparer.OrdinalIgnoreCase);
    // #833: the dialog is built by ExcelMappingDialog (required markers, masked samples, missing count, first-problem
    // jump, confirm gated on a mapping that can import); the page only supplies the file's headers and first row.
    internal ExcelMappingDialogView BuildExcelMappingDialog(string path, ExcelColumnMapping? existing)
    {
        var headers = CatalogExcel.Headers(path);
        IReadOnlyDictionary<string, string> firstRow;
        try { firstRow = CatalogExcel.FirstRow(path); } catch (Exception e) { firstRow = new Dictionary<string, string>(); Log("Örnek satır okunamadı: " + Safe(e)); }
        return ExcelMappingDialog.Build(this, headers, header => firstRow.TryGetValue(header, out var v) ? v : null, existing);
    }
    ExcelColumnMapping? ShowMappingDialog(string path, IReadOnlyList<string> headers, ExcelColumnMapping? existing)
    {
        _ = headers;
        var view = BuildExcelMappingDialog(path, existing);
        return view.Window.ShowDialog() == true ? view.Result : null;
    }
}
