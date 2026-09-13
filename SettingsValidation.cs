using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public enum SettingsIssueKind { InvalidConfig, MissingSecret, StaleConnection, SaveConflict }

/// <summary>One thing wrong with the settings, tied to the taxonomy entry that owns the fix. Title and detail are copy, never a value.</summary>
public sealed record SettingsIssue(SettingsIssueKind Kind, string EntryKey, string Title, string Detail, SeverityLevel Severity);

/// <summary>
/// The settings validation model (#856). Facts about the real stores -- whether a connection has credentials and
/// whether the secret in them is present, whether the record still passes its own rules, when the registry last
/// verified it, whether a locale row still validates, which conflicts a form reported -- become issues tied to the
/// taxonomy entry that owns the fix: invalid configuration and a missing secret are blocking, a failed or old
/// verification is a warning, credentials never verified are information, and a save conflict is what the form
/// said it was. Blocking first, then warnings, then information; inside a level the taxonomy's order, so the
/// banner reads like the tree. Every text is redacted and capped; a fact carries presence, never a value.
/// </summary>
public static class SettingsValidation
{
    public const int StaleAfterDays = 30;
    public const int MaxDetailLength = 200;
    public const string NoIssues = "Ayar sorunu yok";

    /// <summary>What is known about one channel's connection: the registry's row and the credential store's presence, never a value.</summary>
    public sealed record ConnectionFacts(string Channel, bool Enabled, string Status, DateTime? LastTestUtc, string LastError, bool CredentialsSaved, bool SecretPresent, string? ConfigError);

    /// <summary>One stored locale row and the rule it fails, if any.</summary>
    public sealed record LocaleFacts(string Channel, string ShopId, string? ConfigError);

    /// <summary>The taxonomy entry that owns a channel's credentials.</summary>
    public static string EntryKeyFor(string channel) { var id = (channel ?? "").Trim().ToLowerInvariant(); return id == "navlungo" ? "shipping" : id + "-connection"; }

