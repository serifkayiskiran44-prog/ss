using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The settings shell (#853): the taxonomy's categories down the left, the selected category's entries on the
/// right -- each entry a link that opens the workspace (and the section) that owns the setting, or, for the few
/// pieces the settings shell owns itself, the inline host. A secret-bearing entry says so and is only ever linked:
/// credentials are edited on their own page with its masked controls. A search across every category's labels
/// and descriptions lands on the entry. Deep links ("settings/pricing") select a category from outside.
/// </summary>
public static class SettingsPanel
{
    /// <param name="OpenSection">Opens a route and selects a section inside it (a channel's connection tab); null falls back to Navigate.</param>
    /// <param name="EditState">#854: the app's settings edit state; a category and an entry with unsaved changes carry a badge that names the fields, never a value.</param>
    /// <param name="Validate">#856: the check behind the banner at the top -- the issues of the real stores plus what the forms reported; run on open, after every announced save and on every reported conflict.</param>
    public sealed record Context(string? Directory, Action<string>? Navigate, Func<string, bool> RouteExists, Action<string, string>? OpenSection = null, SettingsEditState? EditState = null, Func<IReadOnlyList<SettingsIssue>>? Validate = null);

    public const double CategoryWidth = 240;
    public const string SecretBadge = "Gizli bilgiler maskeli alanlarda, kendi sayfasında düzenlenir.";
    public const string DirtyMark = "●";

    public static FrameworkElement Create(Context context, Action<Action<string>>? exposeSelect = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var categories = SettingsTaxonomy.Visible(context.RouteExists);
        var root = new DockPanel { Margin = new Thickness(20), LastChildFill = true };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Ayarlar", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 8, 4, 6) });
        top.Children.Add(Hint("Var olan ayarlar tek ağaçta: her giriş, ayarı sahiplenen ekranı açar. Pazaryeri erişim bilgileri ilgili kanalın Bağlantı sekmesindeki maskeli alanlarda düzenlenir; bağlantı doğrulaması ürün aktarımının etkin olduğu anlamına gelmez."));
        // #856: the validation banner sits above the search, under the title, so an issue is the first thing on the page.
        var banner = new StackPanel { Tag = "settings-banner-host", Visibility = Visibility.Collapsed }; top.Children.Add(banner);
        var bar = new WrapPanel { Margin = new Thickness(4, 0, 4, 6) };
        var search = new TextBox { Tag = "settings-search", Width = 260, ToolTip = "Ayar ara: kategori, ad veya açıklama" }; AutomationProperties.SetName(search, "Ayar ara");
        bar.Children.Add(new TextBlock { Text = "Ara", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }); bar.Children.Add(search); top.Children.Add(bar);

        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CategoryWidth) }); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = new ListBox { Tag = "settings-categories", Margin = new Thickness(4, 0, 12, 0), MinHeight = 200, VerticalAlignment = VerticalAlignment.Top }; AutomationProperties.SetName(list, "Ayar kategorileri");
        foreach (var category in categories)
        {
            var item = new ListBoxItem { Tag = category.Key, Content = new TextBlock { Text = SettingsTaxonomy.ShortLabel(category.Label), TextWrapping = TextWrapping.Wrap }, ToolTip = category.Label + Environment.NewLine + category.Description, Padding = new Thickness(8, 6, 8, 6) };
            AutomationProperties.SetName(item, $"{category.Label}: {category.Entries.Count} ayar"); AutomationProperties.SetHelpText(item, category.Description);
            list.Items.Add(item);
        }
        var content = new StackPanel { Tag = "settings-content", Margin = new Thickness(4, 0, 4, 0) };
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); Grid.SetColumn(list, 0);
        layout.Children.Add(list); layout.Children.Add(scroll); root.Children.Add(layout);

        void ShowEntries(string title, string description, IEnumerable<(SettingsCategory Category, SettingsEntry Entry)> entries, bool nameCategory)
        {
            content.Children.Clear();
            content.Children.Add(new TextBlock { Tag = "settings-content-title", Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
            if (description.Length > 0) content.Children.Add(Hint(description));
            var any = false;
            foreach (var (category, entry) in entries) { content.Children.Add(Row(category, entry, context, nameCategory)); any = true; }
            if (!any) content.Children.Add(new TextBlock { Tag = "settings-empty", Text = "Bu aramaya uyan ayar yok; arama metnini kısaltın.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        void ShowCategory(SettingsCategory category) => ShowEntries(category.Label, category.Description, category.Entries.Select(e => (category, e)), nameCategory: false);
        // #854: a category with unsaved changes in any of its entries carries the mark; the tooltip and the accessible name say which fields, by label only.
        void RefreshBadges()
        {
            foreach (var item in list.Items.OfType<ListBoxItem>())
            {
                var category = categories.First(c => c.Key == (string)item.Tag);
                List<DirtySection> dirty = context.EditState is null ? new() : category.Entries.Select(e => context.EditState.Dirty(e.Key)).Where(d => d is not null).Select(d => d!).ToList();
                ((TextBlock)item.Content).Text = SettingsTaxonomy.ShortLabel(category.Label) + (dirty.Count > 0 ? " " + DirtyMark : "");
                item.ToolTip = category.Label + Environment.NewLine + category.Description + (dirty.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, dirty.Select(d => $"{category.Entries.First(e => e.Key == d.EntryKey).Label}: {d.Summary}")) : "");
                AutomationProperties.SetName(item, $"{category.Label}: {category.Entries.Count} ayar" + (dirty.Count > 0 ? ", kaydedilmemiş değişiklik" : ""));
            }
        }
        if (context.EditState is { } editState)
        {
            editState.Changed += () => list.Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshBadges();
                if (search.Text.Trim().Length == 0 && list.SelectedItem is ListBoxItem { Tag: string key }) ShowCategory(categories.First(c => c.Key == key));
            }));
        }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: string key } && SettingsTaxonomy.Find(key) is { } category && search.Text.Trim().Length == 0) ShowCategory(category with { Entries = categories.First(c => c.Key == key).Entries }); };
        search.TextChanged += (_, _) =>
        {
            var q = search.Text.Trim();
            if (q.Length == 0) { if (list.SelectedItem is ListBoxItem { Tag: string key }) ShowCategory(categories.First(c => c.Key == key)); return; }
            ShowEntries($"Arama: {q}", "", SettingsTaxonomy.Search(q, context.RouteExists), nameCategory: true);
        };
        void Select(string categoryKey)
        {
            var item = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, categoryKey, StringComparison.OrdinalIgnoreCase));
            if (item is null) return;
            search.Text = ""; list.SelectedItem = item; item.Focus();
        }
        exposeSelect?.Invoke(Select);
        // #856: a banner row's "Git" selects the owning category, then opens the owner the way the entry's own "Aç" does.
        void OpenEntry(string entryKey)
        {
            var hit = categories.SelectMany(c => c.Entries.Select(e => (Category: c, Entry: e))).FirstOrDefault(x => string.Equals(x.Entry.Key, entryKey, StringComparison.OrdinalIgnoreCase));
            if (hit.Category is null) return;
            Select(hit.Category.Key);
            if (hit.Entry.Inline) return;
            if (hit.Entry.Section.Length > 0 && context.OpenSection is not null) context.OpenSection(hit.Entry.Route, hit.Entry.Section); else context.Navigate?.Invoke(hit.Entry.Route);
        }
        var hadIssues = false;
        void Refresh()
        {
            if (context.Validate is null) return;
            IReadOnlyList<SettingsIssue> issues;
            try { issues = context.Validate(); }
            catch (Exception error) { issues = new[] { new SettingsIssue(SettingsIssueKind.InvalidConfig, "diagnostics", "Ayar denetimi çalıştırılamadı", SettingsValidation.Safe(error.Message), SeverityLevel.Warning) }; }
            SettingsValidationBanner.Render(banner, issues, showResolved: issues.Count == 0 && hadIssues, OpenEntry, Refresh, () => { banner.Children.Clear(); banner.Visibility = Visibility.Collapsed; });
            hadIssues = issues.Count > 0;
        }
        if (context.EditState is { } edits)
        {
            edits.Saved += () => banner.Dispatcher.BeginInvoke(new Action(Refresh));
            edits.IssuesChanged += () => banner.Dispatcher.BeginInvoke(new Action(Refresh));
        }
        RefreshBadges();
        Refresh();
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        return root;
    }

    static FrameworkElement Row(SettingsCategory category, SettingsEntry entry, Context context, bool nameCategory)
    {
        var body = new StackPanel();
        var dirty = context.EditState?.Dirty(entry.Key);
        var label = new TextBlock { Text = (nameCategory ? category.Label + " › " : "") + entry.Label + (dirty is null ? "" : " " + DirtyMark), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(label);
        body.Children.Add(new TextBlock { Text = entry.Description, TextWrapping = TextWrapping.Wrap, Opacity = 0.9, Margin = new Thickness(0, 2, 0, 4) });
        if (dirty is not null) body.Children.Add(new TextBlock { Tag = "settings-dirty-badge", Text = $"{DirtyMark} {dirty.Summary}", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = SeverityStyle.AccentBrush(SeverityLevel.Warning, SeverityStyle.IsHighContrast), Margin = new Thickness(0, 0, 0, 4) });
        if (entry.SecretBearing) body.Children.Add(new TextBlock { Tag = "settings-secret-badge", Text = "🔒 " + SecretBadge, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 4) });
        if (entry.Inline)
        {
            var inline = Inline(entry, context.Directory);
            if (inline is not null) { inline.Margin = new Thickness(0, 4, 0, 0); body.Children.Add(inline); }
        }
        else
        {
            var open = new Button { Tag = "settings-open", Content = "Aç", Padding = new Thickness(12, 3, 12, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
            AutomationProperties.SetAutomationId(open, entry.Key); AutomationProperties.SetName(open, "Aç: " + entry.Label);
            open.Click += (_, _) => { if (entry.Section.Length > 0 && context.OpenSection is not null) context.OpenSection(entry.Route, entry.Section); else context.Navigate?.Invoke(entry.Route); };
            body.Children.Add(open);
        }
        var border = new Border { Tag = "settings-entry", BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(222, 228, 232)), Padding = new Thickness(0, 8, 0, 8), Child = body };
        AutomationProperties.SetAutomationId(border, entry.Key); AutomationProperties.SetName(border, entry.Label + (entry.SecretBearing ? ", gizli bilgi içerir" : "") + (dirty is null ? "" : ", " + dirty.Summary));
        return border;
    }

    static FrameworkElement? Inline(SettingsEntry entry, string? directory) => entry.Key switch
    {
        "images" => MarketplaceImagePanel.Create(),
        "backup" => DataBackupPanel.Create(directory),
        "local-data" => new TextBlock { Text = "XML kaynakları ve ürün kilitleri XML yönetimi / Ürün yönetimi ekranlarından düzenlenir. Zamanlı XML yenilemesi yalnız uygulama açıkken çalışır. İşlem geçmişi pencerenin altındadır.", TextWrapping = TextWrapping.Wrap, Opacity = 0.9 },
        _ => null,
    };

    static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(87, 112, 125)), Margin = new Thickness(4, 4, 4, 8) };
}
