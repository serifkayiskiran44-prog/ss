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
/// The run view (#848): the run's stages -- query, generate, export -- as real progress rows (count and an
/// indeterminate bar while the total is unknown, a percentage once it is), a cancel that stops the run at its
/// stage, terminal diagnostics that are sanitized and lead to the diagnostics screen, and a retry that re-runs the
/// same parameters into the same file.
/// </summary>
public static class ReportParameterPanel
{
    /// <param name="Run">Runs the report with the validated parameters, reporting stage events and honouring the token; <c>retry</c> re-runs into the previous file. Returns the outcome text, or null when the operator picked no file. Null when the report is not runnable here.</param>
    public sealed record Context(ReportDefinition Definition, Func<IReadOnlyCollection<string>?> AllowedStoreKeys, UiPreferenceStore Preferences, Action<string>? Navigate, Func<ReportParameterSet, IProgress<ReportRunProgressEvent>, CancellationToken, bool, Task<string?>>? Run, DateTime? NowUtc = null);

    public const string RunLabel = "Çalıştır";
    public const string OpenLabel = "Ekranda aç";
    public const string CancelLabel = "İptal";
    public const string RetryLabel = "Yeniden dene";
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
        var query = new TextBox { Tag = "report-param-query", MaxLength = ReportParameters.MaxQueryLength }; var queryField = FormField.Build(new FormFieldSpec("Arama metni", Help: "Sipariş no, SKU veya ürün adı; kişisel veri aranmaz ve bu metin kayıtlara yazılmaz."), query);
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
        actions.Children.Add(run); actions.Children.Add(open); root.Children.Add(actions);