    public static IReadOnlyList<SettingsIssue> Evaluate(IEnumerable<ConnectionFacts> connections, IEnumerable<LocaleFacts> locales, IEnumerable<SettingsIssue> reported, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(connections); ArgumentNullException.ThrowIfNull(locales); ArgumentNullException.ThrowIfNull(reported);
        var issues = new List<SettingsIssue>();
        foreach (var c in connections)
        {
            var key = EntryKeyFor(c.Channel);
            if (SettingsTaxonomy.FindEntry(key) is null || !c.Enabled) continue; // nothing to link to, or disabled by choice
            var status = (c.Status ?? "").Trim().ToUpperInvariant();
            if (c.ConfigError is not null)
            { issues.Add(new(SettingsIssueKind.InvalidConfig, key, "Bağlantı ayarı geçersiz", Safe(c.ConfigError), SeverityLevel.Blocking)); continue; }
            if (c.CredentialsSaved && !c.SecretPresent)
            { issues.Add(new(SettingsIssueKind.MissingSecret, key, "Gizli erişim bilgisi eksik", "Kayıtta gizli değer yok; bağlantı sayfasında yeniden girin.", SeverityLevel.Blocking)); continue; }
            if (!c.CredentialsSaved)
            {
                // Never set up is not a problem; set up once (the registry verified it) and gone now is.
                if (status is "CONNECTED_READ_ONLY" or "FAILED" or "LIVE_API_BLOCKED")
                    issues.Add(new(SettingsIssueKind.MissingSecret, key, "Erişim bilgisi kayıtlı değil", "Bağlantı daha önce doğrulanmış, erişim bilgisi artık bulunamıyor; bağlantı sayfasında yeniden girin.", SeverityLevel.Blocking));
                continue;
            }
            if (status == "FAILED")
            { issues.Add(new(SettingsIssueKind.StaleConnection, key, "Son bağlantı testi başarısız", Safe(string.IsNullOrWhiteSpace(c.LastError) ? "Bağlantı testi başarısız." : c.LastError) + Age(c.LastTestUtc, nowUtc), SeverityLevel.Warning)); continue; }
            if (status == "LIVE_API_BLOCKED") continue; // a policy state, not staleness
            if (c.LastTestUtc is null)
            { issues.Add(new(SettingsIssueKind.StaleConnection, key, "Bağlantı henüz doğrulanmadı", "Kayıtlı erişim bilgisi salt okunur bağlantı testinden geçmedi.", SeverityLevel.Info)); continue; }
            var days = (int)Math.Floor((nowUtc - c.LastTestUtc.Value).TotalDays);
            if (days >= StaleAfterDays)
                issues.Add(new(SettingsIssueKind.StaleConnection, key, "Bağlantı doğrulaması eski", $"Son doğrulama {days} gün önce; bağlantıyı yeniden test edin.", SeverityLevel.Warning));
        }
        foreach (var l in locales)
            if (l.ConfigError is not null) issues.Add(new(SettingsIssueKind.InvalidConfig, "locale", $"Yerel ayar geçersiz: {Safe(l.Channel)}/{Safe(l.ShopId)}", Safe(l.ConfigError), SeverityLevel.Blocking));
        foreach (var r in reported) issues.Add(r with { EntryKey = (r.EntryKey ?? "").Trim(), Title = Safe(r.Title), Detail = Safe(r.Detail) });
        return issues
            .GroupBy(i => (i.Kind, i.EntryKey, i.Title)).Select(g => g.Last())
            .OrderByDescending(i => i.Severity).ThenBy(i => Position(i.EntryKey)).ThenBy(i => (int)i.Kind).ThenBy(i => i.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    public static string Headline(IReadOnlyList<SettingsIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Count == 0) return NoIssues;
        var blocking = issues.Count(i => i.Severity == SeverityLevel.Blocking); var warning = issues.Count(i => i.Severity == SeverityLevel.Warning); var info = issues.Count - blocking - warning;
        var parts = new List<string>();
        if (blocking > 0) parts.Add($"{blocking} engelleyici"); if (warning > 0) parts.Add($"{warning} uyarı"); if (info > 0) parts.Add($"{info} bilgi");
        return $"{issues.Count} ayar sorunu: {string.Join(", ", parts)}";
    }

    /// <summary>Banner text: redacted without a cap first, a raw payload hidden whole, whitespace folded, then capped.</summary>
    public static string Safe(string? text)
    {
        var raw = (text ?? "").Trim(); if (raw.Length == 0) return "";
        var safe = StatusTooltip.LooksLikeRawPayload(raw) ? StatusTooltip.RawPayloadHidden : AuditStore.Redact(raw);
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"\s+", " ").Trim();
        return safe.Length <= MaxDetailLength ? safe : safe[..(MaxDetailLength - 1)] + "…";
    }

    /// <summary>Reads the real stores in a directory (the profile's when null) and evaluates them. Credentials are read for presence and validity only.</summary>
    public static IReadOnlyList<SettingsIssue> Collect(string? directory, SettingsEditState? editState, DateTime nowUtc)
        => Evaluate(ConnectionFactsFrom(directory), LocaleFactsFrom(directory), editState?.Reported ?? Array.Empty<SettingsIssue>(), nowUtc);

