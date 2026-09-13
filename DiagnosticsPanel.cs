using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TrMarketplaceHubDesktop;

public static class DiagnosticsPanel
{
    /// <param name="exposeReveal">#883: receives the function a correlation deep link calls to open its chain here (true when the chain has a visible event).</param>
    /// <param name="allowedStoreKeys">#883: the stores this session may open; a chain's events of other stores are counted, not shown.</param>
    /// <param name="openLink">#883: opens an entity a chain event points at (a product, an order) through the shell's own trail.</param>
    public static FrameworkElement Create(string? directory, Action<string>? navigate = null, Action<Func<string, bool>>? exposeReveal = null, Func<IReadOnlyList<string>>? allowedStoreKeys = null, Action<DrillTarget>? openLink = null)
    {
        var panel = new StackPanel { Margin = new Thickness(DesignTokens.SpacePage), MaxWidth = 1350 }; var errorHost = new StackPanel { Visibility = Visibility.Collapsed }; var errors = new ErrorSurface(errorHost, navigate); /* #816 */ var diagnostics = new DiagnosticsService(directory); var audit = new AuditStore(directory); var status = Hint(""); var checks = new DataGrid { AutoGenerateColumns = true, IsReadOnly = true, Height = 220, EnableRowVirtualization = true }; var events = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 390, EnableRowVirtualization = true }; foreach (var (header, path, width) in new[] { ("Zaman", "AtUtc", 160d), ("Modül", "Module", 110d), ("İşlem", "Action", 150d), ("Sonuç", "Outcome", 90d), ("Ürün", "ProductId", 120d), ("Sipariş", "OrderId", 120d), ("Korelasyon", "Correlation", 120d), ("Detay", "Detail", 430d) }) events.Columns.Add(GridColumns.Text(header, path, width));
        void Refresh() { var snapshot = diagnostics.Build(); checks.ItemsSource = snapshot.Checks; events.ItemsSource = audit.List(500); status.Text = $"{snapshot.ApplicationVersion} · {snapshot.PendingSync} bekleyen sync · {snapshot.FailedSync} başarısız · Son hata: {snapshot.LastError}"; }
        var query = new TextBox { Width = 260, ToolTip = "Audit modül, işlem, sipariş veya detay ara" }; query.TextChanged += (_, _) => events.ItemsSource = audit.List(500, query.Text); var refresh = Button(errors, "Sağlığı yenile", Refresh); var export = Button(errors, "Güvenli destek paketi dışa aktar", () => { var dialog = new SaveFileDialog { Filter = "Destek paketi (*.zip)|*.zip", FileName = ExportFileNames.Build("monobridge-destek", "zip") }; if (dialog.ShowDialog() != true) return; var started = DateTime.UtcNow; var path = SupportPackageService.Export(dialog.FileName, directory, overwrite: true); new ReportRunStore(directory).Record("support-package", started, ReportRunState.Succeeded, 0); status.Text = "Destek paketi oluşturuldu: " + path; }); var resetPreferences = Button(errors, PreferenceSchema.ResetTitle, () => { if (!DialogShell.Confirm(Window.GetWindow(panel), PreferenceSchema.ResetTitle, PreferenceSchema.ResetMessage, PreferenceSchema.ResetLabel)) return; var removed = PreferenceSchema.Reset(PreferenceSchema.OpenStore(directory)); audit.Append(new() { Module = "preferences", Action = "reset", Outcome = "OK", Detail = $"{removed} tercih kaydı silindi" }); Refresh(); status.Text = $"{removed} tercih kaydı silindi; varsayılanlar bir sonraki açılışta uygulanır."; }); var openSync = Button(errors, "Sync merkezine git", () => navigate?.Invoke("sync")); var openConnections = Button(errors, "Mağaza bağlantılarına git", () => navigate?.Invoke("connections"));