        // #848: the run view -- one row per stage, cancel while running, diagnostics and retry after a failure or cancellation.
        var progressHost = new StackPanel { Tag = "report-run-progress", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        var progressHeadline = new TextBlock { Tag = "report-run-headline", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) }; AutomationProperties.SetLiveSetting(progressHeadline, AutomationLiveSetting.Polite);
        var progressRows = new StackPanel();
        var diagnostics = new TextBlock { Tag = "report-run-diagnostics", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) }; AutomationProperties.SetLiveSetting(diagnostics, AutomationLiveSetting.Assertive);
        var runActions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var cancel = new Button { Tag = "report-run-cancel", Content = CancelLabel, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed, IsCancel = true };
        var retry = new Button { Tag = "report-run-retry", Content = RetryLabel, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
        var openDiagnostics = new Button { Tag = "report-run-diagnostics-open", Content = "Tanılamaya git", Padding = new Thickness(10, 2, 10, 2), Visibility = Visibility.Collapsed };
        runActions.Children.Add(cancel); runActions.Children.Add(retry); runActions.Children.Add(openDiagnostics);
        progressHost.Children.Add(progressHeadline); progressHost.Children.Add(progressRows); progressHost.Children.Add(diagnostics); progressHost.Children.Add(runActions);
        if (runnable) root.Children.Add(progressHost);
        root.Children.Add(status);

        var applying = false; var running = false; var progress = new ReportRunProgressState(); CancellationTokenSource? runCts = null;
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
        void RenderProgress()
        {
            var now = DateTime.UtcNow; var hc = SeverityStyle.IsHighContrast; progressRows.Children.Clear();
            foreach (var stage in progress.Snapshot(now))
            {
                var row = new DockPanel { Tag = "report-run-stage-" + stage.Stage.ToString().ToLowerInvariant(), Margin = new Thickness(0, 1, 0, 1) };
                var label = new TextBlock { Text = $"{ReportRunProgressState.Glyph(stage.Status)} {stage.Label}", Width = 120, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(label, Dock.Left); row.Children.Add(label);
                var counter = new TextBlock { Tag = "report-run-counter", Text = (stage.Counter.Length > 0 ? stage.Counter + " · " : "") + ReportRunProgressState.StatusWord(stage.Status) + (stage.Elapsed > TimeSpan.Zero ? $" · {stage.Elapsed.TotalSeconds:0.#} sn" : ""), Width = 150, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 }; DockPanel.SetDock(counter, Dock.Right); row.Children.Add(counter);
                var bar = new ProgressBar { Tag = "report-run-bar-" + stage.Stage.ToString().ToLowerInvariant(), Height = 10, Margin = new Thickness(6, 0, 6, 0), Minimum = 0, Maximum = 100, IsIndeterminate = stage.IsIndeterminate, Value = stage.Percent ?? (stage.Status == ReportRunStageStatus.Done ? 100 : 0) };
                AutomationProperties.SetName(bar, $"{stage.Label}: {ReportRunProgressState.StatusWord(stage.Status)}" + (stage.Percent is { } pc ? $", yüzde {pc:0}" : stage.Counter.Length > 0 ? ", " + stage.Counter : ""));
                row.Children.Add(bar); progressRows.Children.Add(row);
            }
            progressHeadline.Text = progress.Headline(now);
            cancel.Visibility = running && progress.Failed is null && !progress.IsComplete ? Visibility.Visible : Visibility.Collapsed;
            retry.Visibility = !running && progress.Failed is not null ? Visibility.Visible : Visibility.Collapsed;
            var failed = progress.Failed is { } f && progress[f].Status == ReportRunStageStatus.Failed;
            diagnostics.Text = progress.Diagnostics; diagnostics.Visibility = progress.Failed is null ? Visibility.Collapsed : Visibility.Visible;
            diagnostics.Foreground = SeverityStyle.AccentBrush(failed ? SeverityLevel.Blocking : SeverityLevel.Warning, hc);
            openDiagnostics.Visibility = failed && context.Navigate is not null ? Visibility.Visible : Visibility.Collapsed;
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
        async Task StartRun(bool isRetry)
        {
            if (running || context.Run is null) return;
            var findings = Revalidate(); if (!ReportParameters.IsValid(findings)) { status.Text = "Önce parametre hatalarını düzeltin."; return; }
            progress = new ReportRunProgressState(); runCts?.Dispose(); runCts = new CancellationTokenSource();
            running = true; run.IsEnabled = false; progressHost.Visibility = Visibility.Visible; status.Text = "Çalıştırılıyor…"; RenderProgress();
            var reporter = new Progress<ReportRunProgressEvent>(e => { progress.Apply(e); RenderProgress(); });
            try
            {
                var message = await context.Run(Current(), reporter, runCts.Token, isRetry);
                if (message is null) { status.Text = "Çalıştırma vazgeçildi; dosya seçilmedi."; progressHost.Visibility = Visibility.Collapsed; }
                else status.Text = message;
            }
            catch (OperationCanceledException) { status.Text = "Rapor iptal edildi; dosya yazılmadı."; }
            catch (Exception error) { var safe = AuditStore.Sanitize(error.Message); status.Text = "Çalıştırılamadı: " + safe; progress.Apply(new(progress.Running ?? ReportRunStage.Query, ReportRunStageStatus.Failed, Note: safe)); }
            finally
            {
                running = false;
                if (progress.Running is not null) progress.Cancel(DateTime.UtcNow);
                RenderProgress(); Revalidate();
            }
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
        run.Click += async (_, _) => await StartRun(isRetry: false);
        retry.Click += async (_, _) => await StartRun(isRetry: true);
        cancel.Click += (_, _) => { if (!running) return; runCts?.Cancel(); status.Text = "İptal istendi; sürmekte olan aşama durduruluyor."; cancel.IsEnabled = false; };
        openDiagnostics.Click += (_, _) => context.Navigate?.Invoke("diagnostics");
        Apply(ReportParameters.Defaults(definition, allowed, context.NowUtc ?? DateTime.UtcNow)); RefreshSaved();
        return root;
    }

    static DateTime? AsUtcDay(DateTime? picked) => picked is null ? null : DateTime.SpecifyKind(picked.Value.Date, DateTimeKind.Utc);
    static DateTime? AsPickerDay(DateTime? day) => day is null ? null : DateTime.SpecifyKind(day.Value.Date, DateTimeKind.Unspecified);
}