    public static IReadOnlyList<ConnectionFacts> ConnectionFactsFrom(string? directory)
    {
        var registry = new MarketplaceConnectionStore(directory).List();
        var facts = new List<ConnectionFacts>();
        foreach (var definition in MarketplaceConnectionCatalog.All)
        {
            var rows = registry.Where(r => string.Equals(r.Channel, definition.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0) continue;
            var credentials = Credentials(definition.Id, directory);
            if (credentials is null) continue; // a channel whose credentials this probe cannot read: nothing to say
            var enabled = rows.Any(r => r.Enabled);
            var latest = rows.Where(r => r.Enabled).OrderByDescending(r => r.LastTestUtc ?? DateTime.MinValue).FirstOrDefault() ?? rows[0];
            facts.Add(new(definition.Id, enabled, latest.Status, latest.LastTestUtc, latest.LastError, credentials.Value.Saved, credentials.Value.Secret, credentials.Value.Error));
        }
        return facts;
    }

    public static IReadOnlyList<LocaleFacts> LocaleFactsFrom(string? directory)
    {
        var facts = new List<LocaleFacts>();
        foreach (var row in new LocaleSettingsStore(directory).List())
        {
            string? error = null;
            try { LocaleSettings.Validate(new StoreLocaleSettings { Channel = row.Channel, ShopId = row.ShopId, Currency = row.Currency, CultureName = row.CultureName, VatRate = row.VatRate, DatePattern = row.DatePattern }); }
            catch (Exception failure) { error = failure.Message; }
            facts.Add(new(row.Channel, row.ShopId, error));
        }
        return facts;
    }

    /// <summary>Presence and validity of a channel's credential file; the secret is looked at for emptiness and dropped.</summary>
    static (bool Saved, bool Secret, string? Error)? Credentials(string channel, string? directory)
    {
        var file = channel switch { "trendyol" => "trendyol.bin", "ebay" => "ebay.bin", "ozon" => "ozon.bin", "amazon" => "amazon.bin", "hepsiburada" => "hepsiburada.bin", "joom" => "joom.bin", "wish" => "wish.bin", "fruugo" => "fruugo.bin", "allegro" => "allegro.bin", _ => null };
        if (file is null) return null;
        var path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"), file);
        if (!File.Exists(path)) return (false, false, null);
        try
        {
            var secret = channel switch
            {
                "trendyol" => new TrendyolSettingsStore(path).Load()?.ApiSecret,
                "ebay" => new EbaySettingsStore(path).Load()?.Settings.ClientSecret,
                "ozon" => new OzonSettingsStore(path).Load()?.ApiKey,
                "amazon" => new AmazonSettingsStore(path).Load()?.ClientSecret,
                "hepsiburada" => new HepsiburadaSettingsStore(path).Load()?.Password,
                "joom" => new JoomSettingsStore(path).Load()?.ApiKey,
                "wish" => new WishSettingsStore(path).Load()?.ApiKey,
                "fruugo" => new FruugoSettingsStore(path).Load()?.Password,
                _ => new AllegroSettingsStore(path).Load()?.ClientSecret,
            };
            return secret is null ? (false, false, null) : (true, !string.IsNullOrWhiteSpace(secret), null);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or JsonException) { return (true, false, error.Message); }
        catch (Exception) { return (true, false, "Kayıtlı erişim bilgisi okunamadı; bu Windows kullanıcısıyla kaydedilmemiş veya dosya bozulmuş olabilir."); }
    }

    static string Age(DateTime? thenUtc, DateTime nowUtc) => thenUtc is null ? "" : $" (son test {StatusTooltip.Relative(thenUtc.Value, nowUtc)})";

    static int Position(string entryKey)
    {
        for (var c = 0; c < SettingsTaxonomy.Categories.Count; c++)
            for (var e = 0; e < SettingsTaxonomy.Categories[c].Entries.Count; e++)
                if (string.Equals(SettingsTaxonomy.Categories[c].Entries[e].Key, entryKey, StringComparison.OrdinalIgnoreCase)) return c * 100 + e;
        return int.MaxValue;
    }
}

/// <summary>
/// The banner at the top of the settings shell (#856): a headline with the counts, one focusable row per issue --
/// severity glyph and word, title, the owning entry's label, the detail -- each with a "Git" link that selects the
/// owning category and opens the owner's section, then "Yeniden denetle" and "Kapat". Escape inside the banner
/// dismisses it until the next check. When every issue of the previous check is gone, one success line says so.
/// DIP paddings and wrapping text: a narrow or scaled window grows the banner instead of clipping it.
/// </summary>
public static class SettingsValidationBanner
{
    public const string ResolvedText = "Ayar sorunları giderildi.";
    public const string GoLabel = "Git";

