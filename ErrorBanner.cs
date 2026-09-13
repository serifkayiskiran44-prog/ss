using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

public enum ErrorRecovery { Retryable, Terminal }

/// <summary>What a workspace shows about a failure: how bad, whether trying again can help, and what to press.</summary>
public sealed record ErrorBannerModel(NotificationSeverity Severity, ErrorRecovery Recovery, string Title, string Text, bool CanRetry, bool CanOpenDiagnostics, string SourceRoute, string SourceLabel, int Count);

/// <summary>
/// The recoverable-error banner (#816). Classification is one rule set for every workspace: a busy/locked
/// database, a network failure, a timeout, a cancelled request or an I/O error is <b>retryable</b> and offers
/// "Yeniden dene" when the surface can actually re-run the action; a validation failure (invalid operation,
/// bad argument) is <b>terminal</b> -- retrying the same input cannot help -- and shows the message as the
/// instruction it is; anything else is terminal with a generic sentence, never its raw message. Every banner
/// offers diagnostics, and a source route ("go to the source") when the surface knows where the fix lives. A
/// duplicate (same recovery, same text) becomes a count on the standing banner, not a second banner. Text goes
/// through the central sanitizer and the raw-payload check, so a body, a token or an address never lands on a
/// banner someone else can read over the operator's shoulder.
/// </summary>
public static class ErrorBanner
{
    public const int MaxTextLength = 300;