        // #883: one operation's chain — every audit row written under its correlation id — as safe summaries, scoped to the stores this session may open.
        var correlationBox = new TextBox { Width = 200, Tag = "correlation-box", ToolTip = "Korelasyon kimliği: bir işlemin içe aktarma, iş, sipariş ve kaynak olaylarını birlikte gösterir" };
        var chainSummary = Hint(""); chainSummary.Tag = "correlation-summary"; System.Windows.Automation.AutomationProperties.SetName(chainSummary, "Korelasyon zinciri özeti");
        var chainLinks = new WrapPanel();
        bool Reveal(string? correlation)
        {
            var allowed = allowedStoreKeys?.Invoke();
            var id = CorrelationChain.SafeId(correlation);
            correlationBox.Text = id.Length == 0 ? (correlation ?? "").Trim() : id;
            var rows = id.Length == 0 ? Array.Empty<AuditEvent>() : audit.ListByCorrelation(id);
            var view = CorrelationChain.Compose(id.Length == 0 ? correlation : id, rows, allowed);
            events.ItemsSource = CorrelationChain.Visible(id, rows, allowed);
            chainSummary.Text = "Korelasyon " + (id.Length == 0 ? "" : id + ": ") + CorrelationChain.Headline(view) + (view.Note.Length > 0 && !view.IsEmpty ? " · " + view.Note : "");
            chainLinks.Children.Clear();
            foreach (var entity in view.Events.Select(e => e.Entity).Where(e => e is not null).DistinctBy(e => e!.EntityKind + "/" + e.EntityId))
            {
                var link = entity!; var button = new Button { Content = link.EntityLabel + " aç", Margin = Spacing.Control, Tag = "correlation-entity" };
                button.Click += (_, _) => { try { errors.Clear(); openLink?.Invoke(link); } catch (Exception error) { errors.Show(error); } };
                chainLinks.Children.Add(button);
            }
            return !view.IsEmpty;
        }
        var openChain = Button(errors, "Zinciri aç", () => Reveal(correlationBox.Text)); openChain.Tag = "correlation-open";
        var chainOfSelection = Button(errors, "Seçili kaydın zinciri", () => { if (events.SelectedItem is AuditEvent row && CorrelationChain.IsId(row.Correlation)) Reveal(row.Correlation); else status.Text = "Seçili kaydın korelasyon kimliği yok."; }); chainOfSelection.Tag = "correlation-selected";
        var clearChain = Button(errors, "Listeye dön", () => { correlationBox.Text = ""; chainSummary.Text = ""; chainLinks.Children.Clear(); events.ItemsSource = audit.List(500, query.Text); }); clearChain.Tag = "correlation-clear";
        events.MouseDoubleClick += (_, _) => { if (events.SelectedItem is AuditEvent row && CorrelationChain.IsId(row.Correlation)) Reveal(row.Correlation); };
        exposeReveal?.Invoke(Reveal);

        panel.Children.Add(Heading("Tanılama, audit trail ve destek merkezi")); panel.Children.Add(errorHost); panel.Children.Add(Hint("Sağlık özeti yerel DB/queue durumunu okur. Audit yalnız sanitized metadata saklar; destek paketi token, password, API key veya şifreli credential byte'larını içermez.")); var bar = new WrapPanel(); bar.Children.Add(new TextBlock { Text = "Audit ara", Margin = Spacing.Inline, VerticalAlignment = VerticalAlignment.Center }); bar.Children.Add(query); bar.Children.Add(refresh); bar.Children.Add(export); bar.Children.Add(resetPreferences); bar.Children.Add(openSync); bar.Children.Add(openConnections); panel.Children.Add(bar);
        var chainBar = new WrapPanel(); chainBar.Children.Add(new TextBlock { Text = "Korelasyon", Margin = Spacing.Inline, VerticalAlignment = VerticalAlignment.Center }); chainBar.Children.Add(correlationBox); chainBar.Children.Add(openChain); chainBar.Children.Add(chainOfSelection); chainBar.Children.Add(clearChain); panel.Children.Add(chainBar); panel.Children.Add(chainSummary); panel.Children.Add(chainLinks);
        panel.Children.Add(Heading("Sistem sağlık özeti")); panel.Children.Add(checks); panel.Children.Add(Heading("Audit trail")); panel.Children.Add(events); panel.Children.Add(status); Refresh(); return Scroll(panel);
    }
    static TextBlock Heading(string text) => TextStyles.Apply(new TextBlock { Text = text, Margin = Spacing.TitleBlock }, TextRole.SectionTitle);
    static TextBlock Hint(string text) => TextStyles.Apply(new TextBlock { Text = text, Margin = Spacing.HintBlock, TextWrapping = TextWrapping.Wrap }, TextRole.Hint);
    static Button Button(ErrorSurface errors, string text, Action action) { var button = new Button { Content = text, Margin = Spacing.Control }; button.Click += (_, _) => { try { errors.Clear(); action(); } catch (Exception error) { errors.Show(error); } }; return button; }
    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
}
