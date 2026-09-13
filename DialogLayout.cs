using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace TrMarketplaceHubDesktop;

/// <summary>A dialog's size decision, in DIPs: what it asked for, clamped to what the screen actually has.</summary>
public sealed record DialogSize(double Width, double Height, double MinWidth, double MinHeight, double MaxWidth, double MaxHeight);

/// <summary>
/// The dialog layout standard (#818), in one place. Sizing is decided in DIPs against the work area WPF reports
/// -- at 200% DPI the work area is half as many DIPs, so the same rule fits the same dialog without a pixel
/// conversion anywhere -- and a dialog never asks for more than the work area minus a margin, never less than
/// a usable minimum, and always allows resizing between the two. The shell every dialog gets: the body in a
/// vertical scroll viewer that grows with the content; a button bar docked under it that is never scrolled
/// away; the primary action on Enter and the cancel action on Escape, unless the action is destructive, in
/// which case Enter goes to Cancel so a stray keystroke cannot confirm it. Titles and messages pass the
/// central sanitizer: a modal is the last place a token or an address should surface.
/// </summary>
public static class DialogLayout
{
    public const double MinDialogWidth = 320;
    public const double MinDialogHeight = 240;
    public const double WorkAreaMargin = 48;

    public static DialogSize Fit(double requestedWidth, double requestedHeight, double workAreaWidth, double workAreaHeight)
    {
        var maxWidth = Math.Max(MinDialogWidth, workAreaWidth - WorkAreaMargin);
        var maxHeight = Math.Max(MinDialogHeight, workAreaHeight - WorkAreaMargin);
        var width = Clamp(double.IsNaN(requestedWidth) ? MinDialogWidth : requestedWidth, MinDialogWidth, maxWidth);
        var height = Clamp(double.IsNaN(requestedHeight) ? MinDialogHeight : requestedHeight, MinDialogHeight, maxHeight);
        return new(width, height, Math.Min(MinDialogWidth, maxWidth), Math.Min(MinDialogHeight, maxHeight), maxWidth, maxHeight);
    }

    static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

    // Redacted, never truncated: the audit sanitizer's caps are for stored rows, and a modal must show the whole text.
    public static string SafeText(string? value) => AuditStore.Redact(value ?? "").Trim();
}

/// <summary>The one dialog window every code-built dialog is made from.</summary>
public static class DialogShell
{
    public sealed record Action(string Label, bool IsPrimary = false, bool IsCancel = false, Func<bool>? OnClick = null);

    /// <summary>
    /// Builds the standard dialog: sanitized title, scrollable body, docked button bar. Returns the window; the
    /// caller decides ShowDialog. <paramref name="destructive"/> puts Enter on the cancel action.
    /// </summary>
    public static Window Create(Window? owner, string title, UIElement body, IReadOnlyList<Action> actions, double requestedWidth, double requestedHeight, bool destructive = false)
    {
        ArgumentNullException.ThrowIfNull(body); ArgumentNullException.ThrowIfNull(actions);
        var workArea = SystemParameters.WorkArea;
        var size = DialogLayout.Fit(requestedWidth, requestedHeight, workArea.Width, workArea.Height);
        var window = new Window
        {
            Owner = owner, Title = DialogLayout.SafeText(title), Width = size.Width, Height = size.Height,
            MinWidth = size.MinWidth, MinHeight = size.MinHeight, MaxWidth = size.MaxWidth, MaxHeight = size.MaxHeight,
            ResizeMode = ResizeMode.CanResize, SizeToContent = SizeToContent.Manual, ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        // #863: a dialog resolves the same tokens and control styles as the main window, with or without an Application.
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DesignTokens.Source });
        var root = new DockPanel { Margin = new Thickness(14), LastChildFill = true };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bar, Dock.Bottom);
        foreach (var action in actions)
        {
            var button = new Button { Content = action.Label, MinWidth = 96, Margin = new Thickness(6, 0, 0, 0), IsDefault = destructive ? action.IsCancel : action.IsPrimary, IsCancel = action.IsCancel };
            AutomationProperties.SetName(button, action.Label);
            var current = action;
            button.Click += (_, _) =>
            {
                var close = current.OnClick?.Invoke() ?? true;
                if (!close) return;
                if (window.IsVisible) { try { window.DialogResult = current.IsPrimary; } catch (InvalidOperationException) { } window.Close(); }
            };
            bar.Children.Add(button);
        }
        root.Children.Add(bar);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        root.Children.Add(scroll);
        window.Content = root;
        return window;
    }

    /// <summary>A wrapped, sanitized message body for confirmations and validation text: it wraps, it never widens the dialog.</summary>
    public static TextBlock Message(string text) => new() { Text = DialogLayout.SafeText(text), TextWrapping = TextWrapping.Wrap, Margin = Spacing.BelowInline };

    /// <summary>The standard confirmation: Enter is Cancel when the action is destructive, Escape always cancels.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmLabel, string cancelLabel = "Vazgeç", bool destructive = true)
    {
        var window = Create(owner, title, Message(message), new Action[] { new(cancelLabel, IsCancel: true), new(confirmLabel, IsPrimary: true) }, 460, 240, destructive);
        return window.ShowDialog() == true;
    }
}
