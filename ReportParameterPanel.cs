using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace TrMarketplaceHubDesktop;

public sealed record ReportStoreOption(string Key, string Label);
public sealed record ReportStateOption(string Key, string Label);

/// <summary>
/// The report run setup (#847): a side panel per report with typed controls for the parameters its schema names
/// (store combo restricted to the shell's offered stores, two date pickers, a delivery-state combo, a search box),
/// defaults filled on open, a validation line per field plus a summary, saved filters (load / save / delete under
/// the report's own preference module), and the actions -- "Çalıştır" for the report the workspace runs itself,
/// "Ekranda aç" for a report another screen owns. Every control is a labelled tab stop; Enter runs.
/// </summary>
public static class ReportParameterPanel
{
    /// <param name="Run">Runs the report with the validated parameters and returns the outcome text; null when the report is not runnable here.</param>
    public sealed record Context(ReportDefinition Definition, Func<IReadOnlyCollection<string>?> AllowedStoreKeys, UiPreferenceStore Preferences, Action<string>? Navigate, Func<ReportParameterSet, Task<string?>>? Run, DateTime? NowUtc = null);

    public const string RunLabel = "Çalıştır";
    public const string OpenLabel = "Ekranda aç";
    public const string NoParametersText = "Bu raporun parametresi yok.";

    public static FrameworkElement Build(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var definition = context.Definition; var schema = ReportParameters.SchemaFor(definition); var module = ReportCatalog.FilterModulePrefix + definition.Key;
        var allowed = context.AllowedStoreKeys();
        var root = new StackPanel { Tag = "report-setup", Margin = new Thickness(12) };
        root.Children.Add(new TextBlock { Tag = "report-setup-title", Text = definition.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = definition.Purpose, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10), Opacity = 0.9 });

