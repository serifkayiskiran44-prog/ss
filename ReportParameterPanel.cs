using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

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
/// stage, terminal diagnostics that are sanitized and lead to the diagnostics screen, and a retry that repeats the
/// last action. The result (#849): the rows in a grid whose columns are the report's schema as the operator
/// arranged them -- order, visibility and DIP widths persisted per report, the classified column hidden until the
/// PII policy allows and masked even then -- a chooser to arrange them, and "CSV'ye aktar" that writes exactly the
/// visible columns in their order.
/// </summary>
public static class ReportParameterPanel
{
    /// <param name="Query">Runs the report's query with the validated parameters, reporting stage events and honouring the token; null when the report is not runnable here.</param>
    /// <param name="Export">Writes the given columns of a result; <c>retry</c> re-writes into the previous file. Returns the outcome text, or null when the operator picked no file.</param>
    /// <param name="ClassifiedAllowed">Whether the PII policy currently allows classified columns to be shown (masked).</param>
    public sealed record Context(ReportDefinition Definition, Func<IReadOnlyCollection<string>?> AllowedStoreKeys, UiPreferenceStore Preferences, Action<string>? Navigate,
        Func<ReportParameterSet, IProgress<ReportRunProgressEvent>, CancellationToken, Task<ReportQueryOutcome>>? Query,
        Func<ReportResult, IReadOnlyList<string>, IProgress<ReportRunProgressEvent>, CancellationToken, bool, Task<string?>>? Export,
        Func<bool>? ClassifiedAllowed = null, DateTime? NowUtc = null);

    public const string RunLabel = "Çalıştır";
    public const string OpenLabel = "Ekranda aç";
    public const string CancelLabel = "İptal";
    public const string RetryLabel = "Yeniden dene";
    public const string ExportLabel = "CSV'ye aktar";
    public const string ColumnsLabel = "Kolonlar";
    public const string NoParametersText = "Bu raporun parametresi yok.";
    public const double ResultGridHeight = 260;

    public static FrameworkElement Build(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var definition = context.Definition; var schema = ReportParameters.SchemaFor(definition); var module = ReportCatalog.FilterModulePrefix + definition.Key;
        var allowed = context.AllowedStoreKeys();
        var root = new StackPanel { Tag = "report-setup", Margin = Spacing.Section };
        root.Children.Add(new TextBlock { Tag = "report-setup-title", Text = definition.Title, FontSize = DesignTokens.TextSubsectionTitleSize, FontWeight = DesignTokens.FontWeightTitle, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock { Text = definition.Purpose, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10), Opacity = 0.9 });

        var storeItems = (allowed ?? Array.Empty<string>()).Select(k => new ReportStoreOption(k, ReportParameters.StoreLabel(k))).ToList();
        var store = new ComboBox { Tag = "report-param-store", ItemsSource = storeItems, DisplayMemberPath = "Label" }; CommandState.Apply(store, storeItems.Count == 0 ? DisabledReason.StoreState("Bağlı mağaza yok.") : null);
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
        var load = new Button { Tag = "report-param-load", Content = "Yükle", Padding = Spacing.Chip, Margin = new Thickness(0, 0, 6, 4) };
        var delete = new Button { Tag = "report-param-delete", Content = "Sil", Padding = Spacing.Chip, Margin = new Thickness(0, 0, 6, 4) };
        var save = new Button { Tag = "report-param-save", Content = "Kaydet", Padding = Spacing.Chip, Margin = Spacing.BelowInline };
        var savedBar = new WrapPanel(); savedBar.Children.Add(saved); savedBar.Children.Add(load); savedBar.Children.Add(delete); savedBar.Children.Add(savedName); savedBar.Children.Add(save);
        if (schema.Any) { root.Children.Add(new TextBlock { Text = "Kayıtlı filtreler", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) }); root.Children.Add(savedBar); }

