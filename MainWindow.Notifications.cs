using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrMarketplaceHubDesktop;

// #814: the window's notification publisher. Log(text) is unchanged for its fifty-odd callers -- status line,
// history, log file, audit row. Log(text, severity) does all of that and also raises a toast through the one
// queue that owns duration, dedupe, stacking and the pending line; Notify(request) is the same without the
// status-line write, for a toast with an action.
public partial class MainWindow
{
    readonly NotificationQueue notificationQueue = new();
    DispatcherTimer? toastTimer;

    void Log(string text, NotificationSeverity severity)
    {
        Log(text);
        Notify(new NotificationRequest(severity, text));
    }

    void Notify(NotificationRequest request)
    {
        notificationQueue.Publish(request, DateTime.UtcNow);
        RenderToasts();
        EnsureToastTimer();
    }

    void EnsureToastTimer()
    {
        toastTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        toastTimer.Tick -= ToastTimer_Tick;
        toastTimer.Tick += ToastTimer_Tick;
        if (!toastTimer.IsEnabled) toastTimer.Start();
    }

    void ToastTimer_Tick(object? sender, EventArgs e)
    {
        if (notificationQueue.Expire(DateTime.UtcNow).Count > 0) RenderToasts();
        if (notificationQueue.Visible.Count == 0) toastTimer?.Stop();
    }

    void DismissTopToast()
    {
        if (notificationQueue.DismissTop() is null) return;
        RenderToasts();
    }

    void RenderToasts()
    {
        ToastHost.Children.Clear();
        foreach (var toast in notificationQueue.Visible)
        {
            // #817: the toast draws the same glyph, word and colour as every other severity surface.
            var style = SeverityStyle.For(SeverityStyle.FromNotification(toast.Severity), SeverityStyle.IsHighContrast);
            var (glyph, word, brush) = (style.Glyph, style.Word, style.Accent);
            var body = new DockPanel { LastChildFill = true };
            var close = new Button { Content = "✕", Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0), MinWidth = 26, Background = Brushes.Transparent, Foreground = new SolidColorBrush(brush), BorderBrush = new SolidColorBrush(brush), ToolTip = "Bildirimi kapat (Esc)" };
            AutomationProperties.SetName(close, $"{word} bildirimini kapat");
            var id = toast.Id;
            close.Click += (_, _) => { notificationQueue.Dismiss(id); RenderToasts(); };
            DockPanel.SetDock(close, System.Windows.Controls.Dock.Right);
            body.Children.Add(close);
            if (toast.ActionLabel.Length > 0 && toast.Route.Length > 0 && routes.ContainsKey(toast.Route))
            {
                var go = new Button { Content = toast.ActionLabel, Padding = Spacing.Chip, Margin = new Thickness(6, 0, 0, 0) };
                var route = toast.Route;
                go.Click += (_, _) => { notificationQueue.Dismiss(id); RenderToasts(); Navigate(route); };
                DockPanel.SetDock(go, System.Windows.Controls.Dock.Right);
                body.Children.Add(go);
            }
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(23, 54, 70)), VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new System.Windows.Documents.Run($"{glyph} {word} · ") { FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(brush) });
            text.Inlines.Add(new System.Windows.Documents.Run(toast.Text));
            if (toast.Count > 1) text.Inlines.Add(new System.Windows.Documents.Run($"  ×{toast.Count}") { FontWeight = FontWeights.SemiBold });
            body.Children.Add(text);
            var border = new Border
            {
                Child = body, Background = Brushes.White, BorderBrush = new SolidColorBrush(brush), BorderThickness = new Thickness(1, 1, 1, 1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 8, 8), Margin = new Thickness(0, 0, 0, 6), Tag = toast.Id,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.18 },
            };
            AutomationProperties.SetName(border, $"{word}: {toast.Text}{(toast.Count > 1 ? $", {toast.Count} kez" : "")}");
            AutomationProperties.SetLiveSetting(border, toast.Severity == NotificationSeverity.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
            ToastHost.Children.Add(border);
        }
        ToastHost.Visibility = ToastHost.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