        var storeItems = (allowed ?? Array.Empty<string>()).Select(k => new ReportStoreOption(k, ReportParameters.StoreLabel(k))).ToList();
        var store = new ComboBox { Tag = "report-param-store", ItemsSource = storeItems, DisplayMemberPath = "Label", IsEnabled = storeItems.Count > 0 };
        var storeField = FormField.Build(new FormFieldSpec("Mağaza", Required: true, Help: storeItems.Count == 0 ? "Bu oturumda sunulan mağaza yok; önce bir bağlantı ekleyip etkinleştirin." : "Rapor yalnız seçili mağazanın kayıtlarını okur."), store);
        var from = new DatePicker { Tag = "report-param-from" }; var fromField = FormField.Build(new FormFieldSpec("Başlangıç tarihi", Required: true, Help: "Kayıt güncelleme gününe göre, gün dahil."), from);
        var to = new DatePicker { Tag = "report-param-to" }; var toField = FormField.Build(new FormFieldSpec("Bitiş tarihi", Required: true, Help: $"En fazla {ReportParameters.MaxRangeDays} günlük aralık."), to);
        var stateItems = new[] { new ReportStateOption("", "Tümü") }.Concat(OrdersRules.States.Select(s => new ReportStateOption(s, OrdersRules.Label(s)))).ToList();
        var state = new ComboBox { Tag = "report-param-state", ItemsSource = stateItems, DisplayMemberPath = "Label", SelectedIndex = 0 }; var stateField = FormField.Build(new FormFieldSpec("Teslimat durumu", Help: "Boş bırakınca tüm durumlar."), state);
        var query = new TextBox { Tag = "report-param-query", MaxLength = ReportParameters.MaxQueryLength }; var queryField = FormField.Build(new FormFieldSpec("Arama metni", Help: "Sipariş no, SKU veya ürün adı; kişisel veri aranmaz."), query);
        if (schema.Store) root.Children.Add(storeField.Root);
        if (schema.DateRange) { root.Children.Add(fromField.Root); root.Children.Add(toField.Root); }
        if (schema.DeliveryState) root.Children.Add(stateField.Root);
        if (schema.Query) root.Children.Add(queryField.Root);
        if (!schema.Any) root.Children.Add(new TextBlock { Tag = "report-setup-none", Text = NoParametersText, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 6) });
        var validation = new TextBlock { Tag = "report-param-validation", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 6), Foreground = SeverityStyle.AccentBrush(SeverityLevel.Blocking, SeverityStyle.IsHighContrast) };
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Polite); AutomationProperties.SetName(validation, "Parametre doğrulaması");
        root.Children.Add(validation);

        var saved = new ComboBox { Tag = "report-param-saved", DisplayMemberPath = "Name", Width = 190, Margin = new Thickness(0, 0, 6, 4) }; AutomationProperties.SetName(saved, "Kayıtlı filtre");
        var savedName = new TextBox { Tag = "report-param-saved-name", Width = 150, MaxLength = 100, Margin = new Thickness(0, 0, 6, 4), ToolTip = "Yeni filtre adı" }; AutomationProperties.SetName(savedName, "Filtre adı");
        var load = new Button { Tag = "report-param-load", Content = "Yükle", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4) };
        var delete = new Button { Tag = "report-param-delete", Content = "Sil", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4) };
        var save = new Button { Tag = "report-param-save", Content = "Kaydet", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 0, 4) };
        var savedBar = new WrapPanel(); savedBar.Children.Add(saved); savedBar.Children.Add(load); savedBar.Children.Add(delete); savedBar.Children.Add(savedName); savedBar.Children.Add(save);
        if (schema.Any) { root.Children.Add(new TextBlock { Text = "Kayıtlı filtreler", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) }); root.Children.Add(savedBar); }

        var status = new TextBlock { Tag = "report-param-status", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }; AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var runnable = context.Run is not null && ReportRunner.CanRun(definition);
        var run = new Button { Tag = "report-param-run", Content = RunLabel, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0), Visibility = runnable ? Visibility.Visible : Visibility.Collapsed, IsDefault = runnable };
        var open = new Button { Tag = "report-param-open", Content = OpenLabel, Padding = new Thickness(12, 3, 12, 3), Visibility = definition.Route == "reports" || context.Navigate is null ? Visibility.Collapsed : Visibility.Visible, IsDefault = !runnable };
        actions.Children.Add(run); actions.Children.Add(open); root.Children.Add(actions); root.Children.Add(status);

        var applying = false; var running = false;
        ReportParameterSet Current() => new(definition.Key,
            schema.Store ? (store.SelectedItem as ReportStoreOption)?.Key ?? "" : "",
            schema.DateRange ? AsUtcDay(from.SelectedDate) : null, schema.DateRange ? AsUtcDay(to.SelectedDate) : null,
            schema.DeliveryState ? (state.SelectedItem as ReportStateOption)?.Key ?? "" : "",
            schema.Query ? query.Text.Trim() : "");
        static string Message(IEnumerable<ReportParameterFinding> findings, params string[] fields) => string.Join(" ", findings.Where(f => fields.Contains(f.Field)).Select(f => f.Message));
        static SeverityLevel Level(IEnumerable<ReportParameterFinding> findings, params string[] fields) => findings.Where(f => fields.Contains(f.Field)).Select(f => f.Level).DefaultIfEmpty(SeverityLevel.Blocking).Max();
        IReadOnlyList<ReportParameterFinding> Revalidate()
        {
            var findings = ReportParameters.Validate(Current(), definition, context.AllowedStoreKeys(), context.NowUtc ?? DateTime.UtcNow);
            storeField.SetValidation(Message(findings, "store"), Level(findings, "store"));
            fromField.SetValidation(Message(findings, "range"), Level(findings, "range"));
            toField.SetValidation(Message(findings, "to"), Level(findings, "to"));
            stateField.SetValidation(Message(findings, "state"), Level(findings, "state"));
            queryField.SetValidation(Message(findings, "query"), Level(findings, "query"));
            var blocking = findings.Where(f => f.Level == SeverityLevel.Blocking).ToList(); var glyph = SeverityStyle.For(SeverityLevel.Blocking, SeverityStyle.IsHighContrast).Glyph;
            validation.Text = blocking.Count == 0 ? "" : string.Join(Environment.NewLine, blocking.Select(f => $"{glyph} {f.Message}")); validation.Visibility = blocking.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            run.IsEnabled = blocking.Count == 0 && !running;
            return findings;
        }
        void Apply(ReportParameterSet p)
        {
            applying = true;
            try
            {
                store.SelectedItem = storeItems.FirstOrDefault(o => o.Key == p.StoreKey);
                from.SelectedDate = AsPickerDay(p.FromUtc); to.SelectedDate = AsPickerDay(p.ToUtc);
                state.SelectedItem = stateItems.FirstOrDefault(o => o.Key == p.DeliveryState) ?? stateItems[0];
                query.Text = p.Query;
            }
            finally { applying = false; }
            Revalidate();
        }
        void RefreshSaved(string? select = null)
        {
            var views = context.Preferences.ListViews(module); saved.ItemsSource = views; saved.SelectedItem = select is null ? null : views.FirstOrDefault(v => v.Name == select);
            load.IsEnabled = views.Count > 0; delete.IsEnabled = views.Count > 0;
        }
        store.SelectionChanged += (_, _) => { if (!applying) Revalidate(); };
        from.SelectedDateChanged += (_, _) => { if (!applying) Revalidate(); };
        to.SelectedDateChanged += (_, _) => { if (!applying) Revalidate(); };
        state.SelectionChanged += (_, _) => { if (!applying) Revalidate(); };
        query.TextChanged += (_, _) => { if (!applying) Revalidate(); };
        load.Click += (_, _) =>
        {
            if (saved.SelectedItem is not SavedUiView view) { status.Text = "Önce kayıtlı bir filtre seçin."; return; }
            var loaded = ReportParameters.Deserialize(definition.Key, view.Payload);
            if (loaded is null) { status.Text = $"'{view.Name}' okunamadı; mevcut değerler korunuyor."; return; }
            Apply(loaded); status.Text = $"'{view.Name}' yüklendi.";
        };
        save.Click += (_, _) =>
        {
            var name = savedName.Text.Trim();
            if (name.Length == 0) { status.Text = "Kaydetmek için filtre adı girin."; savedName.Focus(); return; }
            try { context.Preferences.SaveView(module, name, ReportParameters.Serialize(Current())); RefreshSaved(name); savedName.Text = ""; status.Text = $"'{name}' kaydedildi."; }
            catch (ArgumentException error) { status.Text = AuditStore.Sanitize(error.Message); }
        };
        delete.Click += (_, _) =>
        {
            if (saved.SelectedItem is not SavedUiView view) { status.Text = "Önce silinecek filtreyi seçin."; return; }
            context.Preferences.DeleteView(module, view.Name); RefreshSaved(); status.Text = $"'{view.Name}' silindi.";
        };
        open.Click += (_, _) => context.Navigate?.Invoke(definition.Route);
        run.Click += async (_, _) =>
        {
            if (running || context.Run is null) return;
            var findings = Revalidate(); if (!ReportParameters.IsValid(findings)) { status.Text = "Önce parametre hatalarını düzeltin."; return; }
            running = true; run.IsEnabled = false; status.Text = "Çalıştırılıyor…";
            try { status.Text = await context.Run(Current()) ?? "Çalıştırma vazgeçildi; dosya seçilmedi."; }
            catch (Exception error) { status.Text = "Çalıştırılamadı: " + AuditStore.Sanitize(error.Message); }
            finally { running = false; Revalidate(); }
        };
        Apply(ReportParameters.Defaults(definition, allowed, context.NowUtc ?? DateTime.UtcNow)); RefreshSaved();
        return root;
    }

    static DateTime? AsUtcDay(DateTime? picked) => picked is null ? null : DateTime.SpecifyKind(picked.Value.Date, DateTimeKind.Utc);
    static DateTime? AsPickerDay(DateTime? day) => day is null ? null : DateTime.SpecifyKind(day.Value.Date, DateTimeKind.Unspecified);
}
