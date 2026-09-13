using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>Why a command is disabled: a capability the channel lacks, a validation that blocks, a selection that is missing or too large, a store state that is not there yet, or work in progress.</summary>
public enum DisabledReasonKind { Capability, Validation, Selection, StoreState, Busy }

/// <summary>A disabled command's reason, with its kind kept apart so the words say what a person can do about it.</summary>
public sealed record DisabledReason(DisabledReasonKind Kind, string Text)
{
    public static DisabledReason Capability(string text) => new(DisabledReasonKind.Capability, text);
    public static DisabledReason Validation(string text) => new(DisabledReasonKind.Validation, text);
    public static DisabledReason Selection(string text) => new(DisabledReasonKind.Selection, text);
    public static DisabledReason StoreState(string text) => new(DisabledReasonKind.StoreState, text);
    public static DisabledReason Busy(string text) => new(DisabledReasonKind.Busy, text);

    /// <summary>A capability reason exists only for an operation the real capability set lacks; nothing is invented.</summary>
    public static DisabledReason? ForCapability(MarketplaceCapabilities capabilities, MarketplaceOperation operation, string channel)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.Enabled.Contains(operation) ? null : Capability($"{channel} bu işlemi desteklemiyor: {OperationWord(operation)}.");
    }

    public string Label => Kind switch
    {
        DisabledReasonKind.Capability => "Desteklenmiyor",
        DisabledReasonKind.Validation => "Doğrulama",
        DisabledReasonKind.Selection => "Seçim",
        DisabledReasonKind.StoreState => "Durum",
        _ => "Sürüyor",
    };

    /// <summary>The words a tooltip, a help text or a reason line shows: the kind, then the text through the central redaction (a reason never carries a secret).</summary>
    public string Describe() => $"{Label}: {AuditStore.Redact((Text ?? "").Trim())}";

    static string OperationWord(MarketplaceOperation operation) => operation switch
    {
        MarketplaceOperation.ProductsRead => "ürün okuma",
        MarketplaceOperation.OrdersRead => "sipariş okuma",
        MarketplaceOperation.StockWrite => "stok yazma",
        MarketplaceOperation.PriceWrite => "fiyat yazma",
        MarketplaceOperation.Shipment => "gönderi",
        _ => operation.ToString(),
    };
}

/// <summary>
/// The shared way a command is disabled with its reason (#871): the control's tooltip shows the reason and shows
/// while disabled, its automation help text carries it for a screen reader, the structured reason stays readable on
/// the control, and, where the caller gives a reason line, the line shows it in plain view so a keyboard user who
/// cannot focus a disabled control still reads it. Enabled again, the ordinary tooltip and the line go back to normal.
/// </summary>
public static class CommandState
{
    public static readonly DependencyProperty ReasonProperty = DependencyProperty.RegisterAttached("Reason", typeof(DisabledReason), typeof(CommandState), new PropertyMetadata(null));
    static readonly DependencyProperty OrdinaryTooltipProperty = DependencyProperty.RegisterAttached("OrdinaryTooltip", typeof(object), typeof(CommandState), new PropertyMetadata(null));

    public static DisabledReason? ReasonOf(DependencyObject control) => (DisabledReason?)control.GetValue(ReasonProperty);

    /// <summary>The reason that disables an element: its own, or the nearest ancestor's when a whole panel (an editor with nothing loaded) is disabled at once.</summary>
    public static DisabledReason? ReasonFor(DependencyObject element)
    {
        for (var node = element; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReasonOf(node) is { } reason) return reason;
        return null;
    }

    public static void Apply(FrameworkElement control, DisabledReason? reason, TextBlock? reasonLine = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        var had = ReasonOf(control);
        var text = reason?.Describe();
        if (reason is null)
        {
            if (had is not null) control.ToolTip = control.GetValue(OrdinaryTooltipProperty); // the ordinary tooltip comes back
        }
        else
        {
            if (had is null) control.SetValue(OrdinaryTooltipProperty, control.ToolTip); // remembered once, when a reason first joins it
            // A disabled command keeps saying what it does (and its shortcut) and adds why it cannot right now.
            control.ToolTip = control.GetValue(OrdinaryTooltipProperty) is string ordinary && ordinary.Trim().Length > 0 ? $"{ordinary} — {text}" : text;
        }
        control.SetValue(ReasonProperty, reason);
        control.IsEnabled = reason is null;
        ToolTipService.SetShowOnDisabled(control, true);
        AutomationProperties.SetHelpText(control, text ?? "");
        if (reasonLine is not null) { reasonLine.Text = text ?? ""; reasonLine.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible; }
    }

    /// <summary>A reason line to sit beside a command: a hint that wraps, hidden until a reason arrives, announced politely; under high contrast it takes the system's disabled text colour.</summary>
    public static TextBlock ReasonLine(bool highContrast = false)
    {
        var line = TextStyles.Create(TextRole.Hint, "");
        line.Tag = "command-reason"; line.TextWrapping = TextWrapping.Wrap; line.Visibility = Visibility.Collapsed; line.Margin = Spacing.AboveControl;
        if (highContrast) line.Foreground = SystemColors.GrayTextBrush;
        AutomationProperties.SetLiveSetting(line, AutomationLiveSetting.Polite);
        return line;
    }
}
