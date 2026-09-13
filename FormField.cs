using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrMarketplaceHubDesktop;

/// <summary>What a form row is, before it is drawn: the label, whether it is required, and the help that goes under it.</summary>
public sealed record FormFieldSpec(string Label, bool Required = false, string Help = "");

/// <summary>The built row: the pieces a form or a test needs to reach.</summary>
public sealed class FormFieldControl
{
    public required StackPanel Root { get; init; }
    public required TextBlock LabelText { get; init; }
    public required Control Input { get; init; }
    public required TextBlock HelpText { get; init; }
    public required TextBlock ValidationText { get; init; }

    /// <summary>Shows a validation message under the input in the blocking or warning style, or clears it.</summary>
    public void SetValidation(string message, SeverityLevel level = SeverityLevel.Blocking)
    {
        var text = FormField.SafeHelp(message);
        if (text.Length == 0) { ValidationText.Text = ""; ValidationText.Visibility = Visibility.Collapsed; AutomationProperties.SetHelpText(Input, HelpText.Text); return; }
        var style = SeverityStyle.For(level, SeverityStyle.IsHighContrast);
        ValidationText.Text = $"{style.Glyph} {text}";
        ValidationText.Foreground = SeverityStyle.AccentBrush(level, SeverityStyle.IsHighContrast);
        ValidationText.Visibility = Visibility.Visible;
        // The input's help text carries the message too, so a screen reader hears it on the field, not only in a list.
        AutomationProperties.SetHelpText(Input, string.IsNullOrEmpty(HelpText.Text) ? text : HelpText.Text + " " + text);
    }
}

/// <summary>
/// The form row standard (#819): label above the input, a required marker that is also spoken ("zorunlu" in the
/// accessible name, not only an asterisk), help text under the input in the quiet colour, and a validation slot
/// under that which is empty until a rule speaks. Spacing is a handful of DIP constants, so 100–200 % DPI is
/// the same rhythm; labels wrap, so a long Turkish label grows the row instead of being clipped. Help text is
/// copy, not a specimen: it passes the uncapped redaction and a rule that refuses example secrets ("örn.
/// token=…"), so a help line can never teach the operator by showing a credential.
/// </summary>
public static class FormField
{
    public const double LabelGap = 2;
    public const double RowGap = 8;
    public const double HelpGap = 3;
    public const string RequiredMarker = "*";
    public const string RequiredWord = "zorunlu";
    public const string OptionalWord = "isteğe bağlı";

    static readonly Brush LabelBrush = Frozen(Color.FromRgb(74, 96, 108));
    static readonly Brush HelpBrush = Frozen(Color.FromRgb(126, 146, 158));
    static readonly Brush RequiredBrush = Frozen(Color.FromRgb(190, 52, 52));
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>The visible label: the marker is appended, never prepended, so labels still sort and scan by their first letter.</summary>
    public static string LabelText(FormFieldSpec spec) => spec.Required ? $"{spec.Label.Trim()} {RequiredMarker}" : spec.Label.Trim();

    /// <summary>The spoken name: the word, not the glyph.</summary>
    public static string AccessibleName(FormFieldSpec spec) => spec.Required ? $"{spec.Label.Trim()}, {RequiredWord}" : spec.Label.Trim();

    /// <summary>Help text is sanitized without a cap, and an example that looks like a credential is refused outright.</summary>
    public static string SafeHelp(string? help)
    {
        var raw = (help ?? "").Trim();
        if (raw.Length == 0) return "";
        // "Örn: api_key=…", "token: …", "Bearer …" -- a help line must not carry a specimen secret even a fake one.
        // Checked on the raw text: redaction would rewrite the specimen into "[redacted]=[redacted]" and hide it.
        if (Regex.IsMatch(raw, @"(?i)\b(api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|parola|şifre|token|secret)\s*[:=]\s*\S+")) return "";
        if (Regex.IsMatch(raw, @"(?i)\bbearer\s+\S+")) return "";
        return Regex.Replace(AuditStore.Redact(raw), @"\s+", " ").Trim();
    }

    public static FormFieldControl Build(FormFieldSpec spec, Control input)
    {
        ArgumentNullException.ThrowIfNull(spec); ArgumentNullException.ThrowIfNull(input);
        var root = new StackPanel { Margin = new Thickness(0, 0, 0, RowGap) };
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = LabelBrush, Margin = new Thickness(0, 0, 0, LabelGap) };
        label.Inlines.Add(new System.Windows.Documents.Run(spec.Label.Trim()));
        if (spec.Required) label.Inlines.Add(new System.Windows.Documents.Run(" " + RequiredMarker) { Foreground = RequiredBrush, FontWeight = FontWeights.Bold });
        AutomationProperties.SetName(label, AccessibleName(spec));
        root.Children.Add(label);
        input.Margin = new Thickness(0);
        AutomationProperties.SetLabeledBy(input, label);
        AutomationProperties.SetName(input, AccessibleName(spec));
        root.Children.Add(input);
        var help = new TextBlock { Text = SafeHelp(spec.Help), TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = HelpBrush, Margin = new Thickness(0, HelpGap, 0, 0) };
        help.Visibility = help.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (help.Text.Length > 0) AutomationProperties.SetHelpText(input, help.Text);
        root.Children.Add(help);
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, HelpGap, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(validation, AutomationLiveSetting.Polite);
        root.Children.Add(validation);
        return new FormFieldControl { Root = root, LabelText = label, Input = input, HelpText = help, ValidationText = validation };
    }
}