        var status = new TextBlock { Tag = "report-param-status", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }; AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var runnable = context.Query is not null && ReportRunner.CanRun(definition);
        var run = new Button { Tag = "report-param-run", Content = RunLabel, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0), Visibility = runnable ? Visibility.Visible : Visibility.Collapsed, IsDefault = runnable };
        var open = new Button { Tag = "report-param-open", Content = OpenLabel, Padding = new Thickness(12, 3, 12, 3), Visibility = definition.Route == "reports" || context.Navigate is null ? Visibility.Collapsed : Visibility.Visible, IsDefault = !runnable };
        actions.Children.Add(run); actions.Children.Add(open); root.Children.Add(actions);

        // #848: the run view -- one row per stage, cancel while running, diagnostics and retry after a failure or cancellation.
        var progressHost = new StackPanel { Tag = "report-run-progress", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        var progressHeadline = new TextBlock { Tag = "report-run-headline", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = Spacing.BelowInline }; AutomationProperties.SetLiveSetting(progressHeadline, AutomationLiveSetting.Polite);
        var progressRows = new StackPanel();
        var diagnostics = new TextBlock { Tag = "report-run-diagnostics", TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = Spacing.AboveInline }; AutomationProperties.SetLiveSetting(diagnostics, AutomationLiveSetting.Assertive);
        var runActions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var cancel = new Button { Tag = "report-run-cancel", Content = CancelLabel, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed, IsCancel = true };
        var retry = new Button { Tag = "report-run-retry", Content = RetryLabel, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
        var openDiagnostics = new Button { Tag = "report-run-diagnostics-open", Content = "Tanılamaya git", Padding = new Thickness(10, 2, 10, 2), Visibility = Visibility.Collapsed };
        runActions.Children.Add(cancel); runActions.Children.Add(retry); runActions.Children.Add(openDiagnostics);
        var cancelOutcome = new TextBlock { Tag = CancellationOutcome.Tag, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = Spacing.BelowInline }; AutomationProperties.SetLiveSetting(cancelOutcome, AutomationLiveSetting.Polite);
        progressHost.Children.Add(progressHeadline); progressHost.Children.Add(cancelOutcome); progressHost.Children.Add(progressRows); progressHost.Children.Add(diagnostics); progressHost.Children.Add(runActions);
        if (runnable) root.Children.Add(progressHost);

        // #849: the result -- summary, the column chooser, the export, the grid.
        var resultHost = new StackPanel { Tag = "report-result", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        var resultSummary = new TextBlock { Tag = "report-result-summary", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = Spacing.BelowInline }; AutomationProperties.SetLiveSetting(resultSummary, AutomationLiveSetting.Polite);
        var resultBar = new WrapPanel { Margin = Spacing.BelowInline };
        var columnsButton = new Button { Tag = "report-result-columns", Content = ColumnsLabel + "…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) }; AutomationProperties.SetName(columnsButton, "Sonuç kolonlarını düzenle");
        var exportButton = new Button { Tag = "report-result-export", Content = ExportLabel, Padding = new Thickness(10, 2, 10, 2), Visibility = context.Export is null ? Visibility.Collapsed : Visibility.Visible };
        resultBar.Children.Add(columnsButton); resultBar.Children.Add(exportButton);
        var grid = new DataGrid { Tag = "report-result-grid", AutoGenerateColumns = false, IsReadOnly = true, Height = ResultGridHeight, EnableRowVirtualization = true, EnableColumnVirtualization = true, CanUserReorderColumns = true, CanUserResizeColumns = true, CanUserSortColumns = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column };
        AutomationProperties.SetName(grid, "Rapor sonucu");
        // #850: one surface for every outcome that is not a listed result -- true empty, filtered empty, failed, cancelled, schema incompatible -- each with its own calls to action.
        var stateHost = new Border { Tag = "report-result-state", Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1), Padding = new Thickness(10), Margin = new Thickness(0, 4, 0, 4) };
        var stateTitle = new TextBlock { Tag = "report-result-state-title", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetLiveSetting(stateTitle, AutomationLiveSetting.Assertive);
        var stateText = new TextBlock { Tag = "report-result-state-text", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
        var stateActions = new WrapPanel { Tag = "report-result-state-actions" };
        var stateBody = new StackPanel(); stateBody.Children.Add(stateTitle); stateBody.Children.Add(stateText); stateBody.Children.Add(stateActions); stateHost.Child = stateBody;
        resultHost.Children.Add(resultSummary); resultHost.Children.Add(resultBar); resultHost.Children.Add(stateHost); resultHost.Children.Add(grid);
        if (runnable) root.Children.Add(resultHost);
        root.Children.Add(status);

        var applying = false; var running = false; var buildingGrid = false; var lastAction = "query";
        var progress = new ReportRunProgressState(); CancellationTokenSource? runCts = null; ReportResult? result = null; ReportQueryOutcome? lastOutcome = null; ReportParameterSet? lastParameters = null;
        var columnSchema = ReportColumns.SchemaFor(definition);
        bool ClassifiedAllowed() { try { return context.ClassifiedAllowed?.Invoke() ?? false; } catch (Exception) { return false; } }
        var layout = ReportColumns.Resolve(columnSchema, SafeGet(context.Preferences, ReportColumns.PreferenceKey(definition.Key)), ClassifiedAllowed());

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
            CommandState.Apply(run, running ? DisabledReason.Busy("Rapor çalışıyor.") : blocking.Count > 0 ? DisabledReason.Validation($"{blocking.Count} engelleyici bulgu var.") : null);
            CommandState.Apply(exportButton, running ? DisabledReason.Busy("Rapor çalışıyor.") : result is null ? DisabledReason.StoreState("Henüz sonuç yok.") : null);
            return findings;
        }
        void RenderProgress()
        {
            var now = DateTime.UtcNow; var hc = SeverityStyle.IsHighContrast; progressRows.Children.Clear();
            foreach (var stage in progress.Snapshot(now))
            {
                var row = new DockPanel { Tag = "report-run-stage-" + stage.Stage.ToString().ToLowerInvariant(), Margin = new Thickness(0, 1, 0, 1) };
                var label = new TextBlock { Text = $"{ReportRunProgressState.Glyph(stage.Status)} {stage.Label}", Width = 120, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(label, Dock.Left); row.Children.Add(label);
                var counter = new TextBlock { Tag = "report-run-counter", Text = (stage.Counter.Length > 0 ? stage.Counter + " · " : "") + ReportRunProgressState.StatusWord(stage.Status) + (stage.Elapsed > TimeSpan.Zero ? $" · {stage.Elapsed.TotalSeconds:0.#} sn" : ""), Width = 150, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = DesignTokens.TextCaptionSize }; DockPanel.SetDock(counter, Dock.Right); row.Children.Add(counter);
                var bar = new ProgressBar { Tag = "report-run-bar-" + stage.Stage.ToString().ToLowerInvariant(), Height = 10, Margin = new Thickness(6, 0, 6, 0), Minimum = 0, Maximum = 100, IsIndeterminate = stage.IsIndeterminate, Value = stage.Percent ?? (stage.Status == ReportRunStageStatus.Done ? 100 : 0) };
                AutomationProperties.SetName(bar, $"{stage.Label}: {ReportRunProgressState.StatusWord(stage.Status)}" + (stage.Percent is { } pc ? $", yüzde {pc:0}" : stage.Counter.Length > 0 ? ", " + stage.Counter : ""));
                row.Children.Add(bar); progressRows.Children.Add(row);
            }
            progressHeadline.Text = progress.Headline(now);
            // #890: the cancellation outcome from the run's real state, beside the headline.
            var verdict = CancellationOutcome.Describe(progress.CancellationFacts(now), "Dosya yazılmadı; sonuç değişmedi.");
            cancelOutcome.Text = verdict.Line; cancelOutcome.Visibility = verdict.Phase == CancellationPhase.NotRequested ? Visibility.Collapsed : Visibility.Visible; cancelOutcome.Foreground = SeverityStyle.AccentBrush(verdict.Level, hc);
            cancel.Visibility = running && progress.Failed is null && !progress.IsComplete ? Visibility.Visible : Visibility.Collapsed;
            retry.Visibility = !running && progress.Failed is not null ? Visibility.Visible : Visibility.Collapsed;
            var failed = progress.Failed is { } f && progress[f].Status == ReportRunStageStatus.Failed;
            diagnostics.Text = progress.Diagnostics; diagnostics.Visibility = progress.Failed is null ? Visibility.Collapsed : Visibility.Visible;
            diagnostics.Foreground = SeverityStyle.AccentBrush(failed ? SeverityLevel.Blocking : SeverityLevel.Warning, hc);
            openDiagnostics.Visibility = failed && context.Navigate is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        void CaptureWidths()
        {
            foreach (var column in grid.Columns) if (ColumnKey(column) is { } key && column.ActualWidth > 0) layout = ReportColumns.Resize(layout, key, column.ActualWidth);
        }
        void SaveLayout()
        {
            try { PreferenceSchema.Write(context.Preferences, ReportColumns.PreferenceKey(definition.Key), ReportColumns.Persist(layout)); }
            catch (Exception error) { status.Text = "Kolon düzeni kaydedilemedi: " + AuditStore.Sanitize(error.Message); }
        }
        void RenderState(ReportResultStateModel model)
        {
            stateActions.Children.Clear();
            if (model.ShowsGrid || model.Kind == ReportResultKind.None) { stateHost.Visibility = Visibility.Collapsed; return; }
            var hc = SeverityStyle.IsHighContrast; var style = SeverityStyle.For(model.Level, hc);
            stateTitle.Text = $"{style.Glyph} {model.Title}"; stateText.Text = model.Text;
            stateHost.BorderBrush = SeverityStyle.AccentBrush(model.Level, hc); stateHost.BorderThickness = new Thickness(style.BorderWeight);
            AutomationProperties.SetName(stateHost, $"{style.Word}: {model.Title}. {model.Text}");
            Button? first = null;
            foreach (var action in model.Actions)
            {
                var button = new Button { Tag = "report-result-action-" + action.Key, Content = action.Label, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 4) };
                var key = action.Key; button.Click += async (_, _) => await RunAction(key);
                stateActions.Children.Add(button); first ??= button;
            }
            stateHost.Visibility = Visibility.Visible;
            // The primary call to action takes the keyboard once the surface is laid out (#819 trap: focus before layout is refused).
            if (first is not null) stateHost.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { if (stateHost.IsVisible && !running) first.Focus(); }));
        }
        async Task RunAction(string key)
        {
            switch (key)
            {
                case ReportResultStates.ActionRetry: await StartQuery(); break;
                case ReportResultStates.ActionDiagnostics: context.Navigate?.Invoke("diagnostics"); break;
                case ReportResultStates.ActionOpenOrders: context.Navigate?.Invoke("orders"); break;
                case ReportResultStates.ActionWidenRange:
                    var today = DateTime.SpecifyKind((context.NowUtc ?? DateTime.UtcNow).Date, DateTimeKind.Utc);
                    applying = true; try { from.SelectedDate = AsPickerDay(today.AddDays(-ReportResultStates.WidenToDays)); to.SelectedDate = AsPickerDay(today); } finally { applying = false; }
                    Revalidate(); await StartQuery(); break;
                case ReportResultStates.ActionClearState: state.SelectedIndex = 0; await StartQuery(); break;
                case ReportResultStates.ActionResetColumns: layout = ReportColumns.Default(columnSchema); SaveLayout(); RenderResult(); status.Text = "Kolon düzeni varsayılana döndü."; break;
            }
        }
        void RenderResult()
        {
            var model = ReportResultStates.Compose(lastOutcome, lastParameters, schema, layout.VisibleKeys);
            if (model.Kind == ReportResultKind.None) { resultHost.Visibility = Visibility.Collapsed; return; }
            RenderState(model);
            if (!model.ShowsGrid || result is null)
            {
                grid.Visibility = Visibility.Collapsed; grid.ItemsSource = null; resultSummary.Text = result is null ? model.Title : $"{result.Rows.Count:N0} satır · {ReportColumns.Summary(layout, ClassifiedAllowed())}";
                resultHost.Visibility = Visibility.Visible; CommandState.Apply(exportButton, DisabledReason.StoreState("Dışa aktarılacak sonuç yok.")); CommandState.Apply(columnsButton, result is null ? DisabledReason.StoreState("Henüz sonuç yok.") : null); return;
            }
            grid.Visibility = Visibility.Visible; CommandState.Apply(columnsButton, null);
            buildingGrid = true;
            try
            {
                grid.ItemsSource = null; grid.Columns.Clear();
                foreach (var choice in layout.Columns.Where(c => c.Visible))
                {
                    var column = GridColumns.Text(choice.Column.Label, $"[{choice.Column.Key}]", new DataGridLength(choice.Width), GridColumns.KindFor(choice.Column.Key));
                    column.SortMemberPath = $"[{choice.Column.Key}]"; column.MinWidth = Math.Max(column.MinWidth, ReportColumns.MinWidth); grid.Columns.Add(column);
                }
                grid.ItemsSource = result.Rows;
            }
            finally { buildingGrid = false; }
            resultSummary.Text = $"{result.Rows.Count:N0} satır · {ReportColumns.Summary(layout, ClassifiedAllowed())}";
            resultHost.Visibility = Visibility.Visible;
            CommandState.Apply(exportButton, running ? DisabledReason.Busy("Rapor çalışıyor.") : null);
        }
        void PersistGridOrder()
        {
            if (buildingGrid) return;
            var order = grid.Columns.OrderBy(c => c.DisplayIndex).Select(ColumnKey).Where(k => k is not null).Select(k => k!).ToList();
            layout = ReportColumns.Reorder(layout, order); CaptureWidths(); SaveLayout();
            if (result is not null) resultSummary.Text = $"{result.Rows.Count:N0} satır · {ReportColumns.Summary(layout, ClassifiedAllowed())}";
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
            var noViews = views.Count == 0 ? DisabledReason.StoreState("Kayıtlı filtre yok.") : null; CommandState.Apply(load, noViews); CommandState.Apply(delete, noViews);
        }
        void BeginRun() { runCts?.Dispose(); runCts = new CancellationTokenSource(); running = true; progress.BeginOperation(); CommandState.Apply(run, DisabledReason.Busy("Rapor çalışıyor.")); CommandState.Apply(exportButton, DisabledReason.Busy("Rapor çalışıyor.")); CommandState.Apply(cancel, null); progressHost.Visibility = Visibility.Visible; RenderProgress(); }
        void EndRun() { running = false; if (progress.Running is not null) progress.Cancel(DateTime.UtcNow); RenderProgress(); Revalidate(); }
        async Task StartQuery()
        {
            if (running || context.Query is null) return;
            var findings = Revalidate(); if (!ReportParameters.IsValid(findings)) { status.Text = "Önce parametre hatalarını düzeltin."; return; }
            lastAction = "query"; progress = new ReportRunProgressState(); BeginRun(); status.Text = "Sorgu çalıştırılıyor…";
            var reporter = new Progress<ReportRunProgressEvent>(e => { progress.Apply(e); RenderProgress(); });
            lastParameters = Current();
            try
            {
                var outcome = await context.Query(lastParameters, reporter, runCts!.Token);
                status.Text = outcome.Message; lastOutcome = outcome; result = outcome.State == ReportRunState.Succeeded ? outcome.Result : null;
                layout = ReportColumns.Resolve(columnSchema, SafeGet(context.Preferences, ReportColumns.PreferenceKey(definition.Key)), ClassifiedAllowed());
            }
            catch (OperationCanceledException) { status.Text = "Sorgu iptal edildi."; lastOutcome = new(ReportRunState.Cancelled, null, status.Text); result = null; }
            catch (Exception error) { var safe = AuditStore.Sanitize(error.Message); status.Text = "Çalıştırılamadı: " + safe; progress.Apply(new(progress.Running ?? ReportRunStage.Query, ReportRunStageStatus.Failed, Note: safe)); lastOutcome = new(ReportRunState.Failed, null, status.Text); result = null; }
            finally { EndRun(); RenderResult(); }
        }
        async Task StartExport(bool isRetry)
        {
            if (running || context.Export is null) return;
            if (result is null) { status.Text = "Önce raporu çalıştırın; dışa aktarılacak sonuç yok."; return; }
            lastAction = "export"; CaptureWidths(); var columns = layout.VisibleKeys;
            if (columns.Count == 0) { status.Text = "Dışa aktarılacak kolon seçin."; return; }
            progress.Apply(new(ReportRunStage.Export, ReportRunStageStatus.Pending)); BeginRun(); status.Text = "Dışa aktarılıyor…";
            var reporter = new Progress<ReportRunProgressEvent>(e => { progress.Apply(e); RenderProgress(); });
            try
            {
                var message = await context.Export(result, columns, reporter, runCts!.Token, isRetry);
                status.Text = message ?? "Dışa aktarma vazgeçildi; dosya seçilmedi.";
            }
            catch (OperationCanceledException) { status.Text = "Dışa aktarma iptal edildi; dosya yazılmadı."; }
            catch (Exception error) { var safe = AuditStore.Sanitize(error.Message); status.Text = "Dışa aktarılamadı: " + safe; progress.Apply(new(ReportRunStage.Export, ReportRunStageStatus.Failed, Note: safe)); }
            finally { EndRun(); }
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
        run.Click += async (_, _) => await StartQuery();
        exportButton.Click += async (_, _) => await StartExport(isRetry: false);
        retry.Click += async (_, _) => { if (lastAction == "export") await StartExport(isRetry: true); else await StartQuery(); };
        cancel.Click += (_, _) => { if (!running) return; progress.RequestCancel(DateTime.UtcNow); runCts?.Cancel(); RenderProgress(); status.Text = CancellationOutcome.Describe(progress.CancellationFacts(DateTime.UtcNow), "Dosya yazılmadı; sonuç değişmedi.").Line; cancel.IsEnabled = false; };
        openDiagnostics.Click += (_, _) => context.Navigate?.Invoke("diagnostics");
        columnsButton.Click += (_, _) =>
        {
            CaptureWidths();
            var dialog = ReportColumnChooserDialog.Build(Window.GetWindow(root), layout, ClassifiedAllowed(), chosen => { layout = chosen; SaveLayout(); RenderResult(); status.Text = "Kolon düzeni kaydedildi."; });
            dialog.ShowDialog();
        };
        grid.ColumnDisplayIndexChanged += (_, _) => PersistGridOrder();
        grid.ColumnReordered += (_, _) => PersistGridOrder();
        Apply(ReportParameters.Defaults(definition, allowed, context.NowUtc ?? DateTime.UtcNow)); RefreshSaved();
        return root;
    }

    /// <summary>A result column carries its schema key as its sort path ("[OrderId]"); the header is a label and may change.</summary>
    public static string? ColumnKey(DataGridColumn column) => column?.SortMemberPath is { Length: > 2 } path && path[0] == '[' && path[^1] == ']' ? path[1..^1] : null;
    static string? SafeGet(UiPreferenceStore preferences, string key) { try { return PreferenceSchema.Read(preferences, key); } catch (Exception) { return null; } }
    static DateTime? AsUtcDay(DateTime? picked) => picked is null ? null : DateTime.SpecifyKind(picked.Value.Date, DateTimeKind.Utc);
    static DateTime? AsPickerDay(DateTime? day) => day is null ? null : DateTime.SpecifyKind(day.Value.Date, DateTimeKind.Unspecified);
}