    public static ErrorRecovery Classify(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is AggregateException aggregate && aggregate.InnerException is { } inner) error = inner;
        if (SqliteBusyDiagnostics.IsBusyOrLocked(error)) return ErrorRecovery.Retryable;
        return error switch
        {
            HttpRequestException or TimeoutException or TaskCanceledException or OperationCanceledException or IOException => ErrorRecovery.Retryable,
            _ => ErrorRecovery.Terminal,
        };
    }

    public static ErrorBannerModel Describe(Exception error, bool retryAvailable, string sourceRoute = "", string sourceLabel = "")
    {
        ArgumentNullException.ThrowIfNull(error);
        var recovery = Classify(error);
        var text = recovery == ErrorRecovery.Retryable
            ? (SqliteBusyDiagnostics.IsBusyOrLocked(error) ? SqliteBusyDiagnostics.Describe(error) : "Bağlantı veya dosya erişimi geçici olarak başarısız oldu; yeniden denenebilir.")
            : error is InvalidOperationException or ArgumentException ? error.Message
            : "İşlem tamamlanamadı. Dosya biçimini, erişim izinlerini ve bağlantıyı kontrol edin; ayrıntı Tanılama ekranında.";
        var title = recovery == ErrorRecovery.Retryable ? "Geçici hata" : "İşlem yapılamadı";
        var severity = recovery == ErrorRecovery.Retryable ? NotificationSeverity.Warning : NotificationSeverity.Error;
        return new(severity, recovery, title, Clean(text), retryAvailable && recovery == ErrorRecovery.Retryable, true,
            (sourceRoute ?? "").Trim(), Clean(sourceLabel ?? ""), 1);
    }

    /// <summary>The same failure again is a count on the standing banner, not another banner.</summary>
    public static ErrorBannerModel Merge(ErrorBannerModel? standing, ErrorBannerModel incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (standing is null || standing.Recovery != incoming.Recovery || standing.Text != incoming.Text) return incoming;
        return standing with { Count = standing.Count + 1, CanRetry = standing.CanRetry || incoming.CanRetry };
    }

    static string Clean(string raw)
    {
        var safe = StatusTooltip.LooksLikeRawPayload(raw) ? StatusTooltip.RawPayloadHidden : AuditStore.Sanitize(raw ?? "");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"\s+", " ").Trim();
        return safe.Length <= MaxTextLength ? safe : safe[..(MaxTextLength - 1)] + "…";
    }

    /// <summary>
    /// Renders a model as a banner: glyph + word + text (+ count), a wrap panel of focusable actions so a narrow
    /// window wraps them instead of clipping, Escape inside the banner dismisses, and an automation name that
    /// says severity, text and count. <paramref name="retry"/> runs the surface's own action again.
    /// </summary>
    public static Border Create(ErrorBannerModel model, Func<Task>? retry, Action<string>? navigate, Action dismiss)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(dismiss);
        // #817: glyph, word, weight and colour come from the one severity table.
        var style = SeverityStyle.For(SeverityStyle.FromNotification(model.Severity), SeverityStyle.IsHighContrast);
        var (glyph, word) = (style.Glyph, style.Word);
        var brush = new SolidColorBrush(style.Accent);
        var body = new StackPanel();
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(23, 54, 70)) };
        // #860: the glyph is a run on the text's own baseline, sized for a status surface.
        text.Inlines.Add(IconStyles.GlyphRun(glyph, IconRole.Status, brush));
        text.Inlines.Add(new System.Windows.Documents.Run($" {model.Title} · ") { FontWeight = FontWeights.SemiBold, Foreground = brush });
        text.Inlines.Add(new System.Windows.Documents.Run(model.Text));
        if (model.Count > 1) text.Inlines.Add(new System.Windows.Documents.Run($"  ×{model.Count}") { FontWeight = FontWeights.SemiBold });
        body.Children.Add(text);
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Button Action(string label, string name, Action click)
        {
            var button = new Button { Content = label, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), Focusable = true };
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => click();
            actions.Children.Add(button);
            return button;
        }
        if (model.CanRetry && retry is not null)
        {
            Button? retryButton = null;
            retryButton = Action("Yeniden dene", "Yeniden dene", async () => { retryButton!.IsEnabled = false; try { dismiss(); await retry(); } finally { retryButton.IsEnabled = true; } });
        }
        if (model.SourceRoute.Length > 0 && navigate is not null)
            Action(model.SourceLabel.Length > 0 ? model.SourceLabel : "Kaynağa git", "Kaynağa git", () => navigate(model.SourceRoute));
        if (model.CanOpenDiagnostics && navigate is not null)
            Action("Tanılamayı aç", "Tanılamayı aç", () => navigate("diagnostics"));
        Action("Kapat", $"{word} bildirimini kapat", dismiss);
        body.Children.Add(actions);
        var border = new Border
        {
            Child = body, Background = new SolidColorBrush(style.Surface),
            BorderBrush = brush, BorderThickness = new Thickness(style.BorderWeight), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(4, 0, 4, 8),
            Focusable = false, Tag = model,
        };
        border.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { dismiss(); e.Handled = true; } };
        AutomationProperties.SetName(border, $"{word}: {model.Text}{(model.Count > 1 ? $", {model.Count} kez" : "")}");
        AutomationProperties.SetLiveSetting(border, model.Severity == NotificationSeverity.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        return border;
    }
}

/// <summary>
/// One error surface per workspace: a host panel that holds at most one banner, merges duplicates into its
/// count, and re-renders. Panels call <see cref="Show"/> from their catch blocks instead of MessageBox.
/// </summary>
public sealed class ErrorSurface
{
    readonly Panel host;
    readonly Action<string>? navigate;
    ErrorBannerModel? standing;
    Func<Task>? standingRetry;

    public ErrorSurface(Panel host, Action<string>? navigate) { this.host = host ?? throw new ArgumentNullException(nameof(host)); this.navigate = navigate; }

    public ErrorBannerModel? Current => standing;

    public void Show(Exception error, Func<Task>? retry = null, string sourceRoute = "", string sourceLabel = "")
    {
        var incoming = ErrorBanner.Describe(error, retry is not null, sourceRoute, sourceLabel);
        standing = ErrorBanner.Merge(standing, incoming);
        standingRetry = retry ?? standingRetry;
        Render();
    }

    public void Clear() { standing = null; standingRetry = null; Render(); }

    void Render()
    {
        host.Children.Clear();
        if (standing is null) { host.Visibility = Visibility.Collapsed; return; }
        host.Visibility = Visibility.Visible;
        host.Children.Add(ErrorBanner.Create(standing, standingRetry, navigate, Clear));
    }
}