    public static void Render(Panel host, IReadOnlyList<SettingsIssue> issues, bool showResolved, Action<string> open, Action recheck, Action dismiss)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(issues); ArgumentNullException.ThrowIfNull(open); ArgumentNullException.ThrowIfNull(recheck); ArgumentNullException.ThrowIfNull(dismiss);
        host.Children.Clear();
        if (issues.Count == 0 && !showResolved) { host.Visibility = Visibility.Collapsed; return; }
        host.Visibility = Visibility.Visible;
        var hc = SeverityStyle.IsHighContrast;
        var body = new StackPanel();
        if (issues.Count == 0)
        {
            var ok = SeverityStyle.For(SeverityLevel.Success, hc);
            body.Children.Add(new TextBlock { Tag = "settings-banner-resolved", Text = $"{ok.Glyph} {ResolvedText}", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(ok.Accent) });
            body.Children.Add(Actions(("Kapat", "Bildirimi kapat", "settings-banner-dismiss", dismiss)));
            host.Children.Add(Shell(ok, body, $"{ok.Word}: {ResolvedText}", AutomationLiveSetting.Polite, dismiss));
            return;
        }
        var highest = SeverityStyle.Highest(issues.Select(i => i.Severity));
        var style = SeverityStyle.For(highest, hc);
        var headline = SettingsValidation.Headline(issues);
        body.Children.Add(new TextBlock { Tag = "settings-banner-headline", Text = $"{style.Glyph} {headline}", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(style.Accent) });
        foreach (var issue in issues) body.Children.Add(Row(issue, open, hc));
        body.Children.Add(Actions(("Yeniden denetle", "Ayarları yeniden denetle", "settings-banner-recheck", recheck), ("Kapat", $"{style.Word} bildirimini kapat", "settings-banner-dismiss", dismiss)));
        host.Children.Add(Shell(style, body, $"{style.Word}: {headline}", highest == SeverityLevel.Blocking ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite, dismiss));
    }

    static Border Row(SettingsIssue issue, Action<string> open, bool highContrast)
    {
        var style = SeverityStyle.For(issue.Severity, highContrast);
        var label = SettingsTaxonomy.FindEntry(issue.EntryKey)?.Entry.Label ?? issue.EntryKey;
        var text = new StackPanel();
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        title.Inlines.Add(new Run($"{style.Glyph} {style.Word} · ") { FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(style.Accent) });
        title.Inlines.Add(new Run(issue.Title) { FontWeight = FontWeights.SemiBold });
        title.Inlines.Add(new Run($" · {label}"));
        text.Children.Add(title);
        if (issue.Detail.Length > 0) text.Children.Add(new TextBlock { Text = issue.Detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var go = new Button { Tag = "settings-issue-open", Content = GoLabel, Padding = new Thickness(12, 3, 12, 3), Margin = Spacing.LeftControl, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(go, issue.EntryKey); AutomationProperties.SetName(go, $"{GoLabel}: {label}");
        go.Click += (_, _) => open(issue.EntryKey);
        var dock = new DockPanel { LastChildFill = true }; DockPanel.SetDock(go, Dock.Right); dock.Children.Add(go); dock.Children.Add(text);
        var row = new Border { Tag = "settings-issue", Child = dock, Focusable = true, BorderThickness = new Thickness(Math.Max(style.BorderWeight, 1), 0, 0, 0), BorderBrush = new SolidColorBrush(style.Accent), Padding = new Thickness(8, 4, 0, 4), Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(row, issue.EntryKey); AutomationProperties.SetName(row, $"{style.Word}: {issue.Title}, {label}"); AutomationProperties.SetHelpText(row, issue.Detail);
        row.KeyDown += (_, e) => { if (e.Key == Key.Enter) { open(issue.EntryKey); e.Handled = true; } };
        return row;
    }

    static WrapPanel Actions(params (string Label, string Name, string Tag, Action Click)[] actions)
    {
        var panel = new WrapPanel { Margin = Spacing.AboveControl };
        foreach (var (label, name, tag, click) in actions)
        {
            var button = new Button { Tag = tag, Content = label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            AutomationProperties.SetName(button, name); button.Click += (_, _) => click(); panel.Children.Add(button);
        }
        return panel;
    }

    static Border Shell(SeverityPresentation style, UIElement body, string name, AutomationLiveSetting live, Action dismiss)
    {
        var border = new Border { Tag = "settings-banner", Child = body, Background = new SolidColorBrush(style.Surface), BorderBrush = new SolidColorBrush(style.Accent), BorderThickness = new Thickness(Math.Max(style.BorderWeight, 1)), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(4, 0, 4, 8) };
        AutomationProperties.SetName(border, name); AutomationProperties.SetLiveSetting(border, live);
        border.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { dismiss(); e.Handled = true; } };
        return border;
    }
}
